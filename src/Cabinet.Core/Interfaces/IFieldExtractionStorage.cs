namespace Cabinet.Core.Interfaces;

/// <summary>
/// Persistent storage for runtime field extraction configurations.
/// </summary>
public interface IFieldExtractionStorage
{
    /// <summary>Initialize storage (create tables if needed)</summary>
    Task InitializeAsync();

    /// <summary>Save a field extraction configuration</summary>
    Task<FieldExtraction> SaveAsync(FieldExtraction extraction);

    /// <summary>Get all field extractions ordered by priority</summary>
    Task<List<FieldExtraction>> GetAllAsync();

    /// <summary>Get a field extraction by ID</summary>
    Task<FieldExtraction?> GetByIdAsync(string id);

    /// <summary>Delete a field extraction by ID</summary>
    Task<bool> DeleteAsync(string id);
}

/// <summary>
/// A runtime field extraction configuration (regex pattern applied at search time).
/// </summary>
public class FieldExtraction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public string? SourceType { get; set; }
    public string? Index { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
