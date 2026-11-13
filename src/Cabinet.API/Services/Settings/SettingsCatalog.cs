using Cabinet.API.Infrastructure;
using Cabinet.Query.Bql;

namespace Cabinet.API.Services.Settings;

/// <summary>
/// Every setting the server reads, described for the settings screen. Defaults are the values the
/// code uses when nothing sets them; a test checks that each setting the code reads is here.
/// </summary>
public static class SettingsCatalog
{
    /// <summary>The groups, in the order they're shown.</summary>
    public static IReadOnlyList<SettingGroup> Groups { get; } =
    [
        new("Storage", "Where events and metadata are kept."),
        new("Retention", "How long events and audit records are kept."),
        new("Archive", "Where events go before retention deletes them, so they can be brought back."),
        new("Ingest", "Limits on what senders can send."),
        new("Search", "What searches can look up, such as where IP addresses are."),
        new("Search limits", "Time and memory one search may use."),
        new("AI investigator", "Answering questions about the logs with a language model that runs searches."),
        new("Rate limits", "Requests a client can make each minute."),
        new("Detection", "Rules checked on every event as it arrives."),
        new("Alerts & email", "Alert history, and sending alerts by email."),
        new("Webhooks", "Sending alerts to Slack, Discord, Teams, PagerDuty or any URL."),
        new("Syslog", "Receiving syslog over UDP, TCP and TLS."),
        new("OpenTelemetry", "Sending the server's own traces to a collector."),
        new("Encryption", "Encrypting chosen fields before they're stored."),
        new("Masking", "Hiding values from roles that shouldn't see them."),
        new("Audit", "The tamper-evident record of who did what."),
        new("Backups", "Copies of the SQLite database."),
        new("Updates", "Installing new releases from GitHub."),
        new("Gaming", "Parsers and detection for game servers."),
        new("Network & security", "Proxies, browser origins and single sign-on."),
        new("Background work", "How often scheduled work runs, and how instances find each other."),
    ];

    private static readonly ServerLimits Limits = new();
    private static readonly QueryLimits QueryDefaults = new();
    private static readonly Middleware.ConcurrencyOptions ConcurrencyDefaults = new();

    private static SettingDefinition S(
        string key, string group, string label, SettingType type, object? @default, string description,
        string? unit = null, double? min = null, double? max = null, IReadOnlyList<string>? options = null) => new()
        {
            Key = key,
            Group = group,
            Label = label,
            Type = type,
            Default = @default,
            Description = description,
            Unit = unit,
            Min = min,
            Max = max,
            Options = options,
        };

    private static SettingDefinition Limit(string property, string group, string label, string description, string unit, double min, double? max = null)
    {
        var value = typeof(ServerLimits).GetProperty(property)!.GetValue(Limits);
        var type = value is long ? SettingType.Long : SettingType.Int;
        return S($"Limits:{property}", group, label, type, value, description, unit, min, max);
    }

    /// <summary>Every setting, in display order within its group.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        // Storage
        S("Storage:Type", "Storage", "Storage backend", SettingType.Enum, "SQLite",
            "Where events are stored. SQLite is a single file on this machine and suits one server. PostgreSQL suits larger volumes and several instances. OpenSearch suits very large volumes. Cabinet is the built-in columnar engine: files under the data folder, fast searches and counts over large volumes on one server. ClickHouse suits volumes past one machine's disk, on a ClickHouse server you run. Memory keeps nothing across restarts and is for testing. Changing it doesn't move existing events.",
            options: ["SQLite", "PostgreSQL", "OpenSearch", "Cabinet", "ClickHouse", "Memory"]),
        S("Storage:ConnectionString", "Storage", "Connection string", SettingType.Secret, null,
            "For SQLite, the database file, such as \"Data Source=D:\\Cabinet\\cabinet.db\". For PostgreSQL, the host, database and credentials. For OpenSearch, its URL. Hidden because it can hold a password; the storage panel shows where events are."),
        S("Storage:DataPath", "Storage", "Data folder", SettingType.String, "cabinet-data",
            "With the Cabinet engine, the folder its files go in, one subfolder per index. Put it on a fast local disk."),
        S("Storage:EngineFlushEvents", "Storage", "Engine events in memory", SettingType.Int, 100000,
            "With the Cabinet engine, how many new events an index holds in memory (and in its write-ahead log) before writing them to a compressed segment.",
            min: 1000, max: 10000000),
        S("Storage:EngineFlushSeconds", "Storage", "Engine flush interval", SettingType.Int, 10,
            "With the Cabinet engine, the longest new events wait in memory before going into a segment.", unit: "seconds", min: 1, max: 3600),
        S("Storage:EngineMaxSegmentEvents", "Storage", "Engine segment size", SettingType.Int, 5000000,
            "With the Cabinet engine, segments are merged in the background until they hold about this many events. Larger means fewer files; smaller means less to rewrite when retention cuts through one.",
            min: 100000, max: 100000000),
        S("Storage:EngineSyncWrites", "Storage", "Engine waits for the disk", SettingType.Bool, true,
            "With the Cabinet engine, wait until each batch is on disk before acknowledging it. Turning it off is faster, but a power cut can lose the last moments of events."),
        S("Storage:MetadataConnectionString", "Storage", "Metadata database (SQLite)", SettingType.String, null,
            "With OpenSearch or the Cabinet engine, a SQLite file for saved searches, rules, dashboards and other settings, such as \"Data Source=cabinet-meta.db\". Leave empty otherwise."),
        S("Storage:MetadataPostgresConnectionString", "Storage", "Metadata database (PostgreSQL)", SettingType.Secret, null,
            "With OpenSearch or the Cabinet engine and several instances, a PostgreSQL database for metadata, so instances share it. Takes precedence over the SQLite metadata file."),
        S("Storage:ClickHouseDatabase", "Storage", "ClickHouse database", SettingType.String, "cabinet",
            "With ClickHouse, the database events go in, created if missing. Storage:ConnectionString is its HTTP address, and Storage:Username and Storage:Password sign in."),
        S("Storage:ClickHouseTable", "Storage", "ClickHouse table", SettingType.String, "events",
            "With ClickHouse, the table events go in, created if missing."),
        S("Storage:IndexPrefix", "Storage", "OpenSearch index prefix", SettingType.String, "cabinet-logs",
            "Prefix for the OpenSearch indexes events go in."),
        S("Storage:Username", "Storage", "OpenSearch username", SettingType.String, null, "User for OpenSearch, if it requires one."),
        S("Storage:Password", "Storage", "OpenSearch password", SettingType.Secret, null, "Password for OpenSearch, if it requires one."),
        S("Storage:AllowInsecureSsl", "Storage", "Accept any OpenSearch certificate", SettingType.Bool, false,
            "Skip checking OpenSearch's TLS certificate. Only for testing: anyone between the two could read and change events."),
        S("Storage:OpenSearchShards", "Storage", "OpenSearch shards per index", SettingType.Int, 1,
            "Primary shards for each daily OpenSearch index. One suits most volumes; add more only when one day's events outgrow a shard (tens of GB). Applies to indexes created after a restart."),
        S("Storage:OpenSearchReplicas", "Storage", "OpenSearch replicas", SettingType.Int, null,
            "Copies of each shard on other nodes. Empty means none for one node and one for several. Applies to indexes created after a restart."),
        S("Storage:OpenSearchFieldLimit", "Storage", "OpenSearch field limit", SettingType.Int, 10000,
            "Most distinct field names one daily index can hold. OpenSearch's own default of 1,000 rejects events once logs have used that many names. Applies to indexes created after a restart."),
        S("Storage:PostgresWorkMemMb", "Storage", "PostgreSQL search memory", SettingType.Int, 256,
            "Memory (MB) each PostgreSQL connection may use per operation. Full-text searches over large result sets need enough that the match stays exact instead of being rechecked row by row. Lower it on a small server; raise it if broad searches are slow. Takes effect on restart.",
            unit: "MB", min: 4, max: 4096),

        // Retention
        S("Storage:RetentionDays", "Retention", "Keep events for", SettingType.Int, 0,
            "Events older than this are deleted, across every index without its own policy. 0 keeps everything. Per-index policies are on the Retention panel.",
            unit: "days", min: 0, max: 36500),
        S("Audit:RetentionDays", "Retention", "Keep audit records for", SettingType.Int, 365,
            "Audit records older than this are removed, in a way verification still accepts.", unit: "days", min: 1, max: 36500),
        Limit(nameof(ServerLimits.RetentionCheckMinutes), "Retention", "Check retention every",
            "How often old events and audit records are removed.", "minutes", 1, 1440),

        // Ingest
        Limit(nameof(ServerLimits.MaxRequestBodyMB), "Ingest", "Largest request",
            "The largest request body any route accepts.", "MB", 1, 2048),
        Limit(nameof(ServerLimits.HecMaxRequestMB), "Ingest", "Largest HEC request",
            "The largest request to the HTTP Event Collector, which agents send to. Agents send batches well under 1 MB.", "MB", 1, 1024),
        Limit(nameof(ServerLimits.OtlpMaxRequestMB), "Ingest", "Largest OTLP request",
            "The largest OpenTelemetry request, as sent.", "MB", 1, 1024),
        Limit(nameof(ServerLimits.OtlpMaxDecompressedMB), "Ingest", "Largest OTLP request, decompressed",
            "The largest an OpenTelemetry request may be once decompressed. A small compressed request can expand a lot, so this stops one using all memory.", "MB", 1, 4096),
        S("MultiLine:Pattern", "Ingest", "Multi-line pattern", SettingType.String, null,
            "For lines sent to /api/v1/logs/ingest: a regular expression matching the first line of each event, so stack traces stay with their message. Empty uses lines starting with a timestamp. Agents merge their own lines."),
        S("MultiLine:Mode", "Ingest", "Multi-line mode", SettingType.Enum, "start",
            "Whether the pattern matches the line that starts an event (start) or lines that continue one (continuation).",
            options: ["start", "continuation"]),

        // Search limits
        Limit(nameof(ServerLimits.QueryTimeoutSeconds), "Search limits", "Search time limit",
            "How long a search on the search page may run before it stops. Use a search job for longer ones.", "seconds", 1, 3600),
        Limit(nameof(ServerLimits.QueryMaxResults), "Search limits", "Most results",
            "The most results one search returns.", "results", 1, 1_000_000),
        Limit(nameof(ServerLimits.MaxQueryLength), "Search limits", "Longest search",
            "The longest search text accepted.", "characters", 100, 1_000_000),
        S("Ai:Enabled", "AI investigator", "Turn on the AI investigator", SettingType.Bool, false,
            "Lets people ask questions that a language model answers by running searches with their access. Values are masked as for a Viewer before the model sees them, and every question and search is audited. Takes effect on restart."),
        S("Ai:Provider", "AI investigator", "Provider", SettingType.Enum, "openai-compatible",
            "openai-compatible is a model server on your own machines, such as Ollama, LM Studio or vLLM, so logs stay in-house. anthropic sends questions and masked search results to Claude.", options: ["openai-compatible", "anthropic"]),
        S("Ai:BaseUrl", "AI investigator", "Model server URL", SettingType.String, "http://localhost:11434/v1",
            "For openai-compatible: the server's base URL, ending in /v1. Ollama's is http://localhost:11434/v1."),
        S("Ai:Model", "AI investigator", "Model", SettingType.String, null,
            "The model to use. Empty means llama3.1 for openai-compatible and claude-opus-5-5 for anthropic. Pick one that supports tool calling."),
        S("Ai:ApiKey", "AI investigator", "API key", SettingType.Secret, null,
            "A key for the model server, if it needs one. For anthropic, empty means the ANTHROPIC_API_KEY environment variable."),
        S("Ai:MaxSearches", "AI investigator", "Most searches per question", SettingType.Int, 8, "How many searches the model may run to answer one question."),
        S("Ai:RowsPerSearch", "AI investigator", "Rows the model sees per search", SettingType.Int, 50, "The first rows of each search sent to the model."),
        S("Ai:TimeoutSeconds", "AI investigator", "Time limit", SettingType.Int, 180, "Seconds one question may take in all."),
        S("Archive:Type", "Archive", "Archive", SettingType.Enum, "None",
            "Where retention puts events before deleting them: a directory (a local disk or a mounted share) or an S3 bucket. Takes effect on restart.", options: ["None", "Directory", "S3"]),
        S("Archive:Path", "Archive", "Archive directory", SettingType.String, "archive", "For Directory: where archived events go."),
        S("Archive:S3Bucket", "Archive", "S3 bucket", SettingType.String, null, "For S3: the bucket. Credentials come from the usual AWS sources."),
        S("Archive:S3Prefix", "Archive", "S3 prefix", SettingType.String, "cabinet-events", "For S3: the folder in the bucket."),
        S("Archive:SearchMaxEvents", "Archive", "Most events an archive search holds", SettingType.Int, 500000,
            "Searching the archive where it is reads the archived days in range into memory. A range holding more than this is refused: search a shorter time, or rehydrate.",
            min: 1000, max: 10000000),
        S("Archive:BeforeRetention", "Archive", "Archive every index before deleting", SettingType.Bool, false,
            "Archive events of indexes without their own retention policy before retention deletes them. A policy's own setting wins."),
        S("GeoIp:DatabasePath", "Search", "GeoIP database", SettingType.String, null,
            "A MaxMind GeoLite2-City or GeoIP2-City .mmdb file, for iplocation and map charts. GeoLite2 is free from maxmind.com with an account. Takes effect on restart."),
        S("Query:Limits:MaxRowsInMemory", "Search limits", "Rows held in memory", SettingType.Int, QueryDefaults.MaxRowsInMemory,
            "Rows kept for stages that need all their input at once, such as sort without head, eventstats and transaction. Beyond this the input is cut off and the result marked incomplete.",
            unit: "rows", min: 1000, max: 100_000_000),
        S("Query:Limits:MaxGroups", "Search limits", "Most groups", SettingType.Int, QueryDefaults.MaxGroups,
            "Groups a stats, timechart or chart can produce before the search fails.", unit: "groups", min: 100, max: 100_000_000),
        S("Query:Limits:MaxRetainedValues", "Search limits", "Values held for distinct counts and percentiles", SettingType.Long, QueryDefaults.MaxRetainedValues,
            "Values kept across dc, median and percentile aggregates before the search fails.", unit: "values", min: 1000, max: 10_000_000_000),
        S("Query:Limits:MaxExactPercentileValues", "Search limits", "Exact percentile values", SettingType.Int, QueryDefaults.MaxExactPercentileValues,
            "Values kept for one median or percentile. Beyond this it samples, and the result is marked approximate.", unit: "values", min: 100, max: 100_000_000),
        S("Query:Limits:MaxEventsScanned", "Search limits", "Most events read", SettingType.Long, QueryDefaults.MaxEventsScanned,
            "Events one search reads before it stops and is marked incomplete. 0 leaves the time limit to stop it.", unit: "events", min: 0),
        S("Query:Concurrency:MaxConcurrent", "Search limits", "Searches at once", SettingType.Int, ConcurrencyDefaults.MaxConcurrent,
            "Searches running at once across all clients. More wait or are refused with 429.", unit: "searches", min: 1, max: 10_000),
        S("Query:Concurrency:MaxConcurrentPerClient", "Search limits", "Searches at once per client", SettingType.Int, ConcurrencyDefaults.MaxConcurrentPerClient,
            "Searches one key or user can run at once.", unit: "searches", min: 1, max: 10_000),
        S("Query:Concurrency:MaxStreams", "Search limits", "Streams at once", SettingType.Int, ConcurrencyDefaults.MaxStreams,
            "Live tails and streamed searches open at once across all clients.", unit: "streams", min: 1, max: 10_000),
        S("Query:Concurrency:MaxStreamsPerClient", "Search limits", "Streams at once per client", SettingType.Int, ConcurrencyDefaults.MaxStreamsPerClient,
            "Live tails and streamed searches one key or user can have open.", unit: "streams", min: 1, max: 10_000),
        S("Query:CacheTtlSeconds", "Search limits", "Cache results for", SettingType.Int, 30,
            "How long the same search by the same caller reuses its results, which helps dashboards that refresh often. 0 turns caching off.", unit: "seconds", min: 0, max: 3600),
        Limit(nameof(ServerLimits.StreamTimeoutMinutes), "Search limits", "Stream time limit",
            "How long a streamed search or log stream may run.", "minutes", 1, 1440),
        Limit(nameof(ServerLimits.StreamMaxResults), "Search limits", "Most streamed results",
            "The most results a streamed search sends.", "results", 1, 100_000_000),
        Limit(nameof(ServerLimits.ExportTimeoutSeconds), "Search limits", "Export time limit",
            "How long an export may run.", "seconds", 1, 3600),
        Limit(nameof(ServerLimits.ExportMaxRows), "Search limits", "Most exported rows",
            "The most rows one export holds.", "rows", 1, 100_000_000),
        Limit(nameof(ServerLimits.FieldSummaryMaxEvents), "Search limits", "Events in a field summary",
            "Events looked at to summarize fields.", "events", 100, 10_000_000),
        Limit(nameof(ServerLimits.LiveTailMaxMinutes), "Search limits", "Longest live tail",
            "How long one live tail stays connected.", "minutes", 1, 1440),
        Limit(nameof(ServerLimits.MaxActiveSearchJobsPerUser), "Search limits", "Search jobs per user",
            "Search jobs one user can have waiting or running at once.", "jobs", 1, 1000),
        Limit(nameof(ServerLimits.QueryRegexTimeoutMs), "Search limits", "Regular expression time per event",
            "How long rex or regex may spend on one event. A pattern that keeps running out fails the search, rather than using its whole time limit.", "ms", 1, 60_000),
        Limit(nameof(ServerLimits.FieldExtractionRegexTimeoutMs), "Search limits", "Field extraction time per event",
            "How long a saved field extraction may spend on one event before it's skipped for that event.", "ms", 1, 60_000),
        Limit(nameof(ServerLimits.FieldExtractionReloadSeconds), "Search limits", "Reload field extractions every",
            "How often saved field extractions are reread, so changes made on another instance apply. Changes made on this instance apply at once.", "seconds", 1, 3600),

        // Rate limits
        S("RateLimiting:PreAuth:MaxRequestsPerMinute", "Rate limits", "Requests per address", SettingType.Int, 300,
            "Requests one address can make each window, before authentication, across all routes but health checks and /metrics.",
            unit: "requests", min: 1, max: 10_000_000),
        S("RateLimiting:PreAuth:WindowSeconds", "Rate limits", "Per-address window", SettingType.Int, 60,
            "The window the per-address limit counts over.", unit: "seconds", min: 1, max: 3600),
        S("RateLimiting:Anonymous:MaxRequestsPerMinute", "Rate limits", "Anonymous requests per address", SettingType.Int, 100,
            "Requests a minute without a key, per address. Over the limit they're refused rather than queued.", unit: "requests/min", min: 1, max: 10_000_000),
        S("RateLimiting:Tiers:Basic", "Rate limits", "Basic keys", SettingType.Int, 100,
            "Requests a minute for each Basic key, shared out among running instances.", unit: "requests/min", min: 1, max: 100_000_000),
        S("RateLimiting:Tiers:Professional", "Rate limits", "Professional keys", SettingType.Int, 1000,
            "Requests a minute for each Professional key.", unit: "requests/min", min: 1, max: 100_000_000),
        S("RateLimiting:Tiers:Enterprise", "Rate limits", "Enterprise, Admin and Ingest keys", SettingType.Int, 10000,
            "Requests a minute for each Enterprise, Admin or Ingest key. Agents use Ingest keys, so keep this well above what they send.",
            unit: "requests/min", min: 1, max: 100_000_000),

        // Detection
        S("Detection:BuiltinRulesEnabled", "Detection", "Built-in rules on by default", SettingType.Bool, false,
            "Whether built-in rules start turned on. Rules you've turned on or off yourself keep that choice."),
        S("Detection:ActionTimeoutSeconds", "Detection", "Time per rule action", SettingType.Int, 15,
            "How long one action (a webhook, connector or notification) may take before it's stopped. Actions run on their own queue, so a slow one never holds up ingest. Takes effect on restart.",
            unit: "seconds", min: 1, max: 600),
        S("Detection:ActionQueueSize", "Detection", "Queued rule actions", SettingType.Int, 1000,
            "How many firings may wait for their actions. Beyond this, actions are skipped and logged; the firing is still recorded in alert history. Takes effect on restart.",
            min: 10, max: 1_000_000),
        S("Outbound:AllowPrivateTargets", "Detection", "Allow private webhook targets", SettingType.Bool, false,
            "Let webhooks and connectors reach loopback, private and link-local addresses. Off, they can't be aimed at this server, the cloud metadata endpoint or the internal network. Takes effect on restart."),
        S("Outbound:AllowedHosts", "Detection", "Allowed internal hosts", SettingType.List, null,
            "Host names or addresses webhooks and connectors may reach even when private targets aren't allowed, such as an internal Jira. Takes effect on restart."),
        Limit(nameof(ServerLimits.DetectionRegexTimeoutMs), "Detection", "Rule time per event",
            "How long a rule's regular expression may spend on one event.", "ms", 1, 60_000),
        Limit(nameof(ServerLimits.DetectionRuleReloadSeconds), "Detection", "Reload rules every",
            "How often rules are reread, so changes made on another instance apply.", "seconds", 1, 3600),
        Limit(nameof(ServerLimits.DetectionMaxRegexTimeouts), "Detection", "Slow pattern warnings",
            "Timeouts logged for one rule before it stops being reported.", "warnings", 1, 1_000_000),
        Limit(nameof(ServerLimits.BacktestMaxHours), "Detection", "Longest backtest",
            "The longest period a backtest can cover.", "hours", 1, 24 * 3650),
        Limit(nameof(ServerLimits.BacktestMaxEvents), "Detection", "Most events in a backtest",
            "Events one backtest can read.", "events", 1, 10_000_000_000),

        // Alerts & email
        Limit(nameof(ServerLimits.AlertHistoryMax), "Alerts & email", "Alerts kept",
            "Alerts kept in history. Older ones are removed.", "alerts", 100, 10_000_000),
        S("Email:Enabled", "Alerts & email", "Send alerts by email", SettingType.Bool, false, "Send alerts to the recipients below."),
        S("Email:SmtpHost", "Alerts & email", "SMTP server", SettingType.String, "localhost", "The mail server alerts are sent through."),
        S("Email:SmtpPort", "Alerts & email", "SMTP port", SettingType.Int, 587, "Usually 587 with TLS, 465 for implicit TLS, or 25.", min: 1, max: 65535),
        S("Email:EnableTls", "Alerts & email", "Use TLS", SettingType.Bool, true, "Encrypt the connection to the mail server."),
        S("Email:Username", "Alerts & email", "SMTP username", SettingType.String, null, "The mail server account, if it needs one."),
        S("Email:Password", "Alerts & email", "SMTP password", SettingType.Secret, null, "The mail server account's password."),
        S("Email:FromAddress", "Alerts & email", "From address", SettingType.String, "cabinet-alerts@localhost", "The address alerts come from."),
        S("Email:FromName", "Alerts & email", "From name", SettingType.String, "Cabinet Alert System", "The name alerts come from."),
        S("Email:ToAddresses", "Alerts & email", "Recipients", SettingType.List, Array.Empty<string>(), "Addresses every alert is sent to."),
        S("Email:TimeoutSeconds", "Alerts & email", "Send time limit", SettingType.Int, 30, "How long sending one email may take.", unit: "seconds", min: 1, max: 600),

        // Webhooks
        S("Webhook:Url", "Webhooks", "Webhook URL", SettingType.Secret, null,
            "Where alerts are posted. Hidden because chat webhook URLs carry their own access token. Empty turns webhooks off."),
        S("Webhook:Format", "Webhooks", "Format", SettingType.Enum, "generic",
            "The shape of the message: generic JSON, or what Slack, Discord, Teams or PagerDuty expect.",
            options: ["generic", "slack", "discord", "teams", "pagerduty"]),
        S("Webhook:AuthorizationType", "Webhooks", "Authorization type", SettingType.String, null,
            "The scheme for the Authorization header, such as Bearer, if the receiver needs one."),
        S("Webhook:AuthorizationHeader", "Webhooks", "Authorization value", SettingType.Secret, null, "The credential sent in the Authorization header."),
        S("Webhook:Headers", "Webhooks", "Extra headers", SettingType.Json, null, "Other headers to send, as a JSON object of names and values."),
        S("Webhook:PagerDutyRoutingKey", "Webhooks", "PagerDuty routing key", SettingType.Secret, null, "The integration's routing key, for the PagerDuty format."),
        S("Webhook:TimeoutSeconds", "Webhooks", "Send time limit", SettingType.Int, 30, "How long one post may take.", unit: "seconds", min: 1, max: 600),
        S("Webhook:RetryCount", "Webhooks", "Retries", SettingType.Int, 3, "How many times a failed post is retried.", unit: "retries", min: 0, max: 100),

        // Syslog
        S("Syslog:Enabled", "Syslog", "Receive syslog", SettingType.Bool, false, "Listen for syslog (RFC 3164 and RFC 5424). Restrict these ports at the network: senders aren't authenticated."),
        S("Syslog:BindAddress", "Syslog", "Listen on", SettingType.String, "0.0.0.0", "The address to listen on. 0.0.0.0 is every address on this machine."),
        S("Syslog:UdpPort", "Syslog", "UDP port", SettingType.Int, 1514, "The UDP port. Ports below 1024 need extra privileges on Linux.", min: 1, max: 65535),
        S("Syslog:TcpPort", "Syslog", "TCP port", SettingType.Int, 1514, "The TCP port, which also takes RFC 6587 octet-counted framing.", min: 1, max: 65535),
        S("Syslog:TlsPort", "Syslog", "TLS port", SettingType.Int, 0, "0 turns TLS off. Needs a certificate below.", min: 0, max: 65535),
        S("Syslog:TlsCertificatePath", "Syslog", "TLS certificate", SettingType.String, null, "A PEM file, or a .pfx or .p12 file."),
        S("Syslog:TlsKeyPath", "Syslog", "TLS key", SettingType.String, null, "The private key, if the PEM certificate doesn't include it."),
        S("Syslog:TlsCertificatePassword", "Syslog", "Certificate password", SettingType.Secret, null, "For a .pfx or .p12 certificate."),
        S("Syslog:MaxMessageBytes", "Syslog", "Largest message", SettingType.Int, 64 * 1024, "Longer messages are cut off.", unit: "bytes", min: 480, max: 16 * 1024 * 1024),
        S("Syslog:MaxConnections", "Syslog", "Connections at once", SettingType.Int, 1000, "TCP and TLS senders connected at once.", unit: "connections", min: 1, max: 1_000_000),

        // OpenTelemetry
        S("Telemetry:OtlpEndpoint", "OpenTelemetry", "Collector endpoint", SettingType.String, null,
            "Where the server sends traces of its own requests, searches and ingest, such as http://collector:4317. Empty sends nothing."),
        S("Telemetry:OtlpProtocol", "OpenTelemetry", "Protocol", SettingType.Enum, "grpc", "gRPC (usually port 4317) or HTTP (usually 4318).", options: ["grpc", "http"]),
        S("Telemetry:ServiceName", "OpenTelemetry", "Service name", SettingType.String, "cabinet-api", "How this server is named in traces."),

        // Encryption
        S("Encryption:Enabled", "Encryption", "Encrypt fields", SettingType.Bool, false,
            "Encrypt the fields below with AES-256-GCM before they're stored. Encrypted values can't be searched in storage. Back up the key: without it the data can't be read."),
        S("Encryption:KeyProvider", "Encryption", "Key from", SettingType.Enum, "Local",
            "Local keeps a key file on this machine. Environment reads a base64 key from CABINET_ENCRYPTION_KEY, which several instances can share.",
            options: ["Local", "Environment"]),
        S("Encryption:KeyFilePath", "Encryption", "Key file", SettingType.String, null, "For Local: where the key file is. Created if missing."),
        S("Encryption:EncryptedFields", "Encryption", "Fields to encrypt", SettingType.List, Array.Empty<string>(),
            "Field names to encrypt; * matches any characters, such as secret_*. Their values are also removed from the raw line."),
        S("Encryption:EncryptRawLine", "Encryption", "Encrypt whole lines", SettingType.Bool, false,
            "Encrypt each event's whole line. Text search then finds nothing in storage."),
        S("Encryption:PreviousKeyFilePaths", "Encryption", "Previous key files", SettingType.List, Array.Empty<string>(),
            "Keys from before a rotation, so older values stay readable. Keep each until retention has removed what it encrypted."),

        // Masking
        S("Masking:Rules", "Masking", "Masking rules", SettingType.Json, null,
            "Rules applied to every read, by role: [{\"fieldPattern\": \"password\", \"strategy\": \"Redact\", \"exemptRoles\": [\"Admin\"]}]. Strategies: Redact, Hash, Partial, Pattern, Remove. valuePattern masks matches in the raw line too."),

        // Audit
        S("Audit:HmacKey", "Audit", "Signing key", SettingType.Secret, null,
            "At least 32 characters. Makes the audit chain's hashes HMACs, so rewriting it also needs this key. Keep it away from the database."),
        Limit(nameof(ServerLimits.AuditHeadLogInterval), "Audit", "Write the chain head every",
            "Audit records between copies of the chain's newest hash in the log output, to keep elsewhere.", "records", 1, 1_000_000),

        // Backups
        S("Backup:ArchivePath", "Backups", "Backup folder", SettingType.String, "./backups", "Where SQLite backups are written."),
        S("Backup:RetentionDays", "Backups", "Keep backups for", SettingType.Int, 30, "Backups older than this are deleted. 0 keeps them all.", unit: "days", min: 0, max: 36500),
        S("Backup:AutoBackupEnabled", "Backups", "Back up automatically", SettingType.Bool, false, "Take a backup on a schedule."),
        S("Backup:AutoBackupIntervalHours", "Backups", "Back up every", SettingType.Int, 24, "Time between automatic backups.", unit: "hours", min: 1, max: 8760),
        S("Backup:S3Bucket", "Backups", "S3 bucket", SettingType.String, null, "Also upload each backup to this S3 bucket. Empty keeps them local."),
        S("Backup:S3Prefix", "Backups", "S3 prefix", SettingType.String, "cabinet-backups/", "Key prefix for uploaded backups."),
        Limit(nameof(ServerLimits.BackupMinimumIntervalMinutes), "Backups", "Time between manual backups",
            "Each backup is a full copy, so requests closer together than this are refused.", "minutes", 0, 1440),

        // Updates
        S("Metrics:RequireKey", "Network & security", "Require a key for /metrics", SettingType.Bool, false,
            "Prometheus metrics need an API key above Ingest (X-API-Key, or a bearer token in the scrape config), except from this machine and the addresses below. New installs turn it on."),
        S("Metrics:AllowFrom", "Network & security", "Addresses that read /metrics without a key", SettingType.List, Array.Empty<string>(),
            "Addresses or ranges, such as your Prometheus server or 10.0.0.0/8, allowed to read /metrics without a key when one is required."),
        S("Updates:AgentHosts", "Updates", "Agents that update", SettingType.List, Array.Empty<string>(),
            "Host names of agents that take this server's agent version, * as a wildcard, for a staged rollout: a test machine first, then the rest. Empty updates every agent. Takes effect on restart."),
        S("Updates:Repository", "Updates", "Release repository", SettingType.String, "bogware/cabinet", "The GitHub repository releases come from."),
        S("Updates:Token", "Updates", "GitHub token", SettingType.Secret, null,
            "A token that can read the repository's releases: fine-grained, this repository only, Contents read-only. Needed while the repository is private."),
        S("Updates:CheckIntervalHours", "Updates", "Check every", SettingType.Int, 6, "How often to look for a new release. Releases are never installed without being asked. 0 checks only when asked.", unit: "hours", min: 0, max: 720),
        S("Updates:WorkDirectory", "Updates", "Download folder", SettingType.String, null, "Where downloads are kept. Empty uses an updates folder beside the program's."),
        S("Updates:ServiceName", "Updates", "Service name", SettingType.String, null, "The service restarted when updating or restarting. Empty uses CabinetAPI on Windows and cabinet-api elsewhere."),

        // Gaming
        S("Gaming:Enabled", "Gaming", "Game server parsers", SettingType.Bool, false,
            "Read Left 4 Dead 2, SourceMod and SteamCMD logs, and turn on the moderation detectors at /api/v1/detection."),

        // Network & security
        S("Network:TrustedProxies", "Network & security", "Trusted proxies", SettingType.List, Array.Empty<string>(),
            "Addresses or ranges (such as 10.0.0.0/8) of reverse proxies allowed to set X-Forwarded-For. Others' headers are ignored, so clients can't fake their address."),
        S("Cors:AllowedOrigins", "Network & security", "Allowed browser origins", SettingType.List, Array.Empty<string>(),
            "Other websites allowed to call the API from a browser, such as https://grafana.example.com. The dashboard doesn't need this."),
        S("ApiKeys:RefreshSeconds", "Network & security", "Reread API keys every", SettingType.Int, 30,
            "How often keys made or revoked on another instance are picked up. Changes made on this instance apply at once.", unit: "seconds", min: 1, max: 3600),
        S("SSO:Enabled", "Network & security", "Single sign-on", SettingType.Bool, false, "Accept OpenID Connect tokens alongside API keys."),
        S("SSO:Provider", "Network & security", "Sign-on provider", SettingType.Enum, "Oidc", "Only OpenID Connect is implemented.", options: ["Oidc"]),
        S("SSO:Oidc:Authority", "Network & security", "OIDC authority", SettingType.String, null, "The identity provider's issuer URL, such as https://login.microsoftonline.com/<tenant>/v2.0."),
        S("SSO:Oidc:ClientId", "Network & security", "OIDC client ID", SettingType.String, null, "The application ID registered with the identity provider; tokens must be for it."),
        S("SSO:AutoProvision", "Network & security", "Give unmatched users the default role", SettingType.Bool, false, "Users matching no role mapping get the default role. Off, the default, they get none and can't sign in: on, every account the identity provider or directory knows gets it."),
        S("SSO:SessionHours", "Network & security", "Dashboard sign-in length", SettingType.Int, 8, "Hours a dashboard sign-in through SSO lasts while it's used. Takes effect on restart."),
        S("SSO:MaxSessionHours", "Network & security", "Longest dashboard sign-in", SettingType.Int, 24, "Hours after which a dashboard sign-in ends however much it's used, so a role taken away at the identity provider or directory takes effect. Takes effect on restart."),
        S("SSO:DefaultRole", "Network & security", "Default role", SettingType.Enum, "Viewer", "The role for signed-in users no mapping matches.", options: ["Viewer", "Analyst", "Admin", "SuperAdmin"]),
        S("SSO:RoleMappings", "Network & security", "Role mappings", SettingType.Json, null,
            "Claims to roles and index permissions: [{\"claimType\": \"groups\", \"claimValue\": \"ops\", \"role\": \"Analyst\", \"indexPermissions\": [{\"indexPattern\": \"ops-*\", \"capabilities\": \"Search\"}]}]."),

        // Background work
        Limit(nameof(ServerLimits.ScheduledSearchCheckSeconds), "Background work", "Check scheduled searches every",
            "How often scheduled searches are checked for being due.", "seconds", 1, 3600),
        Limit(nameof(ServerLimits.DataModelRebuildMinutes), "Background work", "Rebuild data models every",
            "How often data model summaries for tstats are rebuilt.", "minutes", 1, 1440),
        Limit(nameof(ServerLimits.ClusterHeartbeatSeconds), "Background work", "Instance heartbeat",
            "How often each instance reports that it's running.", "seconds", 1, 3600),
        Limit(nameof(ServerLimits.ClusterInstanceTimeoutSeconds), "Background work", "Instance timeout",
            "An instance not heard from for this long is no longer counted when sharing out rate limits.", "seconds", 2, 86400),
        Limit(nameof(ServerLimits.MetricsCacheSeconds), "Background work", "Reuse /metrics figures for",
            "Storage figures on /metrics count every event, so they're reused for this long.", "seconds", 0, 3600),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>A setting by key, ignoring case.</summary>
    public static SettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>Lists and structured settings, which a saved value replaces entirely.</summary>
    public static IReadOnlyList<string> ReplacedAsAWhole { get; } =
        All.Where(setting => setting.Type is SettingType.List or SettingType.Json).Select(setting => setting.Key).ToList();
}
