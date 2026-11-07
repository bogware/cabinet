using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cabinet.Query.Bql;

/// <summary>
/// Evaluates BQL expressions against event dictionaries.
/// Used by both the legacy operator wrappers and the BQL query engine.
/// </summary>
public static class BqlExpressionEvaluator
{
    /// <summary>Timeout for user-supplied regex patterns to prevent ReDoS.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Evaluate an expression and return a boolean result (for where/filter)
    /// </summary>
    public static bool EvaluateBool(BqlExpression expr, IDictionary<string, object?> context)
    {
        var result = Evaluate(expr, context);
        return IsTruthy(result);
    }

    /// <summary>
    /// Evaluate an expression and return the raw result (for eval/compute)
    /// </summary>
    public static object? Evaluate(BqlExpression expr, IDictionary<string, object?> context) => expr switch
    {
        LiteralExpression lit => lit.Value,
        FieldExpression field => GetFieldValue(context, field.Name),
        ArithmeticExpression arith => Finite(EvaluateArithmetic(arith, context)),
        ComparisonExpression comp => EvaluateComparison(comp, context),
        LogicalExpression logical => EvaluateLogical(logical, context),
        NotExpression not => !EvaluateBool(not.Operand, context),
        InExpression inExpr => EvaluateIn(inExpr, context),
        LikeExpression like => EvaluateLike(like, context),
        FunctionExpression func => Finite(EvaluateFunction(func, context)),
        _ => null
    };

    private static object? Finite(object? value) =>
        value is double number && !double.IsFinite(number) ? null : value;

    private static object? EvaluateArithmetic(ArithmeticExpression expr, IDictionary<string, object?> context)
    {
        var left = Evaluate(expr.Left, context);
        var right = Evaluate(expr.Right, context);

        if (expr.Operator == ".")
            return left == null || right == null ? null : $"{FormatValue(left)}{FormatValue(right)}";

        // String concatenation with +
        if (expr.Operator == "+" && (left is string || right is string))
            return $"{FormatValue(left)}{FormatValue(right)}";

        var l = ToDouble(left);
        var r = ToDouble(right);
        if (!l.HasValue || !r.HasValue) return null;

        return expr.Operator switch
        {
            "+" => l.Value + r.Value,
            "-" => l.Value - r.Value,
            "*" => l.Value * r.Value,
            "/" when r.Value == 0 => null,
            "/" => l.Value / r.Value,
            "%" when r.Value == 0 => null,
            "%" => l.Value % r.Value,
            _ => null
        };
    }

    private static object EvaluateComparison(ComparisonExpression expr, IDictionary<string, object?> context)
    {
        var left = Evaluate(expr.Left, context);
        var right = Evaluate(expr.Right, context);

        // Null handling
        if (left == null && right == null) return expr.Operator is "=" or "==";
        if (left == null || right == null) return expr.Operator is "!=" or "<>";

        // A time compared with a date string compares as times
        left = CoerceTimeOperand(left, right);
        right = CoerceTimeOperand(right, left);

        // Numeric comparison
        var numLeft = ToDouble(left);
        var numRight = ToDouble(right);
        if (numLeft.HasValue && numRight.HasValue)
        {
            // Use absolute epsilon for near-zero values, relative epsilon otherwise
            var diff = Math.Abs(numLeft.Value - numRight.Value);
            var absMax = Math.Max(Math.Abs(numLeft.Value), Math.Abs(numRight.Value));
            var areEqual = diff <= 1e-9 || (absMax > 0 && diff / absMax < 1e-9);

            return expr.Operator switch
            {
                "=" or "==" => areEqual,
                "!=" or "<>" => !areEqual,
                ">" => numLeft.Value > numRight.Value && !areEqual,
                ">=" => numLeft.Value >= numRight.Value || areEqual,
                "<" => numLeft.Value < numRight.Value && !areEqual,
                "<=" => numLeft.Value <= numRight.Value || areEqual,
                _ => false
            };
        }

        // String comparison (case-insensitive)
        var strLeft = left?.ToString() ?? "";
        var strRight = right?.ToString() ?? "";
        var cmp = string.Compare(strLeft, strRight, StringComparison.OrdinalIgnoreCase);

        return expr.Operator switch
        {
            "=" or "==" => cmp == 0,
            "!=" or "<>" => cmp != 0,
            ">" => cmp > 0,
            ">=" => cmp >= 0,
            "<" => cmp < 0,
            "<=" => cmp <= 0,
            _ => false
        };
    }

    private static object? CoerceTimeOperand(object? value, object? other) =>
        value is string text && other is DateTime && BqlTime.Parse(text, null) is { } parsed ? parsed : value;

    private static object EvaluateLogical(LogicalExpression expr, IDictionary<string, object?> context)
    {
        return expr.Operator switch
        {
            "AND" => EvaluateBool(expr.Left, context) && EvaluateBool(expr.Right, context),
            "OR" => EvaluateBool(expr.Left, context) || EvaluateBool(expr.Right, context),
            _ => false
        };
    }

    private static object EvaluateIn(InExpression expr, IDictionary<string, object?> context)
    {
        var value = Evaluate(expr.Field, context);
        // Null values never match IN lists (consistent with SQL/Splunk semantics)
        if (value == null) return false;
        var strValue = value.ToString() ?? "";

        foreach (var item in expr.Values)
        {
            var itemValue = Evaluate(item, context);
            if (string.Equals(strValue, itemValue?.ToString(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static object EvaluateLike(LikeExpression expr, IDictionary<string, object?> context)
    {
        var rawValue = Evaluate(expr.Field, context);
        // Null values never match LIKE patterns (consistent with SQL/Splunk semantics)
        if (rawValue == null) return false;
        var value = rawValue.ToString() ?? "";
        // Guard against excessively long LIKE patterns
        if (expr.Pattern.Length > 10_000) return false;
        // Convert SQL LIKE pattern to regex: first replace wildcards, then escape the rest
        var pattern = expr.Pattern;
        // Temporarily replace % and _ with placeholders that won't be escaped
        pattern = pattern.Replace("%%", "\x01").Replace("%", "\x02").Replace("_", "\x03").Replace("\x01", "%");
        pattern = "^" + Regex.Escape(pattern)
            .Replace("\x02", ".*")
            .Replace("\x03", ".") + "$";
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static object? EvaluateFunction(FunctionExpression func, IDictionary<string, object?> context)
    {
        var args = func.Arguments.Select(a => Evaluate(a, context)).ToList();

        return func.Name switch
        {
            // String functions
            "len" or "length" => args.FirstOrDefault()?.ToString()?.Length ?? 0,
            "lower" => args.FirstOrDefault()?.ToString()?.ToLowerInvariant(),
            "upper" => args.FirstOrDefault()?.ToString()?.ToUpperInvariant(),
            "trim" => args.FirstOrDefault()?.ToString()?.Trim(),
            "ltrim" => args.FirstOrDefault()?.ToString()?.TrimStart(),
            "rtrim" => args.FirstOrDefault()?.ToString()?.TrimEnd(),
            "substr" or "substring" => EvalSubstring(args),
            "replace" => EvalReplace(args),
            "split" => EvalSplit(args),
            "urlencode" => Uri.EscapeDataString(args.FirstOrDefault()?.ToString() ?? ""),
            "urldecode" => Uri.UnescapeDataString(args.FirstOrDefault()?.ToString() ?? ""),
            "startswith" => args.ElementAtOrDefault(0)?.ToString()?.StartsWith(args.ElementAtOrDefault(1)?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            "endswith" => args.ElementAtOrDefault(0)?.ToString()?.EndsWith(args.ElementAtOrDefault(1)?.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false,
            "match" => EvalMatch(args),
            "lpad" => EvalLpad(args),
            "rpad" => EvalRpad(args),
            "like" => EvalLike(args),

            // Null/coalesce
            "coalesce" => args.FirstOrDefault(a => a != null),
            "isnull" => args.FirstOrDefault() == null,
            "isnotnull" => args.FirstOrDefault() != null,
            "nullif" => Equals(args.ElementAtOrDefault(0)?.ToString(), args.ElementAtOrDefault(1)?.ToString()) ? null : args.ElementAtOrDefault(0),

            // Conditional
            "if" => EvalIf(args, context, func),
            "case" => EvalCase(args, context, func),

            // Type conversion
            "tostring" => args.FirstOrDefault()?.ToString(),
            "tonumber" when args.Count > 1 => EvalToNumberBase(args),
            "tonumber" => ToDouble(args.FirstOrDefault()),

            // Math functions
            "abs" => ToDouble(args.FirstOrDefault()) is { } number ? Math.Abs(number) : null,
            "round" => ToDouble(args.FirstOrDefault()) is { } number
                ? Math.Round(number, Math.Clamp((int)(ToDouble(args.ElementAtOrDefault(1)) ?? 0), 0, 15))
                : null,
            "ceil" or "ceiling" => ToDouble(args.FirstOrDefault()) is { } number ? Math.Ceiling(number) : null,
            "floor" => ToDouble(args.FirstOrDefault()) is { } number ? Math.Floor(number) : null,
            "pow" => ToDouble(args.ElementAtOrDefault(0)) is { } power && ToDouble(args.ElementAtOrDefault(1)) is { } exponent
                ? Math.Pow(power, exponent)
                : null,
            "sqrt" => ToDouble(args.FirstOrDefault()) is { } number ? Math.Sqrt(number) : null,
            "log" => EvalLog(args),
            "ln" => Math.Log(ToDouble(args.FirstOrDefault()) ?? 1),
            "exp" => ToDouble(args.FirstOrDefault()) is { } number ? Math.Exp(number) : null,
            "pi" => Math.PI,
            "atan2" => Math.Atan2(ToDouble(args.ElementAtOrDefault(0)) ?? 0, ToDouble(args.ElementAtOrDefault(1)) ?? 1),
            "sigfig" => EvalSigfig(args),
            "min" => EvalMinMax(args, min: true),
            "max" => EvalMinMax(args, min: false),
            "random" => Random.Shared.Next(),

            // Date/time functions
            "now" => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            "time" => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            "strftime" => EvalStrftime(args),
            "strptime" => EvalStrptime(args),
            "relative_time" => EvalRelativeTime(args),

            // JSON functions
            "tojson" or "json_object" => EvalToJson(args),
            "fromjson" or "spath" => EvalFromJson(args),
            "json_extract" => EvalJsonExtract(args),

            // Multi-value functions
            "mvcount" => EvalMvCount(args),
            "mvindex" => EvalMvIndex(args),
            "mvjoin" => EvalMvJoin(args),
            "mvappend" => EvalMvAppend(args),
            "mvdedup" => EvalMvDedup(args),
            "mvsort" => EvalMvSort(args),
            "mvfind" => EvalMvFind(args),
            "mvfilter" => EvalMvFilter(args, context, func),

            // Hash functions
            "md5" => EvalHash(args, "MD5"),
            "sha1" => EvalHash(args, "SHA1"),
            "sha256" => EvalHash(args, "SHA256"),
            "sha512" => EvalHash(args, "SHA512"),

            // Encoding functions
            "base64encode" or "base64_encode" => EvalBase64Encode(args),
            "base64decode" or "base64_decode" => EvalBase64Decode(args),

            // Type / validation functions
            "typeof" => EvalTypeOf(args),
            "validate" => EvalValidate(args, context, func),
            "searchmatch" => EvalSearchMatch(args, context),
            "isnum" or "isnumber" => ToDouble(args.FirstOrDefault()).HasValue,
            "isstr" or "isstring" => args.FirstOrDefault() is string,
            "isint" or "isinteger" => IsInteger(args.FirstOrDefault()),
            "isbool" or "isboolean" => args.FirstOrDefault() is bool,

            // Convert functions (Splunk parity: ctime, mktime, dur2sec, memk, rmunit)
            "ctime" => EvalCtime(args),
            "mktime" => EvalMktime(args),
            "dur2sec" => EvalDur2Sec(args),
            "memk" => EvalMemK(args),
            "rmunit" => EvalRmUnit(args),
            "rmcomma" => args.FirstOrDefault()?.ToString()?.Replace(",", ""),

            // String formatting
            "printf" or "format" => EvalPrintf(args),
            "exact" => args.FirstOrDefault(),

            _ => throw new QueryParseException($"Unknown function: {func.Name}")
        };
    }

    private static object? EvalLpad(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString() ?? "";
        var width = (int)(ToDouble(args.ElementAtOrDefault(1)) ?? 0);
        var padChar = args.ElementAtOrDefault(2)?.ToString()?.FirstOrDefault() ?? ' ';
        return str.PadLeft(Math.Min(width, 1000), padChar);
    }

    private static object? EvalRpad(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString() ?? "";
        var width = (int)(ToDouble(args.ElementAtOrDefault(1)) ?? 0);
        var padChar = args.ElementAtOrDefault(2)?.ToString()?.FirstOrDefault() ?? ' ';
        return str.PadRight(Math.Min(width, 1000), padChar);
    }

    private static object? EvalLike(List<object?> args)
    {
        // Null values should not match any pattern (SQL/Splunk semantics)
        if (args.ElementAtOrDefault(0) == null) return false;
        var str = args[0]!.ToString() ?? "";
        var pattern = args.ElementAtOrDefault(1)?.ToString() ?? "";
        if (pattern.Length > 10_000) return false; // Guard against excessively long patterns
        // SQL-like LIKE: % = any chars, _ = single char
        var regex = "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$";
        try { return Regex.IsMatch(str, regex, RegexOptions.IgnoreCase, RegexTimeout); }
        catch { return false; }
    }

    private static object? EvalSigfig(List<object?> args)
    {
        var val = ToDouble(args.ElementAtOrDefault(0));
        var figs = (int)(ToDouble(args.ElementAtOrDefault(1)) ?? 3);
        if (!val.HasValue || figs <= 0) return val;
        if (val.Value == 0) return 0.0;
        var scale = Math.Pow(10, Math.Floor(Math.Log10(Math.Abs(val.Value))) + 1 - figs);
        return Math.Round(val.Value / scale) * scale;
    }

    private static object? EvalSubstring(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString();
        if (str == null) return null;
        var start = (int)(ToDouble(args.ElementAtOrDefault(1)) ?? 0);
        var length = (int)(ToDouble(args.ElementAtOrDefault(2)) ?? str.Length);

        // Guard against negative/out-of-range index arguments
        if (start < 0) start = 0;
        if (start >= str.Length) return "";
        if (length <= 0) return "";
        return str.Substring(start, Math.Min(length, str.Length - start));
    }

    private static object? EvalIf(List<object?> args, IDictionary<string, object?> context, FunctionExpression func)
    {
        if (func.Arguments.Count >= 3)
        {
            var condition = EvaluateBool(func.Arguments[0], context);
            return condition ? Evaluate(func.Arguments[1], context) : Evaluate(func.Arguments[2], context);
        }
        return null;
    }

    private static object? EvalReplace(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString();
        var pattern = args.ElementAtOrDefault(1)?.ToString();
        var replacement = args.ElementAtOrDefault(2)?.ToString() ?? "";
        if (str == null || pattern == null) return str;

        try
        {
            return Regex.Replace(str, pattern, replacement, RegexOptions.None, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new QueryParseException($"Invalid regular expression '{pattern}': {ex.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            return str;
        }
    }

    private static object? EvalSplit(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString();
        var sep = args.ElementAtOrDefault(1)?.ToString() ?? ",";
        return str?.Split(sep);
    }

    private static object EvalMvCount(List<object?> args)
    {
        var val = args.FirstOrDefault();
        return AsMultiValue(val)?.Count ?? (val != null ? 1 : 0);
    }

    /// <summary>The values of a multi-value field, or null if the value isn't multi-valued.</summary>
    private static List<object?>? AsMultiValue(object? value) =>
        value is string ? null : (value as IEnumerable<object?>)?.ToList();

    private static object? EvalMatch(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString();
        var pattern = args.ElementAtOrDefault(1)?.ToString();
        if (str == null || pattern == null) return false;
        if (pattern.Length > 10_000) return false; // Guard against excessively long patterns

        try
        {
            return Regex.IsMatch(str, pattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new QueryParseException($"Invalid regular expression '{pattern}': {ex.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static object? EvalCase(List<object?> args, IDictionary<string, object?> context, FunctionExpression func)
    {
        // case(cond1, val1, cond2, val2, ..., default)
        for (var i = 0; i + 1 < func.Arguments.Count; i += 2)
        {
            if (EvaluateBool(func.Arguments[i], context))
                return Evaluate(func.Arguments[i + 1], context);
        }
        // If odd number of args, last is default
        if (func.Arguments.Count % 2 == 1)
            return Evaluate(func.Arguments[^1], context);
        return null;
    }

    private static object? EvalLog(List<object?> args)
    {
        var val = ToDouble(args.ElementAtOrDefault(0));
        if (!val.HasValue) return null;
        var @base = ToDouble(args.ElementAtOrDefault(1));
        return @base.HasValue ? Math.Log(val.Value, @base.Value) : Math.Log(val.Value);
    }

    private static object? EvalMinMax(List<object?> args, bool min)
    {
        var values = args.Select(ToDouble).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (values.Count == 0) return null;
        return min ? values.Min() : values.Max();
    }

    private static object? EvalStrftime(List<object?> args)
    {
        var value = args.ElementAtOrDefault(0);
        var format = args.ElementAtOrDefault(1)?.ToString() ?? "%Y-%m-%d %H:%M:%S";
        return BqlTime.FromValue(value, ToDouble(value)) is { } time ? BqlTime.Format(time, format) : null;
    }

    private static object? EvalStrptime(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString();
        if (str == null) return null;
        return BqlTime.Parse(str, args.ElementAtOrDefault(1)?.ToString()) is { } time ? BqlTime.ToEpoch(time) : null;
    }

    private static object? EvalRelativeTime(List<object?> args)
    {
        var value = args.ElementAtOrDefault(0);
        var spec = args.ElementAtOrDefault(1)?.ToString();
        if (BqlTime.FromValue(value, ToDouble(value)) is not { } time) return null;
        if (string.IsNullOrEmpty(spec)) return BqlTime.ToEpoch(time);
        return BqlTime.ApplyRelative(time, spec) is { } result ? BqlTime.ToEpoch(result) : null;
    }

    private static object? EvalToJson(List<object?> args)
    {
        var val = args.FirstOrDefault();
        if (val == null) return "null";
        try { return JsonSerializer.Serialize(val); }
        catch { return val.ToString(); }
    }

    private static object? EvalFromJson(List<object?> args)
    {
        var json = args.ElementAtOrDefault(0)?.ToString();
        if (string.IsNullOrEmpty(json)) return null;
        var path = args.ElementAtOrDefault(1)?.ToString();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (string.IsNullOrEmpty(path))
            {
                return ExtractJsonElement(doc.RootElement);
            }
            // Navigate JSON path (simple dot notation: "field.subfield")
            var current = doc.RootElement;
            foreach (var segment in path.Split('.'))
            {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next))
                    current = next;
                else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var idx) && idx < current.GetArrayLength())
                    current = current[idx];
                else
                    return null;
            }
            return ExtractJsonElement(current);
        }
        catch { return null; }
    }

    private static object? EvalJsonExtract(List<object?> args) => EvalFromJson(args);

    private static object? ExtractJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };

    private static object? EvalMvIndex(List<object?> args)
    {
        var val = args.ElementAtOrDefault(0);
        var idx = (int)(ToDouble(args.ElementAtOrDefault(1)) ?? 0);
        if (AsMultiValue(val) is { } values) return idx >= 0 && idx < values.Count ? values[idx] : null;
        return idx == 0 ? val : null;
    }

    private static object? EvalMvJoin(List<object?> args)
    {
        var val = args.ElementAtOrDefault(0);
        var sep = args.ElementAtOrDefault(1)?.ToString() ?? ",";
        return AsMultiValue(val) is { } values ? string.Join(sep, values) : val?.ToString();
    }

    private static object? EvalMvAppend(List<object?> args)
    {
        var result = new List<object?>();
        foreach (var arg in args)
        {
            if (AsMultiValue(arg) is { } values) result.AddRange(values);
            else if (arg != null) result.Add(arg);
        }
        return result.ToArray();
    }

    private static object? EvalMvDedup(List<object?> args)
    {
        var val = args.FirstOrDefault();
        return AsMultiValue(val) is { } values ? values.Distinct().ToArray() : val;
    }

    private static object? EvalMvSort(List<object?> args)
    {
        var val = args.FirstOrDefault();
        return AsMultiValue(val) is { } values
            ? values.OrderBy(v => v?.ToString(), StringComparer.OrdinalIgnoreCase).ToArray()
            : val;
    }

    // ─── Hash / encoding / type helpers ──────────────────────

    private static object? EvalHash(List<object?> args, string algorithm)
    {
        var str = args.FirstOrDefault()?.ToString();
        if (str == null) return null;
        var bytes = Encoding.UTF8.GetBytes(str);
        byte[] hash = algorithm switch
        {
            "MD5" => MD5.HashData(bytes),
            "SHA1" => SHA1.HashData(bytes),
            "SHA256" => SHA256.HashData(bytes),
            "SHA512" => SHA512.HashData(bytes),
            _ => SHA256.HashData(bytes)
        };
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static object? EvalBase64Encode(List<object?> args)
    {
        var str = args.FirstOrDefault()?.ToString();
        if (str == null) return null;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(str));
    }

    private static object? EvalBase64Decode(List<object?> args)
    {
        var str = args.FirstOrDefault()?.ToString();
        if (str == null) return null;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(str)); }
        catch { return null; }
    }

    private static object? EvalTypeOf(List<object?> args)
    {
        var val = args.FirstOrDefault();
        if (val == null) return "Null";
        if (val is bool) return "Boolean";
        if (val is int or long or double or float or decimal) return "Number";
        if (val is DateTime) return "DateTime";
        if (AsMultiValue(val) != null) return "MultiValue";
        return "String";
    }

    private static object? EvalValidate(List<object?> args, IDictionary<string, object?> context, FunctionExpression func)
    {
        // Splunk validate(condition1, message1, condition2, message2, ...)
        // Returns null if all conditions pass, otherwise the first failing message
        for (var i = 0; i + 1 < func.Arguments.Count; i += 2)
        {
            var condition = EvaluateBool(func.Arguments[i], context);
            if (!condition)
            {
                return Evaluate(func.Arguments[i + 1], context)?.ToString();
            }
        }
        return null;
    }

    private static object? EvalSearchMatch(List<object?> args, IDictionary<string, object?> context)
    {
        // searchmatch(searchText) — returns true if any field value contains the search text
        var searchText = args.FirstOrDefault()?.ToString();
        if (string.IsNullOrEmpty(searchText)) return false;

        foreach (var value in context.Values)
        {
            if (value?.ToString()?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }

    private static bool IsInteger(object? val)
    {
        if (val is int or long) return true;
        if (val is double number) return double.IsFinite(number) && Math.Floor(number) == number;
        if (val is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
        return false;
    }

    private static object? EvalMvFind(List<object?> args)
    {
        var val = args.ElementAtOrDefault(0);
        var pattern = args.ElementAtOrDefault(1)?.ToString();
        if (pattern == null) return null;
        string[] items;
        if (val is string[] arr) items = arr;
        else if (val is IEnumerable<object?> list) items = list.Select(x => x?.ToString() ?? "").ToArray();
        else return null;

        try
        {
            for (var i = 0; i < items.Length; i++)
            {
                if (Regex.IsMatch(items[i], pattern, RegexOptions.IgnoreCase, RegexTimeout))
                    return i;
            }
        }
        catch { /* invalid regex */ }
        return null;
    }

    private static object? EvalMvFilter(List<object?> args, IDictionary<string, object?> context, FunctionExpression func)
    {
        // mvfilter(mv, condition), where the condition sees each value under the field's name and
        // as `mv`; or mvfilter(condition), which filters the first field the condition names.
        var field = func.Arguments.Count >= 2
            ? func.Arguments[0] as FieldExpression
            : FirstField(func.Arguments.FirstOrDefault());
        var condition = func.Arguments.Count >= 2 ? func.Arguments[1] : func.Arguments.FirstOrDefault();
        var source = func.Arguments.Count >= 2 || field == null ? args.FirstOrDefault() : Evaluate(field, context);
        if (condition == null) return source;

        var values = AsMultiValue(source) ?? (source != null ? new List<object?> { source } : []);
        var scope = new BqlRow();
        foreach (var kvp in context)
        {
            scope[kvp.Key] = kvp.Value;
        }

        var kept = new List<object?>();
        foreach (var value in values)
        {
            scope["mv"] = value;
            if (field != null)
            {
                scope[field.Name] = value;
            }

            if (EvaluateBool(condition, scope))
            {
                kept.Add(value);
            }
        }

        return kept.ToArray();
    }

    private static FieldExpression? FirstField(BqlExpression? expression) => expression switch
    {
        FieldExpression field => field,
        FunctionExpression function => function.Arguments.Select(FirstField).FirstOrDefault(f => f != null),
        ComparisonExpression comparison => FirstField(comparison.Left) ?? FirstField(comparison.Right),
        LogicalExpression logical => FirstField(logical.Left) ?? FirstField(logical.Right),
        NotExpression not => FirstField(not.Operand),
        _ => null,
    };

    private static object? EvalCtime(List<object?> args)
    {
        // Convert a time or epoch to a readable string
        var value = args.ElementAtOrDefault(0);
        var format = args.ElementAtOrDefault(1)?.ToString() ?? "%Y-%m-%d %H:%M:%S";
        return BqlTime.FromValue(value, ToDouble(value)) is { } time ? BqlTime.Format(time, format) : null;
    }

    private static object? EvalMktime(List<object?> args)
    {
        // Convert a time or date string to epoch seconds
        var value = args.ElementAtOrDefault(0);
        if (value is DateTime time) return BqlTime.ToEpoch(time);
        var text = value?.ToString();
        if (text == null) return null;
        return BqlTime.Parse(text, null) is { } parsed ? BqlTime.ToEpoch(parsed) : ToDouble(text);
    }

    private static object? EvalDur2Sec(List<object?> args)
    {
        // Durations: "5m30s", "1h", "HH:MM:SS", "MM:SS" or plain seconds
        var str = args.FirstOrDefault()?.ToString()?.Trim();
        if (string.IsNullOrEmpty(str)) return null;

        var units = Regex.Match(
            str,
            @"^(?:(?<d>\d+(?:\.\d+)?)d)?(?:(?<h>\d+(?:\.\d+)?)h)?(?:(?<m>\d+(?:\.\d+)?)m)?(?:(?<s>\d+(?:\.\d+)?)s)?$",
            RegexOptions.IgnoreCase,
            RegexTimeout);
        if (units.Success)
        {
            double Part(string name, double scale) => units.Groups[name].Success
                ? double.Parse(units.Groups[name].Value, CultureInfo.InvariantCulture) * scale
                : 0;
            return Part("d", 86400) + Part("h", 3600) + Part("m", 60) + Part("s", 1);
        }

        var parts = str.Split(':').Select(ToDouble).ToList();
        if (parts.Any(p => !p.HasValue)) return null;
        return parts.Count switch
        {
            3 => parts[0]!.Value * 3600 + parts[1]!.Value * 60 + parts[2]!.Value,
            2 => parts[0]!.Value * 60 + parts[1]!.Value,
            1 => parts[0],
            _ => null,
        };
    }

    private static object? EvalMemK(List<object?> args)
    {
        // Convert memory string (e.g., "512MB", "2GB") to kilobytes
        var str = args.FirstOrDefault()?.ToString()?.Trim();
        if (str == null) return null;
        var match = Regex.Match(str, @"^([\d.]+)\s*(B|K|KB|M|MB|G|GB|T|TB)?$", RegexOptions.IgnoreCase, RegexTimeout);
        if (!match.Success) return ToDouble(str);
        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value.ToUpperInvariant();
        return unit switch
        {
            "B" => value / 1024.0,
            "K" or "KB" or "" => value,
            "M" or "MB" => value * 1024.0,
            "G" or "GB" => value * 1024.0 * 1024.0,
            "T" or "TB" => value * 1024.0 * 1024.0 * 1024.0,
            _ => value
        };
    }

    private static object? EvalRmUnit(List<object?> args)
    {
        // Remove unit suffix, keep numeric part
        var str = args.FirstOrDefault()?.ToString()?.Trim();
        if (str == null) return null;
        var match = Regex.Match(str, @"^([\d.,-]+)", RegexOptions.None, RegexTimeout);
        return match.Success ? ToDouble(match.Groups[1].Value.Replace(",", "")) : null;
    }

    private static object? EvalToNumberBase(List<object?> args)
    {
        // tonumber(str, base) — convert string with given base to number
        var str = args.ElementAtOrDefault(0)?.ToString();
        var numBase = (int)(ToDouble(args.ElementAtOrDefault(1)) ?? 10);
        if (str == null) return null;
        try { return (double)System.Convert.ToInt64(str, numBase); }
        catch { return null; }
    }

    private static object? EvalPrintf(List<object?> args)
    {
        // printf("%s has %d items", name, count). Supports %s %d %i %f %e %g %x %X %%, with
        // optional left-justify, zero padding, width and precision.
        var format = args.ElementAtOrDefault(0)?.ToString();
        if (format == null) return null;

        var next = 1;
        return Regex.Replace(format, @"%(?<flags>[-0]*)(?<width>\d*)(?:\.(?<precision>\d+))?(?<type>[sdifeEgxX%])", match =>
        {
            var type = match.Groups["type"].Value[0];
            if (type == '%') return "%";

            var arg = args.ElementAtOrDefault(next++);
            var precision = match.Groups["precision"].Success ? int.Parse(match.Groups["precision"].Value, CultureInfo.InvariantCulture) : (int?)null;

            // .NET allows hundreds of millions of digits, so one query could allocate gigabytes
            if (precision > 100) precision = 100;
            var number = ToDouble(arg);
            var text = type switch
            {
                's' => precision is { } max && FormatValue(arg).Length > max ? FormatValue(arg)[..max] : FormatValue(arg),
                'd' or 'i' => number is { } d ? Math.Truncate(d).ToString("0", CultureInfo.InvariantCulture) : "",
                'f' => number?.ToString($"F{precision ?? 6}", CultureInfo.InvariantCulture) ?? "",
                'e' or 'E' => number?.ToString($"{type}{precision ?? 6}", CultureInfo.InvariantCulture) ?? "",
                'g' => number?.ToString("G", CultureInfo.InvariantCulture) ?? "",
                _ => number is { } x ? ((long)x).ToString(type.ToString(), CultureInfo.InvariantCulture) : "",
            };

            var flags = match.Groups["flags"].Value;
            var width = match.Groups["width"].Length > 0 ? int.Parse(match.Groups["width"].Value, CultureInfo.InvariantCulture) : 0;
            width = Math.Min(width, 1000);
            if (flags.Contains('-')) return text.PadRight(width);
            return flags.Contains('0') && type != 's' ? text.PadLeft(width, '0') : text.PadLeft(width);
        }, RegexOptions.None, RegexTimeout);
    }

    /// <summary>Text form of a value: whole numbers without decimals, times in ISO 8601.</summary>
    private static string FormatValue(object? value) => value switch
    {
        null => "",
        bool flag => flag ? "true" : "false",
        DateTime time => BqlTime.ToUtc(time).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        double number when Math.Floor(number) == number && Math.Abs(number) < 1e15 => number.ToString("0", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    // ─── Utility ─────────────────────────────────────────────

    // Thread-local cache to avoid rebuilding per-expression evaluation.
    // The cache is keyed on the context dictionary reference — if the same event
    // dictionary is evaluated multiple times, the case-insensitive index is reused.
    [ThreadStatic]
    private static WeakReference<IDictionary<string, object?>>? _lastContext;
    [ThreadStatic]
    private static Dictionary<string, string>? _ciKeyMap;

    private static object? GetFieldValue(IDictionary<string, object?> context, string name)
    {
        // Fast path: exact case match
        if (context.TryGetValue(name, out var val)) return val;

        // Build or reuse a case-insensitive key map for the current context
        IDictionary<string, object?>? cached = null;
        if (_lastContext == null || !_lastContext.TryGetTarget(out cached) || !ReferenceEquals(cached, context))
        {
            _ciKeyMap = new Dictionary<string, string>(context.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var key in context.Keys)
            {
                _ciKeyMap.TryAdd(key, key);
            }
            _lastContext = new WeakReference<IDictionary<string, object?>>(context);
        }

        if (_ciKeyMap != null && _ciKeyMap.TryGetValue(name, out var actualKey))
        {
            context.TryGetValue(actualKey, out val);
            return val;
        }

        return null;
    }

    /// <summary>
    /// Convert a value to double. Public for use by operators that need numeric coercion.
    /// </summary>
    public static double? ToDoublePublic(object? value) => ToDouble(value);

    private static double? ToDouble(object? value)
    {
        if (value == null) return null;
        if (value is double d) return d;
        if (value is long l) return l;
        if (value is int i) return i;
        if (value is float f) return f;
        if (value is decimal dec) return (double)dec;
        if (value is bool b) return b ? 1.0 : 0.0;
        if (value is DateTime time) return BqlTime.ToEpoch(time);
        if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
            return parsed;
        return null;
    }

    private static bool IsTruthy(object? value)
    {
        if (value == null) return false;
        if (value is bool b) return b;
        if (value is int i) return i != 0;
        if (value is long l) return l != 0;
        if (value is double d) return d != 0;
        if (value is string s) return !string.IsNullOrEmpty(s);
        return true;
    }
}
