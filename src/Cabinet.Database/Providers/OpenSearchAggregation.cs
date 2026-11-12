using System.Globalization;
using Cabinet.Core.Interfaces;
using OpenSearch.Client;

namespace Cabinet.Database.Providers;

/// <summary>
/// Counts by built-in fields and time buckets in OpenSearch, with a composite aggregation so that
/// every group is exact. Custom fields are left to the engine: their mapping is dynamic, so a
/// name can differ in case from the query, and long text isn't indexed for grouping.
/// </summary>
public sealed partial class OpenSearchLogStorage : IAggregatingLogStorage
{
    private const int CompositePageSize = 1000;

    /// <inheritdoc />
    public async Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        var fields = request.GroupBy.Select(AggregateFields.Builtin).ToList();
        if (request.ExactTextKeys
            || fields.Any(field => field == null)
            || request.Aggregates.Any(spec => spec.Kind != AggregateKind.Count || (spec.Field != null && AggregateFields.Builtin(spec.Field) == null))
            || (request.Bucket is { } span && (span.TotalMilliseconds < 1 || span.Ticks % TimeSpan.TicksPerMillisecond != 0
                // OpenSearch buckets from the Unix epoch, and the engine from year 1. They agree
                // when a bucket fits a whole number of times into the days between.
                || (AggregateFields.EpochOffsetSeconds * 1000) % (long)span.TotalMilliseconds != 0)))
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken);

        // A composite aggregation needs something to group by
        if (fields.Count == 0 && request.Bucket == null)
        {
            var total = await GetCountAsync(request.Filter, cancellationToken);
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in request.Aggregates)
            {
                row[spec.OutputName] = total <= int.MaxValue ? (int)total : total;
            }

            return new AggregateResult { Rows = [row], EventsScanned = total };
        }

        var searchDescriptor = BuildSearchDescriptor(request.Filter);
        var groups = new List<(Dictionary<string, object?> Row, long Count, double NewestMs, long BucketMs)>();
        IReadOnlyDictionary<string, object>? after = null;
        long events = 0;

        while (true)
        {
            var cursor = after;
            var response = await _client.SearchAsync<OpenSearchLogEvent>(s =>
            {
                searchDescriptor(s);
                s.Size(0).Aggregations(a => a.Composite("groups", c =>
                {
                    c.Size(CompositePageSize).Sources(sources =>
                    {
                        if (request.Bucket is { } bucket)
                        {
                            // Buckets count from the start of year 1, as the engine's do
                            var spanMs = (long)bucket.TotalMilliseconds;
                            sources.DateHistogram("t", d => d.Field(e => e.Timestamp).FixedInterval(new Time(spanMs)));
                        }

                        for (var i = 0; i < fields.Count; i++)
                        {
                            var name = $"k{i}";
                            switch (fields[i])
                            {
                                case "host":
                                    sources.Terms(name, t => t.Field(e => e.Host).MissingBucket());
                                    break;
                                case "source_type":
                                    sources.Terms(name, t => t.Field(e => e.SourceType).MissingBucket());
                                    break;
                                case "index":
                                    sources.Terms(name, t => t.Field(e => e.IndexName).MissingBucket());
                                    break;
                                case "severity":
                                    sources.Terms(name, t => t.Field(e => e.Severity).MissingBucket());
                                    break;
                                case "source_file":
                                    sources.Terms(name, t => t.Field(e => e.SourceFile).MissingBucket());
                                    break;
                            }
                        }

                        return sources;
                    });
                    if (cursor != null)
                    {
                        c.After(new CompositeKey(cursor));
                    }

                    return c.Aggregations(sub => sub.Max("newest", m => m.Field(e => e.Timestamp)));
                }));
                return s;
            }, cancellationToken);

            ThrowIfFailed(response, "aggregate");
            if (response.Shards?.Failed > 0)
            {
                throw new InvalidOperationException($"OpenSearch aggregation failed on {response.Shards.Failed} shards");
            }

            var composite = response.Aggregations.Composite("groups");
            foreach (var bucket in composite.Buckets)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                long bucketMs = 0;
                object? time = null;
                if (request.Bucket != null && bucket.Key.TryGetValue("t", out time))
                {
                    bucketMs = Convert.ToInt64(time, CultureInfo.InvariantCulture);
                    row["_time"] = DateTimeOffset.FromUnixTimeMilliseconds(bucketMs).UtcDateTime;
                }

                for (var i = 0; i < fields.Count; i++)
                {
                    bucket.Key.TryGetValue($"k{i}", out object? key);
                    object? value = fields[i] == "severity"
                        ? key == null ? null : Convert.ToInt32(key, CultureInfo.InvariantCulture)
                        : key?.ToString() ?? string.Empty;

                    // A timechart's split field comes out as text, as the engine writes it
                    row[request.GroupBy[i]] = request.Bucket != null ? value?.ToString() ?? string.Empty : value;
                }

                foreach (var spec in request.Aggregates)
                {
                    row[spec.OutputName] = bucket.DocCount is long count && count <= int.MaxValue ? (int)count : bucket.DocCount;
                }

                events += bucket.DocCount ?? 0;
                groups.Add((row, bucket.DocCount ?? 0, bucket.Max("newest")?.Value ?? 0, bucketMs));
            }

            if (groups.Count > request.MaxGroups || composite.AfterKey == null || composite.Buckets.Count < CompositePageSize)
            {
                break;
            }

            after = composite.AfterKey;
        }

        // Without grouping there's always one row, even with no events
        if (fields.Count == 0 && request.Bucket == null && groups.Count == 0)
        {
            var empty = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in request.Aggregates)
            {
                empty[spec.OutputName] = 0;
            }

            groups.Add((empty, 0, 0, 0));
        }

        var ordered = request.Bucket != null
            ? groups.OrderBy(group => group.BucketMs).ThenByDescending(group => group.NewestMs)
            : groups.OrderByDescending(group => group.NewestMs);
        return new AggregateResult { Rows = ordered.Select(group => group.Row).ToList(), EventsScanned = events };
    }
}
