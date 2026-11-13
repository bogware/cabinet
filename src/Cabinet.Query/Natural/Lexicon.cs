namespace Cabinet.Query.Natural;

/// <summary>
/// The words natural queries are read with. English only for now; another language would be
/// another instance of these lists, so the reader itself never names a word.
/// </summary>
public sealed class Lexicon
{
    /// <summary>The English word lists.</summary>
    public static Lexicon English { get; } = new();

    /// <summary>Words that carry no meaning in a query: "show me all the", "please", "events".</summary>
    public HashSet<string> Filler { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "show", "me", "all", "the", "a", "an", "please", "give", "get", "find", "list", "display", "search", "look", "looking",
        "for", "fetch", "pull", "select", "retrieve", "return", "query", "what", "whats", "what's", "are", "were", "there",
        "any", "events", "event", "logs", "log", "lines", "line", "entries", "entry", "records", "record", "messages",
        "results", "result", "rows", "row", "of", "that", "have", "has", "had", "did", "do", "does", "i", "we", "see",
        "want", "need", "to", "can", "you", "could", "would", "with", "happened", "happening", "occurred", "anything",
        "everything", "stuff", "things", "data", "just", "only", "now", "currently", "let", "lets", "let's", "tell",
        "so", "far", "it", "them", "their", "its", "my", "our", "some", "been", "be", "being", "occurrences", "instances",
        "and", "or", "in", "on", "at", "from", "by", "where", "when", "which", "who", "how",
        "requests", "request", "hits", "calls", "activity", "traffic", "compare", "comparing", "breakdown", "summary", "summarize", "overview", "distribution", "happen", "happens", "is", "was", "out", "up", "about", "into", "during", "while", "then",
    };

    /// <summary>Common words never corrected toward a keyword, however close: "rate" isn't a typo for "rare".</summary>
    public HashSet<string> KeepAsIs { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "rate", "rates", "data", "date", "time", "times", "state", "stats", "store", "most", "post", "host", "lost", "past", "fast", "slow",
        "sort", "port", "part", "user", "used", "uses", "over", "even", "ever", "very", "down", "done", "fail", "failed", "file", "line", "link",
        "login", "logins", "logon", "auth", "deny", "denied", "drop", "dropped", "block", "blocked", "allow", "allowed", "accept", "reject",
        "timeout", "timeouts", "crash", "panic", "kill", "killed", "start", "started", "stop", "stopped", "restart", "boot", "disk", "memory",
        "where", "table", "fields", "head", "tail", "eval", "dedup", "chart", "timechart", "rare", "where", "regex", "lookup", "join", "append",
    };

    /// <summary>Severity words, read as "this level or worse".</summary>
    public Dictionary<string, string> Severities { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["error"] = "error", ["errors"] = "error", ["err"] = "error",
        ["warning"] = "warning", ["warnings"] = "warning", ["warn"] = "warning", ["warns"] = "warning",
        ["critical"] = "critical", ["criticals"] = "critical", ["crit"] = "critical",
        ["fatal"] = "fatal", ["fatals"] = "fatal",
    };

    /// <summary>Spoken names for fields, each with the stored names it might be, most likely first.</summary>
    public Dictionary<string, string[]> FieldWords { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["host"] = ["host"], ["hosts"] = ["host"], ["hostname"] = ["host"], ["hostnames"] = ["host"],
        ["server"] = ["host"], ["servers"] = ["host"], ["machine"] = ["host"], ["machines"] = ["host"],
        ["computer"] = ["host"], ["computers"] = ["host"], ["node"] = ["host"], ["nodes"] = ["host"], ["box"] = ["host"],
        ["boxes"] = ["host"], ["instance"] = ["host"], ["instances"] = ["host"], ["device"] = ["host"], ["devices"] = ["host"],
        ["sourcetype"] = ["sourcetype"], ["sourcetypes"] = ["sourcetype"], ["source type"] = ["sourcetype"], ["source types"] = ["sourcetype"],
        ["log type"] = ["sourcetype"], ["log types"] = ["sourcetype"],
        ["index"] = ["index"], ["indexes"] = ["index"], ["indices"] = ["index"],
        ["severity"] = ["severity"], ["severities"] = ["severity"], ["level"] = ["severity"], ["levels"] = ["severity"],
        ["log level"] = ["severity"], ["loglevel"] = ["severity"], ["priority"] = ["severity"],
        ["source"] = ["source_file"], ["sources"] = ["source_file"], ["file"] = ["source_file"], ["files"] = ["source_file"],
        ["source file"] = ["source_file"], ["log file"] = ["source_file"],
        ["message"] = ["message"], ["messages"] = ["message"],
        ["user"] = ["user", "username", "user_name", "account", "src_user"], ["users"] = ["user", "username", "user_name", "account", "src_user"],
        ["username"] = ["username", "user", "user_name"], ["usernames"] = ["username", "user", "user_name"],
        ["account"] = ["account", "user", "username"], ["accounts"] = ["account", "user", "username"],
        ["ip"] = ["src_ip", "client_ip", "clientip", "ip", "src", "remote_addr"], ["ips"] = ["src_ip", "client_ip", "clientip", "ip", "src", "remote_addr"],
        ["ip address"] = ["src_ip", "client_ip", "clientip", "ip", "src", "remote_addr"], ["ip addresses"] = ["src_ip", "client_ip", "clientip", "ip", "src", "remote_addr"],
        ["client ip"] = ["client_ip", "clientip", "src_ip", "ip"], ["client ips"] = ["client_ip", "clientip", "src_ip", "ip"],
        ["source ip"] = ["src_ip", "source_ip", "src"], ["source ips"] = ["src_ip", "source_ip", "src"],
        ["src ip"] = ["src_ip", "source_ip", "src"], ["src ips"] = ["src_ip", "source_ip", "src"], ["dst ip"] = ["dest_ip", "dst_ip", "dest"],
        ["destination ip"] = ["dest_ip", "dst_ip", "dest"], ["dest ip"] = ["dest_ip", "dst_ip", "dest"],
        ["client"] = ["client_ip", "clientip", "src_ip", "client"], ["clients"] = ["client_ip", "clientip", "src_ip", "client"],
        ["status"] = ["status", "status_code", "http_status"], ["statuses"] = ["status", "status_code", "http_status"],
        ["status code"] = ["status", "status_code", "http_status"], ["status codes"] = ["status", "status_code", "http_status"],
        ["response code"] = ["status", "status_code", "response_code"], ["response codes"] = ["status", "status_code", "response_code"],
        ["http status"] = ["status", "http_status", "status_code"],
        ["duration"] = ["duration_ms", "duration", "latency_ms", "latency", "response_time", "elapsed"],
        ["durations"] = ["duration_ms", "duration", "latency_ms", "latency", "response_time", "elapsed"],
        ["latency"] = ["latency_ms", "latency", "duration_ms", "duration", "response_time"],
        ["response time"] = ["response_time", "duration_ms", "duration", "latency_ms", "latency"],
        ["response times"] = ["response_time", "duration_ms", "duration", "latency_ms", "latency"],
        ["time taken"] = ["duration_ms", "duration", "elapsed", "time_taken"], ["elapsed"] = ["elapsed", "duration_ms", "duration"],
        ["path"] = ["path", "url", "uri", "uri_path", "endpoint"], ["paths"] = ["path", "url", "uri", "uri_path", "endpoint"],
        ["url"] = ["url", "uri", "path"], ["urls"] = ["url", "uri", "path"], ["uri"] = ["uri", "url", "path"],
        ["endpoint"] = ["endpoint", "path", "url", "uri"], ["endpoints"] = ["endpoint", "path", "url", "uri"],
        ["page"] = ["path", "url", "uri", "page"], ["pages"] = ["path", "url", "uri", "page"], ["route"] = ["route", "path", "url"],
        ["method"] = ["method", "http_method", "verb"], ["methods"] = ["method", "http_method", "verb"],
        ["bytes"] = ["bytes", "bytes_sent", "size", "bytes_out"], ["size"] = ["size", "bytes", "bytes_sent"],
        ["process"] = ["process", "process_name", "program", "app"], ["processes"] = ["process", "process_name", "program", "app"],
        ["program"] = ["program", "process", "app"], ["app"] = ["app", "application", "program"], ["apps"] = ["app", "application", "program"],
        ["application"] = ["application", "app"], ["applications"] = ["application", "app"],
        ["port"] = ["port", "dest_port", "dst_port", "src_port"], ["ports"] = ["port", "dest_port", "dst_port", "src_port"],
        ["country"] = ["Country", "country"], ["countries"] = ["Country", "country"], ["city"] = ["City", "city"], ["cities"] = ["City", "city"],
        ["action"] = ["action"], ["actions"] = ["action"], ["event id"] = ["EventID", "event_id", "EventCode"], ["event ids"] = ["EventID", "event_id", "EventCode"],
    };

    /// <summary>Words that start a comparison, longest first, with the operator they mean.</summary>
    public (string Phrase, string Op)[] Comparisons { get; } =
    [
        ("is greater than or equal to", ">="), ("is less than or equal to", "<="), ("greater than or equal to", ">="), ("less than or equal to", "<="),
        ("is not equal to", "!="), ("does not equal", "!="), ("doesn't equal", "!="), ("not equal to", "!="), ("is other than", "!="),
        ("is greater than", ">"), ("is more than", ">"), ("is bigger than", ">"), ("is larger than", ">"), ("is higher than", ">"), ("is longer than", ">"),
        ("is less than", "<"), ("is smaller than", "<"), ("is lower than", "<"), ("is shorter than", "<"),
        ("is at least", ">="), ("is at most", "<="), ("no less than", ">="), ("no more than", "<="), ("at least", ">="), ("at most", "<="),
        ("greater than", ">"), ("more than", ">"), ("bigger than", ">"), ("larger than", ">"), ("higher than", ">"), ("longer than", ">"), ("slower than", ">"),
        ("less than", "<"), ("fewer than", "<"), ("smaller than", "<"), ("lower than", "<"), ("shorter than", "<"), ("faster than", "<"),
        ("is not", "!="), ("isn't", "!="), ("is no", "!="), ("other than", "!="), ("except", "!="), ("not", "!="), ("!=", "!="), ("<>", "!="),
        ("starts with", "starts"), ("starting with", "starts"), ("begins with", "starts"), ("beginning with", "starts"),
        ("ends with", "ends"), ("ending with", "ends"),
        ("contains", "contains"), ("containing", "contains"), ("includes", "contains"), ("including", "contains"),
        ("like", "contains"), ("matching", "contains"), ("matches", "contains"),
        ("is one of", "in"), ("one of", "in"), ("is in", "in"),
        ("exceeds", ">"), ("exceeding", ">"), ("over", ">"), ("above", ">"), ("under", "<"), ("below", "<"), ("beyond", ">"),
        ("equals", "="), ("equal to", "="), ("is equal to", "="), ("is", "="), ("was", "="), ("were", "="), ("are", "="),
        (">=", ">="), ("<=", "<="), ("==", "="), ("=", "="), (">", ">"), ("<", "<"), (":", "="),
    ];

    /// <summary>Aggregate words before a field, as in "average duration" or "95th percentile of latency".</summary>
    public Dictionary<string, string> Aggregates { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["average"] = "avg", ["avg"] = "avg", ["mean"] = "avg", ["typical"] = "avg",
        ["sum"] = "sum", ["total"] = "sum", ["combined"] = "sum",
        ["max"] = "max", ["maximum"] = "max", ["highest"] = "max", ["largest"] = "max", ["biggest"] = "max", ["peak"] = "max",
        ["longest"] = "max", ["slowest"] = "max", ["worst"] = "max",
        ["min"] = "min", ["minimum"] = "min", ["lowest"] = "min", ["smallest"] = "min", ["shortest"] = "min", ["fastest"] = "min",
        ["median"] = "median", ["middle"] = "median",
        ["stdev"] = "stdev", ["std"] = "stdev", ["deviation"] = "stdev", ["variance"] = "variance",
        ["distinct"] = "dc", ["unique"] = "dc", ["different"] = "dc",
    };

    /// <summary>Words that mean counting.</summary>
    public HashSet<string> CountWords { get; } = new(StringComparer.OrdinalIgnoreCase) { "count", "counts", "number", "numbers", "tally", "volume", "amount", "total", "how many", "how much" };

    /// <summary>Words that introduce grouping, longest first.</summary>
    public string[] GroupWords { get; } =
    [
        "grouped by", "group by", "broken down by", "break down by", "broken out by", "split by", "partitioned by", "bucketed by",
        "for each", "for every", "per", "by", "each", "across", "for all",
    ];

    /// <summary>Words for a chart over time.</summary>
    public string[] OverTime { get; } = ["over time", "as a timeline", "timeline", "trend", "trends", "trending", "histogram", "time series", "timechart", "graph", "chart", "rate", "rates"];

    /// <summary>Single words for a bucket size: "hourly", "per minute".</summary>
    public Dictionary<string, string> Spans { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["secondly"] = "1s", ["second"] = "1s", ["minutely"] = "1m", ["minute"] = "1m", ["min"] = "1m",
        ["hourly"] = "1h", ["hour"] = "1h", ["hr"] = "1h", ["daily"] = "1d", ["day"] = "1d", ["weekly"] = "1w", ["week"] = "1w",
    };

    /// <summary>Time units and their BQL letter.</summary>
    public Dictionary<string, string> Units { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["s"] = "s", ["sec"] = "s", ["secs"] = "s", ["second"] = "s", ["seconds"] = "s",
        ["m"] = "m", ["min"] = "m", ["mins"] = "m", ["minute"] = "m", ["minutes"] = "m",
        ["h"] = "h", ["hr"] = "h", ["hrs"] = "h", ["hour"] = "h", ["hours"] = "h",
        ["d"] = "d", ["day"] = "d", ["days"] = "d",
        ["w"] = "w", ["wk"] = "w", ["wks"] = "w", ["week"] = "w", ["weeks"] = "w",
        ["mo"] = "M", ["month"] = "M", ["months"] = "M", ["y"] = "y", ["yr"] = "y", ["year"] = "y", ["years"] = "y",
    };

    /// <summary>Words for "the recent stretch of time".</summary>
    public HashSet<string> Recent { get; } = new(StringComparer.OrdinalIgnoreCase) { "last", "past", "previous", "recent", "preceding", "trailing" };

    /// <summary>Words for the newest events first, and for the oldest first.</summary>
    public HashSet<string> Newest { get; } = new(StringComparer.OrdinalIgnoreCase) { "latest", "newest", "recent", "last", "most recent" };

    public HashSet<string> Oldest { get; } = new(StringComparer.OrdinalIgnoreCase) { "oldest", "earliest", "first" };

    /// <summary>Words for "the most frequent" and "the least frequent".</summary>
    public string[] MostCommon { get; } = ["most common", "most frequent", "most popular", "most active", "most seen", "most", "busiest", "noisiest", "loudest", "frequent", "common", "popular"];

    public string[] LeastCommon { get; } = ["least common", "least frequent", "least popular", "least seen", "rarest", "rare", "uncommon", "unusual", "least"];

    /// <summary>Words that ask for a sort.</summary>
    public HashSet<string> SortWords { get; } = new(StringComparer.OrdinalIgnoreCase) { "sort", "sorted", "order", "ordered", "rank", "ranked", "arrange", "arranged" };

    /// <summary>Directions, as true for descending.</summary>
    public Dictionary<string, bool> Directions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["desc"] = true, ["descending"] = true, ["highest first"] = true, ["largest first"] = true, ["biggest first"] = true, ["most first"] = true,
        ["high to low"] = true, ["largest to smallest"] = true, ["z-a"] = true, ["newest first"] = true, ["latest first"] = true, ["reverse"] = true,
        ["asc"] = false, ["ascending"] = false, ["lowest first"] = false, ["smallest first"] = false, ["least first"] = false,
        ["low to high"] = false, ["smallest to largest"] = false, ["a-z"] = false, ["oldest first"] = false, ["earliest first"] = false,
        ["chronological"] = false, ["chronologically"] = false, ["in order"] = false,
    };

    /// <summary>Weekday names, for "since monday".</summary>
    public Dictionary<string, DayOfWeek> Weekdays { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday, ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday, ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday, ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday,
    };

    /// <summary>Number words up to twenty, and a few round ones.</summary>
    public Dictionary<string, int> Numbers { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30, ["fifty"] = 50,
        ["hundred"] = 100, ["couple"] = 2, ["few"] = 3, ["dozen"] = 12,
    };

    /// <summary>Every word that means something here, for correcting typos toward.</summary>
    public IReadOnlyCollection<string> Vocabulary => _vocabulary ??= BuildVocabulary();

    private HashSet<string>? _vocabulary;

    private HashSet<string> BuildVocabulary()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddPhrase(string phrase)
        {
            foreach (var word in phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 3 && word.All(char.IsLetter))
                {
                    words.Add(word);
                }
            }
        }

        foreach (var word in Severities.Keys.Concat(FieldWords.Keys).Concat(Aggregates.Keys).Concat(CountWords).Concat(GroupWords).Concat(OverTime)
                     .Concat(Spans.Keys).Concat(Units.Keys).Concat(Recent).Concat(Newest).Concat(Oldest).Concat(MostCommon).Concat(LeastCommon)
                     .Concat(SortWords).Concat(Directions.Keys).Concat(Weekdays.Keys).Concat(Comparisons.Select(c => c.Phrase))
                     .Concat(["today", "yesterday", "since", "until", "before", "after", "between", "ago", "this", "week", "month", "year", "hours", "minutes", "days"]))
        {
            AddPhrase(word);
        }

        return words;
    }
}
