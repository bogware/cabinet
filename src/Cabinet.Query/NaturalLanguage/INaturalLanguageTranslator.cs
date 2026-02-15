namespace Cabinet.Query.NaturalLanguage;

/// <summary>
/// Interface for translating natural language queries into BQL.
/// Implementations may use rule-based pattern matching, LLM APIs, or hybrid approaches.
/// </summary>
public interface INaturalLanguageTranslator
{
    /// <summary>
    /// Translate a natural language query into a BQL query string.
    /// </summary>
    /// <param name="naturalLanguageQuery">The user's natural language input</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A valid BQL query string, or null if translation is not possible</returns>
    Task<NaturalLanguageTranslation> TranslateAsync(
        string naturalLanguageQuery,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the input looks like natural language rather than a structured query.
    /// </summary>
    bool IsNaturalLanguage(string input);
}

/// <summary>
/// Result of a natural language to BQL translation
/// </summary>
public sealed class NaturalLanguageTranslation
{
    /// <summary>
    /// Whether the translation succeeded
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The translated BQL query string
    /// </summary>
    public string? BqlQuery { get; init; }

    /// <summary>
    /// Confidence score (0.0 to 1.0) for rule-based translators,
    /// or the model's confidence for AI-based translators
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Human-readable explanation of the translation
    /// </summary>
    public string? Explanation { get; init; }

    /// <summary>
    /// The original natural language input
    /// </summary>
    public string OriginalQuery { get; init; } = "";

    public static NaturalLanguageTranslation Failed(string originalQuery, string? reason = null) =>
        new()
        {
            Success = false,
            OriginalQuery = originalQuery,
            Explanation = reason ?? "Could not translate natural language query"
        };
}
