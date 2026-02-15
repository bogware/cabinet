using System.Linq;

namespace Cabinet.Query;

/// <summary>
/// Extension methods for QueryPlan
/// </summary>
public static class QueryPlanExtensions
{
    /// <summary>
    /// Get the count of operators in the plan
    /// </summary>
    public static int Count(this QueryPlan plan)
    {
        return plan.Operators?.Count ?? 0;
    }

    /// <summary>
    /// Select values from operators
    /// </summary>
    public static IEnumerable<string> Select(this QueryPlan plan, Func<QueryOperator, string> selector)
    {
        return plan.Operators?.Select(selector) ?? Enumerable.Empty<string>();
    }

    /// <summary>
    /// Get operator names
    /// </summary>
    public static IEnumerable<string> GetOperatorNames(this QueryPlan plan)
    {
        return plan.Operators?.Select(o => o.Name) ?? Enumerable.Empty<string>();
    }
}