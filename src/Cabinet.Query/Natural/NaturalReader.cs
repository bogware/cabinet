using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cabinet.Query.Natural;

/// <summary>
/// Reads a question in plain words into BQL, in any word order: "errors on web servers since
/// yesterday by host", "how many unique users per day", "top 5 slowest endpoints". It recognizes
/// what it can (times, fields compared to values, counts and averages, grouping, sorting) and
/// keeps the remaining words as a text search, then writes one canonical BQL query.
/// </summary>
public sealed partial class NaturalReader
{
    private readonly Lexicon _words;

    public NaturalReader(Lexicon? words = null)
    {
        _words = words ?? Lexicon.English;
    }

    /// <summary>Reads text with no pipes. <paramref name="now"/> anchors "since monday" and the like.</summary>
    public NaturalReading Read(string text, FieldCatalog catalog, DateTime now) => new Pass(this, text, catalog, now, stagesOnly: false).Run();

    /// <summary>Reads the text of one stage after a pipe, such as "count by host", into stages.</summary>
    public NaturalReading ReadStages(string text, FieldCatalog catalog, DateTime now) => new Pass(this, text, catalog, now, stagesOnly: true).Run();

    internal enum Kind
    {
        Word,
        Number,
        Quoted,
        Operator,
        Comma,
    }

    internal sealed class Token(Kind kind, string text, int start)
    {
        public Kind Kind { get; } = kind;
        public string Text { get; set; } = text;
        public string Lower { get; set; } = text.ToLowerInvariant();
        public int Start { get; } = start;
        public int Length { get; } = text.Length;
        public bool Used { get; set; }
    }

    [GeneratedRegex(@"(?<![\w'])'(?<q>[^']+)'(?![\w])|""(?<q>[^""]*)""|(?<op>>=|<=|!=|==|<>|[=<>])|(?<comma>,)|(?<word>/[\w\-./@%*':]*[\w*%/]|[\w*%][\w\-./@%*':]*[\w*%]|[\w*%])", RegexOptions.None, 1000)]
    private static partial Regex Tokens();

    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        foreach (Match match in Tokens().Matches(text))
        {
            if (match.Groups["q"].Success)
            {
                tokens.Add(new Token(Kind.Quoted, match.Groups["q"].Value, match.Index));
            }
            else if (match.Groups["op"].Success)
            {
                tokens.Add(new Token(Kind.Operator, match.Value, match.Index));
            }
            else if (match.Groups["comma"].Success)
            {
                tokens.Add(new Token(Kind.Comma, ",", match.Index));
            }
            else
            {
                var word = match.Value;
                var kind = double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? Kind.Number : Kind.Word;
                tokens.Add(new Token(kind, word, match.Index));
            }
        }

        return tokens;
    }

    /// <summary>One reading of one text: its state as the passes fill it in.</summary>
    private sealed partial class Pass
    {

        private readonly Lexicon _w;
        private readonly FieldCatalog _catalog;
        private readonly DateTime _now;
        private readonly bool _stagesOnly;
        private readonly List<Token> _t;
        private readonly NaturalReading _r = new();

        public Pass(NaturalReader reader, string text, FieldCatalog catalog, DateTime now, bool stagesOnly)
        {
            _w = reader._words;
            _catalog = catalog;
            _now = now;
            _stagesOnly = stagesOnly;
            _t = Tokenize(text);
            _r.Input = text;
        }

        public NaturalReading Run()
        {
            CorrectTypos();
            ReadStatusAndPaths();
            ReadTimes();
            ReadOverTime();
            ReadLatestPerGroup();
            ReadTopAndRare();
            ReadAggregates();
            ReadSortAndLimit();
            ReadGrouping();
            ReadComparisons();
            ReadSeverity();
            ReadKnownValues();
            ReadColumns();
            ReadMeasures();
            ReadLeftovers();
            Complete();
            _r.Query = Write();
            _r.Explanation = Explain();
            WriteWordReading();
            return _r;
        }

        // ─── Token helpers ───────────────────────────────────────

        private Token? At(int i) => i >= 0 && i < _t.Count && !_t[i].Used ? _t[i] : null;

        private bool IsWord(int i, string word) => At(i) is { Kind: Kind.Word } token && token.Lower == word;

        /// <summary>Whether unused tokens from i spell the phrase; returns how many tokens it took.</summary>
        private int Phrase(int i, string phrase)
        {
            var words = phrase.Split(' ');
            for (var k = 0; k < words.Length; k++)
            {
                if (At(i + k) is not { } token || token.Lower != words[k])
                {
                    return 0;
                }
            }

            return words.Length;
        }

        /// <summary>The longest of the phrases at i, and how many tokens it took.</summary>
        private (string? Phrase, int Length) Longest(int i, IEnumerable<string> phrases)
        {
            string? best = null;
            var length = 0;
            foreach (var phrase in phrases)
            {
                var taken = Phrase(i, phrase);
                if (taken > length)
                {
                    best = phrase;
                    length = taken;
                }
            }

            return (best, length);
        }

        private void Use(int i, int count = 1)
        {
            for (var k = i; k < i + count && k < _t.Count; k++)
            {
                _t[k].Used = true;
                if (_pending.Remove(k, out var guess))
                {
                    _r.Penalty += guess.Penalty;
                    if (guess.Correction != null)
                    {
                        _r.Corrections.Add(guess.Correction);
                    }
                }
            }
        }

        // Guesses FieldAt made about a token, which count only once the token is used
        private readonly Dictionary<int, (double Penalty, QueryCorrection? Correction)> _pending = [];

        /// <summary>Uses filler words just before i, such as "in the" before "last hour".</summary>
        private void UseBefore(int i, params string[] words)
        {
            for (var k = i - 1; k >= 0 && At(k) is { } token && words.Contains(token.Lower); k--)
            {
                token.Used = true;
            }
        }

        private int? Number(int i)
        {
            if (At(i) is not { } token)
            {
                return null;
            }

            if (token.Kind == Kind.Number && int.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }

            return token.Kind == Kind.Word && _w.Numbers.TryGetValue(token.Lower, out var named) ? named : null;
        }

        private static bool IsValueToken(Token? token) => token is { Kind: Kind.Word or Kind.Number or Kind.Quoted };

        // ─── Typos ───────────────────────────────────────────────

        private void CorrectTypos()
        {
            var known = _w.Vocabulary.Concat(_catalog.Fields).ToList();
            foreach (var token in _t.Where(t => t.Kind == Kind.Word && t.Text.All(char.IsLetter) && t.Length >= 4))
            {
                if (_w.Vocabulary.Contains(token.Lower) || _w.Filler.Contains(token.Lower) || _w.KeepAsIs.Contains(token.Lower) || _catalog.Field(token.Text) != null
                    || _catalog.FieldsWithValue(token.Text).Any() || _w.Numbers.ContainsKey(token.Lower))
                {
                    continue;
                }

                if (Fuzzy.Closest(token.Lower, known) is { } corrected)
                {
                    _r.Corrections.Add(new QueryCorrection(token.Text, corrected, token.Start, token.Length));
                    token.Lower = corrected.ToLowerInvariant();
                    token.Text = corrected;
                    _r.Penalty += 0.05;
                }
            }
        }

        // ─── Fields ──────────────────────────────────────────────

        /// <summary>
        /// A field named at i: a known field, a spoken name for one ("server", "response time"), a
        /// plural, or a near spelling. <paramref name="loose"/> also accepts an unknown word, where
        /// the position says it must be a field, as after "by" or before "is greater than".
        /// </summary>
        private (string Field, int Length, bool Plural)? FieldAt(int i, bool loose = false)
        {
            if (At(i) is not { Kind: Kind.Word } token || _w.Severities.ContainsKey(token.Lower) || _w.Units.ContainsKey(token.Lower))
            {
                return null;
            }

            // Two-word names first: "response time", "source type"
            if (At(i + 1) is { Kind: Kind.Word } next && _w.FieldWords.TryGetValue($"{token.Lower} {next.Lower}", out var pair))
            {
                return (Choose(pair), 2, next.Lower.EndsWith('s'));
            }

            if (_catalog.Field(token.Text) is { } exact)
            {
                return (exact, 1, false);
            }

            if (_w.FieldWords.TryGetValue(token.Lower, out var candidates))
            {
                return (Choose(candidates), 1, token.Lower.EndsWith('s') && !candidates.Contains(token.Lower));
            }

            if (token.Lower.Length > 3 && token.Lower.EndsWith('s') && _catalog.Field(token.Lower[..^1]) is { } singular)
            {
                return (singular, 1, true);
            }

            if (_catalog.HasData && !_catalog.FieldsWithValue(token.Text).Any() && Fuzzy.Closest(token.Lower, _catalog.Fields) is { } near)
            {
                _pending[i] = (0.1, new QueryCorrection(token.Text, near, token.Start, token.Length));
                return (near, 1, false);
            }

            if (loose && !_w.Filler.Contains(token.Lower) && !_w.Vocabulary.Contains(token.Lower) && Regex.IsMatch(token.Text, @"^[A-Za-z_][\w.]*$"))
            {
                _pending[i] = (0.05, null);
                return (token.Text, 1, false);
            }

            return null;
        }

        /// <summary>The first of the likely names that the data has, else the first.</summary>
        private string Choose(string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (_catalog.Field(candidate) is { } stored)
                {
                    return stored;
                }
            }

            return candidates[0];
        }

        // ─── Status codes and paths: "404s", "5xx", "/api/orders" ─

        [GeneratedRegex(@"^(?<code>[1-5]\d\d)s$|^(?<class>[1-5])xx$", RegexOptions.IgnoreCase, 1000)]
        private static partial Regex StatusWord();

        private void ReadStatusAndPaths()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { Kind: Kind.Word } token)
                {
                    continue;
                }

                if (StatusWord().Match(token.Text) is { Success: true } status)
                {
                    var field = Choose(_w.FieldWords["status"]);
                    _r.Conditions.Add(status.Groups["code"].Success
                        ? new ConditionRequest(field, "=", [status.Groups["code"].Value])
                        : new ConditionRequest(field, "between", [$"{status.Groups["class"].Value}00", $"{status.Groups["class"].Value}99"]));
                    Use(i);

                    // "5xx errors" are status codes, not log levels
                    if (At(i + 1) is { } after && _w.Severities.ContainsKey(after.Lower))
                    {
                        Use(i + 1);
                    }

                    continue;
                }

                // "/api/orders" names a path, when the data has a field for one
                if (token.Text.StartsWith('/') && token.Text.Length > 1 && !_w.Comparisons.Any(c => i > 0 && _t[i - 1].Lower == c.Phrase.Split(' ')[^1]))
                {
                    var field = Choose(_w.FieldWords["path"]);
                    if (_catalog.Field(field) != null)
                    {
                        _r.Conditions.Add(new ConditionRequest(field, token.Text.Contains('*') ? "=" : "=", [token.Text]));
                        UseBefore(i, "for", "on", "to", "at", "of");
                        Use(i);
                    }
                }
            }
        }

        // ─── Times ───────────────────────────────────────────────

        [GeneratedRegex(@"^(?<n>\d+)(?<u>[a-z]+)$", RegexOptions.IgnoreCase, 1000)]
        private static partial Regex NumberWithUnit();

        [GeneratedRegex(@"^(?<h>\d{1,2})(?::(?<m>\d{2}))?(?<ampm>am|pm)?$", RegexOptions.IgnoreCase, 1000)]
        private static partial Regex ClockTime();

        private static readonly string[] TimePrepositions = ["in", "within", "during", "over", "for", "from", "on", "the", "of"];

        private void ReadTimes()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { } token || token.Kind == Kind.Quoted)
                {
                    continue;
                }

                // "last 24 hours", "past week", "in the last 15 min", "last 24h", "past few days"
                if (_w.Recent.Contains(token.Lower) && ReadSpan(i + 1) is { } span)
                {
                    _r.Earliest = $"-{span.Amount}{span.Unit}";
                    UseBefore(i, TimePrepositions);
                    Use(i, 1 + span.Length);
                    continue;
                }

                // "24 hours ago", "2 days ago"
                if (ReadSpan(i) is { } ago && IsWord(i + ago.Length, "ago"))
                {
                    var since = i > 0 && _t[i - 1].Lower is "since" or "after" or "from";
                    var until = i > 0 && _t[i - 1].Lower is "until" or "before" or "till";
                    if (until)
                    {
                        _r.Latest = $"-{ago.Amount}{ago.Unit}";
                    }
                    else
                    {
                        _r.Earliest = $"-{ago.Amount}{ago.Unit}";
                    }

                    if (since || until)
                    {
                        Use(i - 1);
                    }

                    Use(i, ago.Length + 1);
                    continue;
                }

                switch (token.Lower)
                {
                    case "today":
                        SetDay("@d", null, i);
                        continue;
                    case "yesterday":
                        if (i > 0 && At(i - 1)?.Lower is "since" or "after")
                        {
                            Use(i - 1);
                            SetDay("-1d@d", null, i);
                        }
                        else
                        {
                            SetDay("-1d@d", "@d", i);
                        }

                        continue;
                    case "tonight" or "morning" when i > 0 && At(i - 1)?.Lower == "this":
                        Use(i - 1);
                        SetDay("@d", null, i);
                        continue;
                }

                // "this week", "this month", "this year"
                if (token.Lower == "this" && At(i + 1) is { } period && period.Lower is "week" or "month" or "year" or "hour")
                {
                    _r.Earliest = "@" + (period.Lower switch { "week" => "w", "month" => "M", "year" => "y", _ => "h" });
                    UseBefore(i, TimePrepositions);
                    Use(i, 2);
                    continue;
                }

                // "since monday", "since 10am", "since 2026-11-01", "before 3pm", "until yesterday"
                if (token.Lower is "since" or "after" or "until" or "before" or "till" && TimeAt(i + 1) is { } moment)
                {
                    if (token.Lower is "since" or "after")
                    {
                        _r.Earliest = moment.Spec;
                    }
                    else
                    {
                        _r.Latest = moment.Spec;
                    }

                    Use(i, 1 + moment.Length);
                    continue;
                }

                // "between 2pm and 4pm", "between monday and wednesday"
                if (token.Lower == "between" && TimeAt(i + 1) is { } from && IsWord(i + 1 + from.Length, "and") && TimeAt(i + 2 + from.Length) is { } to)
                {
                    _r.Earliest = from.Spec;
                    _r.Latest = to.Spec;
                    Use(i, 2 + from.Length + to.Length);
                    continue;
                }

                // "on monday"
                if (token.Lower is "on" && At(i + 1) is { } day && _w.Weekdays.ContainsKey(day.Lower) && TimeAt(i + 1) is { } start)
                {
                    _r.Earliest = start.Spec;
                    _r.Latest = Iso(DateTime.Parse(start.Spec, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).AddDays(1));
                    Use(i, 2);
                }
            }
        }

        private void SetDay(string earliest, string? latest, int i)
        {
            _r.Earliest = earliest;
            _r.Latest = latest;
            UseBefore(i, TimePrepositions);
            Use(i);
        }

        /// <summary>"24 hours", "24h", "hour" (one), "few days", "an hour", "15 min".</summary>
        private (int Amount, string Unit, int Length)? ReadSpan(int i)
        {
            if (At(i) is not { } token)
            {
                return null;
            }

            if (token.Kind == Kind.Word && NumberWithUnit().Match(token.Text) is { Success: true } joined && _w.Units.TryGetValue(joined.Groups["u"].Value, out var joinedUnit))
            {
                return (int.Parse(joined.Groups["n"].Value, CultureInfo.InvariantCulture), joinedUnit, 1);
            }

            if (Number(i) is { } amount && At(i + 1) is { } unitToken && _w.Units.TryGetValue(unitToken.Lower, out var unit) && unitToken.Lower.Length > 1)
            {
                return (amount, unit, 2);
            }

            if (token.Kind == Kind.Word && token.Lower.Length > 1 && _w.Units.TryGetValue(token.Lower, out var single) && !token.Lower.EndsWith('s'))
            {
                return (1, single, 1);
            }

            return null;
        }

        /// <summary>A moment: a weekday, today, yesterday, a date, or a clock time today. As an ISO time or a relative spec.</summary>
        private (string Spec, int Length)? TimeAt(int i)
        {
            if (At(i) is not { } token)
            {
                return null;
            }

            if (token.Lower == "today")
            {
                return ("@d", 1);
            }

            if (token.Lower == "yesterday")
            {
                return ("-1d@d", 1);
            }

            if (_w.Weekdays.TryGetValue(token.Lower, out var weekday))
            {
                var back = ((int)_now.DayOfWeek - (int)weekday + 7) % 7;
                return (Iso(_now.Date.AddDays(-back)), 1);
            }

            if (token.Kind is Kind.Word or Kind.Quoted && Regex.IsMatch(token.Text, @"^\d{4}-\d{2}-\d{2}") &&
                DateTime.TryParse(token.Text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            {
                return (Iso(date), 1);
            }

            var text = token.Text;
            var length = 1;
            if (At(i + 1) is { Lower: "am" or "pm" } suffix && token.Kind == Kind.Number)
            {
                text += suffix.Lower;
                length = 2;
            }

            if (ClockTime().Match(text) is { Success: true } clock && (clock.Groups["ampm"].Success || clock.Groups["m"].Success))
            {
                var hour = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture);
                var minute = clock.Groups["m"].Success ? int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
                if (clock.Groups["ampm"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) && hour < 12)
                {
                    hour += 12;
                }
                else if (clock.Groups["ampm"].Value.Equals("am", StringComparison.OrdinalIgnoreCase) && hour == 12)
                {
                    hour = 0;
                }

                if (hour < 24 && minute < 60)
                {
                    return (Iso(_now.Date.AddHours(hour).AddMinutes(minute)), length);
                }
            }

            return null;
        }

        private static string Iso(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        // ─── Over time ───────────────────────────────────────────

        [GeneratedRegex(@"^(?<n>\d+)-(?<u>[a-z]+)$", RegexOptions.IgnoreCase, 1000)]
        private static partial Regex HyphenSpan();

        private void ReadOverTime()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { Kind: Kind.Word } token)
                {
                    continue;
                }

                var (phrase, length) = Longest(i, _w.OverTime);
                if (phrase != null)
                {
                    _r.OverTime = true;
                    UseBefore(i, "as", "a", "the", "on", "in", "show");
                    Use(i, length);
                    continue;
                }

                // "hourly", "daily"
                if (token.Lower is "hourly" or "daily" or "weekly" or "minutely" && _w.Spans.TryGetValue(token.Lower, out var named))
                {
                    _r.Span = named;
                    _r.OverTime = true;
                    Use(i);
                    continue;
                }

                // "per hour", "by day", "each minute", "every 5 minutes", "in 10 minute buckets"
                if (token.Lower is "per" or "by" or "each" or "every" or "in")
                {
                    if (ReadSpan(i + 1) is { } span && span.Length + i + 1 <= _t.Count
                        && (token.Lower != "in" || IsBucketWord(i + 1 + span.Length)))
                    {
                        _r.Span = $"{span.Amount}{span.Unit}";
                        _r.OverTime = true;
                        Use(i, 1 + span.Length);
                        UseBucketWord(i + 1 + span.Length);
                        continue;
                    }
                }

                // "5-minute buckets"
                if (HyphenSpan().Match(token.Text) is { Success: true } hyphen && _w.Units.TryGetValue(hyphen.Groups["u"].Value, out var unit))
                {
                    _r.Span = $"{hyphen.Groups["n"].Value}{unit}";
                    _r.OverTime = true;
                    UseBefore(i, "in", "every", "per");
                    Use(i);
                    UseBucketWord(i + 1);
                }
            }
        }

        private bool IsBucketWord(int i) => At(i)?.Lower is "buckets" or "bucket" or "intervals" or "interval" or "windows" or "window" or "bins" or "slices" or "chunks";

        private void UseBucketWord(int i)
        {
            if (IsBucketWord(i))
            {
                Use(i);
            }
        }

        // ─── Latest per group: "latest event per host" ──────────

        private void ReadLatestPerGroup()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { Kind: Kind.Word } token || !(_w.Newest.Contains(token.Lower) || Phrase(i, "most recent") > 0))
                {
                    continue;
                }

                var k = i + (Phrase(i, "most recent") > 0 ? 2 : 1);
                if (At(k)?.Lower is "event" or "log" or "entry" or "line" or "one" or "message" or "record")
                {
                    k++;
                }

                var (group, length) = Longest(k, ["for each", "for every", "per", "by", "from each", "on each", "of each"]);
                if (group != null && FieldAt(k + length, loose: true) is { } field)
                {
                    _r.Dedup = field.Field;
                    Use(i, k - i + length + field.Length);
                }
            }
        }

        // ─── Top and rare ────────────────────────────────────────

        private void ReadTopAndRare()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { } token)
                {
                    continue;
                }

                // "slowest endpoints", "top 5 slowest pages": a measure ranked by a field
                if (SuperlativeAt(i) == true)
                {
                    continue;
                }

                // "top 5 hosts", "top users", "bottom 3 paths"
                if (token.Lower is "top" or "bottom")
                {
                    var n = Number(i + 1);
                    var at = i + 1 + (n == null ? 0 : 1);
                    if (FieldAt(at) is { } field)
                    {
                        Use(i, at - i + field.Length);
                        TopBy(field.Field, n ?? 10, rare: token.Lower == "bottom", at + field.Length);
                        continue;
                    }

                    if (n != null)
                    {
                        // "top 10" alone is a limit; "top 10 slowest requests" is read by the aggregates
                        if (!(At(at) is { } after && _w.Aggregates.ContainsKey(after.Lower)))
                        {
                            _r.Limit = n;
                            Use(i, 2);
                        }
                    }

                    continue;
                }

                // "most common users", "5 most frequent paths", "rarest status codes"
                var (most, mostLength) = Longest(i, _w.MostCommon);
                var (least, leastLength) = Longest(i, _w.LeastCommon);
                var phraseLength = Math.Max(mostLength, leastLength);
                if (phraseLength > 0 && FieldAt(i + phraseLength) is { } common)
                {
                    var count = Number(i - 1);
                    if (count != null)
                    {
                        Use(i - 1);
                    }

                    Use(i, phraseLength + common.Length);
                    TopBy(common.Field, count ?? 10, rare: leastLength > mostLength, i + phraseLength + common.Length);
                    continue;
                }

                // "which host has the most errors", "what users had the most failed logins"
                if (token.Lower is "which" or "what" && FieldAt(i + 1) is { } subject)
                {
                    var k = i + 1 + subject.Length;
                    while (At(k)?.Lower is "has" or "have" or "had" or "got" or "gets" or "get" or "sees" or "saw" or "see" or "with" or "logged" or "sent" or "made" or "produced" or "generated" or "threw" or "caused" or "the")
                    {
                        k++;
                    }

                    var (mostWord, mostWordLength) = Longest(k, ["most", "least", "fewest"]);
                    if (mostWord != null)
                    {
                        Use(i, k - i + mostWordLength);
                        _r.Top = new TopRequest(subject.Plural ? 10 : 1, subject.Field, Rare: mostWord != "most");
                    }
                }
            }
        }

        private static readonly Dictionary<string, (string Measure, string Function, bool Descending)> Superlatives = new(StringComparer.OrdinalIgnoreCase)
        {
            ["slowest"] = ("duration", "avg", true), ["fastest"] = ("duration", "avg", false), ["quickest"] = ("duration", "avg", false),
            ["longest"] = ("duration", "avg", true), ["shortest"] = ("duration", "avg", false),
            ["largest"] = ("bytes", "avg", true), ["biggest"] = ("bytes", "avg", true), ["heaviest"] = ("bytes", "sum", true), ["smallest"] = ("bytes", "avg", false),
        };

        /// <summary>"[top N] slowest endpoints": the field ranked by a measure its word implies, N of them.</summary>
        private bool? SuperlativeAt(int i)
        {
            var n = 10;
            var k = i;
            if (At(k)?.Lower is "top" or "the")
            {
                k++;
            }

            if (Number(k) is { } count && At(k) is { Kind: Kind.Number })
            {
                n = count;
                k++;
            }

            if (At(k) is not { } word || !Superlatives.TryGetValue(word.Lower, out var meaning) || FieldAt(k + 1) is not { } subject)
            {
                return null;
            }

            var aggregate = new AggregateRequest(meaning.Function, Choose(_w.FieldWords[meaning.Measure]));
            _r.Aggregates.Add(aggregate);
            _r.By.Add(subject.Field);
            _r.Sort = new SortRequest(aggregate.Column, meaning.Descending);
            _r.Limit = n;
            Use(i, k - i + 1 + subject.Length);
            return true;
        }

        /// <summary>Top or rare values of a field, or "top 5 paths by average duration" as a ranked aggregate.</summary>
        private void TopBy(string field, int n, bool rare, int next)
        {
            if (IsWord(next, "by") && At(next + 1) is { } measure)
            {
                // "by count", "by volume", "by number of requests"
                if (_w.CountWords.Contains(measure.Lower) || measure.Lower is "events" or "requests" or "hits" or "traffic" or "frequency" or "occurrences")
                {
                    Use(next, 2);
                    if (IsWord(next + 2, "of"))
                    {
                        Use(next + 2);
                    }
                }
                else if (_w.Severities.ContainsKey(measure.Lower))
                {
                    // "top 5 hosts by errors": errors filter, then rank by count
                    Use(next);
                }
                else if (AggregateAt(next + 1) is { } aggregate)
                {
                    Use(next, 1 + aggregate.Length);
                    _r.Aggregates.Add(aggregate.Spec);
                    _r.By.Add(field);
                    _r.Sort = new SortRequest(aggregate.Spec.Column, !rare);
                    _r.Limit = n;
                    return;
                }
            }

            _r.Top = new TopRequest(n, field, rare);
        }

        // ─── Aggregates ──────────────────────────────────────────

        [GeneratedRegex(@"^(?:p|perc|percentile)(?<n>\d{1,2}(?:\.\d+)?)$|^(?<n>\d{1,2})(?:st|nd|rd|th)$", RegexOptions.IgnoreCase, 1000)]
        private static partial Regex PercentileWord();

        /// <summary>"average duration", "p95 latency", "95th percentile of response time", "max bytes".</summary>
        private (AggregateRequest Spec, int Length)? AggregateAt(int i)
        {
            if (At(i) is not { Kind: Kind.Word } token)
            {
                return null;
            }

            string? function = null;
            var length = 1;
            if (PercentileWord().Match(token.Text) is { Success: true } percentile)
            {
                function = $"perc{percentile.Groups["n"].Value}";
                if (IsWord(i + 1, "percentile"))
                {
                    length = 2;
                }
                else if (token.Text.EndsWith("th", StringComparison.OrdinalIgnoreCase) || token.Text.EndsWith("st", StringComparison.OrdinalIgnoreCase)
                         || token.Text.EndsWith("nd", StringComparison.OrdinalIgnoreCase) || token.Text.EndsWith("rd", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
            else if (token.Lower == "percentile" && Number(i + 1) is { } p)
            {
                function = $"perc{p}";
                length = 2;
            }
            else if (_w.Aggregates.TryGetValue(token.Lower, out var named))
            {
                function = named;

                // "standard deviation"
                if (token.Lower == "standard" || IsWord(i + 1, "deviation"))
                {
                    length += IsWord(i + 1, "deviation") ? 1 : 0;
                }
            }
            else if (token.Lower == "standard" && IsWord(i + 1, "deviation"))
            {
                function = "stdev";
                length = 2;
            }

            if (function == null)
            {
                return null;
            }

            var k = i + length;
            while (At(k)?.Lower is "of" or "the" or "a" or "an")
            {
                k++;
            }

            // "total number of x" and "unique count" are counts
            if (function is "sum" or "dc" && At(k) is { } after && (after.Lower is "number" or "count" or "amount"))
            {
                if (function == "sum")
                {
                    return null;
                }

                k++;
                while (At(k)?.Lower is "of" or "the")
                {
                    k++;
                }
            }

            if (FieldAt(k, loose: true) is not { } field)
            {
                return null;
            }

            return (new AggregateRequest(function, field.Field), k - i + field.Length);
        }

        private void ReadAggregates()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { } token)
                {
                    continue;
                }

                // "how many", "count of", "number of", "total number of"
                var countLength = Phrase(i, "how many") > 0 ? 2 : Phrase(i, "how much") > 0 ? 2
                    : Phrase(i, "total number of") > 0 ? 3 : Phrase(i, "number of") > 0 ? 2 : Phrase(i, "count of") > 0 ? 2
                    : token.Lower is "count" or "tally" or "counts" ? 1 : 0;
                if (countLength > 0)
                {
                    Use(i, countLength);
                    var k = i + countLength;
                    while (At(k)?.Lower is "the" or "of" or "all")
                    {
                        Use(k);
                        k++;
                    }

                    // "how many unique users", "count of distinct ips", "how many different hosts"
                    if (At(k) is { } distinct && distinct.Lower is "unique" or "distinct" or "different" or "separate" && FieldAt(k + 1, loose: true) is { } counted)
                    {
                        Use(k, 1 + counted.Length);
                        _r.Aggregates.Add(new AggregateRequest("dc", counted.Field));
                        continue;
                    }

                    // "how many hosts" counts distinct hosts; "how many errors" counts events
                    if (FieldAt(k) is { Plural: true } plural)
                    {
                        Use(k, plural.Length);
                        _r.Aggregates.Add(new AggregateRequest("dc", plural.Field));
                        continue;
                    }

                    _r.Aggregates.Add(new AggregateRequest("count", null));
                    continue;
                }

                // "unique users" with no count asks for the values
                if (token.Lower is "unique" or "distinct" or "different" && FieldAt(i + 1) is { } values
                    && !(At(i + 1 + values.Length)?.Lower is "count" or "number"))
                {
                    Use(i, 1 + values.Length);
                    _r.ValuesOf ??= values.Field;
                    continue;
                }

                if (AggregateAt(i) is { } aggregate)
                {
                    // "top" alone is read by top-and-rare; here it only counts before a measure, "top latency"
                    Use(i, aggregate.Length);
                    _r.Aggregates.Add(aggregate.Spec);
                }
            }
        }

        // ─── Grouping ────────────────────────────────────────────

        private void ReadGrouping()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                var (group, length) = Longest(i, _w.GroupWords);
                if (group == null)
                {
                    continue;
                }

                var k = i + length;
                while (At(k)?.Lower is "the" or "each" or "every")
                {
                    k++;
                }

                var fields = new List<string>();
                var used = k;
                while (FieldAt(used, loose: group != "by" || _r.Aggregates.Count > 0 || _r.Top != null) is { } field)
                {
                    fields.Add(field.Field);
                    used += field.Length;
                    if (!(At(used) is { Kind: Kind.Comma } || IsWord(used, "and")) || FieldAt(used + 1) == null)
                    {
                        break;
                    }

                    used++;
                }

                if (fields.Count == 0)
                {
                    continue;
                }

                Use(i, used - i);
                foreach (var field in fields.Where(f => !_r.By.Contains(f, StringComparer.OrdinalIgnoreCase)))
                {
                    _r.By.Add(field);
                }
            }
        }

        // ─── Sorting and limits ──────────────────────────────────

        private static readonly string[] EventNouns = ["events", "event", "logs", "log", "lines", "line", "entries", "entry", "records", "record", "messages", "message", "results", "result", "rows", "row", "errors", "error", "warnings", "warning", "requests", "request", "items", "hits"];

        private void ReadSortAndLimit()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { } token)
                {
                    continue;
                }

                // "sorted by duration descending", "order by count", "rank by bytes"
                if (_w.SortWords.Contains(token.Lower))
                {
                    var k = i + 1;
                    if (IsWord(k, "by") || IsWord(k, "on"))
                    {
                        k++;
                    }

                    string? column = null;
                    var length = 0;
                    if (At(k) is { } what && (_w.CountWords.Contains(what.Lower) || what.Lower is "frequency" or "events" or "hits"))
                    {
                        column = "count";
                        length = 1;
                    }
                    else if (AggregateAt(k) is { } aggregate)
                    {
                        if (!_r.Aggregates.Contains(aggregate.Spec))
                        {
                            _r.Aggregates.Add(aggregate.Spec);
                        }

                        column = aggregate.Spec.Column;
                        length = aggregate.Length;
                    }
                    else if (At(k) is { } timeWord && timeWord.Lower is "time" or "date" or "timestamp" or "_time" or "when")
                    {
                        column = "_time";
                        length = 1;
                    }
                    else if (FieldAt(k, loose: true) is { } field)
                    {
                        column = _r.Aggregates.FirstOrDefault(a => string.Equals(a.Field, field.Field, StringComparison.OrdinalIgnoreCase))?.Column ?? field.Field;
                        length = field.Length;
                    }

                    if (column != null)
                    {
                        var (direction, directionLength) = Longest(k + length, _w.Directions.Keys);
                        var descending = direction == null || _w.Directions[direction];
                        _r.Sort = new SortRequest(column, descending);
                        Use(i, k - i + length + directionLength);
                    }

                    continue;
                }

                // "highest first", "oldest first" on their own
                var (alone, aloneLength) = Longest(i, _w.Directions.Keys.Where(d => d.Contains(' ') || d is "chronological" or "chronologically"));
                if (alone != null)
                {
                    var descending = _w.Directions[alone];
                    if (alone.StartsWith("oldest", StringComparison.Ordinal) || alone.StartsWith("earliest", StringComparison.Ordinal) || alone.StartsWith("chronolog", StringComparison.Ordinal)
                        || alone is "newest first" or "latest first")
                    {
                        _r.Sort ??= new SortRequest("_time", descending);
                    }
                    else
                    {
                        _r.Direction = descending;
                    }

                    Use(i, aloneLength);
                    continue;
                }

                // "latest 20 errors", "last 50 events", "10 most recent", "oldest 5"
                var newest = _w.Newest.Contains(token.Lower) ? 1 : Phrase(i, "most recent");
                var oldest = _w.Oldest.Contains(token.Lower) ? 1 : 0;
                if ((newest > 0 || oldest > 0) && Number(i + Math.Max(newest, oldest)) is { } n)
                {
                    _r.Limit = n;
                    if (oldest > 0 && token.Lower != "first")
                    {
                        _r.Sort = new SortRequest("_time", false);
                    }

                    Use(i, Math.Max(newest, oldest) + 1);
                    continue;
                }

                if (Number(i) is { } count && At(i) is { Kind: Kind.Number } && (newest = Phrase(i + 1, "most recent") > 0 ? 2 : _w.Newest.Contains(At(i + 1)?.Lower ?? string.Empty) ? 1 : 0) > 0)
                {
                    _r.Limit = count;
                    Use(i, 1 + newest);
                    continue;
                }

                // "the latest error", "most recent login"
                if (newest > 0 && At(i + newest) is { } noun && EventNouns.Contains(noun.Lower) && !noun.Lower.EndsWith('s'))
                {
                    _r.Limit = 1;
                    Use(i, newest);
                    continue;
                }

                // "first 10", "only 5", "10 results", "limit 20"
                if (token.Lower is "first" or "only" or "just" or "limit" or "max" && Number(i + 1) is { } first)
                {
                    _r.Limit = first;
                    Use(i, 2);
                    continue;
                }

                if (token.Kind == Kind.Number && Number(i) is { } bare && At(i + 1) is { } unitNoun && EventNouns.Contains(unitNoun.Lower)
                    && !(i > 0 && (_t[i - 1].Kind == Kind.Operator || FieldAt(i - 1) != null || _w.Comparisons.Any(c => c.Phrase == _t[i - 1].Lower))))
                {
                    _r.Limit = bare;
                    Use(i);
                }
            }
        }

        // ─── Comparisons ─────────────────────────────────────────

        private static readonly string[] Negations = ["but not", "excluding", "exclude", "except", "without", "ignoring", "ignore", "not", "no"];

        private void ReadComparisons()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is null)
                {
                    continue;
                }

                var (negation, negationLength) = Longest(i, Negations);
                var start = i + negationLength;

                if (FieldAt(start, loose: true) is { } field && ComparisonAfter(start + field.Length, field.Field) is { } comparison)
                {
                    var condition = comparison.Condition;
                    if (negation != null)
                    {
                        condition = condition.Negate();
                    }

                    _r.Conditions.Add(condition);
                    Use(i, start - i + field.Length + comparison.Length);
                    continue;
                }

                // "status 500", "user alice", "host web-1": a known field and a value that fits it
                if (FieldAt(start) is { } named && At(start + named.Length) is { } value && IsValueToken(value) && FitsField(named.Field, value))
                {
                    var values = ValueList(start + named.Length, out var listLength);
                    var condition = new ConditionRequest(named.Field, values.Count > 1 ? "in" : "=", values);
                    _r.Conditions.Add(negation != null ? condition.Negate() : condition);
                    _r.Penalty += 0.03;
                    Use(i, start - i + named.Length + listLength);
                    continue;
                }

                // "on web-1", "from db-2", "on web servers", "from the api hosts", "not on web-01"
                if (At(start)?.Lower is "on" or "from" or "at" or "via")
                {
                    var k = start + 1;
                    if (IsWord(k, "the"))
                    {
                        k++;
                    }

                    if (HostList(k) is { } host)
                    {
                        _r.Conditions.Add(negation != null ? host.Condition.Negate() : host.Condition);
                        Use(i, k - i + host.Length);
                        continue;
                    }

                    // "from alice", "by bob": a known value of some field
                    if (KnownValueAt(k) is { } known)
                    {
                        _r.Conditions.Add(negation != null ? known.Negate() : known);
                        Use(i, k - i + 1);
                    }

                    continue;
                }

                // "over 1000 ms", "slower than 2 seconds", "larger than 1 mb": a measure implied by its unit
                if (UnitComparisonAt(start) is { } measured)
                {
                    _r.Conditions.Add(negation != null ? measured.Condition.Negate() : measured.Condition);
                    Use(i, start - i + measured.Length);
                    continue;
                }

                // "excluding web-01 and web-02"
                if (negation != null && HostList(start) is { } excluded)
                {
                    _r.Conditions.Add(excluded.Condition.Negate());
                    Use(i, start - i + excluded.Length);
                    continue;
                }

                // "web servers", "db-1 host" without a preposition
                if (HostAt(i) is { Length: > 1 } bare)
                {
                    _r.Conditions.Add(negation != null ? bare.Condition.Negate() : bare.Condition);
                    Use(i, bare.Length);
                }
            }
        }

        /// <summary>An operator and its value or values after a field, as in "is greater than 500" or "between 200 and 299".</summary>
        private (ConditionRequest Condition, int Length)? ComparisonAfter(int i, string field)
        {
            if (At(i) is not { } token)
            {
                return null;
            }

            // "between 200 and 299"
            if (token.Lower == "between" && IsValueToken(At(i + 1)) && IsWord(i + 2, "and") && IsValueToken(At(i + 3)))
            {
                return (new ConditionRequest(field, "between", [At(i + 1)!.Text, At(i + 3)!.Text]), 4);
            }

            string? op = null;
            var length = 0;
            foreach (var (phrase, meaning) in _w.Comparisons)
            {
                var taken = token.Kind == Kind.Operator ? (token.Text == phrase ? 1 : 0) : Phrase(i, phrase);
                if (taken > length)
                {
                    op = meaning;
                    length = taken;
                }
            }

            if (op == null || !IsValueToken(At(i + length)))
            {
                return null;
            }

            // "is" alone before a word that isn't a value of anything is too weak: "user is logged in"
            var valueToken = At(i + length)!;
            if (op == "=" && token.Kind == Kind.Word && valueToken.Kind == Kind.Word && (_w.Filler.Contains(valueToken.Lower) || _w.Vocabulary.Contains(valueToken.Lower)))
            {
                return null;
            }

            var values = ValueList(i + length, out var valuesLength);
            length += valuesLength;

            // "2 seconds", "5 mb": in the field's own unit
            if (values.Count == 1 && double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                && At(i + length) is { } unitWord && MeasureUnits.TryGetValue(unitWord.Lower, out var measure))
            {
                var scaled = amount * measure.Factor;
                if (measure.Kind == "time" && !field.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
                {
                    scaled /= 1000;
                }

                values = [scaled.ToString(CultureInfo.InvariantCulture)];
                length++;
            }

            // "500 or more", "100 and above", "10 or less"
            var (postfix, postfixLength) = Longest(i + length, ["or more", "or higher", "or greater", "or above", "and above", "and up", "or over", "or less", "or lower", "or fewer", "or below", "and below", "and under"]);
            if (postfix != null && values.Count == 1)
            {
                op = postfix.EndsWith("more", StringComparison.Ordinal) || postfix.EndsWith("higher", StringComparison.Ordinal) || postfix.EndsWith("greater", StringComparison.Ordinal)
                     || postfix.EndsWith("above", StringComparison.Ordinal) || postfix.EndsWith("up", StringComparison.Ordinal) || postfix.EndsWith("over", StringComparison.Ordinal) ? ">=" : "<=";
                length += postfixLength;
            }

            if (values.Count > 1 && op is "=" or "in")
            {
                op = "in";
            }
            else if (values.Count > 1 && op == "!=")
            {
                op = "not in";
            }

            return (new ConditionRequest(field, op, values), length);
        }

        /// <summary>Values at i: one, or a list joined by "or", "and" or commas.</summary>
        private List<string> ValueList(int i, out int length)
        {
            var values = new List<string> { At(i)!.Text };
            length = 1;
            while (At(i + length) is { } joiner && (joiner.Kind == Kind.Comma || joiner.Lower is "or" || (joiner.Lower == "and" && values.Count > 1))
                   && At(i + length + 1) is { } next && IsValueToken(next) && !_w.Filler.Contains(next.Lower) && !_w.Vocabulary.Contains(next.Lower)
                   && FieldAt(i + length + 1) == null)
            {
                values.Add(next.Text);
                length += 2;
            }

            // "a, b and c"
            if (values.Count > 1 && IsWord(i + length, "and") && At(i + length + 1) is { } last && IsValueToken(last) && !_w.Filler.Contains(last.Lower) && FieldAt(i + length + 1) == null)
            {
                values.Add(last.Text);
                length += 2;
            }

            return values;
        }

        /// <summary>Whether a value plausibly belongs to a field: known for it, a number, quoted, or a host name for host.</summary>
        private bool FitsField(string field, Token value)
        {
            if (_catalog.Value(field, value.Text) != null || value.Kind is Kind.Quoted)
            {
                return true;
            }

            if (_w.Filler.Contains(value.Lower) || _w.Vocabulary.Contains(value.Lower) || _w.Severities.ContainsKey(value.Lower))
            {
                return false;
            }

            if (value.Kind == Kind.Number)
            {
                return true;
            }

            var known = _catalog.Values(field).Any();
            return !known && (value.Text.Any(char.IsDigit) || value.Text.Contains('-') || value.Text.Contains('.') || value.Text.Contains('@'));
        }

        /// <summary>
        /// A host at i: a known host, a name that looks like one ("web-1", "db01.example.com"), or a
        /// prefix before a host word ("web servers", "api hosts"), which matches every host starting so.
        /// </summary>
        private (ConditionRequest Condition, int Length)? HostAt(int i)
        {
            if (At(i) is not { Kind: Kind.Word or Kind.Quoted } token || _w.Filler.Contains(token.Lower) || _w.Severities.ContainsKey(token.Lower)
                || _w.Vocabulary.Contains(token.Lower) || IsIp(token.Text))
            {
                return null;
            }

            var hostWord = At(i + 1) is { } after && _w.FieldWords.TryGetValue(after.Lower, out var named) && named[0] == "host";
            if (_catalog.Value("host", token.Text) is { } knownHost)
            {
                return (new ConditionRequest("host", "=", [knownHost]), hostWord ? 2 : 1);
            }

            if (hostWord)
            {
                var hosts = _catalog.Values("host").ToList();
                var exact = hosts.FirstOrDefault(h => h.Equals(token.Text, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    return (new ConditionRequest("host", "=", [exact]), 2);
                }

                if (hosts.Count == 0 || hosts.Any(h => h.StartsWith(token.Text, StringComparison.OrdinalIgnoreCase)))
                {
                    return (new ConditionRequest("host", "starts", [token.Text]), 2);
                }

                return null;
            }

            // A bare name after "on"/"from" that looks like a host
            return Regex.IsMatch(token.Text, @"^[A-Za-z][\w-]*(\d|-)[\w.-]*$") && !_catalog.Values("host").Any()
                ? (new ConditionRequest("host", "=", [token.Text]), 1)
                : null;
        }

        /// <summary>One host, or several joined by "and", "or" or commas, as one condition.</summary>
        private (ConditionRequest Condition, int Length)? HostList(int i)
        {
            if (HostAt(i) is not { } first)
            {
                return null;
            }

            var hosts = new List<ConditionRequest> { first.Condition };
            var length = first.Length;
            while (At(i + length) is { } joiner && (joiner.Kind == Kind.Comma || joiner.Lower is "and" or "or") && HostAt(i + length + 1) is { } next)
            {
                hosts.Add(next.Condition);
                length += 1 + next.Length;
            }

            if (hosts.Count == 1 || hosts.Any(h => h.Op != "="))
            {
                return hosts.Count == 1 ? first : null;
            }

            return (new ConditionRequest("host", "in", hosts.Select(h => h.Values[0]).ToList()), length);
        }

        /// <summary>A word that's a known value of exactly one kind of field, as "alice" for user or "POST" for method.</summary>
        private ConditionRequest? KnownValueAt(int i)
        {
            if (At(i) is not { Kind: Kind.Word or Kind.Quoted } token || _w.Severities.ContainsKey(token.Lower)
                || (_w.Filler.Contains(token.Lower) && !(token.Text.Length > 1 && token.Text.All(char.IsUpper))))
            {
                return null;
            }

            var fields = _catalog.FieldsWithValue(token.Text).ToList();
            if (fields.Count > 0 && _w.Filler.Contains(token.Lower) && _catalog.Value(fields[0], token.Text) != token.Text)
            {
                return null;
            }
            return fields.Count == 0 ? null : new ConditionRequest(fields[0], "=", [_catalog.Value(fields[0], token.Text)!]);
        }

        private static readonly Dictionary<string, (string Kind, double Factor)> MeasureUnits = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ms"] = ("time", 1), ["msec"] = ("time", 1), ["millisecond"] = ("time", 1), ["milliseconds"] = ("time", 1),
            ["s"] = ("time", 1000), ["sec"] = ("time", 1000), ["secs"] = ("time", 1000), ["second"] = ("time", 1000), ["seconds"] = ("time", 1000),
            ["min"] = ("time", 60_000), ["mins"] = ("time", 60_000), ["minute"] = ("time", 60_000), ["minutes"] = ("time", 60_000),
            ["b"] = ("size", 1), ["bytes"] = ("size", 1), ["kb"] = ("size", 1024), ["mb"] = ("size", 1024 * 1024), ["gb"] = ("size", 1024 * 1024 * 1024),
        };

        [GeneratedRegex(@"^(?<n>\d+(?:\.\d+)?)(?<u>ms|s|sec|secs|kb|mb|gb|b)$", RegexOptions.IgnoreCase, 1000)]
        private static partial Regex JoinedMeasure();

        /// <summary>"over 1000 ms", "taking more than 2s", "bigger than 5 mb": the field is the duration or size the data has.</summary>
        private (ConditionRequest Condition, int Length)? UnitComparisonAt(int i)
        {
            var k = i;
            if (At(k)?.Lower is "taking" or "took" or "takes" or "lasting" or "that" or "which")
            {
                k++;
            }

            string? op = null;
            var opLength = 0;
            foreach (var (phrase, meaning) in _w.Comparisons)
            {
                var taken = Phrase(k, phrase);
                if (taken > opLength && meaning is ">" or ">=" or "<" or "<=")
                {
                    op = meaning;
                    opLength = taken;
                }
            }

            if (op == null)
            {
                return null;
            }

            double amount;
            string unit;
            var length = opLength;
            if (At(k + opLength) is { } joined && JoinedMeasure().Match(joined.Text) is { Success: true } match)
            {
                amount = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
                unit = match.Groups["u"].Value;
                length += 1;
            }
            else if (At(k + opLength) is { Kind: Kind.Number } number && At(k + opLength + 1) is { } unitToken && MeasureUnits.ContainsKey(unitToken.Lower))
            {
                amount = double.Parse(number.Text, CultureInfo.InvariantCulture);
                unit = unitToken.Lower;
                length += 2;
            }
            else
            {
                return null;
            }

            var (kind, factor) = MeasureUnits[unit];
            var field = Choose(kind == "time" ? _w.FieldWords["duration"] : _w.FieldWords["bytes"]);
            var value = amount * factor;
            if (kind == "time" && !field.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                value /= 1000;
            }

            return (new ConditionRequest(field, op, [value.ToString(CultureInfo.InvariantCulture)]), k - i + length);
        }

        private static bool IsIp(string text) => Regex.IsMatch(text, @"^\d{1,3}(\.\d{1,3}){3}$|^[0-9a-f:]+:[0-9a-f:]*$", RegexOptions.IgnoreCase);

        // ─── Severity ────────────────────────────────────────────

        private static readonly string[] SeverityOrder = ["debug", "info", "warning", "error", "critical", "fatal"];

        private void ReadSeverity()
        {
            string? lowest = null;
            var negated = false;
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is { Kind: Kind.Word } token && _w.Severities.TryGetValue(token.Lower, out var level))
                {
                    if (i > 0 && At(i - 1)?.Lower is "no" or "not" or "without" or "excluding" or "except")
                    {
                        negated = true;
                        Use(i - 1);
                    }

                    // "errors and warnings" means both: the lower level and worse
                    if (lowest == null || Array.IndexOf(SeverityOrder, level) < Array.IndexOf(SeverityOrder, lowest))
                    {
                        lowest = level;
                    }

                    _r.SeverityWord ??= token.Text;
                    Use(i);
                }
            }

            if (lowest != null)
            {
                _r.Conditions.Insert(0, new ConditionRequest("severity", negated ? "<" : ">=", [lowest]));
                _r.SeverityOnly = _r.Conditions.Count == 1;
            }
        }

        // ─── Known values: "nginx", "alice", "prod" ─────────────

        private void ReadKnownValues()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { Kind: Kind.Word or Kind.Quoted } token || (_w.Filler.Contains(token.Lower) && !token.Text.All(char.IsUpper)))
                {
                    continue;
                }

                // "in index main", "the security index"
                if (token.Lower == "index" && At(i + 1) is { } indexName && IsValueToken(indexName) && !_w.Filler.Contains(indexName.Lower))
                {
                    _r.Conditions.Add(new ConditionRequest("index", "=", [indexName.Text]));
                    UseBefore(i, "in", "from", "the");
                    Use(i, 2);
                    continue;
                }

                if (At(i + 1) is { Lower: "index" } && _catalog.Value("index", token.Text) is { } beforeIndex)
                {
                    _r.Conditions.Add(new ConditionRequest("index", "=", [beforeIndex]));
                    UseBefore(i, "in", "from", "the");
                    Use(i, 2);
                    continue;
                }

                // "nginx", "POST", "alice": a value the data has for one kind of field
                if ((!_w.Vocabulary.Contains(token.Lower) || token.Text.All(char.IsUpper)) && KnownValueAt(i) is { } known)
                {
                    var negated = i > 0 && At(i - 1)?.Lower is "not" or "without" or "excluding" or "except";
                    _r.Conditions.Add(negated ? known.Negate() : known);
                    if (negated)
                    {
                        Use(i - 1);
                    }

                    UseBefore(i, "in", "from", "the", "by", "for", "of");
                    Use(i);
                    if (At(i + 1)?.Lower is "logs" or "log" or "events" or "index")
                    {
                        Use(i + 1);
                    }

                    _r.Penalty += known.Field is "sourcetype" or "index" or "host" ? 0 : 0.03;
                }
            }
        }

        // ─── Columns: "show host, path and status" ──────────────

        private void ReadColumns()
        {
            if (_r.Aggregates.Count > 0 || _r.Top != null || _r.ValuesOf != null)
            {
                return;
            }

            var fields = new List<(string Field, bool Plural, int Index, int Length)>();
            for (var i = 0; i < _t.Count; i++)
            {
                if (FieldAt(i) is { } field)
                {
                    fields.Add((field.Field, field.Plural, i, field.Length));
                    i += field.Length - 1;
                }
            }

            if (fields.Count == 0)
            {
                return;
            }

            // "list hosts", "which users", "what source types": the values, with how often each appears
            if (fields.Count == 1 && fields[0].Plural && _r.By.Count == 0)
            {
                _r.ValuesOf = fields[0].Field;
                Use(fields[0].Index, fields[0].Length);
                return;
            }

            // Columns need a word that asks for them, or two fields together
            var asks = _t.Any(t => t.Lower is "show" or "display" or "list" or "columns" or "fields" or "table" or "only" or "just" or "with");
            if (!asks && fields.Count < 2)
            {
                return;
            }

            foreach (var (field, _, index, length) in fields)
            {
                if (!_r.Columns.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    _r.Columns.Add(field);
                }

                Use(index, length);
            }

            foreach (var token in _t.Where(t => !t.Used && t.Lower is "columns" or "fields" or "table"))
            {
                token.Used = true;
            }
        }

        // ─── Leftovers become a text search ─────────────────────

        private void ReadLeftovers()
        {
            for (var i = 0; i < _t.Count; i++)
            {
                if (At(i) is not { } token)
                {
                    continue;
                }

                var negated = i > 0 && _t[i - 1].Lower is "not" or "without" or "excluding" or "except" && _t[i - 1].Used == false;
                if (token.Kind == Kind.Quoted)
                {
                    _r.Terms.Add((negated ? "NOT " : string.Empty) + $"\"{token.Text.Replace("\"", string.Empty, StringComparison.Ordinal)}\"");
                    Use(i);
                    continue;
                }

                if (token.Kind is Kind.Operator or Kind.Comma || _w.Filler.Contains(token.Lower) || _w.Negations().Contains(token.Lower)
                    || (_stagesOnly && QueryUnderstander.StageKeywords.Contains(token.Lower)))
                {
                    if (token.Lower is "show" or "list" or "display" or "give" or "get" or "fetch" or "containing" or "contain" or "contains" or "mentioning" or "mention" or "about" or "everything" or "anything" or "happened")
                    {
                        _r.AsksInWords = true;
                    }

                    // Filler isn't a guess, so a field FieldAt guessed it to be doesn't count
                    _pending.Remove(i);
                    token.Used = true;
                    continue;
                }

                if (token.Lower is "containing" or "mentioning" or "about" or "matching" or "including")
                {
                    _r.AsksInWords = true;
                    Use(i);
                    continue;
                }

                if (negated)
                {
                    Use(i - 1);
                }

                var term = token.Text;
                if (token.Kind == Kind.Word && term.Length > 4 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.OrdinalIgnoreCase)
                    && !term.EndsWith("us", StringComparison.OrdinalIgnoreCase) && !term.EndsWith("is", StringComparison.OrdinalIgnoreCase) && term.All(char.IsLetter))
                {
                    term = term[..^1] + "*";
                }

                _r.Terms.Add((negated ? "NOT " : string.Empty) + term);
                _r.UnknownWords.Add(token.Text);
                if (token.Lower is "more" or "less" or "than" or "fewer" or "greater" or "least" or "most" or "over" or "under" or "between" or "same" or "different" or "multiple")
                {
                    _r.Penalty += 0.15;
                }

                Use(i);
            }
        }

        // ─── Defaults that make a reading complete ──────────────

        private void ReadMeasures()
        {
            if (_r.Aggregates.Count == 0 && _r.Top == null && (_r.By.Count > 0 || _r.OverTime))
            {
                for (var i = 0; i < _t.Count; i++)
                {
                    if (_t[i].Used || _t[i].Kind != Kind.Word || !_w.FieldWords.TryGetValue(_t[i].Lower, out var names))
                    {
                        continue;
                    }

                    var function = names[0] switch
                    {
                        "bytes" or "size" => "sum",
                        "duration_ms" or "latency_ms" or "response_time" or "elapsed" => "avg",
                        _ => null,
                    };
                    if (function != null)
                    {
                        _r.Aggregates.Add(new AggregateRequest(function, Choose(names)));
                        _t[i].Used = true;
                        if (At(i + 1)?.Lower is "sent" or "received" or "transferred" or "served" or "taken" or "spent")
                        {
                            Use(i + 1);
                        }

                        break;
                    }
                }
            }
        }

        private void Complete()
        {
            // "by host" with nothing to compute counts
            if (_r.By.Count > 0 && _r.Aggregates.Count == 0 && _r.Top == null && !_r.OverTime)
            {
                _r.Aggregates.Add(new AggregateRequest("count", null));
            }

            if (_r.OverTime && _r.Aggregates.Count == 0)
            {
                _r.Aggregates.Add(new AggregateRequest("count", null));
            }

            if (_r.OverTime && _r.Span == null)
            {
                _r.Span = AutoSpan();
            }

            // A count per group reads best biggest first
            if (_r.Sort == null && !_r.OverTime && _r.By.Count > 0 && _r.Aggregates.Count > 0 && !_stagesOnly)
            {
                _r.Sort = new SortRequest(_r.Aggregates[0].Column, _r.Direction ?? true);
            }
            else if (_r.Sort != null && _r.Direction is { } direction)
            {
                _r.Sort = _r.Sort with { Descending = direction };
            }

            if (_r.Top != null && _r.Limit is { } topLimit && _r.Top.Count == 10)
            {
                _r.Top = _r.Top with { Count = topLimit };
                _r.Limit = null;
            }
        }

        private string AutoSpan()
        {
            var earliest = _r.Earliest is { } spec ? BqlTimeSpec(spec) : null;
            var latest = _r.Latest is { } end ? BqlTimeSpec(end) ?? _now : _now;
            var range = earliest is { } from ? latest - from : TimeSpan.FromDays(1);
            return range.TotalHours switch
            {
                <= 1 => "1m",
                <= 6 => "5m",
                <= 48 => "1h",
                <= 24 * 31 => "1d",
                _ => "1w",
            };
        }

        private DateTime? BqlTimeSpec(string spec) =>
            Bql.BqlTime.ApplyRelative(_now, spec) ?? (DateTime.TryParse(spec, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) ? time : null);

        // ─── Writing BQL ─────────────────────────────────────────

        private string Write()
        {
            var stages = new List<string>();
            var conditions = _r.Conditions.Select(c => c.ToBql()).ToList();
            if (conditions.Count > 0)
            {
                stages.Add("where " + string.Join(" and ", conditions));
            }

            if (_r.Dedup != null)
            {
                stages.Add($"dedup {_r.Dedup}");
            }

            if (_r.Top is { } top)
            {
                stages.Add($"{(top.Rare ? "rare" : "top")} {top.Count.ToString(CultureInfo.InvariantCulture)} {top.Field}");
            }
            else if (_r.OverTime)
            {
                var by = _r.By.Count > 0 ? $" by {_r.By[0]}" : string.Empty;
                stages.Add($"timechart span={_r.Span} {_r.Aggregates[0].ToBql(alias: false)}{by}");
            }
            else if (_r.Aggregates.Count > 0)
            {
                var by = _r.By.Count > 0 ? " by " + string.Join(", ", _r.By) : string.Empty;
                stages.Add($"stats {string.Join(", ", _r.Aggregates.Distinct().Select(a => a.ToBql()))}{by}");
            }
            else if (_r.ValuesOf != null)
            {
                stages.Add($"stats count by {_r.ValuesOf}");
                _r.Sort ??= new SortRequest("count", true);
            }
            else if (_r.Columns.Count > 0)
            {
                stages.Add("table " + string.Join(", ", _r.Columns));
            }

            if (_r.Sort is { } sort && !(sort.Column == "_time" && sort.Descending && _r.Aggregates.Count == 0 && _r.Top == null))
            {
                stages.Add($"sort by {sort.Column} {(sort.Descending ? "desc" : "asc")}");
            }

            if (_r.Limit is { } limit)
            {
                stages.Add($"head {limit.ToString(CultureInfo.InvariantCulture)}");
            }

            _r.Stages = stages;
            var search = new StringBuilder(_r.Terms.Count > 0 ? string.Join(" ", _r.Terms) : "*");
            if (_r.Earliest is { } earliest && _r.Latest == null && Regex.IsMatch(earliest, @"^-\d+[smhdw]$"))
            {
                search.Append(" from last ").Append(earliest[1..]);
            }
            else
            {
                if (_r.Earliest != null)
                {
                    search.Append(" earliest=").Append(_r.Earliest);
                }

                if (_r.Latest != null)
                {
                    search.Append(" latest=").Append(_r.Latest);
                }
            }

            _r.Search = search.ToString();
            var head = _stagesOnly ? string.Empty : "find " + _r.Search;
            return string.Join(" | ", new[] { head }.Concat(stages).Where(s => s.Length > 0));
        }

        /// <summary>The same reading with the severity word searched as a word, offered as an alternative.</summary>
        private void WriteWordReading()
        {
            if (_r.SeverityWord is not { } word || _r.Conditions.FirstOrDefault(c => c.Field == "severity") is not { } level)
            {
                return;
            }

            var (query, stages, search, sort) = (_r.Query, _r.Stages, _r.Search, _r.Sort);
            _r.Conditions.Remove(level);
            var singular = word.ToLowerInvariant().TrimEnd('s');
            _r.Terms.Insert(0, singular);
            _r.WordQuery = Write();
            _r.Terms.RemoveAt(0);
            _r.Conditions.Insert(0, level);
            (_r.Query, _r.Stages, _r.Search, _r.Sort) = (query, stages, search, sort);
        }

        // ─── Saying it back in words ────────────────────────────

        private string Explain()
        {
            var parts = new List<string>();
            var subject = _r.Conditions.FirstOrDefault(c => c.Field == "severity") is { } severity
                ? $"{(severity.Op == ">=" ? severity.Values[0] + " or worse" : "below " + severity.Values[0])} events"
                : "events";
            if (_r.Terms.Count > 0)
            {
                subject += $" matching {string.Join(" ", _r.Terms)}";
            }

            var others = _r.Conditions.Where(c => c.Field != "severity").Select(c => c.Describe()).ToList();
            if (others.Count > 0)
            {
                subject += " where " + string.Join(" and ", others);
            }

            if (_r.Top is { } top)
            {
                parts.Add($"the {top.Count} {(top.Rare ? "least" : "most")} common values of {top.Field} among {subject}");
            }
            else if (_r.OverTime)
            {
                parts.Add($"{_r.Aggregates[0].Describe()} of {subject} every {DescribeSpan(_r.Span!)}{(_r.By.Count > 0 ? $", one line per {_r.By[0]}" : string.Empty)}");
            }
            else if (_r.Aggregates.Count > 0)
            {
                // "the average duration for each path" says enough; only a count needs "of events"
                var of = subject == "events" && _r.Aggregates.All(a => a.Field != null) ? string.Empty : $" of {subject}";
                parts.Add($"{string.Join(", ", _r.Aggregates.Distinct().Select(a => a.Describe()))}{of}{(_r.By.Count > 0 ? " for each " + string.Join(" and ", _r.By) : string.Empty)}");
            }
            else if (_r.ValuesOf != null)
            {
                parts.Add($"each {_r.ValuesOf} in {subject}, with how often it appears");
            }
            else if (_r.Dedup != null)
            {
                parts.Add($"the latest of {subject} for each {_r.Dedup}");
            }
            else
            {
                parts.Add(subject);
            }

            if (_r.Columns.Count > 0)
            {
                parts.Add("showing " + string.Join(", ", _r.Columns));
            }

            parts.Add(DescribeTime());
            if (_r.Sort is { } sort && !(sort.Column == "_time" && sort.Descending))
            {
                parts.Add($"{(sort.Column == "_time" ? "oldest" : sort.Descending ? "highest" : "lowest")} first");
            }

            if (_r.Limit is { } limit)
            {
                parts.Add($"at most {limit.ToString(CultureInfo.InvariantCulture)}");
            }

            var text = string.Join(", ", parts.Where(p => p.Length > 0));
            return char.ToUpperInvariant(text[0]) + text[1..];
        }

        private string DescribeTime()
        {
            if (_r.Earliest == null && _r.Latest == null)
            {
                return string.Empty;
            }

            static string Say(string spec) => spec switch
            {
                "@d" => "the start of today",
                "-1d@d" => "the start of yesterday",
                "@w" => "the start of this week",
                "@M" => "the start of this month",
                "@y" => "the start of this year",
                "@h" => "the start of this hour",
                _ when spec.StartsWith('-') => $"{DescribeSpan(spec[1..])} ago",
                _ when DateTime.TryParse(spec, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) =>
                    time.TimeOfDay == TimeSpan.Zero ? time.ToString("MMM d", CultureInfo.InvariantCulture) : time.ToString("MMM d HH:mm 'UTC'", CultureInfo.InvariantCulture),
                _ => spec,
            };

            if (_r.Earliest == "-1d@d" && _r.Latest == null)
            {
                return "since yesterday";
            }

            if (_r.Earliest == "-1d@d" && _r.Latest == "@d")
            {
                return "yesterday";
            }

            if (_r.Earliest == "@d" && _r.Latest == null)
            {
                return "today";
            }

            if (_r.Earliest is { } earliest && _r.Latest == null && earliest.StartsWith('-') && !earliest.Contains('@'))
            {
                return "in the last " + DescribeSpan(earliest[1..]);
            }

            return (_r.Earliest is { } from ? "from " + Say(from) : string.Empty) + (_r.Latest is { } to ? $"{(_r.Earliest != null ? " " : string.Empty)}until {Say(to)}" : string.Empty);
        }

        private static string DescribeSpan(string span)
        {
            var match = Regex.Match(span, @"^(?<n>\d+)(?<u>[smhdwMy])$");
            if (!match.Success)
            {
                return span;
            }

            var n = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups["u"].Value switch
            {
                "s" => "second",
                "m" => "minute",
                "h" => "hour",
                "d" => "day",
                "w" => "week",
                "M" => "month",
                _ => "year",
            };
            return n == 1 ? unit : $"{n.ToString(CultureInfo.InvariantCulture)} {unit}s";
        }
    }
}

/// <summary>What a natural reading found, and the query it wrote.</summary>
public sealed class NaturalReading
{
    public string Input { get; set; } = string.Empty;

    /// <summary>The canonical BQL, or for <see cref="NaturalReader.ReadStages"/> just the stages.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>The search part of the first stage, without the "find".</summary>
    public string Search { get; set; } = "*";

    /// <summary>The stages after the search.</summary>
    public List<string> Stages { get; set; } = [];

    /// <summary>What the query will do, in words.</summary>
    public string Explanation { get; set; } = string.Empty;

    public List<string> Terms { get; } = [];
    public List<ConditionRequest> Conditions { get; } = [];
    public List<AggregateRequest> Aggregates { get; } = [];
    public List<string> By { get; } = [];
    public List<string> Columns { get; } = [];
    public string? Earliest { get; set; }
    public string? Latest { get; set; }
    public bool OverTime { get; set; }
    public string? Span { get; set; }
    public TopRequest? Top { get; set; }
    public SortRequest? Sort { get; set; }
    public bool? Direction { get; set; }
    public int? Limit { get; set; }
    public string? Dedup { get; set; }
    public string? ValuesOf { get; set; }

    /// <summary>The severity word as written, so a reading can offer it back as a plain word.</summary>
    public string? SeverityWord { get; set; }

    /// <summary>The query with the severity word searched as a plain word instead.</summary>
    public string? WordQuery { get; set; }

    /// <summary>Whether the words ask for something ("show me", "containing") rather than being search text.</summary>
    public bool AsksInWords { get; set; }

    /// <summary>Whether a severity word was the only condition found.</summary>
    public bool SeverityOnly { get; set; }

    /// <summary>Words kept as search text because nothing else fit.</summary>
    public List<string> UnknownWords { get; } = [];

    /// <summary>Typos fixed along the way.</summary>
    public List<QueryCorrection> Corrections { get; } = [];

    /// <summary>How much less sure the reading is, from guesses it made.</summary>
    public double Penalty { get; set; }

    /// <summary>
    /// Whether anything beyond plain words was read: a time, a comparison, a count or an average,
    /// grouping, sorting, a limit or columns. A severity word alone doesn't count, since "error" on
    /// its own has always been a word search.
    /// </summary>
    public bool HasStructure =>
        Earliest != null || Latest != null || Aggregates.Count > 0 || By.Count > 0 || OverTime || Top != null || Sort != null || Limit != null
        || Columns.Count > 0 || Dedup != null || ValuesOf != null || Conditions.Any(c => c.Field != "severity");

    /// <summary>How sure the reading is, from 0 to 1.</summary>
    public double Confidence => Math.Clamp(1.0 - Penalty - (HasStructure ? Math.Min(0.4, UnknownWords.Count * 0.08) : 0), 0, 1);
}

/// <summary>A field compared to one or more values.</summary>
public sealed record ConditionRequest(string Field, string Op, IReadOnlyList<string> Values)
{
    public ConditionRequest Negate() => this with
    {
        Op = Op switch
        {
            "=" => "!=",
            "!=" => "=",
            ">" => "<=",
            ">=" => "<",
            "<" => ">=",
            "<=" => ">",
            "in" => "not in",
            "not in" => "in",
            "contains" => "not contains",
            "starts" => "not starts",
            "ends" => "not ends",
            _ => Op,
        },
    };

    private static string Literal(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? value : $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string Like(string value) => value.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("*", "%", StringComparison.Ordinal);

    public string ToBql() => Op switch
    {
        _ when Field == "severity" => $"severity {Op} {Values[0]}",
        "in" => $"{Field} in ({string.Join(", ", Values.Select(Literal))})",
        "not in" => $"NOT {Field} in ({string.Join(", ", Values.Select(Literal))})",
        "contains" => $"{Field} like \"%{Like(Values[0])}%\"",
        "not contains" => $"NOT {Field} like \"%{Like(Values[0])}%\"",
        "starts" => $"{Field} like \"{Like(Values[0])}%\"",
        "not starts" => $"NOT {Field} like \"{Like(Values[0])}%\"",
        "ends" => $"{Field} like \"%{Like(Values[0])}\"",
        "not ends" => $"NOT {Field} like \"%{Like(Values[0])}\"",
        "between" => $"{Field} >= {Literal(Values[0])} and {Field} <= {Literal(Values[1])}",
        "=" when Values[0].Contains('*') => $"{Field} like \"{Like(Values[0])}\"",
        _ => $"{Field} {Op} {Literal(Values[0])}",
    };

    public string Describe() => Op switch
    {
        "in" => $"{Field} is {string.Join(" or ", Values)}",
        "not in" => $"{Field} isn't {string.Join(" or ", Values)}",
        "contains" => $"{Field} contains {Values[0]}",
        "not contains" => $"{Field} doesn't contain {Values[0]}",
        "starts" => $"{Field} starts with {Values[0]}",
        "not starts" => $"{Field} doesn't start with {Values[0]}",
        "ends" => $"{Field} ends with {Values[0]}",
        "not ends" => $"{Field} doesn't end with {Values[0]}",
        "between" => $"{Field} is between {Values[0]} and {Values[1]}",
        "=" => $"{Field} is {Values[0]}",
        "!=" => $"{Field} isn't {Values[0]}",
        ">" => $"{Field} is over {Values[0]}",
        ">=" => $"{Field} is at least {Values[0]}",
        "<" => $"{Field} is under {Values[0]}",
        "<=" => $"{Field} is at most {Values[0]}",
        _ => $"{Field} {Op} {Values[0]}",
    };
}

/// <summary>One value to compute: a function of a field, or a count.</summary>
public sealed record AggregateRequest(string Function, string? Field)
{
    /// <summary>The column it comes out in.</summary>
    public string Column => Field == null ? "count" : Function switch
    {
        "dc" => $"distinct_{Field}",
        "sum" => $"total_{Field}",
        _ when Function.StartsWith("perc", StringComparison.Ordinal) => $"p{Function[4..]}_{Field}",
        _ => $"{Function}_{Field}",
    };

    public string ToBql(bool alias = true) => Field == null ? "count" : alias ? $"{Function}({Field}) as {Column}" : $"{Function}({Field})";

    public string Describe() => Field == null ? "the count" : Function switch
    {
        "dc" => $"the number of distinct {Field} values",
        "avg" => $"the average {Field}",
        "sum" => $"the total {Field}",
        "max" => $"the highest {Field}",
        "min" => $"the lowest {Field}",
        "median" => $"the median {Field}",
        "stdev" => $"the standard deviation of {Field}",
        "variance" => $"the variance of {Field}",
        _ when Function.StartsWith("perc", StringComparison.Ordinal) => $"the {Function[4..]}th percentile of {Field}",
        _ => $"{Function} of {Field}",
    };
}

/// <summary>The most or least common values of a field.</summary>
public sealed record TopRequest(int Count, string Field, bool Rare);

/// <summary>An order for the results.</summary>
public sealed record SortRequest(string Column, bool Descending);

/// <summary>A word the reader read as another, nearby word.</summary>
public sealed record QueryCorrection(string From, string To, int Start, int Length);

internal static class LexiconExtensions
{
    public static IEnumerable<string> Negations(this Lexicon _) => ["not", "without", "excluding", "exclude", "except", "ignoring", "ignore", "but"];
}
