namespace Cabinet.Query.Bql;

/// <summary>
/// Token kinds produced by the BQL lexer
/// </summary>
public enum BqlTokenKind
{
    // Literals
    StringLiteral,
    NumberLiteral,
    Identifier,
    TimeSpanLiteral,

    // Punctuation
    Pipe,
    Comma,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Dot,

    // Comparison operators
    Equals,
    NotEquals,
    Greater,
    GreaterEquals,
    Less,
    LessEquals,

    // Arithmetic operators
    Plus,
    Minus,
    Star,
    Slash,
    Percent,

    /// <summary>Any other printable character, such as @ or :, which search text keeps as typed.</summary>
    Symbol,

    // End of input
    Eof
}

/// <summary>
/// A single token produced by the BQL lexer
/// </summary>
public readonly struct BqlToken
{
    public BqlTokenKind Kind { get; }
    public string Value { get; }
    public int Position { get; }

    public BqlToken(BqlTokenKind kind, string value, int position)
    {
        Kind = kind;
        Value = value;
        Position = position;
    }

    /// <summary>
    /// Check if this token is a case-insensitive keyword match
    /// </summary>
    public bool IsKeyword(string keyword) =>
        Kind == BqlTokenKind.Identifier &&
        string.Equals(Value, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Check if this token matches any of the given keywords
    /// </summary>
    public bool IsAnyKeyword(params string[] keywords)
    {
        if (Kind != BqlTokenKind.Identifier) return false;
        foreach (var kw in keywords)
        {
            if (string.Equals(Value, kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public override string ToString() => $"{Kind}({Value})@{Position}";
}
