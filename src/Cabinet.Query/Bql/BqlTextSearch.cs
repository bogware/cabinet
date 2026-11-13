using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;

namespace Cabinet.Query.Bql;

/// <summary>
/// A search as SPL writes one: words, "quoted phrases", wild*cards, field=value terms, NOT or a
/// leading <c>-</c>, OR, AND (or just a space) and parentheses. <c>NOT</c>, <c>OR</c> and
/// <c>AND</c> are operators only in capitals, as in SPL.
/// </summary>
/// <remarks>
/// Words match as <see cref="SearchTerms"/> does everywhere: whole words, ignoring case. Storage
/// only understands "all of these words", so <see cref="RequiredWords"/> is what a search can push
/// down, and <see cref="IsPlainWords"/> says whether that alone is the whole search.
/// </remarks>
public sealed class BqlTextSearch
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Node _root;

    private BqlTextSearch(Node root)
    {
        _root = root;
    }

    /// <summary>Words every matching line has, joined by spaces, or null when there are none.</summary>
    public string? RequiredWords { get; private init; }

    /// <summary>Whether the search is only words that must all appear, which storage does exactly.</summary>
    public bool IsPlainWords { get; private init; }

    /// <summary>Whether it has any word or phrase, as opposed to only field terms.</summary>
    public bool HasText { get; private init; }

    /// <summary>Parses search text. Text with nothing to match, like <c>*</c>, matches everything.</summary>
    public static BqlTextSearch Parse(string? text)
    {
        var tokens = Tokenize(text ?? string.Empty);
        var position = 0;
        var root = tokens.Count == 0 ? new All() : ParseOr(tokens, ref position, depth: 0);
        if (position < tokens.Count)
        {
            throw new QueryParseException($"Unexpected '{tokens[position].Text}' in the search");
        }

        var required = new List<string>();
        CollectRequired(root, required);
        return new BqlTextSearch(root)
        {
            RequiredWords = required.Count == 0 ? null : string.Join(' ', required),
            IsPlainWords = IsPlain(root),
            HasText = HasTextNode(root),
        };
    }

    /// <summary>Whether a row matches. Words and phrases look in its line; field terms at its fields.</summary>
    public bool Matches(IDictionary<string, object?> row)
    {
        var line = row.TryGetValue("RawLine", out var raw) ? raw?.ToString() : null;
        return _root.Matches(line, row);
    }

    // ─── Tokens ───────────────────────────────────────────

    private enum Kind { Term, Phrase, Open, Close, Or, And, Not }

    private sealed record Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '(' or ')')
            {
                tokens.Add(new Token(c == '(' ? Kind.Open : Kind.Close, c.ToString()));
                i++;
                continue;
            }

            if (c == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0)
                {
                    throw new QueryParseException("A quoted phrase in the search has no closing quote");
                }

                tokens.Add(new Token(Kind.Phrase, text[(i + 1)..end]));
                i = end + 1;
                continue;
            }

            // A term runs to whitespace or a parenthesis, keeping a quoted value: host="web 1"
            var start = i;
            var term = new StringBuilder();
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')'))
            {
                if (text[i] == '"' && i > start)
                {
                    var end = text.IndexOf('"', i + 1);
                    if (end < 0)
                    {
                        throw new QueryParseException("A quoted value in the search has no closing quote");
                    }

                    term.Append(text, i, end - i + 1);
                    i = end + 1;
                    continue;
                }

                term.Append(text[i]);
                i++;
            }

            var value = term.ToString();
            tokens.Add(value switch
            {
                "OR" => new Token(Kind.Or, value),
                "AND" => new Token(Kind.And, value),
                "NOT" => new Token(Kind.Not, value),
                _ => new Token(Kind.Term, value),
            });
        }

        return tokens;
    }

    // ─── Grammar: or := and (OR and)*; and := unary (AND? unary)*; unary := (NOT|-) unary | primary ─

    /// <summary>
    /// How deep parentheses can nest. Parsing and matching recurse once per level, so without a
    /// limit enough of them would overflow the stack and end the process.
    /// </summary>
    internal const int MaxDepth = 100;

    private static Node ParseOr(List<Token> tokens, ref int position, int depth)
    {
        var parts = new List<Node> { ParseAnd(tokens, ref position, depth) };
        while (position < tokens.Count && tokens[position].Kind == Kind.Or)
        {
            position++;
            parts.Add(ParseAnd(tokens, ref position, depth));
        }

        return parts.Count == 1 ? parts[0] : new Or(parts);
    }

    private static Node ParseAnd(List<Token> tokens, ref int position, int depth)
    {
        var parts = new List<Node> { ParseUnary(tokens, ref position, depth) };
        while (position < tokens.Count && tokens[position].Kind is not (Kind.Or or Kind.Close))
        {
            if (tokens[position].Kind == Kind.And)
            {
                position++;
            }

            parts.Add(ParseUnary(tokens, ref position, depth));
        }

        return parts.Count == 1 ? parts[0] : new And(parts);
    }

    private static Node ParseUnary(List<Token> tokens, ref int position, int depth)
    {
        // In a loop: recursing here would let a long chain of NOT overflow the stack
        var negations = 0;
        while (position < tokens.Count && tokens[position].Kind == Kind.Not)
        {
            position++;
            negations++;
        }

        var operand = ParseOperand(tokens, ref position, depth);
        return negations % 2 == 0 ? operand : new Not(operand);
    }

    private static Node ParseOperand(List<Token> tokens, ref int position, int depth)
    {
        if (position >= tokens.Count)
        {
            throw new QueryParseException("The search ends where a term was expected");
        }

        var token = tokens[position];

        if (token.Kind == Kind.Term && token.Text.Length > 1 && token.Text[0] == '-')
        {
            position++;
            return new Not(Term(token.Text[1..]));
        }

        if (token.Kind == Kind.Open)
        {
            if (depth >= MaxDepth)
            {
                throw new QueryParseException($"The search nests parentheses too deeply (maximum depth: {MaxDepth})");
            }

            position++;
            var inner = ParseOr(tokens, ref position, depth + 1);
            if (position >= tokens.Count || tokens[position].Kind != Kind.Close)
            {
                throw new QueryParseException("Missing ')' in the search");
            }

            position++;
            return inner;
        }

        position++;
        return token.Kind switch
        {
            Kind.Phrase => Phrase.Create(token.Text),
            Kind.Term => Term(token.Text),
            _ => throw new QueryParseException($"Unexpected '{token.Text}' in the search"),
        };
    }

    private static readonly Regex FieldTermPattern = new(
        @"^(?<field>[A-Za-z_][\w.]*)(?<op>!=|>=|<=|=|>|<)(?<value>.+)$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static Node Term(string text)
    {
        if (text == "*")
        {
            return new All();
        }

        if (FieldTermPattern.Match(text) is { Success: true } field)
        {
            var value = field.Groups["value"].Value;
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            return new FieldTerm(field.Groups["field"].Value, field.Groups["op"].Value, value);
        }

        if (text.Contains('*'))
        {
            return new Wildcard(text);
        }

        // A term like 10.0.0.1 or user@host is its words, all of them, as storage reads it
        var words = SearchTerms.Parse(text);
        return words.Count switch
        {
            0 => new All(),
            1 => new Word(words[0]),
            _ => new And(words.Select(w => (Node)new Word(w)).ToList()),
        };
    }

    // ─── What can be pushed ────────────────────────────────

    private static void CollectRequired(Node node, List<string> words)
    {
        switch (node)
        {
            case Word word:
                words.Add(word.Text);
                break;
            case Phrase phrase:
                words.AddRange(phrase.Words);
                break;
            case And and:
                foreach (var part in and.Parts)
                {
                    CollectRequired(part, words);
                }

                break;
        }
    }

    private static bool IsPlain(Node node) => node switch
    {
        All or Word => true,
        And and => and.Parts.All(part => part is Word or All),
        _ => false,
    };

    private static bool HasTextNode(Node node) => node switch
    {
        Word or Phrase or Wildcard => true,
        And and => and.Parts.Any(HasTextNode),
        Or or => or.Parts.Any(HasTextNode),
        Not not => HasTextNode(not.Inner),
        _ => false,
    };

    // ─── Nodes ────────────────────────────────────────────

    private abstract class Node
    {
        public abstract bool Matches(string? line, IDictionary<string, object?> row);
    }

    private sealed class All : Node
    {
        public override bool Matches(string? line, IDictionary<string, object?> row) => true;
    }

    private sealed class Word(string text) : Node
    {
        private readonly IReadOnlyList<string> _terms = [text];

        public string Text { get; } = text;

        public override bool Matches(string? line, IDictionary<string, object?> row) => SearchTerms.Matches(line, _terms);
    }

    private sealed class Phrase : Node
    {
        private readonly Regex? _pattern;

        private Phrase(IReadOnlyList<string> words)
        {
            Words = words;

            // Its words in order, with only separators between them
            if (words.Count > 1)
            {
                _pattern = new Regex(
                    $"{WordStart}{string.Join(@"[^\p{L}\p{N}]+", words.Select(Regex.Escape))}{WordEnd}",
                    LinearTime);
            }
        }

        public IReadOnlyList<string> Words { get; }

        public static Node Create(string text)
        {
            if (text.Contains('*'))
            {
                return new Wildcard(text);
            }

            var words = SearchTerms.Parse(text);
            return words.Count switch
            {
                0 => new All(),
                1 => new Word(words[0]),
                _ => new Phrase(words),
            };
        }

        public override bool Matches(string? line, IDictionary<string, object?> row)
        {
            if (_pattern == null)
            {
                return SearchTerms.Matches(line, Words);
            }

            return !string.IsNullOrEmpty(line) && _pattern.IsMatch(line);
        }
    }

    private sealed class Wildcard : Node
    {
        private readonly Regex _pattern;

        public Wildcard(string text)
        {
            _pattern = WildcardRegex(text, wholeValue: false);
        }

        public override bool Matches(string? line, IDictionary<string, object?> row) =>
            !string.IsNullOrEmpty(line) && _pattern.IsMatch(line);
    }

    /// <summary>
    /// A wildcard as a regex. In a line, a pattern that starts or ends with a letter or digit
    /// starts or ends at a word boundary, so <c>fail*</c> finds "failed" but not "unfailed".
    /// </summary>
    private static Regex WildcardRegex(string text, bool wholeValue)
    {
        var body = string.Join(wholeValue ? ".*" : @"\S*", text.Split('*').Select(Regex.Escape));
        var pattern = wholeValue
            ? $"^{body}$"
            : $"{(char.IsLetterOrDigit(text[0]) ? WordStart : string.Empty)}{body}{(char.IsLetterOrDigit(text[^1]) ? WordEnd : string.Empty)}";
        return new Regex(pattern, LinearTime | RegexOptions.Singleline);
    }

    /// <summary>
    /// Phrases and wildcards never need backtracking, and without it matching takes time linear in
    /// the line, however the line is padded, so there's no timeout to run out of and turn a match
    /// into a miss. Linear matching has no lookarounds, so word edges are matched characters.
    /// </summary>
    private const RegexOptions LinearTime = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    /// <summary>The start of a word: the start of the line, or a character that isn't a letter or digit.</summary>
    private const string WordStart = @"(?:^|[^\p{L}\p{N}])";

    /// <summary>The end of a word: the end of the line, or a character that isn't a letter or digit.</summary>
    private const string WordEnd = @"(?:[^\p{L}\p{N}]|$)";

    private sealed class FieldTerm : Node
    {
        private readonly string _field;
        private readonly string _op;
        private readonly string _value;
        private readonly Regex? _wildcard;
        private readonly double? _number;

        public FieldTerm(string field, string op, string value)
        {
            _field = field;
            _op = op;
            _value = value;
            _wildcard = value.Contains('*') && op is "=" or "!=" ? WildcardRegex(value, wholeValue: true) : null;
            _number = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
        }

        public override bool Matches(string? line, IDictionary<string, object?> row)
        {
            // As in SPL, a term about a field never matches an event without it, != included
            if (!row.TryGetValue(_field, out var raw) || raw == null)
            {
                return false;
            }

            var text = raw is DateTime time ? time.ToString("O", CultureInfo.InvariantCulture) : Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
            if (_op is "=" or "!=")
            {
                var equal = _wildcard?.IsMatch(text)
                    ?? (_number is { } n && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var actual)
                        ? Math.Abs(actual - n) < 1e-9
                        : string.Equals(text, _value, StringComparison.OrdinalIgnoreCase));

                return _op == "=" ? equal : !equal;
            }

            int comparison;
            if (_number is { } limit && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                comparison = value.CompareTo(limit);
            }
            else
            {
                comparison = string.Compare(text, _value, StringComparison.OrdinalIgnoreCase);
            }

            return _op switch
            {
                ">" => comparison > 0,
                ">=" => comparison >= 0,
                "<" => comparison < 0,
                "<=" => comparison <= 0,
                _ => false,
            };
        }
    }

    private sealed class Not(Node inner) : Node
    {
        public Node Inner { get; } = inner;

        public override bool Matches(string? line, IDictionary<string, object?> row) => !Inner.Matches(line, row);
    }

    private sealed class And(List<Node> parts) : Node
    {
        public List<Node> Parts { get; } = parts;

        public override bool Matches(string? line, IDictionary<string, object?> row) => Parts.All(part => part.Matches(line, row));
    }

    private sealed class Or(List<Node> parts) : Node
    {
        public List<Node> Parts { get; } = parts;

        public override bool Matches(string? line, IDictionary<string, object?> row) => Parts.Any(part => part.Matches(line, row));
    }
}
