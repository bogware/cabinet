using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>Hash-chained audit logger providing tamper evidence via blockchain-like integrity chain.
/// Persists the latest hash to a file so the chain survives restarts.</summary>
public sealed class TamperEvidentAuditLogger : IAuditLogger
{
    private readonly IAuditLogger _inner;
    private readonly ILogger<TamperEvidentAuditLogger> _logger;
    private string _previousHash = "genesis";
    private readonly object _chainLock = new();
    private readonly string _chainStatePath;

    private const string DefaultChainStateFile = "audit-chain-state.json";

    public TamperEvidentAuditLogger(IAuditLogger inner, ILogger<TamperEvidentAuditLogger> logger)
    {
        _inner = inner;
        _logger = logger;
        _chainStatePath = Path.Combine(
            AppContext.BaseDirectory,
            DefaultChainStateFile);

        LoadChainState();
    }

    public Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        string hash;
        lock (_chainLock)
        {
            var eventData = JsonSerializer.Serialize(new
            {
                auditEvent.Id,
                auditEvent.Timestamp,
                auditEvent.PrincipalId,
                auditEvent.Action,
                auditEvent.ResourceType,
                auditEvent.ResourceId,
                auditEvent.Success
            });

            var chainInput = _previousHash + "|" + eventData;
            hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chainInput))).ToLowerInvariant();

            auditEvent.Description = $"{auditEvent.Description ?? ""} [chain:{hash[..16]}]".Trim();
            _previousHash = hash;
        }

        // Persist outside lock to avoid holding the lock during file I/O
        PersistChainState(hash);

        return _inner.LogAsync(auditEvent, cancellationToken);
    }

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? principalId = null,
        AuditAction? action = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
        => _inner.QueryAsync(startTime, endTime, principalId, action, limit, cancellationToken);

    private void LoadChainState()
    {
        try
        {
            if (File.Exists(_chainStatePath))
            {
                var json = File.ReadAllText(_chainStatePath);
                var state = JsonSerializer.Deserialize<ChainState>(json);
                if (state != null && !string.IsNullOrEmpty(state.PreviousHash))
                {
                    _previousHash = state.PreviousHash;
                    _logger.LogInformation(
                        "Audit chain restored from persisted state (hash prefix: {HashPrefix})",
                        _previousHash[..Math.Min(16, _previousHash.Length)]);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load audit chain state from {Path}. Starting new chain.", _chainStatePath);
        }
    }

    private void PersistChainState(string hash)
    {
        try
        {
            var state = new ChainState
            {
                PreviousHash = hash,
                LastUpdated = DateTime.UtcNow
            };
            var json = JsonSerializer.Serialize(state);
            var tempPath = _chainStatePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _chainStatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist audit chain state to {Path}", _chainStatePath);
        }
    }

    private sealed class ChainState
    {
        public string PreviousHash { get; set; } = "genesis";
        public DateTime LastUpdated { get; set; }
    }
}
