using Cabinet.Core.Utilities;

namespace Cabinet.Core.Models;

public sealed class Dashboard
{
    public string Id { get; set; } = Ulid.NewUlid();

    public required string Title { get; set; }

    public string? Description { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsShared { get; set; }

    public List<DashboardPanel> Panels { get; set; } = new();

    public int RefreshIntervalSeconds { get; set; }

    public string DefaultTimeRange { get; set; } = "-24h";
}

public sealed class DashboardPanel
{
    public string Id { get; set; } = Ulid.NewUlid();

    public required string Title { get; set; }

    public required string Query { get; set; }

    public VisualizationType Type { get; set; } = VisualizationType.Table;

    public int Row { get; set; }

    public int Column { get; set; }

    public int Width { get; set; } = 6;

    public int Height { get; set; } = 4;

    public Dictionary<string, object> Options { get; set; } = new();
}

public enum VisualizationType
{
    Table,
    LineChart,
    BarChart,
    PieChart,
    AreaChart,
    SingleValue,
    Gauge,
    Map,
    Heatmap,
    Markdown
}
