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
        ArithmeticExpression arith => EvaluateArithmetic(arith, context),
        ComparisonExpression comp => EvaluateComparison(comp, context),
        LogicalExpression logical => EvaluateLogical(logical, context),
        NotExpression not => !EvaluateBool(not.Operand, context),
        InExpression inExpr => EvaluateIn(inExpr, context),
        LikeExpression like => EvaluateLike(like, context),
        FunctionExpression func => EvaluateFunction(func, context),
        _ => null
    };

    private static object? EvaluateArithmetic(ArithmeticExpression expr, IDictionary<string, object?> context)
    {
        var left = Evaluate(expr.Left, context);
        var right = Evaluate(expr.Right, context);

        // String concatenation with +
        if (expr.Operator == "+" && (left is string || right is string))
            return $"{left}{right}";

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
        var strLeft = left.ToString() ?? "";
        var strRight = right.ToString() ?? "";
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
            "abs" => Math.Abs(ToDouble(args.FirstOrDefault()) ?? 0),
            "round" => Math.Round(ToDouble(args.FirstOrDefault()) ?? 0, (int)(ToDouble(args.ElementAtOrDefault(1)) ?? 0)),
            "ceil" or "ceiling" => Math.Ceiling(ToDouble(args.FirstOrDefault()) ?? 0),
            "floor" => Math.Floor(ToDouble(args.FirstOrDefault()) ?? 0),
            "pow" => Math.Pow(ToDouble(args.ElementAtOrDefault(0)) ?? 0, ToDouble(args.ElementAtOrDefault(1)) ?? 1),
            "sqrt" => Math.Sqrt(ToDouble(args.FirstOrDefault()) ?? 0),
            "log" => EvalLog(args),
            "ln" => Math.Log(ToDouble(args.FirstOrDefault()) ?? 1),
            "exp" => Math.Exp(ToDouble(args.FirstOrDefault()) ?? 0),
            "pi" => Math.PI,
            "atan2" => Math.Atan2(ToDouble(args.ElementAtOrDefault(0)) ?? 0, ToDouble(args.ElementAtOrDefault(1)) ?? 1),
            "sigfig" => EvalSigfig(args),
            "min" => EvalMinMax(args, min: true),
            "max" => EvalMinMax(args, min: false),
            "random" => Random.Shared.Next(),

            // Date/time functions
            "now" => DateTime.UtcNow,
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
            "exact" => ToDouble(args.FirstOrDefault()),

            _ => null
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
        var find = args.ElementAtOrDefault(1)?.ToString();
        var replacement = args.ElementAtOrDefault(2)?.ToString() ?? "";
        if (str == null || find == null) return str;
        return str.Replace(find, replacement, StringComparison.Ordinal);
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
        if (val is string[] arr) return arr.Length;
        if (val is IEnumerable<object?> list) return list.Count();
        return val != null ? 1 : 0;
    }

    private static object? EvalMatch(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString();
        var pattern = args.ElementAtOrDefault(1)?.ToString();
        if (str == null || pattern == null) return false;
        if (pattern.Length > 10_000) return false; // Guard against excessively long patterns
        try
        {
            // Pre-compile with timeout to protect against ReDoS during compilation
            var regex = new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout);
            return regex.IsMatch(str);
        }
        catch { return false; }
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
        var dt = args.ElementAtOrDefault(0);
        var format = args.ElementAtOrDefault(1)?.ToString() ?? "yyyy-MM-dd HH:mm:ss";
        if (dt is DateTime dateTime) return dateTime.ToString(format, CultureInfo.InvariantCulture);
        var epoch = ToDouble(dt);
        if (epoch.HasValue)
        {
            try
            {
                var utcDt = DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value).UtcDateTime;
                return utcDt.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        return null;
    }

    private static object? EvalStrptime(List<object?> args)
    {
        var str = args.ElementAtOrDefault(0)?.ToString();
        var format = args.ElementAtOrDefault(1)?.ToString();
        if (str == null) return null;
        if (format != null && DateTime.TryParseExact(str, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
            return exact;
        if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return null;
    }

    private static object? EvalRelativeTime(List<object?> args)
    {
        var dt = args.ElementAtOrDefault(0);
        var offset = args.ElementAtOrDefault(1)?.ToString();
        DateTime baseTime;
        if (dt is DateTime d) baseTime = d;
        else return null;
        if (string.IsNullOrEmpty(offset)) return baseTime;

        // Parse relative time spec like "-1h", "+30m", "-7d"
        var match = Regex.Match(offset, @"^([+-]?\d+)([smhdwMy])$", RegexOptions.None, RegexTimeout);
        if (!match.Success) return baseTime;
        var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "s" => baseTime.AddSeconds(amount),
            "m" => baseTime.AddMinutes(amount),
            "h" => baseTime.AddHours(amount),
            "d" => baseTime.AddDays(amount),
            "w" => baseTime.AddDays(amount * 7),
            "M" => baseTime.AddMonths(amount),
            "y" => baseTime.AddYears(amount),
            _ => baseTime
        };
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
        if (val is string[] arr) return idx >= 0 && idx < arr.Length ? arr[idx] : null;
        if (val is IList<object?> list) return idx >= 0 && idx < list.Count ? list[idx] : null;
        return idx == 0 ? val : null;
    }

    private static object? EvalMvJoin(List<object?> args)
    {
        var val = args.ElementAtOrDefault(0);
        var sep = args.ElementAtOrDefault(1)?.ToString() ?? ",";
        if (val is string[] arr) return string.Join(sep, arr);
        if (val is IEnumerable<object?> list) return string.Join(sep, list);
        return val?.ToString();
    }

    private static object? EvalMvAppend(List<object?> args)
    {
        var result = new List<object?>();
        foreach (var arg in args)
        {
            if (arg is string[] arr) result.AddRange(arr.Cast<object?>());
            else if (arg is IEnumerable<object?> list) result.AddRange(list);
            else if (arg != null) result.Add(arg);
        }
        return result.ToArray();
    }

    private static object? EvalMvDedup(List<object?> args)
    {
        var val = args.FirstOrDefault();
        if (val is string[] arr) return arr.Distinct().ToArray();
        if (val is IEnumerable<object?> list) return list.Distinct().ToArray();
        return val;
    }

    private static object? EvalMvSort(List<object?> args)
    {
        var val = args.FirstOrDefault();
        if (val is string[] arr) { var sorted = arr.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(); return sorted; }
        return val;
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
        if (val is int or long) return "Integer";
        if (val is double or float or decimal) return "Number";
        if (val is DateTime) return "DateTime";
        if (val is string[] or IEnumerable<object?>) return "MultiValue";
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
        // mvfilter(expression) — filter multi-value field by expression
        // In practice this is complex; simplified: filter by truthiness of expression applied to each element
        var val = args.FirstOrDefault();
        if (val is string[] arr)
            return arr.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        if (val is IEnumerable<object?> list)
            return list.Where(x => x != null).ToArray();
        return val;
    }

    private static object? EvalCtime(List<object?> args)
    {
        // Convert epoch time to human-readable string
        var epoch = ToDouble(args.ElementAtOrDefault(0));
        if (!epoch.HasValue) return null;
        var format = args.ElementAtOrDefault(1)?.ToString() ?? "yyyy-MM-dd HH:mm:ss";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value).UtcDateTime;
            return dt.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static object? EvalMktime(List<object?> args)
    {
        // Convert date string to epoch time
        var str = args.ElementAtOrDefault(0)?.ToString();
        if (str == null) return null;
        if (args.ElementAtOrDefault(0) is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeSeconds();
        if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return new DateTimeOffset(parsed, TimeSpan.Zero).ToUnixTimeSeconds();
        return null;
    }

    private static object? EvalDur2Sec(List<object?> args)
    {
        // Convert duration string "HH:MM:SS" or "MM:SS" to seconds
        var str = args.FirstOrDefault()?.ToString();
        if (str == null) return null;
        var parts = str.Split(':');
        try
        {
            return parts.Length switch
            {
                3 => double.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 +
                     double.Parse(parts[1], CultureInfo.InvariantCulture) * 60 +
                     double.Parse(parts[2], CultureInfo.InvariantCulture),
                2 => double.Parse(parts[0], CultureInfo.InvariantCulture) * 60 +
                     double.Parse(parts[1], CultureInfo.InvariantCulture),
                1 => ToDouble(str),
                _ => null
            };
        }
        catch { return null; }
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
        // Simple printf-style formatting: printf("%s has %d items", name, count)
        var format = args.ElementAtOrDefault(0)?.ToString();
        if (format == null) return null;
        var formatArgs = args.Skip(1).Select(a => a ?? "").ToArray();
        try { return string.Format(CultureInfo.InvariantCulture, format, formatArgs); }
        catch { return format; }
    }

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
        if (double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
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
