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
using Serilog.Events;
using System.Reflection;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/cabinet-api-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 100 * 1024 * 1024)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Host.UseSerilog();

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
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50 MB
    });
    builder.Services.AddEndpointsApiExplorer();

    // Configure Swagger/OpenAPI
    // The detection routes belong to the optional gaming extension
    if (!GamingExtension.IsEnabled(builder.Configuration))
    {
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new ExcludeControllersFeatureProvider(
                typeof(Cabinet.API.Controllers.DetectionController))));
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
                Name = "Apache-2.0",
                Url = new Uri("https://www.apache.org/licenses/LICENSE-2.0")
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
    .AddPolicyScheme("MultiAuth", "API Key or Bearer", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                return "Bearer";
            return "ApiKey";
        };
    });

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
    builder.Services.AddCabinetRateLimiting();

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
        context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'");
        context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        context.Response.Headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        await next();
    });

    // API version headers
    app.UseMiddleware<ApiVersionMiddleware>();

    // Serve embedded web dashboard (static files from wwwroot/)
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseHttpsRedirection();
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

        // Initialize RBAC database
        var rbacService = scope.ServiceProvider.GetRequiredService<Cabinet.API.Services.SqliteRbacService>();
        await rbacService.InitializeAsync();

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
    services.AddSingleton<ILogStorage>(sp =>
    {
        var storageType = configuration["Storage:Type"] ?? "SQLite";
        var connectionString = configuration["Storage:ConnectionString"] ?? "Data Source=cabinet.db";

        return storageType.ToUpperInvariant() switch
        {
            "POSTGRESQL" or "POSTGRES" => new Cabinet.Database.Providers.PostgresLogStorage(
                connectionString,
                sp.GetRequiredService<ILogger<Cabinet.Database.Providers.PostgresLogStorage>>()),
            "OPENSEARCH" or "ELASTICSEARCH" => new Cabinet.Database.Providers.OpenSearchLogStorage(
                connectionString,
                sp.GetRequiredService<ILogger<Cabinet.Database.Providers.OpenSearchLogStorage>>(),
                indexPrefix: configuration["Storage:IndexPrefix"] ?? "cabinet-logs",
                username: configuration["Storage:Username"],
                password: configuration["Storage:Password"],
                allowInsecureSsl: configuration.GetValue<bool>("Storage:AllowInsecureSsl")),
            "MEMORY" => new InMemoryStorage(),
            _ => new SqliteLogStorage(
                sp.GetRequiredService<IOptions<Cabinet.Core.Configuration.CabinetConfiguration>>(),
                sp.GetRequiredService<ILogger<SqliteLogStorage>>())
        };
    });

    // Dashboards, knowledge objects, saved queries, schedules, search jobs, data models
    services.AddCabinetMetadataStorage(configuration);

    // RBAC and audit logging — database-backed with config fallback
    services.AddSingleton<Cabinet.API.Services.ConfigurationRbacService>();
    services.AddSingleton<Cabinet.API.Services.SqliteRbacService>();
    services.AddSingleton<IRbacService>(sp => sp.GetRequiredService<Cabinet.API.Services.SqliteRbacService>());
    services.AddSingleton<Cabinet.API.Services.SerilogAuditLogger>();
    services.AddSingleton<IAuditLogger>(sp =>
        new Cabinet.API.Services.TamperEvidentAuditLogger(
            sp.GetRequiredService<Cabinet.API.Services.SerilogAuditLogger>(),
            sp.GetRequiredService<ILogger<Cabinet.API.Services.TamperEvidentAuditLogger>>()));

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
    services.AddSingleton<IAlertHistoryStorage, InMemoryAlertHistoryStorage>();

    // Field extraction storage
    services.AddSingleton<IFieldExtractionStorage, InMemoryFieldExtractionStorage>();

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
    services.AddScoped<IQueryEngine, Cabinet.Query.Bql.BqlQueryEngine>();

    // Query result cache — accelerates dashboard refreshes with TTL-based caching.
    // In distributed mode, wraps with Redis L2 cache + pub/sub invalidation.
    var queryResultTtl = configuration.GetValue("Distributed:Cache:QueryResultTtlSeconds", 30);
    services.AddSingleton<Cabinet.API.Services.QueryCacheService>(sp =>
        new Cabinet.API.Services.QueryCacheService(
            sp.GetRequiredService<ILogger<Cabinet.API.Services.QueryCacheService>>(),
            TimeSpan.FromSeconds(queryResultTtl)));

    // Distributed query cache (registered later if Redis is available — see distributed section below)

    // Async search jobs — background execution for long-running queries
    services.AddHostedService<Cabinet.API.Services.SearchJobService>();

    // Data model acceleration — pre-aggregated summaries for tstats queries
    services.AddSingleton<Cabinet.API.Services.DataModelAccelerationService>();
    services.AddHostedService(sp => sp.GetRequiredService<Cabinet.API.Services.DataModelAccelerationService>());

    // Local backup service — gzipped SQLite backups with manifest and retention
    services.AddSingleton(sp =>
    {
        var backupConfig = new Cabinet.API.Services.BackupConfiguration();
        configuration.GetSection("Backup").Bind(backupConfig);
        var connStr = configuration["Storage:ConnectionString"] ?? "Data Source=cabinet.db";
        var dbPath = Cabinet.API.Services.LocalBackupService.ExtractDatabasePath(connStr);
        Cabinet.AWS.Services.S3ArchiveService? s3 = null;
        if (!string.IsNullOrEmpty(backupConfig.S3Bucket))
        {
            try
            {
                var s3Client = sp.GetService<Amazon.S3.IAmazonS3>();
                if (s3Client != null)
                {
                    s3 = new Cabinet.AWS.Services.S3ArchiveService(
                        s3Client,
                        sp.GetRequiredService<ILogger<Cabinet.AWS.Services.S3ArchiveService>>(),
                        backupConfig.S3Bucket,
                        backupConfig.S3Prefix);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "S3 client not available, backups will be local only");
            }
        }
        return new Cabinet.API.Services.LocalBackupService(
            backupConfig, dbPath,
            sp.GetRequiredService<ILogger<Cabinet.API.Services.LocalBackupService>>(),
            s3);
    });
    services.AddSingleton(sp =>
    {
        var backupConfig = new Cabinet.API.Services.BackupConfiguration();
        configuration.GetSection("Backup").Bind(backupConfig);
        return backupConfig;
    });
    if (configuration.GetValue<bool>("Backup:AutoBackupEnabled"))
    {
        services.AddHostedService<Cabinet.API.Services.BackupBackgroundService>();
    }

    // SSO/OIDC claim mapping (auth schemes configured in top-level Program.cs)
    var ssoConfig = configuration.GetSection("SSO").Get<SsoConfiguration>();
    if (ssoConfig?.Enabled == true)
    {
        services.AddSingleton(ssoConfig);
        services.AddSingleton<ClaimRoleMapper>();
    }

    // Deletes nothing unless Storage:RetentionDays is set to a positive value
    services.AddScoped<RetentionService>();
    services.AddHostedService<RetentionBackgroundService>();

    // Distributed infrastructure (Redis coordination, caching, rate limiting)
    var distributedConfig = configuration.GetSection("Distributed");
    // Keys are validated in-process on every instance. Authorization results are never
    // cached in Redis, where anyone with access could forge a valid one.
    services.AddSingleton<Cabinet.API.Authentication.IApiKeyService, Cabinet.API.Services.ApiKeyService>();

    var distributedEnabled = configuration.GetValue<bool>("Distributed:Enabled");
    var redisConnectionString = configuration["Distributed:RedisConnectionString"];

    if (distributedEnabled && !string.IsNullOrEmpty(redisConnectionString))
    {
        var instanceId = configuration["Distributed:InstanceId"] ?? Environment.MachineName;

        services.AddSingleton(sp => new Cabinet.API.Infrastructure.RedisConnectionManager(
            redisConnectionString,
            sp.GetRequiredService<ILogger<Cabinet.API.Infrastructure.RedisConnectionManager>>()));

        services.AddSingleton(sp => new Cabinet.API.Infrastructure.RedisLeaderElection(
            sp.GetRequiredService<Cabinet.API.Infrastructure.RedisConnectionManager>(),
            instanceId,
            sp.GetRequiredService<ILogger<Cabinet.API.Infrastructure.RedisLeaderElection>>(),
            leaseDuration: TimeSpan.FromSeconds(configuration.GetValue("Distributed:LeaderElection:LeaseTimeSeconds", 30)),
            renewalInterval: TimeSpan.FromSeconds(configuration.GetValue("Distributed:LeaderElection:RenewalIntervalSeconds", 10))));

        // Distributed query cache — L2 Redis cache with pub/sub invalidation across instances
        services.AddSingleton<Cabinet.API.Services.DistributedQueryCacheService>(sp =>
            new Cabinet.API.Services.DistributedQueryCacheService(
                sp.GetRequiredService<Cabinet.API.Services.QueryCacheService>(),
                sp.GetRequiredService<Cabinet.API.Infrastructure.RedisConnectionManager>(),
                sp.GetRequiredService<ILogger<Cabinet.API.Services.DistributedQueryCacheService>>(),
                TimeSpan.FromSeconds(configuration.GetValue("Distributed:Cache:QueryResultTtlSeconds", 60))));

        // Only log the host:port, never credentials — extract first part that doesn't contain '='
        var redisHost = redisConnectionString.Split(',')
            .Select(p => p.Trim())
            .FirstOrDefault(p => !p.Contains('=')) ?? "***";
        Log.Information("Distributed mode enabled (Redis: {Redis}, Instance: {Instance})",
            redisHost, instanceId);
    }
    else
    {
        Log.Information("Single-instance mode (set Distributed:Enabled=true for multi-instance)");
    }
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

    // Validate distributed configuration
    var distributedEnabled = configuration.GetValue<bool>("Distributed:Enabled");
    if (distributedEnabled)
    {
        var redisConn = configuration["Distributed:RedisConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConn))
            throw new InvalidOperationException("Distributed:RedisConnectionString is required when Distributed:Enabled=true");
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