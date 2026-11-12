using System.Text;

namespace Cabinet.Query.Bql;

/// <summary>
/// Tokenizer for BQL (Bog Query Language) and SPL-compatible queries.
/// Handles string literals, numbers, time spans, identifiers, and operators.
/// </summary>
public sealed class BqlLexer
{
    private readonly string _input;
    private int _pos;

    public BqlLexer(string input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _pos = 0;
    }

    /// <summary>
    /// Maximum number of tokens allowed per query to prevent parser DoS from extremely complex queries.
    /// </summary>
    private const int MaxTokenCount = 10_000;

    /// <summary>
    /// Maximum length for a single string literal token to prevent memory exhaustion.
    /// </summary>
    private const int MaxStringLiteralLength = 1_000_000;

    /// <summary>
    /// Tokenize the entire input into a list of tokens
    /// </summary>
    public List<BqlToken> Tokenize()
    {
        var tokens = new List<BqlToken>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == BqlTokenKind.Eof) break;

            if (tokens.Count >= MaxTokenCount)
            {
                throw new QueryParseException(
                    $"Query exceeds maximum token limit ({MaxTokenCount}). Simplify the query or split it into smaller parts.",
                    $"position {token.Position}");
            }
        }
        return tokens;
    }

    private BqlToken NextToken()
    {
        SkipWhitespace();
        if (_pos >= _input.Length)
            return new BqlToken(BqlTokenKind.Eof, "", _pos);

        var start = _pos;
        var ch = _input[_pos];

        // String literals (single or double quoted)
        if (ch is '"' or '\'')
            return ReadString(ch);

        // Numbers (may be followed by time unit → TimeSpanLiteral)
        if (char.IsDigit(ch))
            return ReadNumberOrTimeSpan();

        // Identifiers, keywords, and named severity levels
        if (char.IsLetter(ch) || ch == '_')
            return ReadIdentifier();

        // Two-character operators (must check before single-char)
        if (_pos + 1 < _input.Length)
        {
            var two = _input.Substring(_pos, 2);
            switch (two)
            {
                case "!=": _pos += 2; return new BqlToken(BqlTokenKind.NotEquals, "!=", start);
                case "<>": _pos += 2; return new BqlToken(BqlTokenKind.NotEquals, "<>", start);
                case ">=": _pos += 2; return new BqlToken(BqlTokenKind.GreaterEquals, ">=", start);
                case "<=": _pos += 2; return new BqlToken(BqlTokenKind.LessEquals, "<=", start);
            }
        }

        // Single-character operators and punctuation
        _pos++;
        return ch switch
        {
            '|' => new BqlToken(BqlTokenKind.Pipe, "|", start),
            ',' => new BqlToken(BqlTokenKind.Comma, ",", start),
            '(' => new BqlToken(BqlTokenKind.OpenParen, "(", start),
            ')' => new BqlToken(BqlTokenKind.CloseParen, ")", start),
            '[' => new BqlToken(BqlTokenKind.OpenBracket, "[", start),
            ']' => new BqlToken(BqlTokenKind.CloseBracket, "]", start),
            '.' => new BqlToken(BqlTokenKind.Dot, ".", start),
            '=' => new BqlToken(BqlTokenKind.Equals, "=", start),
            '>' => new BqlToken(BqlTokenKind.Greater, ">", start),
            '<' => new BqlToken(BqlTokenKind.Less, "<", start),
            '+' => new BqlToken(BqlTokenKind.Plus, "+", start),
            '-' => new BqlToken(BqlTokenKind.Minus, "-", start),
            '*' => new BqlToken(BqlTokenKind.Star, "*", start),
            '/' => new BqlToken(BqlTokenKind.Slash, "/", start),
            '%' => new BqlToken(BqlTokenKind.Percent, "%", start),
            // Searches hold addresses, emails and paths: user@host, C:\logs, #channel
            _ when !char.IsControl(ch) && !char.IsWhiteSpace(ch) => new BqlToken(BqlTokenKind.Symbol, ch.ToString(), start),
            _ => throw new QueryParseException($"Unexpected character: '{ch}'", $"position {start}")
        };
    }

    private BqlToken ReadString(char quote)
    {
        var start = _pos;
        _pos++; // skip opening quote
        var sb = new StringBuilder();

        while (_pos < _input.Length)
        {
            var ch = _input[_pos];
            if (ch == '\\' && _pos + 1 < _input.Length)
            {
                _pos++;
                var next = _input[_pos];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    case var q when q == quote: sb.Append(q); break;
                    default: sb.Append('\\'); sb.Append(next); break; // preserve unknown escapes (e.g. \w, \d in regex)
                }
                _pos++;
                continue;
            }

            if (ch == quote)
            {
                _pos++; // skip closing quote
                return new BqlToken(BqlTokenKind.StringLiteral, sb.ToString(), start);
            }

            sb.Append(ch);
            _pos++;

            if (sb.Length > MaxStringLiteralLength)
                throw new QueryParseException(
                    $"String literal exceeds maximum length ({MaxStringLiteralLength})",
                    $"position {start}");
        }

        throw new QueryParseException($"Unterminated string literal starting at position {start}", $"position {start}");
    }

    private BqlToken ReadNumberOrTimeSpan()
    {
        var start = _pos;
        var sb = new StringBuilder();
        var hasDecimal = false;

        while (_pos < _input.Length && (char.IsDigit(_input[_pos]) || _input[_pos] == '.'))
        {
            if (_input[_pos] == '.')
            {
                if (hasDecimal) break; // second dot, stop
                // Check if next char is digit (decimal) vs field access
                if (_pos + 1 >= _input.Length || !char.IsDigit(_input[_pos + 1])) break;
                hasDecimal = true;
            }
            sb.Append(_input[_pos]);
            _pos++;
        }

        // Check for time unit suffix (h, m, d, w, s)
        if (_pos < _input.Length && !hasDecimal)
        {
            var unit = _input[_pos];
            if (unit is 'h' or 'm' or 'd' or 'w' or 's' &&
                (_pos + 1 >= _input.Length || !char.IsLetter(_input[_pos + 1])))
            {
                sb.Append(unit);
                _pos++;
                return new BqlToken(BqlTokenKind.TimeSpanLiteral, sb.ToString(), start);
            }
        }

        return new BqlToken(BqlTokenKind.NumberLiteral, sb.ToString(), start);
    }

    private BqlToken ReadIdentifier()
    {
        var start = _pos;
        var sb = new StringBuilder();

        // A dot between name characters belongs to the name, as in metric.name or service.name,
        // which OpenTelemetry and JSON fields use. A dot with space around it still joins text.
        while (_pos < _input.Length
            && (char.IsLetterOrDigit(_input[_pos]) || _input[_pos] == '_'
                || (_input[_pos] == '.' && _pos + 1 < _input.Length && (char.IsLetter(_input[_pos + 1]) || _input[_pos + 1] == '_'))))
        {
            sb.Append(_input[_pos]);
            _pos++;
        }

        return new BqlToken(BqlTokenKind.Identifier, sb.ToString(), start);
    }

    private void SkipWhitespace()
    {
        while (_pos < _input.Length && char.IsWhiteSpace(_input[_pos]))
            _pos++;
    }
}
