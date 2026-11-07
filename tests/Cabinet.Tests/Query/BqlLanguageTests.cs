using System.Globalization;
using System.Text.Json;
using Cabinet.Core.Models;
using Cabinet.Parsers;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Query.NaturalLanguage;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>
/// Checks what each function and stage in docs/BQL-GUIDE.md returns, on events from the real
/// parser. Timestamps are in UTC, and CI runs these outside UTC to catch host time zone bugs.
/// </summary>
public class BqlLanguageTests : IAsyncLifetime
{
    private BqlQueryEngine _engine = null!;

    public async Task InitializeAsync()
    {
        var storage = new InMemoryStorage();
        await storage.InitializeAsync();

        var knowledge = new InMemoryKnowledgeStorage();
        await knowledge.InitializeAsync();
        await knowledge.SaveLookupAsync(new LookupTable
        {
            Name = "users",
            Columns = ["user", "email", "active"],
            Rows =
            [
                new Dictionary<string, string> { ["user"] = "ann", ["email"] = "ann@example.com", ["active"] = "true" },
                new Dictionary<string, string> { ["user"] = "bob", ["email"] = "bob@example.com", ["active"] = "false" },
            ],
        });

        string[] lines =
        [
            """{"timestamp":"2026-01-01T10:00:00Z","level":"INFO","message":"BEGIN login","host":"web-01","status":"ok","duration":"5m30s","bytes":100,"user":"ann","session":"s1","note":"a|b"}""",
            """{"timestamp":"2026-01-01T10:10:00Z","level":"ERROR","message":"disk full","host":"web-01","status":"bad","duration":"1m","bytes":300,"user":"bob","session":"s1","note":"c"}""",
            """{"timestamp":"2026-01-01T10:20:00Z","level":"INFO","message":"COMMIT login","host":"web-02","status":"ok","duration":"30s","bytes":200,"user":"ann","session":"s1","note":"a"}""",
            """{"timestamp":"2026-01-01T11:30:00Z","level":"INFO","message":"request ok","host":"db-01","status":"ok","bytes":500,"session":"s2","note":"b|c"}""",
        ];

        var parser = new JsonLogParser();
        var events = new List<LogEvent>();
        foreach (var line in lines)
        {
            events.Add((await parser.ParseAsync(line))!);
        }

        await storage.StoreBatchAsync(events);

        _engine = new BqlQueryEngine(
            storage,
            new BqlParser(),
            new BqlOptimizer(),
            NullLogger<BqlQueryEngine>.Instance,
            new RuleBasedTranslator(),
            knowledge);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<IDictionary<string, object?>>> Run(string query)
    {
        var result = await _engine.ExecuteAsync(new QueryExecutionContext { Query = query, Limit = 1000 });
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    private async Task<object?> Eval(string expression)
    {
        var rows = await Run($"| makeresults | eval x = {expression}");
        return rows.Single()["x"];
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    // ─── Expression functions ───

    [Theory]
    [InlineData("len(\"abc\")", "3")]
    [InlineData("lower(\"AbC\")", "abc")]
    [InlineData("upper(\"AbC\")", "ABC")]
    [InlineData("trim(\"  a  \")", "a")]
    [InlineData("ltrim(\"  a\")", "a")]
    [InlineData("rtrim(\"a  \")", "a")]
    [InlineData("substr(\"abcdef\", 0, 4)", "abcd")]
    [InlineData("replace(\"a1b22\", \"\\d+\", \"N\")", "aNbN")]
    [InlineData("startswith(\"web-1\", \"web\")", "true")]
    [InlineData("endswith(\"a.log\", \".log\")", "true")]
    [InlineData("match(\"id 42\", \"\\d+\")", "true")]
    [InlineData("match(\"id\", \"\\d+\")", "false")]
    [InlineData("urlencode(\"a b\")", "a%20b")]
    [InlineData("urldecode(\"a%20b\")", "a b")]
    [InlineData("printf(\"%s: %d\", \"web\", 3)", "web: 3")]
    [InlineData("format(\"%s/%s\", \"a\", \"b\")", "a/b")]
    [InlineData("printf(\"%.2f\", 1.5)", "1.50")]
    [InlineData("exact(\"AbC\")", "AbC")]
    [InlineData("\"a\" . \" \" . \"b\"", "a b")]
    [InlineData("\"n=\" . 5", "n=5")]
    public async Task StringFunctions(string expression, string expected)
    {
        Text(await Eval(expression)).Should().Be(expected);
    }

    [Theory]
    [InlineData("abs(-2)", "2")]
    [InlineData("round(1.234, 2)", "1.23")]
    [InlineData("ceil(1.2)", "2")]
    [InlineData("floor(1.8)", "1")]
    [InlineData("pow(2, 10)", "1024")]
    [InlineData("sqrt(9)", "3")]
    [InlineData("log(1)", "0")]
    [InlineData("exp(0)", "1")]
    [InlineData("min(3, 2)", "2")]
    [InlineData("max(3, 2)", "3")]
    public async Task MathFunctions(string expression, string expected)
    {
        Text(await Eval(expression)).Should().Be(expected);
    }

    [Theory]
    [InlineData("strftime(0, \"%Y-%m-%d\")", "1970-01-01")]
    [InlineData("strftime(1767225600, \"%Y-%m-%d %H:%M:%S\")", "2026-01-01 00:00:00")]
    [InlineData("strftime(1767225600, \"%y/%b/%j %a %%\")", "26/Jan/001 Thu %")]
    [InlineData("strptime(\"2026-01-01\", \"%Y-%m-%d\")", "1767225600")]
    [InlineData("strptime(\"2026-01-01 00:00:10\", \"%Y-%m-%d %H:%M:%S\")", "1767225610")]
    [InlineData("relative_time(1000, \"-1m\")", "940")]
    [InlineData("relative_time(1000, \"+1h\")", "4600")]
    [InlineData("mktime(\"2026-01-01\")", "1767225600")]
    [InlineData("mktime(\"2026-01-01T00:00:10Z\")", "1767225610")]
    [InlineData("dur2sec(\"5m30s\")", "330")]
    [InlineData("dur2sec(\"1h\")", "3600")]
    [InlineData("dur2sec(\"01:00:05\")", "3605")]
    [InlineData("dur2sec(\"90\")", "90")]
    public async Task DateFunctions(string expression, string expected)
    {
        Text(await Eval(expression)).Should().Be(expected);
    }

    [Theory]
    [InlineData("now()")]
    [InlineData("time()")]
    public async Task Now_IsUnixTime(string expression)
    {
        var value = Convert.ToDouble(await Eval(expression), CultureInfo.InvariantCulture);

        value.Should().BeApproximately(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 120);
    }

    [Fact]
    public async Task RelativeTime_AcceptsNow()
    {
        var value = Convert.ToDouble(await Eval("relative_time(now(), \"-1d\")"), CultureInfo.InvariantCulture);

        value.Should().BeApproximately(DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(), 120);
    }

    [Fact]
    public async Task Ctime_FormatsEpochAndEventTimestamps()
    {
        Text(await Eval("ctime(1767225600)")).Should().StartWith("2026-01-01");

        var rows = await Run("find disk | eval x = ctime(timestamp)");
        Text(rows.Single()["x"]).Should().StartWith("2026-01-01");
    }

    [Fact]
    public async Task Strftime_FormatsEventTime()
    {
        var rows = await Run("find disk | eval x = strftime(_time, \"%Y-%m-%d %H:%M\")");

        Text(rows.Single()["x"]).Should().Be("2026-01-01 10:10");
    }

    [Theory]
    [InlineData("typeof(1)", "Number")]
    [InlineData("typeof(\"a\")", "String")]
    [InlineData("typeof(true)", "Boolean")]
    [InlineData("isnum(1)", "true")]
    [InlineData("isnum(\"a\")", "false")]
    [InlineData("isstr(\"a\")", "true")]
    [InlineData("isint(1)", "true")]
    [InlineData("isint(1.5)", "false")]
    [InlineData("isbool(true)", "true")]
    [InlineData("isnull(nofield)", "true")]
    [InlineData("isnotnull(1)", "true")]
    [InlineData("tonumber(\"5\")", "5")]
    [InlineData("tostring(5)", "5")]
    public async Task TypeFunctions(string expression, string expected)
    {
        Text(await Eval(expression)).Should().Be(expected);
    }

    [Theory]
    [InlineData("if(1 > 0, \"yes\", \"no\")", "yes")]
    [InlineData("if(false, \"yes\", \"no\")", "no")]
    [InlineData("case(1 > 2, \"a\", 2 > 1, \"b\", true, \"c\")", "b")]
    [InlineData("coalesce(nofield, \"fallback\")", "fallback")]
    [InlineData("nullif(1, 2)", "1")]
    [InlineData("nullif(1, 1)", null)]
    public async Task ConditionalFunctions(string expression, string? expected)
    {
        Text(await Eval(expression)).Should().Be(expected);
    }

    [Theory]
    [InlineData("md5(\"a\")", "0cc175b9c0f1b6a831c399e269772661")]
    [InlineData("sha1(\"a\")", "86f7e437faa5a7fce15d1ddcb9eaeaea377667b8")]
    [InlineData("sha256(\"a\")", "ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb")]
    [InlineData("base64encode(\"a\")", "YQ==")]
    [InlineData("base64decode(\"YQ==\")", "a")]
    [InlineData("fromjson(\"{\\\"a\\\":{\\\"b\\\":1}}\", \"a.b\")", "1")]
    [InlineData("spath(\"{\\\"a\\\":\\\"x\\\"}\", \"a\")", "x")]
    [InlineData("json_extract(\"{\\\"a\\\":\\\"x\\\"}\", \"a\")", "x")]
    [InlineData("memk(\"1.5GB\")", "1572864")]
    [InlineData("memk(\"1024\")", "1024")]
    [InlineData("rmunit(\"100ms\")", "100")]
    [InlineData("rmcomma(\"1,234,567\")", "1234567")]
    public async Task EncodingAndUtilityFunctions(string expression, string expected)
    {
        Text(await Eval(expression)).Should().Be(expected);
    }

    [Theory]
    [InlineData("mvcount(split(\"a,b,c\", \",\"))", "3")]
    [InlineData("mvindex(split(\"a,b,c\", \",\"), 1)", "b")]
    [InlineData("mvjoin(split(\"a,b\", \",\"), \"-\")", "a-b")]
    [InlineData("mvjoin(mvappend(split(\"a\", \",\"), split(\"b\", \",\")), \"-\")", "a-b")]
    [InlineData("mvjoin(mvdedup(split(\"a,b,a\", \",\")), \"-\")", "a-b")]
    [InlineData("mvjoin(mvsort(split(\"c,a,b\", \",\")), \"-\")", "a-b-c")]
    [InlineData("mvfind(split(\"ok,error\", \",\"), \"err\")", "1")]
    [InlineData("mvjoin(mvfilter(split(\"err1,ok,err2\", \",\"), match(mv, \"err\")), \"-\")", "err1-err2")]
    public async Task MultiValueFunctions(string expression, string expected)
    {
        Text(await Eval(expression)).Should().Be(expected);
    }

    [Fact]
    public async Task Tags_AreMultiValued()
    {
        var rows = await Run("find disk | eval n = mvcount(tags) | mvexpand tags");

        Convert.ToInt32(rows[0]["n"], CultureInfo.InvariantCulture).Should().Be(1);
        Text(rows[0]["tags"]).Should().Be("json");
    }

    // ─── Filtering ───

    [Fact]
    public async Task Regex_FiltersOnNamedField()
    {
        (await Run("find * | regex message=\"BEGIN|COMMIT\"")).Should().HaveCount(2);
        (await Run("find * | regex host=\"web-\\d+\"")).Should().HaveCount(3);
    }

    [Fact]
    public async Task Regex_Negated_ExcludesMatches()
    {
        (await Run("find * | regex host!=\"web-\\d+\"")).Should().ContainSingle();
    }

    [Fact]
    public async Task Regex_WithoutField_SearchesTheRawLine()
    {
        (await Run("find * | regex \"disk\"")).Should().ContainSingle();
    }

    [Fact]
    public async Task Where_FieldsNamedLikeSeverities_AreFields()
    {
        var rows = await Run("| makeresults | eval error = 7 | where error = 7");

        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Where_SeverityName_MatchesSeverityValue()
    {
        (await Run("find * | where severity >= warning")).Should().ContainSingle();
    }

    // ─── Aggregation ───

    [Fact]
    public async Task Stats_CountOfField_CountsNonNull()
    {
        var rows = await Run("find * | stats count(user) as n");

        Convert.ToInt32(rows.Single()["n"], CultureInfo.InvariantCulture).Should().Be(3);
    }

    [Fact]
    public async Task Stats_MinMax_WorkOnStrings()
    {
        var rows = await Run("find * | stats min(user) as lo, max(user) as hi");

        Text(rows.Single()["lo"]).Should().Be("ann");
        Text(rows.Single()["hi"]).Should().Be("bob");
    }

    [Fact]
    public async Task Stats_UnknownFunction_IsAnError()
    {
        var act = () => Run("find * | stats nosuchfunction(bytes)");

        await act.Should().ThrowAsync<QueryParseException>();
    }

    [Fact]
    public async Task Stats_GroupValuesContainingSeparators_StayDistinct()
    {
        // ("a|b", "c") and ("a", "b|c") would collide if keys were joined with "|".
        var rows = await Run("find * | eval k = if(note = \"a\", \"b|c\", \"c\") | stats count by note, k");

        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Stats_StdevOfOneValue_IsNullAndSerializable()
    {
        var rows = await Run("find disk | stats stdev(bytes) as s");

        rows.Single()["s"].Should().BeNull();
        var serialize = () => JsonSerializer.Serialize(rows);
        serialize.Should().NotThrow();
    }

    [Fact]
    public async Task Sqrt_OfNegative_IsNullAndSerializable()
    {
        (await Eval("sqrt(-1)")).Should().BeNull();
    }

    [Theory]
    [InlineData("median(bytes)", 250)]
    [InlineData("perc50(bytes)", 250)]
    [InlineData("p100(bytes)", 500)]
    [InlineData("sum(bytes)", 1100)]
    [InlineData("avg(bytes)", 275)]
    [InlineData("dc(user)", 2)]
    public async Task Stats_Functions(string function, double expected)
    {
        var rows = await Run($"find * | stats {function} as v");

        Convert.ToDouble(rows.Single()["v"], CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [Fact]
    public async Task EventStats_AppendsToEachEvent()
    {
        var rows = await Run("find * | eventstats sum(bytes) as total by host");

        rows.Should().HaveCount(4);
        rows.Should().OnlyContain(r => r.ContainsKey("total") && r.ContainsKey("RawLine"));
    }

    [Fact]
    public async Task StreamStats_IsCumulative()
    {
        var rows = await Run("find * | sort by timestamp asc | streamstats count as running");

        rows.Select(r => Convert.ToInt32(r["running"], CultureInfo.InvariantCulture)).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Timechart_BucketsByTime()
    {
        var rows = await Run("find * | timechart span=1h count");

        rows.Should().HaveCount(2);
        Convert.ToInt32(rows[0]["count"], CultureInfo.InvariantCulture).Should().Be(3);
    }

    [Theory]
    [InlineData("dc(user)", 2)]
    [InlineData("sum(bytes)", 600)]
    [InlineData("perc100(bytes)", 300)]
    [InlineData("median(bytes)", 200)]
    public async Task Timechart_ComputesTheRequestedFunction(string function, double expected)
    {
        var rows = await Run($"find * | timechart span=1h {function}");

        Convert.ToDouble(rows[0][function], CultureInfo.InvariantCulture).Should().Be(expected);
    }

    [Fact]
    public async Task Top_ByField_RanksWithinEachGroup()
    {
        var rows = await Run("find * | top 1 user by host");

        rows.Should().HaveCount(2, "web-01 and web-02 have users; db-01 has none");
        rows.Should().Contain(r => Text(r["host"]) == "web-02" && Text(r["user"]) == "ann");
    }

    [Fact]
    public async Task Rare_ReturnsLeastCommonFirst()
    {
        var rows = await Run("find * | rare user");

        Text(rows[0]["user"]).Should().Be("bob");
    }

    [Fact]
    public async Task Chart_CrossTabulates()
    {
        var rows = await Run("find * | chart count over host by status | sort by host asc");

        rows.Should().HaveCount(3);
        var web01 = rows.Single(r => Text(r["host"]) == "web-01");
        Convert.ToInt32(web01["ok"], CultureInfo.InvariantCulture).Should().Be(1);
        Convert.ToInt32(web01["bad"], CultureInfo.InvariantCulture).Should().Be(1);
    }

    // ─── Transformation ───

    [Fact]
    public async Task Eval_ConcatenatesFields()
    {
        var rows = await Run("find disk | eval x = host . \":\" . user");

        Text(rows.Single()["x"]).Should().Be("web-01:bob");
    }

    [Fact]
    public async Task Eval_NumericFieldsAddAsNumbers()
    {
        var rows = await Run("find disk | eval x = bytes + 1");

        Convert.ToDouble(rows.Single()["x"], CultureInfo.InvariantCulture).Should().Be(301);
    }

    [Fact]
    public async Task Convert_ChangesTheFieldInPlace()
    {
        var rows = await Run("find disk | convert dur2sec(duration)");

        Convert.ToDouble(rows.Single()["duration"], CultureInfo.InvariantCulture).Should().Be(60);
    }

    [Fact]
    public async Task Convert_AsAlias_KeepsTheOriginal()
    {
        var rows = await Run("find disk | convert dur2sec(duration) as secs");

        Convert.ToDouble(rows.Single()["secs"], CultureInfo.InvariantCulture).Should().Be(60);
        Text(rows.Single()["duration"]).Should().Be("1m");
    }

    [Fact]
    public async Task FieldSummary_HasDocumentedColumns()
    {
        var rows = await Run("find * | fieldsummary");
        var bytes = rows.Single(r => Text(r["field"]) == "bytes");

        Convert.ToInt32(bytes["count"], CultureInfo.InvariantCulture).Should().Be(4);
        Convert.ToInt32(bytes["distinct_count"], CultureInfo.InvariantCulture).Should().Be(4);
        Text(bytes["type"]).Should().Be("numeric");
        Convert.ToDouble(bytes["min"], CultureInfo.InvariantCulture).Should().Be(100);
        Convert.ToDouble(bytes["max"], CultureInfo.InvariantCulture).Should().Be(500);
        Convert.ToDouble(bytes["mean"], CultureInfo.InvariantCulture).Should().Be(275);
        bytes["stdev"].Should().NotBeNull();
    }

    [Fact]
    public async Task MvExpand_ExpandsEveryEvent()
    {
        var rows = await Run("find * | eval parts = split(note, \"|\") | mvexpand parts");

        rows.Should().HaveCount(6);
    }

    [Fact]
    public async Task XySeries_Pivots()
    {
        var rows = await Run("find * | stats count by host, status | xyseries host status count");

        rows.Should().HaveCount(3);
        Convert.ToInt32(rows.Single(r => Text(r["host"]) == "web-01")["bad"], CultureInfo.InvariantCulture).Should().Be(1);
    }

    [Fact]
    public async Task Transaction_StartAndEndMarkers_BoundTheGroup()
    {
        var rows = await Run("find * | transaction session startswith=\"BEGIN\" endswith=\"COMMIT\"");

        var transaction = rows.Should().ContainSingle().Subject;
        Convert.ToInt32(transaction["_eventcount"], CultureInfo.InvariantCulture).Should().Be(3);
    }

    [Fact]
    public async Task Lookup_Enriches()
    {
        var rows = await Run("find disk | lookup users user OUTPUT email");

        Text(rows.Single()["email"]).Should().Be("bob@example.com");
    }

    [Fact]
    public async Task InputLookup_ThenWhere_Filters()
    {
        var rows = await Run("| inputlookup users | where active = \"true\"");

        Text(rows.Single()["user"]).Should().Be("ann");
    }

    [Fact]
    public async Task Tail_ReturnsTheLastRows()
    {
        var rows = await Run("find * | sort by timestamp asc | tail 1");

        Text(rows.Single()["message"]).Should().Be("request ok");
    }

    // ─── Errors and missing values ───

    [Theory]
    [InlineData("find * | eval x = nosuchfunction(host)")]
    [InlineData("find * | eval x = replace(host, \"(\", \"x\")")]
    [InlineData("find * | eval x = match(host, \"(\")")]
    [InlineData("find * | regex host=\"(\"")]
    public async Task InvalidQueries_AreErrorsNotEmptyResults(string query)
    {
        var act = () => Run(query);

        await act.Should().ThrowAsync<QueryParseException>();
    }

    [Theory]
    [InlineData("abs(nofield)")]
    [InlineData("sqrt(nofield)")]
    [InlineData("round(nofield, 2)")]
    [InlineData("pow(nofield, 2)")]
    [InlineData("nofield . \"x\"")]
    [InlineData("nofield + 1")]
    [InlineData("strftime(nofield, \"%Y\")")]
    public async Task MissingInput_GivesMissingOutput(string expression)
    {
        (await Eval(expression)).Should().BeNull();
    }

    [Fact]
    public async Task Text_IsOnlyNumericIfItPlainlyIsANumber()
    {
        (await Eval("isnum(\"1,2\")")).Should().Be(false);
        (await Eval("isnum(\"$5\")")).Should().Be(false);
        (await Eval("isnum(\"NaN\")")).Should().Be(false);
        (await Eval("isnum(\"-1.5e3\")")).Should().Be(true);
    }

    // ─── Time comparisons ───

    [Theory]
    [InlineData("find * | where timestamp >= \"2026-01-01T11:00:00Z\"", 1)]
    [InlineData("find * | where _time < \"2026-01-01T10:05:00Z\"", 1)]
    [InlineData("find * | where _time > relative_time(1767265200, \"-1h\")", 3)]
    [InlineData("find * | where severity in (error, critical)", 1)]
    public async Task TimeAndSeverityComparisons(string query, int expected)
    {
        (await Run(query)).Should().HaveCount(expected);
    }

    [Theory]
    [InlineData(1767225600, "-1d@d", 1767139200)]
    [InlineData(1767229199, "@h", 1767225600)]
    [InlineData(1767229199, "@d", 1767225600)]
    public async Task RelativeTime_Snaps(long epoch, string spec, long expected)
    {
        Convert.ToInt64(await Eval($"relative_time({epoch}, \"{spec}\")"), CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    // ─── Natural language ───

    [Fact]
    public async Task NaturalLanguage_TimeRangeAfterWhere_Parses()
    {
        var act = () => Run("find * | where severity >= 4 from last 24h");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NaturalLanguage_Errors()
    {
        var rows = await Run("how many errors");

        Convert.ToInt32(rows.Single()["count"], CultureInfo.InvariantCulture).Should().Be(1);
    }
}
