using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Cabinet.API.Authentication;

/// <summary>
/// API Key authentication handler
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly IApiKeyService _apiKeyService;
    private const string ApiKeyHeaderName = "X-API-Key";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyService apiKeyService)
        : base(options, logger, encoder)
    {
        _apiKeyService = apiKeyService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Only accept API key via header — never from query string (keys in URLs leak via logs, referrer headers, browser history)
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeader))
        {
            return AuthenticateResult.Fail("API Key not provided. Use the X-API-Key header.");
        }

        var apiKey = apiKeyHeader.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticateResult.Fail("API Key not provided");
        }

        // Validate API key
        var validationResult = await _apiKeyService.ValidateKeyAsync(apiKey);
        if (!validationResult.IsValid)
        {
            // Log the specific reason but never expose it to the client (prevents key enumeration attacks)
            Logger.LogDebug("API key validation failed: {Reason}", validationResult.Reason);
            return AuthenticateResult.Fail("Invalid API Key");
        }

        // Create claims (never store the raw API key in claims — it leaks into logs and tokens)
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, validationResult.ClientId),
            new(ClaimTypes.Name, validationResult.ClientName),
            new("ClientId", validationResult.ClientId),
            new("Tier", validationResult.Tier)
        };

        // Add permissions as claims
        foreach (var permission in validationResult.Permissions)
        {
            claims.Add(new Claim("Permission", permission));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.Append("WWW-Authenticate", $"ApiKey realm=\"{Options.Realm}\"");
        return Task.CompletedTask;
    }
}

/// <summary>
/// API Key authentication options
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public string Scheme => DefaultScheme;
    public string Realm { get; set; } = "Cabinet API";
}

/// <summary>
/// API Key service interface
/// </summary>
public interface IApiKeyService
{
    Task<ApiKeyValidationResult> ValidateKeyAsync(string apiKey);
    Task<string> GenerateKeyAsync(string clientName, string tier = "Basic", List<string>? permissions = null);
    Task<bool> RevokeKeyAsync(string apiKey);
    Task<List<ApiKeyInfo>> GetActiveKeysAsync();
}

/// <summary>
/// API Key validation result
/// </summary>
public class ApiKeyValidationResult
{
    public bool IsValid { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string Tier { get; set; } = "Basic";
    public List<string> Permissions { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// API Key information
/// </summary>
public class ApiKeyInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Key { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string Tier { get; set; } = "Basic";
    public List<string> Permissions { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool IsActive { get; set; } = true;
    internal long _requestCount;
    public long RequestCount => Interlocked.Read(ref _requestCount);
}

