using Cabinet.Query.Bql;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlExpressionEvaluatorTests
{
    private static IDictionary<string, object?> MakeContext(params (string key, object? value)[] fields)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
            dict[key] = value;
        return dict;
    }

    // ─── Literals ────────────────────────────────────────────

    [Fact]
    public void Evaluate_StringLiteral_ReturnsString()
    {
        var expr = new LiteralExpression("hello");
        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext());
        result.Should().Be("hello");
    }

    [Fact]
    public void Evaluate_NumberLiteral_ReturnsNumber()
    {
        var expr = new LiteralExpression(42L);
        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext());
        result.Should().Be(42L);
    }

    // ─── Field access ────────────────────────────────────────

    [Fact]
    public void Evaluate_FieldExpression_ReturnsFieldValue()
    {
        var expr = new FieldExpression("host");
        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext(("host", "web-01")));
        result.Should().Be("web-01");
    }

    [Fact]
    public void Evaluate_FieldExpression_CaseInsensitive()
    {
        var expr = new FieldExpression("HOST");
        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext(("host", "web-01")));
        result.Should().Be("web-01");
    }

    [Fact]
    public void Evaluate_MissingField_ReturnsNull()
    {
        var expr = new FieldExpression("nonexistent");
        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext());
        result.Should().BeNull();
    }

    // ─── Comparisons ─────────────────────────────────────────

    [Theory]
    [InlineData(10, "=", 10, true)]
    [InlineData(10, "!=", 5, true)]
    [InlineData(10, ">", 5, true)]
    [InlineData(10, ">=", 10, true)]
    [InlineData(5, "<", 10, true)]
    [InlineData(5, "<=", 5, true)]
    [InlineData(10, "=", 5, false)]
    [InlineData(10, "<", 5, false)]
    public void EvaluateBool_NumericComparisons_Work(int left, string op, int right, bool expected)
    {
        var expr = new ComparisonExpression(
            new LiteralExpression((long)left),
            op,
            new LiteralExpression((long)right));

        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext()).Should().Be(expected);
    }

    [Fact]
    public void EvaluateBool_StringComparison_CaseInsensitive()
    {
        var expr = new ComparisonExpression(
            new LiteralExpression("Hello"),
            "=",
            new LiteralExpression("hello"));

        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext()).Should().BeTrue();
    }

    // ─── Logical operators ───────────────────────────────────

    [Fact]
    public void EvaluateBool_And_BothMustBeTrue()
    {
        var expr = new LogicalExpression(
            new ComparisonExpression(new LiteralExpression(1L), "=", new LiteralExpression(1L)),
            "AND",
            new ComparisonExpression(new LiteralExpression(2L), "=", new LiteralExpression(2L)));

        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext()).Should().BeTrue();
    }

    [Fact]
    public void EvaluateBool_Or_EitherCanBeTrue()
    {
        var expr = new LogicalExpression(
            new ComparisonExpression(new LiteralExpression(1L), "=", new LiteralExpression(2L)),
            "OR",
            new ComparisonExpression(new LiteralExpression(2L), "=", new LiteralExpression(2L)));

        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext()).Should().BeTrue();
    }

    [Fact]
    public void EvaluateBool_Not_InvertsResult()
    {
        var expr = new NotExpression(
            new ComparisonExpression(new LiteralExpression(1L), "=", new LiteralExpression(2L)));

        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext()).Should().BeTrue();
    }

    // ─── Arithmetic ──────────────────────────────────────────

    [Fact]
    public void Evaluate_Addition_Works()
    {
        var expr = new ArithmeticExpression(new LiteralExpression(10.0), "+", new LiteralExpression(5.0));
        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().Be(15.0);
    }

    [Fact]
    public void Evaluate_Subtraction_Works()
    {
        var expr = new ArithmeticExpression(new LiteralExpression(10.0), "-", new LiteralExpression(3.0));
        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().Be(7.0);
    }

    [Fact]
    public void Evaluate_StringConcatenation_WithPlus()
    {
        var expr = new ArithmeticExpression(new LiteralExpression("hello "), "+", new LiteralExpression("world"));
        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().Be("hello world");
    }

    [Fact]
    public void Evaluate_DivisionByZero_ReturnsNull()
    {
        var expr = new ArithmeticExpression(new LiteralExpression(10.0), "/", new LiteralExpression(0.0));
        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().BeNull();
    }

    // ─── IN expression ───────────────────────────────────────

    [Fact]
    public void EvaluateBool_InExpression_MatchesValues()
    {
        var expr = new InExpression(
            new FieldExpression("status"),
            new List<BqlExpression>
            {
                new LiteralExpression("400"),
                new LiteralExpression("401"),
                new LiteralExpression("403")
            });

        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext(("status", "401"))).Should().BeTrue();
        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext(("status", "200"))).Should().BeFalse();
    }

    // ─── LIKE expression ─────────────────────────────────────

    [Fact]
    public void EvaluateBool_LikeExpression_MatchesPattern()
    {
        var expr = new LikeExpression(new FieldExpression("message"), "%timeout%");

        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext(("message", "connection timeout error"))).Should().BeTrue();
        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext(("message", "success"))).Should().BeFalse();
    }

    // ─── Function expressions ────────────────────────────────

    [Fact]
    public void Evaluate_LenFunction_ReturnsStringLength()
    {
        var expr = new FunctionExpression("len", new List<BqlExpression> { new LiteralExpression("hello") });
        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().Be(5);
    }

    [Fact]
    public void Evaluate_LowerFunction_ConvertsToLowerCase()
    {
        var expr = new FunctionExpression("lower", new List<BqlExpression> { new LiteralExpression("HELLO") });
        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().Be("hello");
    }

    [Fact]
    public void Evaluate_UpperFunction_ConvertsToUpperCase()
    {
        var expr = new FunctionExpression("upper", new List<BqlExpression> { new LiteralExpression("hello") });
        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().Be("HELLO");
    }

    [Fact]
    public void Evaluate_CoalesceFunction_ReturnsFirstNonNull()
    {
        var expr = new FunctionExpression("coalesce", new List<BqlExpression>
        {
            new FieldExpression("missing"),
            new FieldExpression("present"),
            new LiteralExpression("default")
        });

        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext(("present", "value")));
        result.Should().Be("value");
    }

    [Fact]
    public void Evaluate_IfFunction_EvalsCondition()
    {
        var expr = new FunctionExpression("if", new List<BqlExpression>
        {
            new ComparisonExpression(new FieldExpression("status"), "=", new LiteralExpression("200")),
            new LiteralExpression("OK"),
            new LiteralExpression("ERROR")
        });

        BqlExpressionEvaluator.Evaluate(expr, MakeContext(("status", "200"))).Should().Be("OK");
        BqlExpressionEvaluator.Evaluate(expr, MakeContext(("status", "500"))).Should().Be("ERROR");
    }

    [Fact]
    public void Evaluate_ReplaceFunction_ReplacesText()
    {
        var expr = new FunctionExpression("replace", new List<BqlExpression>
        {
            new LiteralExpression("hello world"),
            new LiteralExpression("world"),
            new LiteralExpression("everyone")
        });

        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().Be("hello everyone");
    }

    [Fact]
    public void Evaluate_AbsFunction_ReturnsAbsoluteValue()
    {
        var expr = new FunctionExpression("abs", new List<BqlExpression> { new LiteralExpression(-42.0) });
        BqlExpressionEvaluator.Evaluate(expr, MakeContext()).Should().Be(42.0);
    }

    // ─── Complex expressions ─────────────────────────────────

    [Fact]
    public void EvaluateBool_ComplexAndOrNot_Works()
    {
        // (status = 200 and host = "web-01") or (severity >= 4 and not host = "db-01")
        var expr = new LogicalExpression(
            new LogicalExpression(
                new ComparisonExpression(new FieldExpression("status"), "=", new LiteralExpression("200")),
                "AND",
                new ComparisonExpression(new FieldExpression("host"), "=", new LiteralExpression("web-01"))),
            "OR",
            new LogicalExpression(
                new ComparisonExpression(new FieldExpression("severity"), ">=", new LiteralExpression(4L)),
                "AND",
                new NotExpression(
                    new ComparisonExpression(new FieldExpression("host"), "=", new LiteralExpression("db-01")))));

        // Should match: status=200, host=web-01
        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext(
            ("status", "200"), ("host", "web-01"), ("severity", 2))).Should().BeTrue();

        // Should match: severity=5, host=worker-01 (not db-01)
        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext(
            ("status", "500"), ("host", "worker-01"), ("severity", 5))).Should().BeTrue();

        // Should NOT match: severity=5, host=db-01 (NOT condition fails)
        BqlExpressionEvaluator.EvaluateBool(expr, MakeContext(
            ("status", "500"), ("host", "db-01"), ("severity", 5))).Should().BeFalse();
    }

    // ─── New function tests ──────────────────────────────────

    [Fact]
    public void EvalFunction_Lpad_PadsLeft()
    {
        var expr = new FunctionExpression("lpad",
            new List<BqlExpression> { new LiteralExpression("42"), new LiteralExpression(5L), new LiteralExpression("0") });
        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext());
        result.Should().Be("00042");
    }

    [Fact]
    public void EvalFunction_Rpad_PadsRight()
    {
        var expr = new FunctionExpression("rpad",
            new List<BqlExpression> { new LiteralExpression("hi"), new LiteralExpression(5L) });
        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext());
        result.Should().Be("hi   ");
    }

    [Fact]
    public void EvalFunction_Ln_ReturnsNaturalLog()
    {
        var expr = new FunctionExpression("ln",
            new List<BqlExpression> { new LiteralExpression(Math.E) });
        var result = (double)BqlExpressionEvaluator.Evaluate(expr, MakeContext())!;
        result.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void EvalFunction_Atan2_ReturnsAngle()
    {
        var expr = new FunctionExpression("atan2",
            new List<BqlExpression> { new LiteralExpression(1.0), new LiteralExpression(1.0) });
        var result = (double)BqlExpressionEvaluator.Evaluate(expr, MakeContext())!;
        result.Should().BeApproximately(Math.PI / 4, 0.001);
    }

    [Fact]
    public void EvalFunction_Like_MatchesWildcardPattern()
    {
        var expr = new FunctionExpression("like",
            new List<BqlExpression> { new LiteralExpression("error: connection failed"), new LiteralExpression("%connection%") });
        var result = BqlExpressionEvaluator.Evaluate(expr, MakeContext());
        result.Should().Be(true);
    }

    [Fact]
    public void EvalFunction_SearchMatch_FindsFieldValue()
    {
        var expr = new FunctionExpression("searchmatch",
            new List<BqlExpression> { new LiteralExpression("error") });
        var ctx = MakeContext(("message", "An error occurred"), ("host", "web-01"));
        var result = BqlExpressionEvaluator.Evaluate(expr, ctx);
        result.Should().Be(true);
    }

    [Fact]
    public void EvalFunction_SearchMatch_ReturnsFalseWhenNoMatch()
    {
        var expr = new FunctionExpression("searchmatch",
            new List<BqlExpression> { new LiteralExpression("critical") });
        var ctx = MakeContext(("message", "All systems normal"), ("host", "web-01"));
        var result = BqlExpressionEvaluator.Evaluate(expr, ctx);
        result.Should().Be(false);
    }

    // ─── Null handling in IN expression ────────────────────────

    [Fact]
    public void EvaluateIn_NullField_ReturnsFalse()
    {
        var expr = new InExpression(
            new FieldExpression("status"),
            new List<BqlExpression> { new LiteralExpression("active"), new LiteralExpression("pending") });
        var ctx = MakeContext(("status", null));
        var result = BqlExpressionEvaluator.Evaluate(expr, ctx);
        result.Should().Be(false);
    }

    [Fact]
    public void EvaluateIn_MissingField_ReturnsFalse()
    {
        var expr = new InExpression(
            new FieldExpression("nonexistent"),
            new List<BqlExpression> { new LiteralExpression("value") });
        var ctx = MakeContext(("other", "data"));
        var result = BqlExpressionEvaluator.Evaluate(expr, ctx);
        result.Should().Be(false);
    }

    // ─── Null handling in LIKE expression ──────────────────────

    [Fact]
    public void EvaluateLike_NullField_ReturnsFalse()
    {
        var expr = new LikeExpression(new FieldExpression("name"), "%test%");
        var ctx = MakeContext(("name", null));
        var result = BqlExpressionEvaluator.Evaluate(expr, ctx);
        result.Should().Be(false);
    }

    [Fact]
    public void EvaluateLike_WildcardOnNull_ReturnsFalse()
    {
        var expr = new LikeExpression(new FieldExpression("name"), "%");
        var ctx = MakeContext(("name", null));
        var result = BqlExpressionEvaluator.Evaluate(expr, ctx);
        result.Should().Be(false);
    }

    // ─── Floating-point comparison symmetry ────────────────────

    [Fact]
    public void Comparison_FloatingPoint_EqualAndNotEqualAreSymmetric()
    {
        // Values that differ by less than epsilon should be equal
        var ctx = MakeContext(("x", 1.0000000001), ("y", 1.0));
        var eqExpr = new ComparisonExpression(new FieldExpression("x"), "==", new FieldExpression("y"));
        var neExpr = new ComparisonExpression(new FieldExpression("x"), "!=", new FieldExpression("y"));

        var eqResult = BqlExpressionEvaluator.Evaluate(eqExpr, ctx);
        var neResult = BqlExpressionEvaluator.Evaluate(neExpr, ctx);

        // They must be exact opposites — never both true
        ((bool)eqResult!).Should().NotBe((bool)neResult!);
    }

    [Fact]
    public void Comparison_FloatingPoint_NearZeroEpsilon()
    {
        // Near-zero comparison should use absolute epsilon
        var ctx = MakeContext(("x", 0.0000000001), ("y", 0.0));
        var eqExpr = new ComparisonExpression(new FieldExpression("x"), "==", new FieldExpression("y"));
        var result = BqlExpressionEvaluator.Evaluate(eqExpr, ctx);
        ((bool)result!).Should().BeTrue();
    }

    [Fact]
    public void Comparison_FloatingPoint_GreaterEqualWithEpsilon()
    {
        // 1.0000000001 >= 1.0 should be true with epsilon
        var ctx = MakeContext(("x", 1.0000000001), ("y", 1.0));
        var geExpr = new ComparisonExpression(new FieldExpression("x"), ">=", new FieldExpression("y"));
        var result = BqlExpressionEvaluator.Evaluate(geExpr, ctx);
        ((bool)result!).Should().BeTrue();
    }

    [Fact]
    public void Comparison_NullEquality()
    {
        var ctx = MakeContext(("a", null), ("b", null));

        var eqExpr = new ComparisonExpression(new FieldExpression("a"), "==", new FieldExpression("b"));
        ((bool)BqlExpressionEvaluator.Evaluate(eqExpr, ctx)!).Should().BeFalse("as in SPL, a comparison with a missing value is false");

        var neExpr = new ComparisonExpression(new FieldExpression("a"), "!=", new FieldExpression("b"));
        ((bool)BqlExpressionEvaluator.Evaluate(neExpr, ctx)!).Should().BeFalse("null != null should be false");
    }

    [Fact]
    public void Comparison_NullVsValue_NotEqual()
    {
        var ctx = MakeContext(("a", null), ("b", "hello"));

        var eqExpr = new ComparisonExpression(new FieldExpression("a"), "==", new FieldExpression("b"));
        ((bool)BqlExpressionEvaluator.Evaluate(eqExpr, ctx)!).Should().BeFalse();

        var neExpr = new ComparisonExpression(new FieldExpression("a"), "!=", new FieldExpression("b"));
        ((bool)BqlExpressionEvaluator.Evaluate(neExpr, ctx)!).Should().BeFalse("as in SPL, where x != y drops events without x");
    }

    [Fact]
    public void If_EvaluatesOnlyTheBranchTaken()
    {
        var ctx = MakeContext(("a", 1));
        var badRegex = new FunctionExpression("match", [new FieldExpression("a"), new LiteralExpression("(")]);
        var expr = new FunctionExpression("if", [new LiteralExpression(true), new LiteralExpression("ok"), badRegex]);

        BqlExpressionEvaluator.Evaluate(expr, ctx).Should().Be("ok");
        BqlExpressionEvaluator.Evaluate(new FunctionExpression("case", [new LiteralExpression(true), new LiteralExpression("ok"), new LiteralExpression(true), badRegex]), ctx)
            .Should().Be("ok");
        BqlExpressionEvaluator.Evaluate(new FunctionExpression("coalesce", [new LiteralExpression("ok"), badRegex]), ctx).Should().Be("ok");
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    public void If_TreatsFalseTextAsFalse(string value, bool expected)
    {
        var ctx = MakeContext(("flag", value));
        var expr = new FunctionExpression("if", [new FieldExpression("flag"), new LiteralExpression("y"), new LiteralExpression("n")]);

        BqlExpressionEvaluator.Evaluate(expr, ctx).Should().Be(expected ? "y" : "n");
    }
}
