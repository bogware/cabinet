using Cabinet.Core.Models;

namespace Cabinet.Alerts;

/// <summary>
/// Alert service interface
/// </summary>
public interface IAlertService
{
    /// <summary>
    /// Send an alert
    /// </summary>
    Task SendAlertAsync(Alert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register an alert channel
    /// </summary>
    void RegisterChannel(IAlertChannel channel);

    /// <summary>
    /// Get all registered channels
    /// </summary>
    IEnumerable<IAlertChannel> GetChannels();
}

/// <summary>
/// Alert channel interface
/// </summary>
public interface IAlertChannel
{
    /// <summary>
    /// Channel name
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Send alert through this channel
    /// </summary>
    Task SendAsync(Alert alert, CancellationToken cancellationToken = default);
}

/// <summary>
/// Alert model
/// </summary>
public class Alert
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public AlertSeverity Severity { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Source { get; set; } = "";
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Alert severity levels
/// </summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Error,
    Critical
}