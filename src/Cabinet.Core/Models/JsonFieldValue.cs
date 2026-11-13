using System.Text.Json;

namespace Cabinet.Core.Models;

/// <summary>
/// Converts a stored JSON field value to the CLR type queries compare and aggregate on.
/// </summary>
public static class JsonFieldValue
{
    /// <summary>
    /// Unwraps a <see cref="JsonElement"/> to a string, long, double, bool or null. Arrays and
    /// objects stay as their JSON text.
    /// </summary>
    public static object? Unwrap(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        // Boxed apart, or the conditional's type would be double and every whole number a double
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? (object)integer : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };
}
