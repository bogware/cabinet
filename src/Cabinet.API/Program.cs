using Cabinet.API.Infrastructure;
using Cabinet.Alerts;
using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Extras.Gaming;
using Cabinet.Parsers;
using Cabinet.Query;
using Cabinet.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Serilog;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog.Events;
using System.Reflection;

// A Windows service starts in System32, so settings, the dashboard and logs would be looked
// for there. Use the program's own folder instead, as the hosting docs recommend.
var contentRoot = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService()
    ? AppContext.BaseDirectory
    : Directory.GetCurrentDirectory();

// Shared by the logger used during startup and the one configuration can adjust
LoggerConfiguration ConfigureLogging(LoggerConfiguration logging) => logging
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(contentRoot, "logs", "cabinet-api-.log"),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}",
        rollingInterval: RollingInterval.Day,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 100 * 1024 * 1024);

Log.Logger = ConfigureLogging(new LoggerConfiguration()).CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });

    // Settings saved from the dashboard: after the appsettings files, before environment variables
    var overridesPath = Cabinet.API.Services.Settings.SettingsOverrideStore.ResolvePath(builder.Configuration);
    Cabinet.API.Services.Settings.SettingsOverrideStore settingsOverrides;
    try
    {
        settingsOverrides = Cabinet.API.Services.Settings.SettingsOverrideStore.AddTo(builder.Configuration, overridesPath);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Log.Warning(ex, "Settings saved from the dashboard can't be read or kept at {Path}", overridesPath);
        settingsOverrides = new Cabinet.API.Services.Settings.SettingsOverrideStore(overridesPath);
    }

    builder.Services.AddSingleton(settingsOverrides);

    // A one-off command rather than running the server
    if (args.Length > 0 && args[0] == "--restore")
    {
        await RestoreBackupAsync(args, builder.Configuration);
        return;
    }

    // Use Serilog. A "Serilog" configuration section can change levels and add sinks.
    builder.Host.UseSerilog((context, logging) =>
        ConfigureLogging(logging).ReadFrom.Configuration(context.Configuration));

    // Report lifecycle to the service manager; both are no-ops outside a service
    builder.Host.UseSystemd();
    builder.Host.UseWindowsService();

    // Add services to the container
    builder.Services.AddControllers(options =>
    {
        // Limit request body size to 50 MB to prevent memory exhaustion
        options.MaxModelBindingCollectionSize = 50_000;
    }).AddJsonOptions(options =>
    {
        // Accept enum values as strings in JSON (e.g. "SingleValue" instead of 5)
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        // Use camelCase for JSON properties
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });
    var serverLimits = builder.Configuration.GetSection("Limits").Get<ServerLimits>() ?? new ServerLimits();
    builder.Services.AddSingleton(serverLimits);
    Cabinet.Query.Bql.BqlEventMatcher.RegexTimeout = TimeSpan.FromMilliseconds(serverLimits.DetectionRegexTimeoutMs);
    Cabinet.Query.Bql.QueryRegex.PerEventTimeout = TimeSpan.FromMilliseconds(serverLimits.QueryRegexTimeoutMs);

    // iplocation reads a MaxMind GeoLite2 or GeoIP2 City database, which people download themselves
    if (builder.Configuration["GeoIp:DatabasePath"] is { Length: > 0 } geoIpPath)
    {
        try
        {
            Cabinet.Query.Bql.BqlGeoIp.Lookup = new Cabinet.Query.Bql.MaxMindGeoIpLookup(geoIpPath);
            Log.Information("GeoIP database loaded from {Path}", geoIpPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MaxMind.Db.InvalidDatabaseException)
        {
            Log.Error(ex, "Couldn't open the GeoIP database {Path}; iplocation won't work until it's fixed", geoIpPath);
        }
    }
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = serverLimits.MaxRequestBodyMB * ServerLimits.Megabyte;
    });
    builder.Services.AddEndpointsApiExplorer();

    // Configure Swagger/OpenAPI
    // The detection routes belong to the optional gaming extension
    if (!GamingExtension.IsEnabled(builder.Configuration))
    {
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new ExcludeControllersFeatureProvider(
                typeof(Cabinet.API.Controllers.DetectionController),
                typeof(Cabinet.API.Controllers.ModerationController))));
    }

    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Cabinet API",
            Version = "v1",
            Description = "Log ingestion, search and alerting API",
            License = new OpenApiLicense
            {
                Name = "AGPL-3.0-only",
                Url = new Uri("https://www.gnu.org/licenses/agpl-3.0.html")
            }
        });

        // Add API Key authentication
        options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
        {
            Description = "API Key authentication using X-API-Key header",
            In = ParameterLocation.Header,
            Name = "X-API-Key",
            Type = SecuritySchemeType.ApiKey
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "ApiKey"
                    }
                },
                Array.Empty<string>()
            }
        });

        // Include XML comments
        var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
        {
            options.IncludeXmlComments(xmlPath);
        }
    });

    // Configure Authentication — MultiAuth policy scheme supports both API Key and Bearer (OIDC/JWT)
    var ssoConfig = builder.Configuration.GetSection("SSO").Get<SsoConfiguration>();
    builder.Services.Configure<SsoConfiguration>(builder.Configuration.GetSection("SSO"));

    var authBuilder = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "MultiAuth";
        options.DefaultChallengeScheme = "MultiAuth";
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ApiKey", null)
    .AddPolicyScheme("MultiAuth", "API Key, Bearer or dashboard session", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                return "Bearer";
            if (Cabinet.API.Authentication.BrowserSession.UsesSession(context.Request))
                return Cabinet.API.Authentication.BrowserSession.Scheme;
            return "ApiKey";
        };
    })
    .AddCabinetBrowserSession(ssoConfig);

    if (ssoConfig?.Enabled == true && ssoConfig.Oidc != null)
    {
        authBuilder.AddJwtBearer("Bearer", options =>
        {
            options.Authority = ssoConfig.Oidc.Authority;
            options.Audience = ssoConfig.Oidc.ClientId;
            options.RequireHttpsMetadata = !ssoConfig.Oidc.Authority.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
            options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    // Map OIDC claims to Cabinet claims for RBAC
                    var mapper = context.HttpContext.RequestServices.GetService<ClaimRoleMapper>();
                    if (mapper != null && context.Principal != null)
                    {
                        var identity = context.Principal.Identity as System.Security.Claims.ClaimsIdentity;
                        if (identity != null)
                        {
                            mapper.MapClaims(identity, ssoConfig);
                        }
                    }
                    return Task.CompletedTask;
                }
            };
        });
        Log.Information("OIDC/JWT Bearer authentication enabled (Authority: {Authority})", ssoConfig.Oidc.Authority);
    }
    else
    {
        // Register a no-op Bearer scheme so PolicyScheme doesn't fail when no Bearer token is configured
        authBuilder.AddScheme<AuthenticationSchemeOptions, Cabinet.API.Authentication.NoOpAuthHandler>("Bearer", null);
    }

    builder.Services.AddAuthorization(options =>
    {
        options.DefaultPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        options.AddPolicy("RequireWritePermission", policy =>
            policy.RequireClaim("Permission", "Write"));

        options.AddPolicy("RequireAdminPermission", policy =>
            policy.RequireClaim("Tier", "Enterprise"));
    });

    // Add Rate Limiting
    builder.Services.AddCabinetRateLimiting(builder.Configuration);

    // Traces, when something is configured to receive them
    var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];
    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(builder.Configuration["Telemetry:ServiceName"] ?? "cabinet-api"))
            .WithTracing(tracing => tracing
                .AddSource(Cabinet.Core.Utilities.CabinetDiagnostics.SourceName)
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health") && !context.Request.Path.StartsWithSegments("/metrics"))
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = new Uri(otlpEndpoint);
                    options.Protocol = builder.Configuration["Telemetry:OtlpProtocol"]?.Equals("http", StringComparison.OrdinalIgnoreCase) == true
                        ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
                        : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                }));
    }

    // Configure CORS
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowConfiguredOrigins", policy =>
        {
            var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
            var allowedOrigins = configuredOrigins ?? new[] { "http://localhost:3000" };

            if (configuredOrigins == null && !builder.Environment.IsDevelopment())
            {
                Log.Warning("CORS: No 'Cors:AllowedOrigins' configured — falling back to localhost. Set this in production.");
            }

            policy.WithOrigins(allowedOrigins)
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                .WithHeaders("Content-Type", "Authorization", "X-API-Key", "X-Correlation-Id")
                .AllowCredentials();
        });
    });

    // Add Health Checks — real connectivity tests, not stubs
    builder.Services.AddHealthChecks()
        .AddCheck<Cabinet.API.Health.StorageHealthCheck>("storage", tags: new[] { "ready" });

    // Register Cabinet services
    ConfigureCabinetServices(builder.Services, builder.Configuration);

    // Add response caching
    builder.Services.AddResponseCaching();

    // Add compression (disabled over HTTPS to prevent BREACH attack)
    builder.Services.AddResponseCompression();

    var app = builder.Build();

    // Notes settings as they are now, so the dashboard can tell which changes wait for a restart
    app.Services.GetRequiredService<Cabinet.API.Services.Settings.SettingsService>();

    // Configure the HTTP request pipeline
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Cabinet API v1");
            options.RoutePrefix = string.Empty; // Swagger at root
        });
    }

    // Must run first so everything below sees the real client address
    var trustedProxies = TrustedProxies.Read(app.Configuration);
    if (trustedProxies.Count > 0)
    {
        app.UseForwardedHeaders(TrustedProxies.CreateOptions(trustedProxies));
    }

    // Prometheus metrics (before other middleware to capture all requests)
    app.UseMiddleware<MetricsMiddleware>();

    // Correlation ID (must be first to propagate through all middleware)
    app.UseMiddleware<Cabinet.API.Infrastructure.CorrelationIdMiddleware>();

    // Pre-auth IP rate limiting (must run before error handling and logging to block attacks early)
    app.UseMiddleware<IpRateLimitingMiddleware>();

    // While storage switches, senders are asked to resend shortly rather than writing to the old database
    app.Use(async (context, next) =>
    {
        var gate = context.RequestServices.GetRequiredService<Cabinet.API.Services.Storage.IngestGate>();
        if (gate.PausedBecause is { } reason && Cabinet.API.Services.Storage.IngestGate.IsIngest(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "15";
            await context.Response.WriteAsJsonAsync(new { title = "Service Unavailable", status = 503, detail = reason + ". Resend in a moment; nothing was stored." });
            return;
        }

        await next();
    });

    // Global error handling
    app.UseMiddleware<ErrorHandlingMiddleware>();

    // Request logging
    app.UseMiddleware<RequestLoggingMiddleware>();

    // Security headers
    app.Use(async (context, next) =>
    {
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("X-Frame-Options", "DENY");
        context.Response.Headers.Append("X-XSS-Protection", "0"); // Disabled in favor of CSP (modern best practice)
        context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'");
        context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        await next();
    });

    // API version headers
    app.UseMiddleware<ApiVersionMiddleware>();

    // Serve embedded web dashboard (static files from wwwroot/)
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        // Check with the server each time, so an upgrade can't leave old script running against a new API
        OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
    });

    // Requests from this machine stay on plain HTTP, so the dashboard opened on the server itself
    // isn't sent to a certificate it doesn't trust. Everything else goes to HTTPS when it's on.
    app.UseWhen(
        context => context.Connection.RemoteIpAddress is not { } address || !System.Net.IPAddress.IsLoopback(address),
        branch => branch.UseHttpsRedirection());
    app.UseCors("AllowConfiguredOrigins");
    app.UseResponseCaching();
    app.UseResponseCompression();

    app.UseAuthentication();
    app.UseAuthorization();

    // Rate limiter MUST run after authentication so it can read the Tier claim
    app.UseRateLimiter();

    app.MapControllers();

    // Map health check endpoints
    app.MapHealthChecks("/health").DisableRateLimiting();
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    }).DisableRateLimiting();
    app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false // Liveness probe - always healthy if app is running
    }).DisableRateLimiting();

    // Initialize services with graceful degradation for remote backends
    using (var scope = app.Services.CreateScope())
    {
        var storage = scope.ServiceProvider.GetRequiredService<ILogStorage>();
        await InitializeStorageWithRetryAsync(storage, app.Configuration);

        var savedQueryStorage = scope.ServiceProvider.GetRequiredService<ISavedQueryStorage>();
        await savedQueryStorage.InitializeAsync();

        var dashboardStorage = scope.ServiceProvider.GetRequiredService<IDashboardStorage>();
        await dashboardStorage.InitializeAsync();

        var knowledgeStorage = scope.ServiceProvider.GetRequiredService<IKnowledgeStorage>();
        await knowledgeStorage.InitializeAsync();

        var scheduledSearchStorage = scope.ServiceProvider.GetRequiredService<IScheduledSearchStorage>();
        await scheduledSearchStorage.InitializeAsync();

        var searchJobStorage = scope.ServiceProvider.GetRequiredService<Cabinet.Core.Interfaces.ISearchJobStorage>();
        await searchJobStorage.InitializeAsync();

        var alertHistoryStorage = scope.ServiceProvider.GetRequiredService<IAlertHistoryStorage>();
        await alertHistoryStorage.InitializeAsync();

        var fieldExtractionStorage = scope.ServiceProvider.GetRequiredService<IFieldExtractionStorage>();
        await fieldExtractionStorage.InitializeAsync();

        // Initialize data model storage
        var dataModelStorage = scope.ServiceProvider.GetRequiredService<IDataModelStorage>();
        await dataModelStorage.InitializeAsync();

        // Initialize the store for shared state
        await scope.ServiceProvider.GetRequiredService<IDocumentStore>().InitializeAsync();

        // Initialize encryption service (if configured)
        var encryptionService = scope.ServiceProvider.GetRequiredService<EncryptionService>();
        await encryptionService.InitializeAsync();

        Log.Information("Cabinet API initialized successfully");
    }

    Log.Information("Starting Cabinet API on port {Port}", builder.Configuration["ASPNETCORE_URLS"] ?? "5000");
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

void ConfigureCabinetServices(IServiceCollection services, IConfiguration configuration)
{
    // Validate critical configuration at startup
    ValidateConfiguration(configuration);

    // Warn about non-production configurations
    var storageType = configuration["Storage:Type"] ?? "SQLite";
    var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";
    if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
    {
        if (storageType.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
            Log.Warning("SQLite storage is not recommended for production. Consider PostgreSQL or OpenSearch.");
        if (storageType.Equals("Memory", StringComparison.OrdinalIgnoreCase))
            Log.Warning("In-Memory storage is ephemeral. Data will be lost on restart.");
    }

    // Configure options
    services.Configure<Cabinet.Core.Configuration.CabinetConfiguration>(configuration);

    // Storage — supports SQLite, Memory, PostgreSQL, and OpenSearch.
    // The "base" store is everything below detection. Detection events are written to it directly,
    // so they're stored, encrypted and searchable but never fed back into the detection engine.
    services.AddKeyedSingleton<ILogStorage>("base", (sp, _) =>
    {
        // Built the same way a storage migration builds it, so the two can't disagree
        var backend = Cabinet.API.Services.Storage.StorageBackends.CreateLogStorage(configuration, sp.GetRequiredService<ILoggerFactory>());

        if (configuration.GetValue<bool>("Encryption:Enabled"))
        {
            backend = new EncryptingLogStorage(backend, sp.GetRequiredService<EncryptionService>(), configuration.GetValue<bool>("Encryption:EncryptRawLine"));
        }

        return backend;
    });

    // The ingest pipeline runs first: what it drops is never stored or seen by detection, and
    // what it redacts is redacted before either
    services.AddSingleton<Cabinet.API.Services.IngestPipeline>();
    services.AddSingleton<ILogStorage>(sp =>
        new Cabinet.API.Services.PipelineLogStorage(
            // Detection sees what arrived rather than what was encrypted
            new Cabinet.API.Services.DetectingLogStorage(
                sp.GetRequiredKeyedService<ILogStorage>("base"),
                sp.GetRequiredService<Cabinet.API.Services.DetectionEngine>(),
                sp.GetService<Cabinet.API.Services.QueryCacheService>()),
            sp.GetRequiredService<Cabinet.API.Services.IngestPipeline>()));

    services.AddSingleton<Cabinet.API.Services.AlertDispatcher>();

    // Outbound calls people configure (webhook actions, connectors) can't reach private addresses
    // unless allowed, and never follow redirects.
    var outboundOptions = configuration.GetSection("Outbound").Get<Cabinet.API.Services.OutboundUrlOptions>() ?? new();
    services.AddSingleton(outboundOptions);
    services.AddSingleton<Cabinet.API.Services.OutboundUrlGuard>();
    services.AddKeyedSingleton("outbound", (sp, _) =>
        new HttpClient(sp.GetRequiredService<Cabinet.API.Services.OutboundUrlGuard>().CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) });
    services.AddSingleton<Cabinet.API.Services.ConnectorStore>();
    services.AddSingleton(sp => new Cabinet.API.Services.ConnectorRunner(
        sp.GetRequiredService<Cabinet.API.Services.ConnectorStore>(),
        sp.GetRequiredKeyedService<HttpClient>("outbound"),
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<Cabinet.API.Services.ConnectorRunner>>()));
    services.AddSingleton<Cabinet.API.Services.IActionConnectorRunner>(sp => sp.GetRequiredService<Cabinet.API.Services.ConnectorRunner>());

    services.AddSingleton<Cabinet.API.Services.DetectionActionExecutor>(sp => new Cabinet.API.Services.DetectionActionExecutor(
        sp.GetRequiredService<IAlertHistoryStorage>(),
        sp.GetRequiredService<Cabinet.Alerts.IAlertService>(),
        sp.GetRequiredKeyedService<ILogStorage>("base"),
        sp.GetRequiredKeyedService<HttpClient>("outbound"),
        sp.GetRequiredService<ILogger<Cabinet.API.Services.DetectionActionExecutor>>(),
        masking: sp.GetRequiredService<FieldMaskingService>(),
        background: true,
        actionTimeout: TimeSpan.FromSeconds(configuration.GetValue("Detection:ActionTimeoutSeconds", 15)),
        queueCapacity: configuration.GetValue("Detection:ActionQueueSize", 1000),
        connectors: sp.GetRequiredService<Cabinet.API.Services.IActionConnectorRunner>()));
    services.AddHostedService(sp => sp.GetRequiredService<Cabinet.API.Services.DetectionActionExecutor>());
    services.AddSingleton<Cabinet.API.Services.DetectionRuleStore>();
    services.AddSingleton<Cabinet.API.Services.DetectionEngine>();
    services.AddHostedService(sp => sp.GetRequiredService<Cabinet.API.Services.DetectionEngine>());
    services.AddHostedService<Cabinet.API.Services.ClusterHeartbeatService>();

    // Updates from GitHub releases: checked on a schedule, applied only when asked
    var updateOptions = configuration.GetSection("Updates").Get<Cabinet.API.Services.Updates.UpdateOptions>() ?? new();
    services.AddSingleton(updateOptions);
    services.AddSingleton<Cabinet.API.Services.Updates.IReleaseSource>(_ =>
        new Cabinet.API.Services.Updates.GitHubReleaseSource(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, updateOptions));
    services.AddSingleton<Cabinet.API.Services.Updates.UpdateApplier>();
    services.AddSingleton<Cabinet.API.Services.Updates.UpdateService>();
    services.AddHostedService<Cabinet.API.Services.Updates.UpdateCheckService>();

    // Moving to another database, and holding back ingest while it switches
    services.AddSingleton<Cabinet.API.Services.Storage.IngestGate>();
    services.AddSingleton<Cabinet.API.Services.Storage.StorageMigrationService>();

    // The settings screen
    services.AddSingleton<Cabinet.API.Services.Settings.ServiceRestarter>();
    services.AddSingleton(sp => new Cabinet.API.Services.Settings.SettingsService(
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<Cabinet.API.Services.Settings.SettingsOverrideStore>(),
        sp.GetRequiredService<Cabinet.API.Services.Settings.ServiceRestarter>(),
        sp.GetService<IAuditLogger>()));

    // Dashboards, knowledge objects, saved queries, schedules, search jobs, data models
    services.AddCabinetMetadataStorage(configuration);

    // RBAC and audit logging — database-backed with config fallback
    services.AddSingleton<Cabinet.API.Services.ConfigurationRbacService>();
    services.AddSingleton<IRbacService, Cabinet.API.Services.StoredRbacService>();
    services.AddHttpContextAccessor();
    services.AddSingleton<Cabinet.API.Services.FieldExtractor>();
    services.AddScoped(sp => new Cabinet.API.Authentication.IndexAccessScope
    {
        Masking = sp.GetRequiredService<FieldMaskingService>(),
        Extractor = sp.GetRequiredService<Cabinet.API.Services.FieldExtractor>(),
    });
    services.AddSingleton<Cabinet.API.Services.SerilogAuditLogger>();
    services.AddSingleton(sp =>
        new Cabinet.API.Services.TamperEvidentAuditLogger(
            sp.GetRequiredService<Cabinet.API.Services.SerilogAuditLogger>(),
            sp.GetRequiredService<IDocumentStore>(),
            sp.GetRequiredService<ILogger<Cabinet.API.Services.TamperEvidentAuditLogger>>(),
            configuration["Audit:HmacKey"] ?? Environment.GetEnvironmentVariable("CABINET_AUDIT_HMAC_KEY"),
            sp.GetRequiredService<ServerLimits>().AuditHeadLogInterval));
    services.AddSingleton<IAuditLogger>(sp => sp.GetRequiredService<Cabinet.API.Services.TamperEvidentAuditLogger>());

    // Field masking and encryption services
    services.AddSingleton<FieldMaskingService>(sp =>
    {
        var service = new FieldMaskingService();
        var maskingRules = configuration.GetSection("Masking:Rules").Get<List<FieldMaskingRule>>();
        if (maskingRules != null)
        {
            foreach (var rule in maskingRules)
            {
                service.AddRule(rule);
            }
        }
        return service;
    });
    services.AddSingleton(sp =>
    {
        var encConfig = new EncryptionConfiguration();
        configuration.GetSection("Encryption").Bind(encConfig);
        return new EncryptionService(encConfig, sp.GetRequiredService<ILogger<EncryptionService>>());
    });

    // Alert system
    services.AddCabinetAlerts();

    // Field extraction storage

    // Email alert channel (if configured)
    if (configuration.GetValue<bool>("Email:Enabled"))
    {
        services.AddSingleton<IAlertChannel>(sp =>
        {
            var emailCfg = new Cabinet.Alerts.EmailAlertConfiguration();
            configuration.GetSection("Email").Bind(emailCfg);
            return new Cabinet.Alerts.EmailAlertChannel(emailCfg,
                sp.GetRequiredService<ILogger<Cabinet.Alerts.EmailAlertChannel>>());
        });
    }

    // Scheduled search service
    services.AddSingleton<ScheduledSearchService>();
    services.AddHostedService(sp => sp.GetRequiredService<ScheduledSearchService>());

    // Syslog listener (if configured)
    if (configuration.GetValue<bool>("Syslog:Enabled"))
    {
        var syslogCfg = new Cabinet.API.Services.SyslogConfiguration();
        configuration.GetSection("Syslog").Bind(syslogCfg);
        services.AddSingleton(syslogCfg);
        services.AddHostedService<SyslogListenerService>();
    }

    // Parsers (order: specific first, generic last)
    var gamingEnabled = GamingExtension.IsEnabled(configuration);
    services.AddCabinetParsers(gamingEnabled ? s => s.AddGamingParsers() : null);

    // Multi-line log merger (configurable, defaults to timestamp-prefixed)
    services.AddSingleton(sp =>
    {
        var pattern = configuration["MultiLine:Pattern"];
        if (!string.IsNullOrEmpty(pattern))
        {
            var mode = configuration["MultiLine:Mode"]?.ToLowerInvariant() == "continuation"
                ? MultiLineMode.ContinuationPattern
                : MultiLineMode.StartPattern;
            return new MultiLineLogMerger(new MultiLineConfig { Pattern = pattern, Mode = mode });
        }
        return new MultiLineLogMerger(MultiLinePatterns.TimestampPrefixed);
    });

    // Game moderation detectors, served by DetectionController when the extension is on
    if (gamingEnabled)
    {
        services.AddGamingDetection();
    }

    // Query Engine — BQL (Bog Query Language) with predicate pushdown optimization
    services.AddScoped<Cabinet.Query.Bql.BqlParser>();
    services.AddScoped<Cabinet.Query.Bql.BqlOptimizer>();
    services.AddScoped<Cabinet.Query.IQueryParser>(sp => sp.GetRequiredService<Cabinet.Query.Bql.BqlParser>());
    services.AddScoped<Cabinet.Query.NaturalLanguage.INaturalLanguageTranslator, Cabinet.Query.NaturalLanguage.RuleBasedTranslator>();

    // The engine reads through the caller's index scope, so subsearches, joins and macros are
    // limited the same as the query around them.
    services.AddScoped<IQueryEngine>(sp => new Cabinet.Query.Bql.BqlQueryEngine(
        sp.GetRequiredService<Cabinet.API.Authentication.IndexAccessScope>().Restrict(sp.GetRequiredService<ILogStorage>()),
        sp.GetRequiredService<Cabinet.Query.Bql.BqlParser>(),
        sp.GetRequiredService<Cabinet.Query.Bql.BqlOptimizer>(),
        sp.GetRequiredService<ILogger<Cabinet.Query.Bql.BqlQueryEngine>>(),
        sp.GetService<Cabinet.Query.NaturalLanguage.INaturalLanguageTranslator>(),
        sp.GetService<IKnowledgeStorage>(),
        sp.GetRequiredService<Cabinet.Query.Bql.QueryLimits>()));
    services.AddSingleton(configuration.GetSection("Query:Limits").Get<Cabinet.Query.Bql.QueryLimits>() ?? new Cabinet.Query.Bql.QueryLimits());
    services.AddSingleton(configuration.GetSection("Query:Concurrency").Get<Cabinet.API.Middleware.ConcurrencyOptions>() ?? new Cabinet.API.Middleware.ConcurrencyOptions());
    services.AddSingleton<Cabinet.API.Middleware.ConcurrencyLimiter>();

    // Query result cache — accelerates dashboard refreshes with TTL-based caching.
    // Each instance has its own, which can differ from another's for up to the TTL.
    var queryResultTtl = configuration.GetValue("Query:CacheTtlSeconds", 30);
    services.AddSingleton<Cabinet.API.Services.QueryCacheService>(sp =>
        new Cabinet.API.Services.QueryCacheService(
            sp.GetRequiredService<ILogger<Cabinet.API.Services.QueryCacheService>>(),
            TimeSpan.FromSeconds(queryResultTtl)));

    // AI investigator: off unless turned on, and a local OpenAI-compatible server unless Claude is chosen
    var investigatorOptions = configuration.GetSection("Ai").Get<Cabinet.API.Services.Investigator.InvestigatorOptions>() ?? new();
    if (investigatorOptions.Enabled)
    {
        services.AddSingleton(investigatorOptions);
        if (investigatorOptions.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<Cabinet.API.Services.Investigator.IInvestigatorModel>(new Cabinet.API.Services.Investigator.ClaudeInvestigatorModel(investigatorOptions));
        }
        else
        {
            // Not the outbound client: the server is usually on this machine or network, which that one refuses
            services.AddHttpClient("investigator", client => client.Timeout = TimeSpan.FromSeconds(Math.Clamp(investigatorOptions.TimeoutSeconds, 10, 900)));
            services.AddSingleton<Cabinet.API.Services.Investigator.IInvestigatorModel>(sp => new Cabinet.API.Services.Investigator.OpenAiCompatibleModel(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("investigator"), investigatorOptions,
                sp.GetRequiredService<ILogger<Cabinet.API.Services.Investigator.OpenAiCompatibleModel>>()));
        }

        services.AddScoped(sp => new Cabinet.API.Services.Investigator.InvestigatorService(
            investigatorOptions,
            sp.GetRequiredService<Cabinet.API.Services.Investigator.IInvestigatorModel>(),
            sp.GetRequiredService<ILogStorage>(),
            sp.GetRequiredService<FieldMaskingService>(),
            sp.GetRequiredService<Cabinet.Query.Bql.QueryLimits>(),
            sp.GetRequiredService<IAuditLogger>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<Cabinet.API.Services.FieldExtractor>(),
            sp.GetService<IKnowledgeStorage>()));
    }

    // Async search jobs — background execution for long-running queries
    services.AddHostedService<Cabinet.API.Services.SearchJobService>();

    // Anomaly rules: baselines instead of thresholds, checked by the leader as each window ends
    services.AddSingleton<Cabinet.API.Services.AnomalyRuleStore>();
    services.AddSingleton<Cabinet.API.Services.CaseStore>();
    services.AddSingleton<Cabinet.API.Services.ReportStore>();
    services.AddSingleton<Cabinet.API.Services.ReportService>();
    services.AddHostedService(sp => sp.GetRequiredService<Cabinet.API.Services.ReportService>());

    // Cold storage for events retention removes: a directory, or an S3 bucket
    var archiveType = configuration["Archive:Type"];
    if (string.Equals(archiveType, "Directory", StringComparison.OrdinalIgnoreCase))
    {
        var archivePath = configuration["Archive:Path"] ?? "archive";
        services.AddSingleton<Cabinet.API.Services.Archive.IEventArchive>(_ => new Cabinet.API.Services.Archive.DirectoryEventArchive(archivePath));
    }
    else if (string.Equals(archiveType, "S3", StringComparison.OrdinalIgnoreCase) && configuration["Archive:S3Bucket"] is { Length: > 0 } archiveBucket)
    {
        services.AddSingleton<Cabinet.API.Services.Archive.IEventArchive>(sp => new Cabinet.API.Services.Archive.S3EventArchive(
            new Cabinet.AWS.Services.S3ArchiveService(
                sp.GetService<Amazon.S3.IAmazonS3>() ?? new Amazon.S3.AmazonS3Client(),
                sp.GetRequiredService<ILogger<Cabinet.AWS.Services.S3ArchiveService>>(),
                archiveBucket),
            configuration["Archive:S3Prefix"] ?? "cabinet-events"));
    }

    if (services.Any(service => service.ServiceType == typeof(Cabinet.API.Services.Archive.IEventArchive)))
    {
        services.AddSingleton<Cabinet.API.Services.Archive.EventArchiver>();
    }
    services.AddHostedService<Cabinet.API.Services.AnomalyDetector>();

    // Data model acceleration — pre-aggregated summaries for tstats queries
    services.AddSingleton<Cabinet.API.Services.DataModelAccelerationService>();
    services.AddHostedService(sp => sp.GetRequiredService<Cabinet.API.Services.DataModelAccelerationService>());

    // Local backup service — consistent, verified SQLite backups. Other backends have their own tools.
    if (StorageRegistration.ResolveBackend(configuration) == StorageBackend.Sqlite)
    {
        var backupConfig = new Cabinet.API.Services.BackupConfiguration();
        configuration.GetSection("Backup").Bind(backupConfig);
        services.AddSingleton(backupConfig);

        if (!string.IsNullOrEmpty(backupConfig.S3Bucket))
        {
            services.AddSingleton<Amazon.S3.IAmazonS3>(_ => new Amazon.S3.AmazonS3Client());
        }

        services.AddSingleton(sp =>
        {
            var connStr = configuration["Storage:ConnectionString"] ?? "Data Source=cabinet.db";
            var dbPath = Cabinet.API.Services.LocalBackupService.ExtractDatabasePath(connStr);
            var s3 = string.IsNullOrEmpty(backupConfig.S3Bucket)
                ? null
                : new Cabinet.AWS.Services.S3ArchiveService(
                    sp.GetRequiredService<Amazon.S3.IAmazonS3>(),
                    sp.GetRequiredService<ILogger<Cabinet.AWS.Services.S3ArchiveService>>(),
                    backupConfig.S3Bucket,
                    backupConfig.S3Prefix);
            return new Cabinet.API.Services.LocalBackupService(
                backupConfig, dbPath,
                sp.GetRequiredService<ILogger<Cabinet.API.Services.LocalBackupService>>(),
                s3);
        });

        if (backupConfig.AutoBackupEnabled)
        {
            services.AddHostedService<Cabinet.API.Services.BackupBackgroundService>();
        }
    }
    else if (configuration.GetValue<bool>("Backup:AutoBackupEnabled"))
    {
        Log.Warning("Backup:AutoBackupEnabled only applies to SQLite storage, so it's ignored. See the deployment guide for this backend's backups.");
    }

    // SSO/OIDC claim mapping (auth schemes configured in top-level Program.cs)
    var ssoConfig = configuration.GetSection("SSO").Get<SsoConfiguration>();
    if (ssoConfig?.Enabled == true)
    {
        services.AddSingleton(ssoConfig);
        services.AddSingleton<ClaimRoleMapper>();

        // Dashboard sign-in through the directory or a SAML identity provider
        if (ssoConfig.Ldap is { } ldap)
        {
            services.AddSingleton(sp => new Cabinet.API.Authentication.LdapAuthenticator(ldap, sp.GetRequiredService<ILogger<Cabinet.API.Authentication.LdapAuthenticator>>()));
        }

        if (ssoConfig.Saml is { } saml)
        {
            services.AddSingleton(sp => new Cabinet.API.Authentication.SamlSignIn(
                saml, sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILogger<Cabinet.API.Authentication.SamlSignIn>>()));
        }
    }

    // Deletes nothing unless Storage:RetentionDays is set to a positive value
    services.AddScoped<RetentionService>();
    services.AddHostedService<RetentionBackgroundService>();

    services.AddSingleton(sp => new Cabinet.API.Services.ApiKeyService(
        sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ILogger<Cabinet.API.Services.ApiKeyService>>(),
        sp.GetService<IDocumentStore>()));
    services.AddSingleton<Cabinet.API.Authentication.IApiKeyService>(sp => sp.GetRequiredService<Cabinet.API.Services.ApiKeyService>());
}

// cabinet-api --restore <backup.db.gz> [--force]
static async Task RestoreBackupAsync(string[] args, IConfiguration configuration)
{
    if (StorageRegistration.ResolveBackend(configuration) != StorageBackend.Sqlite)
    {
        throw new InvalidOperationException("--restore is for SQLite storage. See the deployment guide for this backend.");
    }

    if (args.Length < 2)
    {
        throw new ArgumentException("Usage: --restore <backup.db.gz> [--force]");
    }

    var database = Cabinet.API.Services.LocalBackupService.ExtractDatabasePath(
        configuration["Storage:ConnectionString"] ?? "Data Source=cabinet.db");
    await Cabinet.API.Services.LocalBackupService.RestoreAsync(args[1], database, overwrite: args.Contains("--force"));
    Log.Information("Restored {Backup} to {Database}", args[1], database);
}

void ValidateConfiguration(IConfiguration configuration)
{
    var validStorageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SQLite", "Memory", "PostgreSQL", "Postgres", "OpenSearch", "Elasticsearch"
    };

    var storageType = configuration["Storage:Type"] ?? "SQLite";
    if (!validStorageTypes.Contains(storageType))
    {
        throw new InvalidOperationException(
            $"Invalid Storage:Type '{storageType}'. Valid values: {string.Join(", ", validStorageTypes)}");
    }

    // Validate connection strings for backends that require them
    if (storageType.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
        storageType.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var connStr = configuration["Storage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException("Storage:ConnectionString is required for PostgreSQL backend");
    }

    if (storageType.Equals("OpenSearch", StringComparison.OrdinalIgnoreCase) ||
        storageType.Equals("Elasticsearch", StringComparison.OrdinalIgnoreCase))
    {
        var connStr = configuration["Storage:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connStr))
            throw new InvalidOperationException("Storage:ConnectionString is required for OpenSearch backend");
    }

    // Validate syslog ports
    var syslogEnabled = configuration.GetValue<bool>("Syslog:Enabled");
    if (syslogEnabled)
    {
        var tcpPort = configuration.GetValue<int>("Syslog:TcpPort");
        var udpPort = configuration.GetValue<int>("Syslog:UdpPort");
        if (tcpPort is < 1 or > 65535)
            throw new InvalidOperationException($"Syslog:TcpPort must be between 1 and 65535, got {tcpPort}");
        if (udpPort is < 1 or > 65535)
            throw new InvalidOperationException($"Syslog:UdpPort must be between 1 and 65535, got {udpPort}");
        var tlsPort = configuration.GetValue<int>("Syslog:TlsPort");
        if (tlsPort is < 0 or > 65535)
            throw new InvalidOperationException($"Syslog:TlsPort must be between 1 and 65535, or 0 for off, got {tlsPort}");
        if (tlsPort > 0 && string.IsNullOrWhiteSpace(configuration["Syslog:TlsCertificatePath"]))
            throw new InvalidOperationException("Syslog:TlsCertificatePath is required when Syslog:TlsPort is set");
    }

    // Validate email configuration when enabled
    var emailEnabled = configuration.GetValue<bool>("Email:Enabled");
    if (emailEnabled)
    {
        var smtpHost = configuration["Email:SmtpHost"];
        if (string.IsNullOrWhiteSpace(smtpHost))
            throw new InvalidOperationException("Email:SmtpHost is required when Email:Enabled=true");

        var fromAddress = configuration["Email:FromAddress"];
        if (string.IsNullOrWhiteSpace(fromAddress))
            throw new InvalidOperationException("Email:FromAddress is required when Email:Enabled=true");
    }
}

/// <summary>
/// Initialize storage with retry and graceful fallback for remote backends.
/// If PostgreSQL/OpenSearch is unreachable after retries, falls back to SQLite.
/// </summary>
async Task InitializeStorageWithRetryAsync(ILogStorage storage, IConfiguration configuration)
{
    var storageType = configuration["Storage:Type"] ?? "SQLite";
    var isRemoteBackend = storageType.ToUpperInvariant() is "POSTGRESQL" or "POSTGRES" or "OPENSEARCH" or "ELASTICSEARCH";

    if (!isRemoteBackend)
    {
        await storage.InitializeAsync();
        return;
    }

    // Retry remote backend initialization with exponential backoff
    var retryDelays = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
    Exception? lastException = null;

    for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
    {
        try
        {
            await storage.InitializeAsync();
            Log.Information("Storage backend ({Type}) initialized successfully", storageType);
            return;
        }
        catch (Exception ex)
        {
            lastException = ex;
            if (attempt < retryDelays.Length)
            {
                Log.Warning(ex, "Storage backend ({Type}) unavailable, retrying in {Delay}s (attempt {Attempt}/{Max})",
                    storageType, retryDelays[attempt].TotalSeconds, attempt + 1, retryDelays.Length);
                await Task.Delay(retryDelays[attempt]);
            }
        }
    }

    // Fail loudly: the exit code lets the service manager restart us, and logs are never
    // silently written somewhere other than the backend the operator configured.
    throw new InvalidOperationException(
        $"Storage backend ({storageType}) is unreachable: {lastException?.Message}",
        lastException);
}

// RetentionBackgroundService moved to Cabinet.API/Services/RetentionBackgroundService.cs