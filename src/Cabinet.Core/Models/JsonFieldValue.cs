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
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };
}
