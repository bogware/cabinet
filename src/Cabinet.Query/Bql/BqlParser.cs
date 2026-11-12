using Cabinet.Core.Models;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cabinet.Query.Bql;

/// <summary>
/// Recursive-descent parser for BQL (Bog Query Language).
/// Supports both BQL natural language syntax and SPL-compatible syntax.
/// Implements IQueryParser for backward compatibility with the existing query engine.
/// </summary>
public sealed class BqlParser : IQueryParser
{
    private List<BqlToken> _tokens = new();
    private string _source = string.Empty;
    private int _pos;
    private int _recursionDepth;
    private const int MaxRecursionDepth = 100;
    private const int MaxQueryLength = 100_000;
    private const int MaxPipelineStages = 100;
    private readonly object _parseLock = new();

    // ─── IQueryParser implementation (legacy compatibility) ───

    public QueryPlan Parse(string query)
    {
        var bqlQuery = ParseBql(query);
        return ConvertToLegacyPlan(bqlQuery);
    }

    // ─── BQL-native parsing ──────────────────────────────────

    /// <summary>
    /// Parse a BQL or SPL query string into a BQL AST.
    /// Thread-safe: concurrent calls are serialized via lock to protect instance state.
    /// </summary>
    public BqlQuery ParseBql(string query)
    {
        lock (_parseLock)
        {
            return ParseBqlCore(query);
        }
    }

    private BqlQuery ParseBqlCore(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new QueryParseException("Query cannot be empty");

        if (query.Length > MaxQueryLength)
            throw new QueryParseException($"Query exceeds maximum length of {MaxQueryLength} characters");

        var lexer = new BqlLexer(query);
        _tokens = lexer.Tokenize();
        _source = query;
        _pos = 0;
        _recursionDepth = 0;

        var bqlQuery = new BqlQuery();

        // Parse the first stage (search/find or implicit search)
        var firstStage = ParseFirstStage(bqlQuery);
        if (firstStage != null)
            bqlQuery.Stages.Add(firstStage);

        // Parse piped stages
        while (Current.Kind == BqlTokenKind.Pipe)
        {
            if (bqlQuery.Stages.Count >= MaxPipelineStages)
                throw new QueryParseException($"Query exceeds maximum of {MaxPipelineStages} pipeline stages");

            Advance(); // skip pipe
            var stage = ParseStage();
            bqlQuery.Stages.Add(stage);

            if (Current.IsKeyword("from") && Peek(1).IsKeyword("last"))
                ParseFromLast(bqlQuery);
        }

        if (Current.Kind != BqlTokenKind.Eof)
            throw new QueryParseException($"Unexpected token: {Current.Value}", $"position {Current.Position}");

        return bqlQuery;
    }

    // ─── First stage parsing ─────────────────────────────────

    private BqlStage? ParseFirstStage(BqlQuery query)
    {
        // Empty or just EOF
        if (Current.Kind == BqlTokenKind.Eof)
            return new SearchStage("*");

        // Explicit find/search keyword
        if (Current.IsAnyKeyword("find", "search"))
        {
            Advance();
            return ParseSearchBody(query);
        }

        // Check if first token is a known stage keyword
        if (IsStageKeyword(Current))
        {
            // Implicit "search *" followed by explicit stage
            return new SearchStage("*");
        }

        // Implicit search: everything before the first pipe is the search text
        return ParseImplicitSearch(query);
    }

    private SearchStage ParseSearchBody(BqlQuery query)
    {
        var sb = new StringBuilder();
        var parenDepth = 0;

        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            // Track parens to avoid breaking on pipes inside expressions
            if (Current.Kind == BqlTokenKind.OpenParen) parenDepth++;
            if (Current.Kind == BqlTokenKind.CloseParen) parenDepth--;

            // Check for time range modifiers: "from last Xh" or "between X and Y"
            if (parenDepth == 0 && Current.IsKeyword("from") && Peek(1).IsKeyword("last"))
            {
                ParseFromLast(query);
                continue;
            }

            if (parenDepth == 0 && Current.IsKeyword("between"))
            {
                ParseBetween(query);
                continue;
            }

            AppendSearchToken(sb);
        }

        return new SearchStage(TakeTimeModifiers(query, sb.ToString()));
    }

    private static readonly System.Text.RegularExpressions.Regex TimeModifier = new(
        @"(?<![\w.])(?<which>earliest|latest)=(?<value>""[^""]*""|\S+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Takes SPL's <c>earliest=-1d@d latest=now</c> out of search text as the query's time range,
    /// and returns the rest of the text.
    /// </summary>
    private static string TakeTimeModifiers(BqlQuery query, string text)
    {
        var rest = TimeModifier.Replace(text, match =>
        {
            var value = match.Groups["value"].Value.Trim('"');
            var now = DateTime.UtcNow;
            var time = value.Equals("now", StringComparison.OrdinalIgnoreCase) ? now
                : value == "0" ? DateTime.UnixEpoch
                : BqlTime.ApplyRelative(now, value) ?? BqlTime.Parse(value, null)
                    ?? throw new QueryParseException($"'{value}' isn't a time: use one such as -24h, -1d@d, now or 2026-03-04T05:00:00Z");
            query.TimeRange ??= new BqlTimeRange();
            if (match.Groups["which"].Value.Equals("earliest", StringComparison.OrdinalIgnoreCase))
                query.TimeRange.Start = time;
            else
                query.TimeRange.End = time;
            return string.Empty;
        }).Trim();
        return rest.Length > 0 ? rest : "*";
    }

    private SearchStage ParseImplicitSearch(BqlQuery query)
    {
        var sb = new StringBuilder();

        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            // Check for time range modifiers
            if (Current.IsKeyword("from") && Peek(1).IsKeyword("last"))
            {
                ParseFromLast(query);
                continue;
            }

            if (Current.IsKeyword("between"))
            {
                ParseBetween(query);
                continue;
            }

            AppendSearchToken(sb);
        }

        return new SearchStage(TakeTimeModifiers(query, sb.ToString()));
    }

    // Spaced as typed, so host=l4d2-dev stays one comparison rather than becoming host = l4d2 - dev
    private void AppendSearchToken(StringBuilder sb)
    {
        var position = Current.Position;
        var spaced = position <= 0 || position > _source.Length || char.IsWhiteSpace(_source[position - 1]);
        if (sb.Length > 0 && spaced) sb.Append(' ');
        sb.Append(Current.Kind == BqlTokenKind.StringLiteral ? $"\"{Current.Value}\"" : Current.Value);
        Advance();
    }

    private void ParseFromLast(BqlQuery query)
    {
        Advance(); // skip "from"
        Advance(); // skip "last"

        if (Current.Kind != BqlTokenKind.TimeSpanLiteral)
            throw new QueryParseException("Expected time span after 'from last' (e.g., 24h, 7d, 30m)", $"position {Current.Position}");

        var ts = ParseTimeSpanValue(Current.Value) ?? TimeSpan.FromHours(24);
        query.TimeRange = new BqlTimeRange { Start = DateTime.UtcNow - ts, End = DateTime.UtcNow };
        Advance();
    }

    private void ParseBetween(BqlQuery query)
    {
        Advance(); // skip "between"

        var start = ParseDateTimeLiteral();
        ExpectKeyword("and");
        var end = ParseDateTimeLiteral();

        query.TimeRange = new BqlTimeRange { Start = start, End = end };
    }

    // ─── Stage parsing ───────────────────────────────────────

    private BqlStage ParseStage()
    {
        if (Current.Kind == BqlTokenKind.Eof)
            throw new QueryParseException("Expected a stage after pipe operator");

        // BQL-style keywords
        if (Current.IsKeyword("where")) { Advance(); return ParseWhereStage(); }
        if (Current.IsAnyKeyword("group")) return ParseGroupByStage();
        if (Current.IsAnyKeyword("sort")) return ParseSortStage();
        if (Current.IsAnyKeyword("limit")) { Advance(); return ParseLimitStage(); }
        if (Current.IsAnyKeyword("compute")) { Advance(); return ParseEvalStage(); }
        if (Current.IsAnyKeyword("extract")) { Advance(); return ParseRexStage(); }
        if (Current.IsAnyKeyword("select")) { Advance(); return ParseTableStage(); }
        if (Current.IsAnyKeyword("unique")) return ParseUniqueByStage();
        if (Current.IsKeyword("rename")) { Advance(); return ParseRenameStage(); }
        if (Current.IsKeyword("fill")) { Advance(); return ParseFillStage(); }
        if (Current.IsKeyword("expand")) { Advance(); return ParseExpandStage(); }
        if (Current.IsKeyword("fillnull")) { Advance(); return ParseFillNullStage(); }
        if (Current.IsAnyKeyword("bucket", "bin")) { Advance(); return ParseBucketStage(); }
        if (Current.IsKeyword("makeresults")) { Advance(); return ParseMakeResultsStage(); }
        if (Current.IsKeyword("mvexpand")) { Advance(); return ParseMvExpandStage(); }
        if (Current.IsKeyword("chart")) { Advance(); return ParseChartStage(); }
        if (Current.IsKeyword("filldown")) { Advance(); return ParseFillDownStage(); }
        if (Current.IsKeyword("addcoltotals")) { Advance(); return ParseAddColTotalsStage(); }
        if (Current.IsKeyword("inputlookup")) { Advance(); return ParseInputLookupStage(); }
        if (Current.IsKeyword("rare")) { Advance(); return ParseRareStage(); }
        if (Current.IsKeyword("regex")) { Advance(); return ParseRegexStage(); }
        if (Current.IsKeyword("fieldsummary")) { Advance(); return ParseFieldSummaryStage(); }
        if (Current.IsKeyword("xyseries")) { Advance(); return ParseXyseriesStage(); }
        if (Current.IsKeyword("untable")) { Advance(); return ParseUntableStage(); }
        if (Current.IsKeyword("convert")) { Advance(); return ParseConvertStage(); }

        // SPL-style keywords
        if (Current.IsKeyword("eventstats")) { Advance(); return ParseEventStatsStage(); }
        if (Current.IsKeyword("streamstats")) { Advance(); return ParseStreamStatsStage(); }
        if (Current.IsKeyword("stats")) { Advance(); return ParseStatsStage(); }
        if (Current.IsKeyword("eval")) { Advance(); return ParseEvalStage(); }
        if (Current.IsKeyword("rex")) { Advance(); return ParseRexStage(); }
        if (Current.IsKeyword("head")) { Advance(); return ParseLimitStage(); }
        if (Current.IsKeyword("tail")) { Advance(); return ParseTailStage(); }
        if (Current.IsAnyKeyword("table", "fields")) { Advance(); return ParseTableStage(); }
        if (Current.IsKeyword("dedup")) { Advance(); return ParseDedupStage(); }
        if (Current.IsKeyword("top")) { Advance(); return ParseTopStage(); }
        if (Current.IsKeyword("timechart")) { Advance(); return ParseTimechartStage(); }
        if (Current.IsKeyword("fieldalias")) { Advance(); return ParseFieldAliasStage(); }
        if (Current.IsKeyword("transaction")) { Advance(); return ParseTransactionStage(); }
        if (Current.IsKeyword("lookup")) { Advance(); return ParseLookupStage(); }
        if (Current.IsKeyword("append")) { Advance(); return ParseAppendStage(); }
        if (Current.IsKeyword("join")) { Advance(); return ParseJoinStage(); }
        if (Current.IsKeyword("macro")) { Advance(); return ParseMacroStage(); }
        if (Current.IsKeyword("subsearch")) { Advance(); return ParseSubsearchStage(); }
        if (Current.IsKeyword("multisearch")) { Advance(); return ParseMultisearchStage(); }
        if (Current.IsAnyKeyword("search", "find")) { Advance(); return ParseSearchStageInPipeline(); }

        throw new QueryParseException($"Unknown stage: {Current.Value}", $"position {Current.Position}");
    }

    // ─── WHERE (with AND/OR/NOT/parentheses) ─────────────────

    private WhereStage ParseWhereStage()
    {
        var expr = ParseOrExpression();
        return new WhereStage(expr);
    }

    private BqlExpression ParseOrExpression()
    {
        var left = ParseAndExpression();

        while (Current.IsKeyword("or"))
        {
            Advance();
            var right = ParseAndExpression();
            left = new LogicalExpression(left, "OR", right);
        }

        return left;
    }

    private BqlExpression ParseAndExpression()
    {
        var left = ParseNotExpression();

        while (Current.IsKeyword("and"))
        {
            Advance();
            var right = ParseNotExpression();
            left = new LogicalExpression(left, "AND", right);
        }

        return left;
    }

    private BqlExpression ParseNotExpression()
    {
        // In a loop: recursing here would let a long chain of "not" overflow the stack.
        var negations = 0;
        while (Current.IsKeyword("not"))
        {
            Advance();
            negations++;
        }

        var expression = ParseComparison();
        return negations % 2 == 0 ? negations == 0 ? expression : new NotExpression(new NotExpression(expression)) : new NotExpression(expression);
    }

    private BqlExpression ParseComparison()
    {
        var left = ParseAdditive();

        // IN expression
        if (Current.IsKeyword("in"))
        {
            Advance();
            Expect(BqlTokenKind.OpenParen, "(");
            var values = new List<BqlExpression>();
            values.Add(SeverityLiteral(left, ParsePrimary()));
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                values.Add(SeverityLiteral(left, ParsePrimary()));
            }
            Expect(BqlTokenKind.CloseParen, ")");
            return new InExpression(left, values);
        }

        // LIKE expression
        if (Current.IsKeyword("like"))
        {
            Advance();
            if (Current.Kind != BqlTokenKind.StringLiteral)
                throw new QueryParseException("Expected string pattern after LIKE", $"position {Current.Position}");
            var pattern = Current.Value;
            Advance();
            return new LikeExpression(left, pattern);
        }

        // Comparison operators
        if (IsComparisonOperator(Current.Kind))
        {
            var op = Current.Value;
            Advance();
            var right = ParseAdditive();
            return new ComparisonExpression(left, op, SeverityLiteral(left, right));
        }

        return left;
    }

    private BqlExpression ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (Current.Kind is BqlTokenKind.Plus or BqlTokenKind.Minus or BqlTokenKind.Dot)
        {
            var op = Current.Value;
            Advance();
            var right = ParseMultiplicative();
            left = new ArithmeticExpression(left, op, right);
        }

        return left;
    }

    private BqlExpression ParseMultiplicative()
    {
        var left = ParseUnary();

        while (Current.Kind is BqlTokenKind.Star or BqlTokenKind.Slash or BqlTokenKind.Percent)
        {
            var op = Current.Value;
            Advance();
            var right = ParseUnary();
            left = new ArithmeticExpression(left, op, right);
        }

        return left;
    }

    private BqlExpression ParseUnary()
    {
        if (Current.Kind == BqlTokenKind.Minus)
        {
            Advance();
            var operand = ParsePrimary();
            return new ArithmeticExpression(new LiteralExpression(0), "-", operand);
        }

        return ParsePrimary();
    }

    private BqlExpression ParsePrimary()
    {
        // Parenthesized expression with recursion depth guard
        if (Current.Kind == BqlTokenKind.OpenParen)
        {
            if (++_recursionDepth > MaxRecursionDepth)
                throw new QueryParseException("Query too deeply nested (maximum depth: 100)");
            try
            {
                Advance();
                var expr = ParseOrExpression();
                Expect(BqlTokenKind.CloseParen, ")");
                return expr;
            }
            finally
            {
                _recursionDepth--;
            }
        }

        // String literal
        if (Current.Kind == BqlTokenKind.StringLiteral)
        {
            var val = Current.Value;
            Advance();
            return new LiteralExpression(val);
        }

        // Number literal
        if (Current.Kind == BqlTokenKind.NumberLiteral)
        {
            var val = Current.Value;
            Advance();
            if (val.Contains('.'))
                return new LiteralExpression(double.Parse(val, CultureInfo.InvariantCulture));
            return new LiteralExpression(long.Parse(val, CultureInfo.InvariantCulture));
        }

        // Star (wildcard)
        if (Current.Kind == BqlTokenKind.Star)
        {
            Advance();
            return new LiteralExpression("*");
        }

        // Boolean literals
        if (Current.IsKeyword("true")) { Advance(); return new LiteralExpression(true); }
        if (Current.IsKeyword("false")) { Advance(); return new LiteralExpression(false); }
        if (Current.IsKeyword("null")) { Advance(); return new LiteralExpression(null); }

        // Identifier — could be field reference or function call
        if (Current.Kind == BqlTokenKind.Identifier)
        {
            var name = Current.Value;
            Advance();

            // Function call: name(args)
            if (Current.Kind == BqlTokenKind.OpenParen)
            {
                Advance();

                // Arguments nest like parentheses, so they count toward the same limit
                if (++_recursionDepth > MaxRecursionDepth)
                    throw new QueryParseException($"Query too deeply nested (maximum depth: {MaxRecursionDepth})");
                try
                {
                    var args = new List<BqlExpression>();
                    if (Current.Kind != BqlTokenKind.CloseParen)
                    {
                        args.Add(ParseOrExpression());
                        while (Current.Kind == BqlTokenKind.Comma)
                        {
                            Advance();
                            args.Add(ParseOrExpression());
                        }
                    }
                    Expect(BqlTokenKind.CloseParen, ")");
                    return new FunctionExpression(name, args);
                }
                finally
                {
                    _recursionDepth--;
                }
            }

            return new FieldExpression(name);
        }

        throw new QueryParseException($"Unexpected token: {Current.Value}", $"position {Current.Position}");
    }

    // ─── STATS / GROUP BY ────────────────────────────────────

    private StatsStage ParseStatsStage()
    {
        var stage = new StatsStage();

        // Parse aggregation functions
        stage.Aggregations.Add(ParseAggregateFunction());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Aggregations.Add(ParseAggregateFunction());
        }

        // Parse optional BY clause
        if (Current.IsKeyword("by"))
        {
            Advance();
            stage.GroupByFields.Add(ExpectIdentifier());
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                stage.GroupByFields.Add(ExpectIdentifier());
            }
        }

        return stage;
    }

    private StatsStage ParseGroupByStage()
    {
        Advance(); // skip "group"

        // "group by field1, field2" → stats count by field1, field2
        if (Current.IsKeyword("by"))
            Advance();

        var stage = new StatsStage();
        stage.Aggregations.Add(new AggregateFunction("count"));

        stage.GroupByFields.Add(ExpectIdentifier());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.GroupByFields.Add(ExpectIdentifier());
        }

        return stage;
    }

    private AggregateFunction ParseAggregateFunction()
    {
        var name = ExpectIdentifier();

        // Function with field: avg(duration), sum(bytes)
        if (Current.Kind == BqlTokenKind.OpenParen)
        {
            Advance();

            // count(eval(status >= 500)): what an expression gives, row by row
            if (Current.IsKeyword("eval") && Peek(1).Kind == BqlTokenKind.OpenParen)
            {
                Advance();
                Advance();
                var start = Current.Position;
                var expression = ParseOrExpression();
                var text = SourceText(start, Current);
                Expect(BqlTokenKind.CloseParen, ")");
                Expect(BqlTokenKind.CloseParen, ")");
                var evaluated = new AggregateFunction(name, $"eval({text})") { Expression = expression };
                if (Current.IsKeyword("as"))
                {
                    Advance();
                    evaluated.Alias = ExpectIdentifier();
                }

                return evaluated;
            }

            var field = ExpectIdentifier();
            Expect(BqlTokenKind.CloseParen, ")");
            var agg = new AggregateFunction(name, field);

            // Optional alias: "as alias_name"
            if (Current.IsKeyword("as"))
            {
                Advance();
                agg.Alias = ExpectIdentifier();
            }

            return agg;
        }

        // Plain function (no parens): "count"
        var plainAgg = new AggregateFunction(name);
        if (Current.IsKeyword("as"))
        {
            Advance();
            plainAgg.Alias = ExpectIdentifier();
        }

        return plainAgg;
    }

    // ─── SORT ────────────────────────────────────────────────

    private SortStage ParseSortStage()
    {
        Advance(); // skip "sort"
        var stage = new SortStage();

        // BQL style: "sort by field asc/desc"
        if (Current.IsKeyword("by"))
        {
            Advance();
            ParseBqlSortFields(stage);
        }
        else
        {
            // SPL style: "sort ±field1, ±field2"
            ParseSplSortFields(stage);
        }

        return stage;
    }

    private void ParseBqlSortFields(SortStage stage)
    {
        stage.Fields.Add(ParseBqlSortField());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Fields.Add(ParseBqlSortField());
        }
    }

    private SortField ParseBqlSortField()
    {
        var field = ExpectIdentifier();
        var desc = false;

        if (Current.IsKeyword("desc"))
        {
            desc = true;
            Advance();
        }
        else if (Current.IsKeyword("asc"))
        {
            Advance();
        }

        return new SortField(field, desc);
    }

    private void ParseSplSortFields(SortStage stage)
    {
        stage.Fields.Add(ParseSplSortField());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Fields.Add(ParseSplSortField());
        }
    }

    private SortField ParseSplSortField()
    {
        var desc = false;
        if (Current.Kind == BqlTokenKind.Minus)
        {
            desc = true;
            Advance();
        }
        else if (Current.Kind == BqlTokenKind.Plus)
        {
            Advance();
        }

        var field = ExpectIdentifier();
        return new SortField(field, desc);
    }

    // ─── Simple stages ───────────────────────────────────────

    private LimitStage ParseLimitStage()
    {
        var count = ExpectNumber();
        return new LimitStage(count);
    }

    private TailStage ParseTailStage()
    {
        var count = Current.Kind == BqlTokenKind.NumberLiteral ? ExpectNumber() : 10;
        return new TailStage(count);
    }

    private EvalStage ParseEvalStage()
    {
        var assignments = new List<EvalAssignment>();

        var fieldName = ExpectIdentifier();
        Expect(BqlTokenKind.Equals, "=");
        var expression = ParseOrExpression();
        assignments.Add(new EvalAssignment(fieldName, expression));

        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            var nextField = ExpectIdentifier();
            Expect(BqlTokenKind.Equals, "=");
            var nextExpr = ParseOrExpression();
            assignments.Add(new EvalAssignment(nextField, nextExpr));
        }

        return new EvalStage(assignments);
    }

    private RexStage ParseRexStage()
    {
        // SPL style: rex field=source "pattern"
        // BQL style: extract "pattern" from source
        var sourceField = "RawLine";

        if (Current.Kind == BqlTokenKind.Identifier && Peek(1).Kind == BqlTokenKind.Equals)
        {
            // SPL: field=source
            var key = Current.Value;
            Advance(); Advance(); // skip "field" and "="
            if (string.Equals(key, "field", StringComparison.OrdinalIgnoreCase))
                sourceField = ExpectIdentifier();
        }

        if (Current.Kind != BqlTokenKind.StringLiteral)
            throw new QueryParseException("Expected regex pattern string", $"position {Current.Position}");

        var pattern = Current.Value;
        Advance();

        // BQL: "from" source
        if (Current.IsKeyword("from"))
        {
            Advance();
            sourceField = ExpectIdentifier();
        }

        return new RexStage(pattern, sourceField);
    }

    private RenameStage ParseRenameStage()
    {
        var mappings = new List<RenameMapping>();

        var oldName = ExpectIdentifier();
        ExpectKeyword("as");
        var newName = ExpectIdentifier();
        mappings.Add(new RenameMapping(oldName, newName));

        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            var nextOld = ExpectIdentifier();
            ExpectKeyword("as");
            var nextNew = ExpectIdentifier();
            mappings.Add(new RenameMapping(nextOld, nextNew));
        }

        return new RenameStage(mappings);
    }

    private TableStage ParseTableStage()
    {
        var fields = new List<string> { ExpectIdentifier() };
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectIdentifier());
        }
        return new TableStage(fields);
    }

    private DedupStage ParseDedupStage()
    {
        var fields = new List<string> { ExpectIdentifier() };
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectIdentifier());
        }
        return new DedupStage(fields);
    }

    private BqlStage ParseUniqueByStage()
    {
        Advance(); // skip "unique"
        if (Current.IsKeyword("by"))
            Advance();
        var fields = new List<string> { ExpectIdentifier() };
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectIdentifier());
        }
        return new DedupStage(fields);
    }

    private TopStage ParseTopStage()
    {
        var count = 10;
        if (Current.Kind == BqlTokenKind.NumberLiteral)
            count = ExpectNumber();
        var field = ExpectIdentifier();
        string? byField = null;
        if (Current.IsKeyword("by"))
        {
            Advance();
            byField = ExpectIdentifier();
        }

        return new TopStage(count, field) { ByField = byField };
    }

    private TimechartStage ParseTimechartStage()
    {
        var span = TimeSpan.FromHours(1);
        var aggregation = "count";
        string? byField = null;

        // Parse span=Xh
        if (Current.Kind == BqlTokenKind.Identifier && Current.Value.StartsWith("span", StringComparison.OrdinalIgnoreCase))
        {
            if (Current.Value.Equals("span", StringComparison.OrdinalIgnoreCase) && Peek(1).Kind == BqlTokenKind.Equals)
            {
                Advance(); Advance(); // skip "span" "="
                if (Current.Kind == BqlTokenKind.TimeSpanLiteral)
                {
                    span = ParseTimeSpanValue(Current.Value) ?? span;
                    Advance();
                }
            }
        }

        // Parse aggregation
        string? aggField = null;
        if (Current.Kind == BqlTokenKind.Identifier && !Current.IsKeyword("by"))
        {
            aggregation = Current.Value.ToLowerInvariant();
            Advance();

            // Function form: count(field)
            if (Current.Kind == BqlTokenKind.OpenParen)
            {
                Advance();
                if (Current.Kind != BqlTokenKind.CloseParen)
                {
                    aggField = Current.Value;
                    Advance();
                }
                Expect(BqlTokenKind.CloseParen, ")");
            }
        }

        // Parse BY clause
        if (Current.IsKeyword("by"))
        {
            Advance();
            byField = ExpectIdentifier();
        }

        return new TimechartStage(span, aggregation, byField, aggField);
    }

    private LookupStage ParseLookupStage()
    {
        // lookup <source> <key_field> [OUTPUT <field1>, <field2>, ...]
        var source = ExpectIdentifier();
        var keyField = ExpectIdentifier();

        var outputFields = new List<string>();
        if (Current.IsKeyword("output"))
        {
            Advance(); // skip "output"
            outputFields.Add(ExpectIdentifier());
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                outputFields.Add(ExpectIdentifier());
            }
        }

        return new LookupStage
        {
            LookupSource = source,
            KeyField = keyField,
            OutputFields = outputFields
        };
    }

    /// <summary>
    /// Reads a bracketed subquery, starting at its "[", and returns its text exactly as written:
    /// re-joining tokens would change spacing (host=l4d2-dev) and lose escaped quotes.
    /// </summary>
    private string ReadBracketed(bool skipSearchKeyword = false)
    {
        Advance(); // skip "["
        if (skipSearchKeyword && Current.IsAnyKeyword("search", "find"))
        {
            Advance();
        }

        var start = Current.Kind == BqlTokenKind.Eof ? -1 : Current.Position;
        var depth = 1;
        while (Current.Kind != BqlTokenKind.Eof)
        {
            if (Current.Kind == BqlTokenKind.OpenBracket)
            {
                depth++;
            }
            else if (Current.Kind == BqlTokenKind.CloseBracket && --depth == 0)
            {
                var text = SourceText(start, Current);
                Advance(); // skip "]"
                return text;
            }

            Advance();
        }

        throw new QueryParseException("Missing ']' to close the subsearch");
    }

    /// <summary>Reads the rest of the stage, up to the next pipe, exactly as written.</summary>
    private string ReadUntilPipe()
    {
        var start = Current.Kind == BqlTokenKind.Eof ? -1 : Current.Position;
        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            Advance();
        }

        return SourceText(start, Current);
    }

    private string SourceText(int start, BqlToken end)
    {
        var stop = end.Kind == BqlTokenKind.Eof || end.Position < 0 || end.Position > _source.Length ? _source.Length : end.Position;
        return start < 0 || stop <= start ? string.Empty : _source[start..stop].Trim();
    }

    private AppendStage ParseAppendStage()
    {
        // append [<subsearch>], or the rest of the stage without brackets
        var text = Current.Kind == BqlTokenKind.OpenBracket ? ReadBracketed() : ReadUntilPipe();
        return new AppendStage { SubsearchQuery = text };
    }

    private JoinStage ParseJoinStage()
    {
        // join [type=<inner|outer|left>] <field> [<subsearch>]
        var joinType = "inner";

        // Check for type= parameter
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("type", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); // skip "type"
            Advance(); // skip "="
            joinType = ExpectIdentifier().ToLowerInvariant();
        }

        var joinField = ExpectIdentifier();

        var subsearch = Current.Kind == BqlTokenKind.OpenBracket ? ReadBracketed() : ReadUntilPipe();

        return new JoinStage
        {
            JoinField = joinField,
            JoinType = joinType,
            SubsearchQuery = subsearch
        };
    }

    // ─── MACRO stage ──────────────────────────────────────────

    private MacroStage ParseMacroStage()
    {
        // macro <name> [arg1, arg2, ...]
        var macroName = ExpectIdentifier();
        var arguments = new List<string>();

        // Parse optional arguments (comma-separated values or identifiers)
        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            string argValue;
            if (Current.Kind == BqlTokenKind.StringLiteral)
            {
                argValue = Current.Value;
                Advance();
            }
            else if (Current.Kind == BqlTokenKind.NumberLiteral)
            {
                argValue = Current.Value;
                Advance();
            }
            else if (Current.Kind == BqlTokenKind.Identifier)
            {
                argValue = Current.Value;
                Advance();
            }
            else
            {
                break;
            }

            arguments.Add(argValue);

            // Skip optional comma separator between arguments
            if (Current.Kind == BqlTokenKind.Comma)
                Advance();
        }

        return new MacroStage(macroName, arguments);
    }

    // ─── SUBSEARCH stage ──────────────────────────────────────

    private SubsearchStage ParseSubsearchStage()
    {
        // subsearch [<inner_query>]
        if (Current.Kind != BqlTokenKind.OpenBracket)
            throw new QueryParseException("Expected '[' after 'subsearch'", $"position {Current.Position}");

        var text = ReadBracketed();
        if (string.IsNullOrWhiteSpace(text))
            throw new QueryParseException("Subsearch query cannot be empty");

        return new SubsearchStage(text);
    }

    // ─── MULTISEARCH stage ──────────────────────────────────────

    private MultisearchStage ParseMultisearchStage()
    {
        // multisearch [search "q1"] [search "q2"] [search "q3"]
        // Each sub-search is enclosed in brackets and prefixed with "search"
        var searchQueries = new List<string>();

        while (Current.Kind == BqlTokenKind.OpenBracket)
        {
            var query = ReadBracketed(skipSearchKeyword: true);
            if (!string.IsNullOrWhiteSpace(query))
            {
                searchQueries.Add(query);
            }
        }

        if (searchQueries.Count == 0)
            throw new QueryParseException("multisearch requires at least one [search \"query\"] clause", $"position {Current.Position}");

        return new MultisearchStage(searchQueries);
    }

    private SearchStage ParseSearchStageInPipeline()
    {
        var sb = new StringBuilder();
        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe)
        {
            AppendSearchToken(sb);
        }
        return new SearchStage(sb.Length > 0 ? sb.ToString() : "*");
    }

    private EvalStage ParseFillStage()
    {
        // fill field with value → eval field = coalesce(field, value)
        var field = ExpectIdentifier();
        ExpectKeyword("with");
        var value = ParsePrimary();
        return new EvalStage(field, new FunctionExpression("coalesce", new List<BqlExpression>
        {
            new FieldExpression(field),
            value
        }));
    }

    private EvalStage ParseExpandStage()
    {
        // expand field → placeholder (expand is not easily representable, use eval identity)
        var field = ExpectIdentifier();
        return new EvalStage(field, new FieldExpression(field));
    }

    // ─── Splunk parity stages ────────────────────────────────

    private FillNullStage ParseFillNullStage()
    {
        // fillnull [value=X] [field1, field2, ...]
        var stage = new FillNullStage();

        // Check for value= parameter
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("value", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); // skip "value"
            Advance(); // skip "="
            stage.DefaultValue = Current.Kind == BqlTokenKind.StringLiteral || Current.Kind == BqlTokenKind.NumberLiteral || Current.Kind == BqlTokenKind.Identifier
                ? Current.Value : "0";
            Advance();
        }

        // Parse optional field list
        while (Current.Kind == BqlTokenKind.Identifier && !IsStageKeyword(Current))
        {
            stage.Fields.Add(Current.Value);
            Advance();
            if (Current.Kind == BqlTokenKind.Comma) Advance();
        }

        return stage;
    }

    private BucketStage ParseBucketStage()
    {
        // bucket/bin field [span=X] [as alias]
        var stage = new BucketStage { Field = ExpectIdentifier() };

        // Parse optional parameters
        while (Current.Kind == BqlTokenKind.Identifier && !IsStageKeyword(Current))
        {
            if (Current.Value.Equals("span", StringComparison.OrdinalIgnoreCase) && Peek(1).Kind == BqlTokenKind.Equals)
            {
                Advance(); Advance(); // skip "span" and "="
                var spanStr = Current.Value;
                Advance();
                // Try time span (1h, 30m, 1d, etc.)
                stage.Span = ParseTimeSpanValue(spanStr);
                if (!stage.Span.HasValue && double.TryParse(spanStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var numSpan))
                    stage.NumericSpan = numSpan;
            }
            else if (Current.IsKeyword("as"))
            {
                Advance();
                stage.Alias = ExpectIdentifier();
            }
            else break;
        }

        return stage;
    }

    private MakeResultsStage ParseMakeResultsStage()
    {
        // makeresults [count=N]
        var stage = new MakeResultsStage();
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("count", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); Advance(); // skip "count" and "="
            if (int.TryParse(Current.Value, out var count))
            {
                stage.Count = Math.Min(count, 50000); // cap at 50K
                Advance();
            }
        }
        else if (Current.Kind == BqlTokenKind.NumberLiteral && int.TryParse(Current.Value, out var directCount))
        {
            stage.Count = Math.Min(directCount, 50000);
            Advance();
        }
        return stage;
    }

    private MvExpandStage ParseMvExpandStage()
    {
        // mvexpand field [limit=N]
        var stage = new MvExpandStage { Field = ExpectIdentifier() };
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("limit", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); Advance();
            if (int.TryParse(Current.Value, out var limit))
            {
                stage.Limit = limit;
                Advance();
            }
        }
        return stage;
    }

    private ChartStage ParseChartStage()
    {
        // chart agg(field) [OVER field1] [BY field2]
        var stage = new ChartStage();
        // Parse aggregation(s)
        while (Current.Kind != BqlTokenKind.Eof && Current.Kind != BqlTokenKind.Pipe &&
               !Current.IsAnyKeyword("over", "by"))
        {
            var aggName = Current.Value.ToLowerInvariant();
            Advance();
            string? aggField = null;
            string? alias = null;
            if (Current.Kind == BqlTokenKind.OpenParen)
            {
                Advance();
                if (Current.Kind != BqlTokenKind.CloseParen) { aggField = Current.Value; Advance(); }
                Expect(BqlTokenKind.CloseParen, ")");
            }
            if (Current.IsKeyword("as")) { Advance(); alias = ExpectIdentifier(); }
            var agg = new AggregateFunction(aggName, aggField) { Alias = alias };
            stage.Aggregations.Add(agg);
            if (Current.Kind == BqlTokenKind.Comma) Advance();
        }
        if (Current.IsKeyword("over")) { Advance(); stage.OverField = ExpectIdentifier(); }
        if (Current.IsKeyword("by")) { Advance(); stage.ByField = ExpectIdentifier(); }
        return stage;
    }

    private FillDownStage ParseFillDownStage()
    {
        // filldown field1, field2, ...
        var stage = new FillDownStage();
        if (Current.Kind == BqlTokenKind.Identifier && !IsStageKeyword(Current))
        {
            stage.Fields.Add(Current.Value);
            Advance();
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                stage.Fields.Add(ExpectIdentifier());
            }
        }
        return stage;
    }

    private AddColTotalsStage ParseAddColTotalsStage()
    {
        // addcoltotals [fieldname=label] [field1, field2, ...]
        var stage = new AddColTotalsStage();
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("fieldname", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); Advance();
            stage.LabelField = Current.Value;
            Advance();
        }
        while (Current.Kind == BqlTokenKind.Identifier && !IsStageKeyword(Current))
        {
            stage.Fields.Add(Current.Value);
            Advance();
            if (Current.Kind == BqlTokenKind.Comma) Advance();
        }
        return stage;
    }

    private InputLookupStage ParseInputLookupStage()
    {
        // inputlookup <lookup_name>
        return new InputLookupStage { LookupName = ExpectIdentifier() };
    }

    private RareStage ParseRareStage()
    {
        // rare [N] field — inverse of top
        var count = 10;
        if (Current.Kind == BqlTokenKind.NumberLiteral)
            count = ExpectNumber();
        var field = ExpectIdentifier();
        return new RareStage { Count = count, Field = field };
    }

    private RegexStage ParseRegexStage()
    {
        // regex "pattern" | regex host="pattern" | regex host!="pattern" | regex field=host "pattern"
        var stage = new RegexStage();

        if (Current.Kind == BqlTokenKind.Identifier && Peek(1).Kind is BqlTokenKind.Equals or BqlTokenKind.NotEquals)
        {
            stage.Field = Current.Value;
            stage.Negate = Peek(1).Kind == BqlTokenKind.NotEquals;
            Advance();
            Advance();

            if (stage.Field.Equals("field", StringComparison.OrdinalIgnoreCase)
                && Current.Kind == BqlTokenKind.Identifier
                && Peek(1).Kind == BqlTokenKind.StringLiteral)
            {
                stage.Field = Current.Value;
                Advance();
            }
        }

        if (Current.Kind is not (BqlTokenKind.StringLiteral or BqlTokenKind.Identifier))
            throw new QueryParseException("Expected a pattern after regex", $"position {Current.Position}");

        stage.Pattern = Current.Value;
        Advance();
        return stage;
    }

    private FieldSummaryStage ParseFieldSummaryStage()
    {
        // fieldsummary [maxvals=N]
        var stage = new FieldSummaryStage();
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("maxvals", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); Advance();
            if (int.TryParse(Current.Value, out var maxVals))
            {
                stage.MaxValues = maxVals;
                Advance();
            }
        }
        return stage;
    }

    private XyseriesStage ParseXyseriesStage()
    {
        // xyseries row_field column_field value_field
        var row = ExpectIdentifier();
        var col = ExpectIdentifier();
        var val = ExpectIdentifier();
        return new XyseriesStage { RowField = row, ColumnField = col, ValueField = val };
    }

    private UntableStage ParseUntableStage()
    {
        // untable row_field column_field value_field
        var row = ExpectIdentifier();
        var col = ExpectIdentifier();
        var val = ExpectIdentifier();
        return new UntableStage { RowField = row, ColumnField = col, ValueField = val };
    }

    private EvalStage ParseConvertStage()
    {
        // convert ctime(field) [as alias], dur2sec(field) [as alias]
        // Translates to eval assignments using expression functions
        var assignments = new List<EvalAssignment>();
        do
        {
            if (Current.Kind == BqlTokenKind.Comma) Advance();
            var funcName = ExpectIdentifier();
            Expect(BqlTokenKind.OpenParen, "(");
            var fieldExpr = ParseOrExpression();
            Expect(BqlTokenKind.CloseParen, ")");
            var alias = fieldExpr is FieldExpression fe ? fe.Name : funcName + "(value)";
            if (Current.IsKeyword("as"))
            {
                Advance();
                alias = ExpectIdentifier();
            }
            assignments.Add(new EvalAssignment(alias, new FunctionExpression(funcName, new List<BqlExpression> { fieldExpr })));
        } while (Current.Kind == BqlTokenKind.Comma);

        return new EvalStage(assignments);
    }

    private static TimeSpan? ParseTimeSpanValue(string value)
    {
        var match = Regex.Match(value, @"^(\d+)(s|m|h|d|w)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!match.Success) return null;
        // Use long.TryParse to prevent overflow on very large values, clamp to reasonable max (10 years)
        if (!long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            return null;
        const long maxDays = 3650; // ~10 years
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "s" => amount > maxDays * 86400 ? null : TimeSpan.FromSeconds(amount),
            "m" => amount > maxDays * 1440 ? null : TimeSpan.FromMinutes(amount),
            "h" => amount > maxDays * 24 ? null : TimeSpan.FromHours(amount),
            "d" => amount > maxDays ? null : TimeSpan.FromDays(amount),
            "w" => amount > maxDays / 7 ? null : TimeSpan.FromDays(amount * 7),
            _ => null
        };
    }

    private FieldAliasStage ParseFieldAliasStage()
    {
        // fieldalias alias1=original1, alias2=original2
        var aliases = new Dictionary<string, string>();

        var aliasName = ExpectIdentifier();
        Expect(BqlTokenKind.Equals, "=");
        var originalField = ExpectIdentifier();
        aliases[aliasName] = originalField;

        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            aliasName = ExpectIdentifier();
            Expect(BqlTokenKind.Equals, "=");
            originalField = ExpectIdentifier();
            aliases[aliasName] = originalField;
        }

        return new FieldAliasStage(aliases);
    }

    private TransactionStage ParseTransactionStage()
    {
        // transaction field1, field2 [maxspan=Xh] [maxevents=N]
        var fields = new List<string>();
        fields.Add(ExpectIdentifier());

        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectIdentifier());
        }

        TimeSpan? maxSpan = null;
        int? maxEvents = null;
        string? startsWith = null;
        string? endsWith = null;

        // Parse optional parameters
        while (Current.Kind == BqlTokenKind.Identifier && Current.Kind != BqlTokenKind.Pipe && Current.Kind != BqlTokenKind.Eof)
        {
            if (Current.Value.StartsWith("maxspan", StringComparison.OrdinalIgnoreCase))
            {
                Advance(); // skip "maxspan"
                if (Current.Kind == BqlTokenKind.Equals) Advance();
                if (Current.Kind == BqlTokenKind.TimeSpanLiteral)
                {
                    maxSpan = ParseTimeSpanValue(Current.Value);
                    Advance();
                }
                continue;
            }
            if (Current.IsAnyKeyword("startswith", "endswith"))
            {
                var isStart = Current.IsKeyword("startswith");
                Advance();
                if (Current.Kind == BqlTokenKind.Equals) Advance();
                if (Current.Kind != BqlTokenKind.StringLiteral)
                    throw new QueryParseException("Expected a quoted string after startswith or endswith", $"position {Current.Position}");

                if (isStart) startsWith = Current.Value;
                else endsWith = Current.Value;
                Advance();
                continue;
            }
            if (Current.Value.StartsWith("maxevents", StringComparison.OrdinalIgnoreCase))
            {
                Advance(); // skip "maxevents"
                if (Current.Kind == BqlTokenKind.Equals) Advance();
                if (Current.Kind == BqlTokenKind.NumberLiteral)
                {
                    maxEvents = Math.Clamp(ExpectNumber(), 1, 100_000);
                }
                continue;
            }
            break;
        }

        return new TransactionStage(fields, maxSpan, maxEvents) { StartsWith = startsWith, EndsWith = endsWith };
    }

    private EventStatsStage ParseEventStatsStage()
    {
        var stage = new EventStatsStage();

        // Parse aggregation functions (same as stats)
        stage.Aggregations.Add(ParseAggregateFunction());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Aggregations.Add(ParseAggregateFunction());
        }

        // Parse optional BY clause
        if (Current.IsKeyword("by"))
        {
            Advance();
            stage.GroupByFields.Add(ExpectIdentifier());
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                stage.GroupByFields.Add(ExpectIdentifier());
            }
        }

        return stage;
    }

    private StreamStatsStage ParseStreamStatsStage()
    {
        var stage = new StreamStatsStage();

        // Parse optional window=N parameter
        if (Current.Kind == BqlTokenKind.Identifier &&
            Current.Value.Equals("window", StringComparison.OrdinalIgnoreCase) &&
            Peek(1).Kind == BqlTokenKind.Equals)
        {
            Advance(); // skip "window"
            Advance(); // skip "="
            stage.Window = ExpectNumber();
        }

        // Parse aggregation functions (same as stats)
        stage.Aggregations.Add(ParseAggregateFunction());
        while (Current.Kind == BqlTokenKind.Comma)
        {
            Advance();
            stage.Aggregations.Add(ParseAggregateFunction());
        }

        // Parse optional BY clause
        if (Current.IsKeyword("by"))
        {
            Advance();
            stage.GroupByFields.Add(ExpectIdentifier());
            while (Current.Kind == BqlTokenKind.Comma)
            {
                Advance();
                stage.GroupByFields.Add(ExpectIdentifier());
            }
        }

        return stage;
    }

    // ─── Helpers ─────────────────────────────────────────────

    private BqlToken Current => _pos < _tokens.Count ? _tokens[_pos] : new BqlToken(BqlTokenKind.Eof, "", -1);

    private BqlToken Peek(int offset)
    {
        var idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : new BqlToken(BqlTokenKind.Eof, "", -1);
    }

    private void Advance() => _pos++;

    private void Expect(BqlTokenKind kind, string display)
    {
        if (Current.Kind != kind)
            throw new QueryParseException($"Expected '{display}' but got '{Current.Value}'", $"position {Current.Position}");
        Advance();
    }

    private void ExpectKeyword(string keyword)
    {
        if (!Current.IsKeyword(keyword))
            throw new QueryParseException($"Expected '{keyword}' but got '{Current.Value}'", $"position {Current.Position}");
        Advance();
    }

    private string ExpectIdentifier()
    {
        if (Current.Kind != BqlTokenKind.Identifier)
            throw new QueryParseException($"Expected identifier but got '{Current.Value}'", $"position {Current.Position}");
        var name = Current.Value;
        Advance();
        return name;
    }

    private int ExpectNumber()
    {
        if (Current.Kind != BqlTokenKind.NumberLiteral)
            throw new QueryParseException($"Expected number but got '{Current.Value}'", $"position {Current.Position}");
        if (!int.TryParse(Current.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
            throw new QueryParseException($"'{Current.Value}' isn't a whole number between {int.MinValue} and {int.MaxValue}", $"position {Current.Position}");
        Advance();
        return num;
    }

    private DateTime ParseDateTimeLiteral()
    {
        if (Current.Kind == BqlTokenKind.StringLiteral)
        {
            var val = Current.Value;
            Advance();
            if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToUniversalTime();
            throw new QueryParseException($"Invalid date/time: '{val}'", $"position {Current.Position}");
        }
        throw new QueryParseException("Expected date/time string literal", $"position {Current.Position}");
    }


    private static int ParseSeverityValue(string name) => name.ToLowerInvariant() switch
    {
        "trace" => 0,
        "debug" => 1,
        "info" or "information" => 2,
        "warning" or "warn" => 3,
        "error" or "err" => 4,
        "critical" or "crit" => 5,
        "fatal" => 6,
        _ => 2
    };

    /// <summary>
    /// In <c>severity >= warning</c> the name is a severity level. Elsewhere it's a field.
    /// </summary>
    private static BqlExpression SeverityLiteral(BqlExpression left, BqlExpression value) =>
        left is FieldExpression { Name: var name }
        && name.Equals("severity", StringComparison.OrdinalIgnoreCase)
        && value is FieldExpression { Name: var level }
        && IsSeverityName(level)
            ? new LiteralExpression(ParseSeverityValue(level))
            : value;

    private static bool IsSeverityName(string name) =>
        name.ToLowerInvariant() is "trace" or "debug" or "info" or "information"
            or "warning" or "warn" or "error" or "err" or "critical" or "crit" or "fatal";

    private static bool IsComparisonOperator(BqlTokenKind kind) =>
        kind is BqlTokenKind.Equals or BqlTokenKind.NotEquals
            or BqlTokenKind.Greater or BqlTokenKind.GreaterEquals
            or BqlTokenKind.Less or BqlTokenKind.LessEquals;

    private static bool IsStageKeyword(BqlToken token) =>
        token.IsAnyKeyword(
            "where", "stats", "group", "sort", "head", "tail", "top",
            "limit", "compute", "eval", "extract", "rex", "rename",
            "select", "table", "fields", "dedup", "unique", "timechart",
            "fill", "expand", "fieldalias", "transaction",
            "lookup", "append", "join", "eventstats", "streamstats",
            "fillnull", "bucket", "bin", "makeresults", "mvexpand",
            "chart", "filldown", "addcoltotals", "inputlookup",
            "rare", "regex", "fieldsummary", "xyseries", "untable", "convert",
            "macro", "subsearch", "multisearch");

    // ─── Legacy QueryPlan conversion ─────────────────────────

    private static QueryPlan ConvertToLegacyPlan(BqlQuery query)
    {
        var plan = new QueryPlan();
        foreach (var stage in query.Stages)
        {
            var op = ConvertStageToOperator(stage);
            if (op != null)
                plan.Operators.Add(op);
        }
        return plan;
    }

    private static QueryOperator? ConvertStageToOperator(BqlStage stage) => stage switch
    {
        SearchStage s => new LegacySearchOperator(s.SearchText),
        WhereStage w => new LegacyWhereOperator(w.Condition),
        StatsStage st => new LegacyStatsOperator(st.Aggregations, st.GroupByFields),
        SortStage so => new LegacySortOperator(so.Fields),
        LimitStage l => new LegacyLimitOperator(l.Count),
        TailStage t => new LegacyTailOperator(t.Count),
        DedupStage d => new LegacyDedupOperator(d.Fields),
        TopStage tp => new LegacyTopOperator(tp.Count, tp.Field, tp.ByField),
        TableStage tb => new LegacyTableOperator(tb.Fields),
        EvalStage e => new LegacyEvalOperator(e.Assignments),
        RexStage r => new LegacyRexOperator(r.Pattern, r.SourceField),
        RenameStage rn => new LegacyRenameOperator(rn.Mappings),
        TimechartStage tc => new LegacyTimechartOperator(tc.Span, tc.Aggregation, tc.ByField, tc.AggregationField),
        FieldAliasStage fa => new LegacyFieldAliasOperator(fa.Aliases),
        TransactionStage tx => new LegacyTransactionOperator(tx.Fields, tx.MaxSpan, tx.MaxEvents, tx.StartsWith, tx.EndsWith),
        EventStatsStage es => new LegacyEventStatsOperator(es.Aggregations, es.GroupByFields),
        StreamStatsStage ss => new LegacyStreamStatsOperator(ss.Aggregations, ss.GroupByFields, ss.Window),
        FillNullStage fn => new LegacyFillNullOperator(fn.DefaultValue, fn.Fields),
        BucketStage bk => new LegacyBucketOperator(bk.Field, bk.Span, bk.NumericSpan, bk.Alias),
        MakeResultsStage mr => new LegacyMakeResultsOperator(mr.Count),
        MvExpandStage mv => new LegacyMvExpandOperator(mv.Field, mv.Limit),
        ChartStage ch => new LegacyChartOperator(ch.Aggregations, ch.OverField, ch.ByField),
        FillDownStage fd => new LegacyFillDownOperator(fd.Fields),
        AddColTotalsStage ac => new LegacyAddColTotalsOperator(ac.LabelField, ac.Fields),
        InputLookupStage => new LegacyPassthroughOperator("inputlookup"),
        LookupStage => new LegacyPassthroughOperator("lookup"),
        AppendStage => new LegacyPassthroughOperator("append"),
        JoinStage => new LegacyPassthroughOperator("join"),
        MacroStage => new LegacyPassthroughOperator("macro"),
        SubsearchStage => new LegacyPassthroughOperator("subsearch"),
        MultisearchStage => new LegacyPassthroughOperator("multisearch"),
        RareStage r => new LegacyRareOperator(r.Count, r.Field),
        RegexStage rx => new LegacyRegexOperator(rx.Field, rx.Pattern, rx.Negate),
        FieldSummaryStage fs => new LegacyFieldSummaryOperator(fs.MaxValues),
        XyseriesStage xy => new LegacyXyseriesOperator(xy.RowField, xy.ColumnField, xy.ValueField),
        UntableStage ut => new LegacyUntableOperator(ut.RowField, ut.ColumnField, ut.ValueField),
        _ => null
    };
}

// ─── Legacy operator wrappers ────────────────────────────────
// These allow the BQL parser to integrate with the existing QueryEngine

internal sealed class LegacySearchOperator : QueryOperator
{
    private readonly BqlTextSearch _search;
    public override string Name => "search";

    public LegacySearchOperator(string pattern) { _search = BqlTextSearch.Parse(pattern); }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default) =>
        Task.FromResult<IEnumerable<dynamic>>(((IEnumerable<object>)input).Where(evt => _search.Matches((IDictionary<string, object?>)evt)));
}

internal sealed class LegacyWhereOperator : QueryOperator
{
    private readonly BqlExpression _condition;
    public override string Name => "where";

    public LegacyWhereOperator(BqlExpression condition) { _condition = condition; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var rows = (IEnumerable<object>)input;
        return Task.FromResult<IEnumerable<dynamic>>(
            rows.Where(row => BqlExpressionEvaluator.EvaluateBool(_condition, (IDictionary<string, object?>)row)));
    }
}

internal sealed class LegacyStatsOperator : QueryOperator
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly List<string> _groupByFields;
    public override string Name => "stats";

    public LegacyStatsOperator(List<AggregateFunction> aggs, List<string> groupBy)
    {
        _aggregations = aggs;
        _groupByFields = groupBy;
    }

    internal List<AggregateFunction> Aggregations => _aggregations;

    internal List<string> GroupByFields => _groupByFields;

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = new StatsSink(_aggregations, _groupByFields, Budget ?? new QueryBudget(new QueryLimits()));
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    internal static object? ComputeAggregate(AggregateFunction agg, List<dynamic> events)
    {
        var accumulator = new AggregateAccumulator(agg, new QueryBudget(new QueryLimits()));
        foreach (var row in events)
        {
            accumulator.Add((IDictionary<string, object?>)row);
        }

        return accumulator.Result();
    }

    internal static double? GetNumericValue(IDictionary<string, object?> dict, string field)
    {
        if (!dict.TryGetValue(field, out var val) || val == null) return null;
        if (val is double d) return d;
        if (val is long l) return l;
        if (val is int i) return i;
        // Float, not Any: "1,000" and "(5)" aren't numbers, which is also what storage aggregates assume
        if (double.TryParse(val.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }
}

internal sealed class LegacySortOperator : QueryOperator
{
    private readonly List<SortField> _fields;
    public override string Name => "sort";

    public LegacySortOperator(List<SortField> fields) { _fields = fields; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default) =>
        Task.FromResult(Order(input));

    /// <summary>Orders rows. Rows that compare equal keep their order.</summary>
    internal IEnumerable<dynamic> Order(IEnumerable<dynamic> input)
    {
        if (_fields.Count == 0)
        {
            return input;
        }

        var first = _fields[0];
        var ordered = first.Descending
            ? input.OrderByDescending<dynamic, object?>(e => GetSortValue(e, first.Field), BqlValueComparer.Instance)
            : input.OrderBy<dynamic, object?>(e => GetSortValue(e, first.Field), BqlValueComparer.Instance);

        for (var i = 1; i < _fields.Count; i++)
        {
            var f = _fields[i];
            ordered = f.Descending
                ? ordered.ThenByDescending<dynamic, object?>(e => GetSortValue(e, f.Field), BqlValueComparer.Instance)
                : ordered.ThenBy<dynamic, object?>(e => GetSortValue(e, f.Field), BqlValueComparer.Instance);
        }

        return ordered;
    }

    private static object? GetSortValue(object evt, string field)
    {
        var dict = (IDictionary<string, object?>)evt;
        return dict.TryGetValue(field, out var v) ? v : null;
    }
}

internal sealed class LegacyLimitOperator : QueryOperator
{
    private readonly int _count;
    public override string Name => "head";

    public LegacyLimitOperator(int count) { _count = count; }

    internal int Count => _count;

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default) =>
        Task.FromResult(input.Take(_count));
}

internal sealed class LegacyTailOperator : QueryOperator
{
    private readonly int _count;
    public override string Name => "tail";

    public LegacyTailOperator(int count) { _count = count; }

    internal int Count => _count;

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var list = input.ToList();
        return Task.FromResult<IEnumerable<dynamic>>(list.Skip(Math.Max(0, list.Count - _count)));
    }
}

internal sealed class LegacyDedupOperator : QueryOperator
{
    private readonly List<string> _fields;
    public override string Name => "dedup";

    public LegacyDedupOperator(List<string> fields) { _fields = fields; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = input.Where(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            var key = BqlGroupKey.For(dict, _fields);
            return seen.Add(key);
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyTopOperator : QueryOperator
{
    private readonly int _count;
    private readonly string _field;
    private readonly string? _byField;
    public override string Name => "top";

    public LegacyTopOperator(int count, string field, string? byField = null)
    {
        _count = count;
        _field = field;
        _byField = byField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = CreateSink(Budget ?? new QueryBudget(new QueryLimits()));
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    internal IRowSink CreateSink(QueryBudget budget) => new ValueCountSink(_field, _byField, budget, Rank);

    private IEnumerable<dynamic> Rank(IEnumerable<KeyValuePair<string, long>> counts, string? group)
    {
        foreach (var (value, count) in counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(_count))
        {
            var row = new BqlRow();
            if (_byField != null)
            {
                row[_byField] = group;
            }

            row[_field] = value;
            row["count"] = AggregateAccumulator.CountValue(count);
            yield return row;
        }
    }
}

internal sealed class LegacyTableOperator : QueryOperator
{
    private readonly List<string> _fields;
    public override string Name => "table";

    public LegacyTableOperator(List<string> fields) { _fields = fields; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            var row = new BqlRow();
            foreach (var field in _fields)
            {
                if (dict.TryGetValue(field, out var val))
                    row[field] = val;
            }
            return (dynamic)row;
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyEvalOperator : QueryOperator
{
    private readonly List<EvalAssignment> _assignments;
    public override string Name => "eval";

    public LegacyEvalOperator(List<EvalAssignment> assignments)
    {
        _assignments = assignments;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var assignment in _assignments)
            {
                dict[assignment.FieldName] = BqlExpressionEvaluator.Evaluate(assignment.Expression, dict);
            }
            return evt;
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyRexOperator : QueryOperator
{
    private readonly string _pattern;
    private readonly string _sourceField;
    private Regex? _regex;
    private RegexTimeoutTracker? _timeouts;
    public override string Name => "rex";

    public LegacyRexOperator(string pattern, string sourceField)
    {
        _pattern = pattern;
        _sourceField = sourceField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        // Runs for each batch of a query, so compile once. The timeout guards against ReDoS.
        _regex ??= new Regex(_pattern, RegexOptions.Compiled, RegexTimeoutTracker.PerEvent);
        _timeouts ??= new RegexTimeoutTracker(_pattern);
        var regex = _regex;
        var timeouts = _timeouts;

        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            var source = dict.TryGetValue(_sourceField, out var v) ? v?.ToString() : null;
            if (source != null)
            {
                try
                {
                    var match = regex.Match(source);
                    if (match.Success)
                    {
                        foreach (var groupName in regex.GetGroupNames())
                        {
                            if (!int.TryParse(groupName, out _))
                                dict[groupName] = match.Groups[groupName].Value;
                        }
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    timeouts.Record();
                }
            }
            return evt;
        });
        return Task.FromResult<IEnumerable<dynamic>>(result);
    }
}

internal sealed class LegacyRenameOperator : QueryOperator
{
    private readonly List<RenameMapping> _mappings;
    public override string Name => "rename";

    public LegacyRenameOperator(List<RenameMapping> mappings)
    {
        _mappings = mappings;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var mapping in _mappings)
            {
                if (dict.TryGetValue(mapping.OldName, out var val))
                {
                    dict[mapping.NewName] = val;
                    dict.Remove(mapping.OldName);
                }
            }
            return evt;
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyTimechartOperator : QueryOperator
{
    private readonly TimeSpan _span;
    private readonly string? _byField;
    private readonly AggregateFunction _function;
    public override string Name => "timechart";

    public LegacyTimechartOperator(TimeSpan span, string aggregation, string? byField, string? aggField)
    {
        _span = span;
        _function = new AggregateFunction(aggregation, aggField);
        _byField = byField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = CreateSink(Budget ?? new QueryBudget(new QueryLimits()));
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    internal IRowSink CreateSink(QueryBudget budget) => new TimechartSink(_span, _function, _byField, budget);
}

internal sealed class LegacyFieldAliasOperator : QueryOperator
{
    private readonly Dictionary<string, string> _aliases;
    public override string Name => "fieldalias";

    public LegacyFieldAliasOperator(Dictionary<string, string> aliases) { _aliases = aliases; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var result = input.Select(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var alias in _aliases)
            {
                if (dict.TryGetValue(alias.Value, out var val))
                    dict[alias.Key] = val;
            }
            return evt;
        });
        return Task.FromResult(result);
    }
}

internal sealed class LegacyTransactionOperator : QueryOperator
{
    private readonly List<string> _fields;
    private readonly TimeSpan? _maxSpan;
    private readonly int? _maxEvents;
    private readonly string? _startsWith;
    private readonly string? _endsWith;
    public override string Name => "transaction";

    public LegacyTransactionOperator(List<string> fields, TimeSpan? maxSpan, int? maxEvents, string? startsWith = null, string? endsWith = null)
    {
        _startsWith = startsWith;
        _endsWith = endsWith;
        _fields = fields;
        _maxSpan = maxSpan;
        _maxEvents = maxEvents;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        var results = new List<dynamic>();

        // Group events by the transaction fields
        var groups = events.GroupBy(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            return BqlGroupKey.For(dict, _fields);
        });

        foreach (var group in groups)
        {
            var orderedEvents = group
                .Select(e =>
                {
                    var dict = (IDictionary<string, object?>)e;
                    var ts = dict.TryGetValue("Timestamp", out var v) && v is DateTime dt ? dt : DateTime.MinValue;
                    return (Event: e, Timestamp: ts);
                })
                .OrderBy(x => x.Timestamp)
                .ToList();

            // Split into transactions based on maxSpan and maxEvents
            var txn = new List<(dynamic Event, DateTime Timestamp)>();

            foreach (var item in orderedEvents)
            {
                // With a start marker, events outside a transaction are dropped, and a new
                // start marker begins a new transaction.
                if (_startsWith != null)
                {
                    var isStart = RawLineContains(item.Event, _startsWith);
                    if (txn.Count == 0 && !isStart)
                        continue;
                    if (txn.Count > 0 && isStart)
                    {
                        if (_endsWith == null)
                            results.Add(BuildTransaction(txn));
                        txn.Clear();
                    }
                }

                var shouldSplit = false;

                if (txn.Count > 0)
                {
                    if (_maxSpan.HasValue && item.Timestamp - txn[0].Timestamp > _maxSpan.Value)
                        shouldSplit = true;
                    if (_maxEvents.HasValue && txn.Count >= _maxEvents.Value)
                        shouldSplit = true;
                }

                if (shouldSplit)
                {
                    // A transaction that never reached its end marker is incomplete
                    if (_endsWith == null)
                        results.Add(BuildTransaction(txn));
                    txn.Clear();
                    if (_startsWith != null && !RawLineContains(item.Event, _startsWith))
                        continue;
                }

                txn.Add(item);

                if (_endsWith != null && RawLineContains(item.Event, _endsWith))
                {
                    results.Add(BuildTransaction(txn));
                    txn.Clear();
                }
            }

            if (txn.Count > 0 && _endsWith == null)
                results.Add(BuildTransaction(txn));
        }

        return Task.FromResult<IEnumerable<dynamic>>(results);
    }

    private static bool RawLineContains(dynamic evt, string text)
    {
        var dict = (IDictionary<string, object?>)evt;
        return dict.TryGetValue("RawLine", out var raw)
            && raw?.ToString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
    }

    private dynamic BuildTransaction(List<(dynamic Event, DateTime Timestamp)> txnEvents)
    {
        var row = new BqlRow();
        var firstDict = (IDictionary<string, object?>)txnEvents[0].Event;

        // Copy fields from first event
        foreach (var kvp in firstDict)
            row[kvp.Key] = kvp.Value;

        // Add transaction metadata
        row["_eventcount"] = txnEvents.Count;
        if (txnEvents.Count > 1)
        {
            row["_duration"] = (txnEvents[^1].Timestamp - txnEvents[0].Timestamp).TotalSeconds;
        }
        else
        {
            row["_duration"] = 0.0;
        }

        // Concatenate raw lines
        var rawLines = txnEvents
            .Select(e => ((IDictionary<string, object?>)e.Event).TryGetValue("RawLine", out var v) ? v?.ToString() : null)
            .Where(l => l != null);
        row["_raw"] = string.Join("\n", rawLines);

        return row;
    }
}

/// <summary>
/// EventStats: compute aggregations per group and append results to all original events.
/// Unlike stats, eventstats is non-reducing — all original events are preserved.
/// </summary>
internal sealed class LegacyEventStatsOperator : QueryOperator
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly List<string> _groupByFields;
    public override string Name => "eventstats";

    public LegacyEventStatsOperator(List<AggregateFunction> aggs, List<string> groupBy)
    {
        _aggregations = aggs;
        _groupByFields = groupBy;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();

        if (_groupByFields.Count == 0)
        {
            // Global aggregation — compute once, append to all events
            var aggValues = new Dictionary<string, object?>();
            foreach (var agg in _aggregations)
            {
                var key = agg.OutputName;
                aggValues[key] = LegacyStatsOperator.ComputeAggregate(agg, events);
            }
            foreach (var evt in events)
            {
                var dict = (IDictionary<string, object?>)evt;
                foreach (var kvp in aggValues)
                    dict[kvp.Key] = kvp.Value;
            }
        }
        else
        {
            // Group and compute per-group, then append to each event in that group
            var groups = events.GroupBy(evt =>
            {
                var dict = (IDictionary<string, object?>)evt;
                return BqlGroupKey.For(dict, _groupByFields);
            });

            foreach (var group in groups)
            {
                var groupEvents = group.ToList();
                var aggValues = new Dictionary<string, object?>();
                foreach (var agg in _aggregations)
                {
                    var key = agg.OutputName;
                    aggValues[key] = LegacyStatsOperator.ComputeAggregate(agg, groupEvents);
                }
                foreach (var evt in groupEvents)
                {
                    var dict = (IDictionary<string, object?>)evt;
                    foreach (var kvp in aggValues)
                        dict[kvp.Key] = kvp.Value;
                }
            }
        }

        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

/// <summary>
/// StreamStats: compute running/cumulative aggregations as events stream through.
/// Supports optional window parameter to limit the number of preceding events considered.
/// </summary>
internal sealed class LegacyStreamStatsOperator : QueryOperator
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly List<string> _groupByFields;
    private readonly int _window;
    public override string Name => "streamstats";

    public LegacyStreamStatsOperator(List<AggregateFunction> aggs, List<string> groupBy, int window)
    {
        _aggregations = aggs;
        _groupByFields = groupBy;
        _window = window;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        if (_window <= 0)
        {
            // Without a window, running totals: each row costs one update, not a pass over all before it
            var budget = Budget ?? new QueryBudget(new QueryLimits());
            var running = new Dictionary<string, AggregateAccumulator[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var evt in events)
            {
                var dict = (IDictionary<string, object?>)evt;
                var groupKey = _groupByFields.Count > 0 ? BqlGroupKey.For(dict, _groupByFields) : "";
                if (!running.TryGetValue(groupKey, out var accumulators))
                {
                    accumulators = _aggregations.Select(agg => new AggregateAccumulator(agg, budget)).ToArray();
                    running[groupKey] = accumulators;
                }

                foreach (var accumulator in accumulators)
                {
                    accumulator.Add(dict);
                }

                for (var i = 0; i < _aggregations.Count; i++)
                {
                    dict[_aggregations[i].OutputName] = accumulators[i].Result();
                }
            }

            return Task.FromResult<IEnumerable<dynamic>>(events);
        }

        // Track running windows per group (or global if no group-by)
        var groupWindows = new Dictionary<string, List<dynamic>>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var groupKey = _groupByFields.Count > 0
                ? BqlGroupKey.For(dict, _groupByFields)
                : "";

            if (!groupWindows.TryGetValue(groupKey, out var window))
            {
                window = new List<dynamic>();
                groupWindows[groupKey] = window;
            }

            window.Add(evt);

            // Keep window bounded to avoid unbounded memory growth.
            // Trim excess entries in-place — zero allocation vs original Skip().ToList().
            if (_window > 0 && window.Count > _window)
            {
                window.RemoveRange(0, window.Count - _window);
            }

            // Compute running aggregations over the (now bounded) window
            foreach (var agg in _aggregations)
            {
                var key = agg.OutputName;
                dict[key] = LegacyStatsOperator.ComputeAggregate(agg, window);
            }
        }

        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

/// <summary>
/// Passthrough operator for stages that are recognized but have no real execution logic yet.
/// Returns the input events unchanged without throwing an exception.
/// </summary>
internal sealed class LegacyPassthroughOperator : QueryOperator
{
    private readonly string _stageName;
    public override string Name => _stageName;

    public LegacyPassthroughOperator(string stageName) { _stageName = stageName; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        return Task.FromResult(input);
    }
}

/// <summary>
/// Stub operator for stages that are recognized by the parser but not yet implemented.
/// Throws NotSupportedException at execution time.
/// </summary>
internal sealed class LegacyNotImplementedOperator : QueryOperator
{
    private readonly string _stageName;
    public override string Name => _stageName;

    public LegacyNotImplementedOperator(string stageName) { _stageName = stageName; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        throw new NotSupportedException($"The '{_stageName}' stage is not yet implemented.");
    }
}

// ─── Splunk parity operators ─────────────────────────────────

internal sealed class LegacyFillNullOperator : QueryOperator
{
    private readonly string _defaultValue;
    private readonly List<string> _fields;
    public override string Name => "fillnull";

    public LegacyFillNullOperator(string defaultValue, List<string> fields)
    {
        _defaultValue = defaultValue;
        _fields = fields;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            if (_fields.Count > 0)
            {
                foreach (var field in _fields)
                {
                    if (!dict.ContainsKey(field) || dict[field] == null)
                        dict[field] = _defaultValue;
                }
            }
            else
            {
                // Fill all null fields
                foreach (var key in dict.Keys.ToList())
                {
                    if (dict[key] == null)
                        dict[key] = _defaultValue;
                }
            }
        }
        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

internal sealed class LegacyBucketOperator : QueryOperator
{
    private readonly string _field;
    private readonly TimeSpan? _span;
    private readonly double? _numericSpan;
    private readonly string? _alias;
    public override string Name => "bucket";

    public LegacyBucketOperator(string field, TimeSpan? span, double? numericSpan, string? alias)
    {
        _field = field;
        _span = span;
        _numericSpan = numericSpan;
        _alias = alias;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        var targetField = _alias ?? _field;

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            if (!dict.TryGetValue(_field, out var val) || val == null) continue;

            if (val is DateTime dt && _span.HasValue)
            {
                // Time bucketing: floor to span interval
                var ticks = dt.Ticks - (dt.Ticks % _span.Value.Ticks);
                dict[targetField] = new DateTime(ticks, DateTimeKind.Utc);
            }
            else if (_numericSpan.HasValue)
            {
                // Numeric bucketing: floor to span interval
                if (BqlExpressionEvaluator.ToDoublePublic(val) is { } numVal)
                    dict[targetField] = Math.Floor(numVal / _numericSpan.Value) * _numericSpan.Value;
            }
            else if (_span.HasValue && BqlExpressionEvaluator.ToDoublePublic(val) is { } epochVal)
            {
                // Epoch seconds bucketing
                var spanSeconds = _span.Value.TotalSeconds;
                dict[targetField] = Math.Floor(epochVal / spanSeconds) * spanSeconds;
            }
        }
        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

internal sealed class LegacyMakeResultsOperator : QueryOperator
{
    private readonly int _count;
    public override string Name => "makeresults";

    public LegacyMakeResultsOperator(int count) { _count = count; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        // makeresults generates synthetic events, ignoring input
        var results = new List<dynamic>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < _count; i++)
        {
            var expando = new BqlRow();
            var dict = (IDictionary<string, object?>)expando;
            dict["_time"] = now;
            dict["Timestamp"] = now;
            dict["_raw"] = "";
            dict["RawLine"] = "";
            results.Add(expando);
        }
        return Task.FromResult<IEnumerable<dynamic>>(results);
    }
}

internal sealed class LegacyMvExpandOperator : QueryOperator
{
    private readonly string _field;
    private readonly int _limit;
    public override string Name => "mvexpand";

    public LegacyMvExpandOperator(string field, int limit) { _field = field; _limit = limit; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var results = new List<dynamic>();
        foreach (var evt in input)
        {
            var count = 0;
            var dict = (IDictionary<string, object?>)evt;
            if (!dict.TryGetValue(_field, out var val) || val == null)
            {
                results.Add(evt);
                continue;
            }

            IEnumerable<object?> values;
            if (val is string[] arr) values = arr;
            else if (val is IEnumerable<object?> list) values = list;
            else if (val is string s && s.Contains(',')) values = s.Split(',').Select(v => (object?)v.Trim());
            else { results.Add(evt); continue; }

            foreach (var v in values)
            {
                if (count++ >= _limit) break;
                var newExpando = new BqlRow();
                var newDict = (IDictionary<string, object?>)newExpando;
                foreach (var kvp in dict) newDict[kvp.Key] = kvp.Value;
                newDict[_field] = v;
                results.Add(newExpando);
            }
        }
        return Task.FromResult<IEnumerable<dynamic>>(results);
    }
}

internal sealed class LegacyChartOperator : QueryOperator
{
    private readonly List<AggregateFunction> _aggregations;
    private readonly string? _overField;
    private readonly string? _byField;
    public override string Name => "chart";

    public LegacyChartOperator(List<AggregateFunction> aggs, string? overField, string? byField)
    {
        _aggregations = aggs;
        _overField = overField;
        _byField = byField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = CreateSink(Budget ?? new QueryBudget(new QueryLimits()));
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    /// <summary>One row per <c>over</c> value. With <c>by</c>, one column per <c>by</c> value.</summary>
    internal IRowSink CreateSink(QueryBudget budget)
    {
        if (_overField != null && _byField != null)
        {
            return new ChartSink(_aggregations, _overField, _byField, budget);
        }

        var groupBy = new List<string>();
        if (_overField != null) groupBy.Add(_overField);
        if (_byField != null) groupBy.Add(_byField);
        return new StatsSink(_aggregations, groupBy, budget);
    }
}

internal sealed class LegacyFillDownOperator : QueryOperator
{
    private readonly List<string> _fields;
    public override string Name => "filldown";

    public LegacyFillDownOperator(List<string> fields) { _fields = fields; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        var lastValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var fieldsToFill = _fields.Count > 0 ? _fields : dict.Keys.ToList();

            foreach (var field in fieldsToFill)
            {
                if (dict.TryGetValue(field, out var val) && val != null)
                {
                    lastValues[field] = val;
                }
                else if (lastValues.TryGetValue(field, out var last) && last != null)
                {
                    dict[field] = last;
                }
            }
        }
        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

internal sealed class LegacyAddColTotalsOperator : QueryOperator
{
    private readonly string _labelField;
    private readonly List<string> _fields;
    public override string Name => "addcoltotals";

    public LegacyAddColTotalsOperator(string labelField, List<string> fields)
    {
        _labelField = labelField;
        _fields = fields;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        if (events.Count == 0) return Task.FromResult<IEnumerable<dynamic>>(events);

        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var fieldsToTotal = _fields.Count > 0 ? _fields : ((IDictionary<string, object?>)events[0]).Keys.ToList();

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var field in fieldsToTotal)
            {
                if (dict.TryGetValue(field, out var val) && BqlExpressionEvaluator.ToDoublePublic(val) is { } numVal)
                {
                    totals[field] = totals.GetValueOrDefault(field) + numVal;
                }
            }
        }

        // Append totals row
        var totalRow = new BqlRow();
        var totalDict = (IDictionary<string, object?>)totalRow;
        totalDict[_labelField] = "TOTALS";
        foreach (var kvp in totals) totalDict[kvp.Key] = kvp.Value;
        events.Add(totalRow);

        return Task.FromResult<IEnumerable<dynamic>>(events);
    }
}

// ─── P1-P3 new operators ─────────────────────────────────────

/// <summary>
/// Rare: find least common values of a field (inverse of top).
/// </summary>
internal sealed class LegacyRareOperator : QueryOperator
{
    private readonly int _count;
    private readonly string _field;
    public override string Name => "rare";

    public LegacyRareOperator(int count, string field) { _count = count; _field = field; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var sink = CreateSink(Budget ?? new QueryBudget(new QueryLimits()));
        foreach (object row in input)
        {
            sink.Add(row);
        }

        return Task.FromResult(sink.Complete());
    }

    internal IRowSink CreateSink(QueryBudget budget) => new ValueCountSink(_field, null, budget, (counts, _) => Rank(counts));

    private IEnumerable<dynamic> Rank(IEnumerable<KeyValuePair<string, long>> counts)
    {
        var rarest = counts
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(_count)
            .ToList();

        var total = rarest.Sum(pair => pair.Value);
        return rarest.Select(pair =>
        {
            var row = new BqlRow
            {
                [_field] = pair.Key,
                ["count"] = AggregateAccumulator.CountValue(pair.Value),
                ["percent"] = total > 0 ? Math.Round(pair.Value * 100.0 / total, 2) : 0.0,
            };
            return (dynamic)row;
        }).ToList();
    }
}

/// <summary>
/// Regex: filter events where a field matches (or doesn't match) a regex pattern.
/// </summary>
internal sealed class LegacyRegexOperator : QueryOperator
{
    private readonly string _field;
    private readonly string _pattern;
    private readonly bool _negate;
    private Regex? _regex;
    private RegexTimeoutTracker? _timeouts;
    public override string Name => "regex";

    public LegacyRegexOperator(string field, string pattern, bool negate)
    {
        _field = field;
        _pattern = pattern;
        _negate = negate;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        // Runs for each batch of a query, so compile once.
        try
        {
            _regex ??= new Regex(_pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeoutTracker.PerEvent);
        }
        catch (ArgumentException ex)
        {
            throw new QueryParseException($"Invalid regular expression '{_pattern}': {ex.Message}");
        }

        _timeouts ??= new RegexTimeoutTracker(_pattern);
        var regex = _regex;
        var timeouts = _timeouts;

        var result = input.Where(evt =>
        {
            var dict = (IDictionary<string, object?>)evt;
            var value = dict.TryGetValue(_field, out var v) ? v?.ToString() ?? "" : "";
            try
            {
                var match = regex.IsMatch(value);
                return _negate ? !match : match;
            }
            catch (RegexMatchTimeoutException)
            {
                timeouts.Record();
                return false;
            }
        });

        return Task.FromResult<IEnumerable<dynamic>>(result);
    }
}

/// <summary>
/// Field summary: compute statistics about each field across all events.
/// Returns one row per field with count, distinct count, numeric min/max/avg, and sample values.
/// </summary>
internal sealed class LegacyFieldSummaryOperator : QueryOperator
{
    private readonly int _maxValues;
    public override string Name => "fieldsummary";

    public LegacyFieldSummaryOperator(int maxValues) { _maxValues = maxValues; }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();
        if (events.Count == 0) return Task.FromResult<IEnumerable<dynamic>>(events);

        // Collect stats per field
        var fieldStats = new Dictionary<string, FieldStatData>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            foreach (var kvp in dict)
            {
                if (!fieldStats.TryGetValue(kvp.Key, out var stats))
                {
                    stats = new FieldStatData();
                    fieldStats[kvp.Key] = stats;
                }

                stats.TotalCount++;
                if (kvp.Value != null)
                {
                    stats.ValueCount++;
                    stats.IsTime |= kvp.Value is DateTime;
                    var strVal = kvp.Value.ToString() ?? "";
                    // Cap distinct value tracking to prevent memory exhaustion
                    if (stats.DistinctValues.Count < 100_000)
                        stats.DistinctValues.Add(strVal);
                    if (stats.SampleValues.Count < _maxValues && !stats.SampleValues.Contains(strVal))
                        stats.SampleValues.Add(strVal);

                    if (BqlExpressionEvaluator.ToDoublePublic(kvp.Value) is { } numVal)
                    {
                        stats.NumericCount++;
                        stats.Sum += numVal;
                        // Running variance (Welford), so values needn't be kept
                        var delta = numVal - stats.RunningMean;
                        stats.RunningMean += delta / stats.NumericCount;
                        stats.SquaredDeviations += delta * (numVal - stats.RunningMean);
                        stats.Min = Math.Min(stats.Min, numVal);
                        stats.Max = Math.Max(stats.Max, numVal);
                    }
                }
            }
        }

        var results = new List<dynamic>();
        foreach (var kvp in fieldStats.OrderBy(f => f.Key))
        {
            var row = new BqlRow();
            row["field"] = kvp.Key;
            row["count"] = kvp.Value.ValueCount;
            row["distinct_count"] = kvp.Value.DistinctValues.Count;
            row["type"] = kvp.Value.IsTime ? "time" : kvp.Value.ValueCount > 0 && kvp.Value.NumericCount == kvp.Value.ValueCount ? "numeric" : "string";
            row["is_exact"] = kvp.Value.DistinctValues.Count <= 1000;
            row["numeric_count"] = kvp.Value.NumericCount;
            row["min"] = kvp.Value.NumericCount > 0 ? kvp.Value.Min : (object?)null;
            row["max"] = kvp.Value.NumericCount > 0 ? kvp.Value.Max : (object?)null;
            row["mean"] = kvp.Value.NumericCount > 0 ? kvp.Value.Sum / kvp.Value.NumericCount : (object?)null;
            row["stdev"] = kvp.Value.NumericCount > 1 ? Math.Sqrt(kvp.Value.SquaredDeviations / (kvp.Value.NumericCount - 1)) : (object?)null;
            row["values"] = string.Join(", ", kvp.Value.SampleValues.Take(_maxValues));
            results.Add(row);
        }

        return Task.FromResult<IEnumerable<dynamic>>(results);
    }

    private sealed class FieldStatData
    {
        public int TotalCount;
        public int ValueCount;
        public int NumericCount;
        public double Sum;
        public double RunningMean;
        public double SquaredDeviations;
        public bool IsTime;
        public double Min = double.MaxValue;
        public double Max = double.MinValue;
        public HashSet<string> DistinctValues = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SampleValues = new();
    }
}

/// <summary>
/// XY Series: pivot data from row format into columnar format.
/// Takes (row_field, column_field, value_field) and creates one row per unique row_field value,
/// with columns named after distinct column_field values.
/// </summary>
internal sealed class LegacyXyseriesOperator : QueryOperator
{
    private readonly string _rowField;
    private readonly string _colField;
    private readonly string _valField;
    public override string Name => "xyseries";

    public LegacyXyseriesOperator(string rowField, string colField, string valField)
    {
        _rowField = rowField;
        _colField = colField;
        _valField = valField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var events = input.ToList();

        // Group by row field, then pivot column values
        var rowGroups = new Dictionary<string, IDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

        foreach (var evt in events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var rowKey = dict.TryGetValue(_rowField, out var rv) ? rv?.ToString() ?? "" : "";
            var colKey = dict.TryGetValue(_colField, out var cv) ? cv?.ToString() ?? "" : "";
            var value = dict.TryGetValue(_valField, out var vv) ? vv : null;

            if (!rowGroups.TryGetValue(rowKey, out var row))
            {
                row = new BqlRow();
                row[_rowField] = rowKey;
                rowGroups[rowKey] = row;
            }
            row[colKey] = value;
        }

        return Task.FromResult<IEnumerable<dynamic>>(rowGroups.Values.Cast<dynamic>().ToList());
    }
}

/// <summary>
/// Untable: inverse of xyseries — unpivot columnar data back into rows.
/// </summary>
internal sealed class LegacyUntableOperator : QueryOperator
{
    private readonly string _rowField;
    private readonly string _colField;
    private readonly string _valField;
    public override string Name => "untable";

    public LegacyUntableOperator(string rowField, string colField, string valField)
    {
        _rowField = rowField;
        _colField = colField;
        _valField = valField;
    }

    public override Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var results = new List<dynamic>();

        foreach (var evt in input)
        {
            var dict = (IDictionary<string, object?>)evt;
            var rowValue = dict.TryGetValue(_rowField, out var rv) ? rv : null;

            foreach (var kvp in dict)
            {
                if (string.Equals(kvp.Key, _rowField, StringComparison.OrdinalIgnoreCase)) continue;

                var row = new BqlRow();
                row[_rowField] = rowValue;
                row[_colField] = kvp.Key;
                row[_valField] = kvp.Value;
                results.Add(row);
            }
        }

        return Task.FromResult<IEnumerable<dynamic>>(results);
    }
}

/// <summary>
/// InputLookup: load all rows from a lookup table as events.
/// Replaces the passthrough stub with real IKnowledgeStorage integration.
/// </summary>
internal sealed class LegacyInputLookupOperator : QueryOperator
{
    private readonly string _lookupName;
    private readonly Cabinet.Core.Interfaces.IKnowledgeStorage _storage;
    public override string Name => "inputlookup";

    public LegacyInputLookupOperator(string lookupName, Cabinet.Core.Interfaces.IKnowledgeStorage storage)
    {
        _lookupName = lookupName;
        _storage = storage;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var lookup = await _storage.GetLookupByNameAsync(_lookupName, ct);
        if (lookup == null)
            throw new QueryExecutionException($"There's no lookup table named '{_lookupName}'");

        var results = new List<dynamic>();
        foreach (var row in lookup.Rows)
        {
            var expando = new BqlRow();
            foreach (var kvp in row)
            {
                expando[kvp.Key] = kvp.Value;
            }
            results.Add(expando);
        }

        return results;
    }
}

/// <summary>
/// Lookup: enrich events by joining with a lookup table on a key field.
/// For each event, looks up the key field value in the lookup table and appends matching columns.
/// </summary>
internal sealed class LegacyLookupOperator : QueryOperator
{
    private readonly string _lookupName;
    private readonly string _keyField;
    private readonly List<string> _outputFields;
    private readonly Cabinet.Core.Interfaces.IKnowledgeStorage _storage;
    public override string Name => "lookup";

    public LegacyLookupOperator(string lookupName, string keyField, List<string> outputFields, Cabinet.Core.Interfaces.IKnowledgeStorage storage)
    {
        _lookupName = lookupName;
        _keyField = keyField;
        _outputFields = outputFields;
        _storage = storage;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var lookup = await _storage.GetLookupByNameAsync(_lookupName, ct);
        if (lookup == null)
            throw new QueryExecutionException($"There's no lookup table named '{_lookupName}'");

        // Build index: key field value → lookup row (first match wins)
        var index = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in lookup.Rows)
        {
            if (row.TryGetValue(_keyField, out var keyVal) && !index.ContainsKey(keyVal))
            {
                index[keyVal] = row;
            }
        }

        // Determine which fields to output from the lookup
        var fieldsToOutput = _outputFields.Count > 0
            ? _outputFields
            : lookup.Columns.Where(c => !string.Equals(c, _keyField, StringComparison.OrdinalIgnoreCase)).ToList();

        var results = new List<dynamic>();
        foreach (var evt in input)
        {
            var dict = (IDictionary<string, object?>)evt;
            var keyValue = dict.TryGetValue(_keyField, out var kv) ? kv?.ToString() : null;

            if (keyValue != null && index.TryGetValue(keyValue, out var lookupRow))
            {
                foreach (var field in fieldsToOutput)
                {
                    if (lookupRow.TryGetValue(field, out var val))
                    {
                        dict[field] = val;
                    }
                }
            }

            results.Add(evt);
        }

        return results;
    }
}
