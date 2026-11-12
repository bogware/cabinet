using System.Globalization;
using System.Text;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage.Engine;

/// <summary>
/// Aggregates where the data is, giving the query engine's answer exactly: an event without a
/// by-field is left out of <c>stats</c>, groups take the values of their newest event and come
/// out newest first, timechart buckets come out oldest first, and numbers held as text count as
/// numbers. Blocks every row of which matches are counted from their summaries, unread.
/// </summary>
public sealed partial class EngineLogStorage : IAggregatingLogStorage
{
    /// <inheritdoc />
    public async Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.GroupBy.Any(field => !AggregateFields.CanPush(field))
            || request.Aggregates.Any(spec => spec.Kind == AggregateKind.Percentile || (spec.Field != null && !AggregateFields.CanPush(spec.Field)))
            || request.Bucket is { Ticks: <= 0 }
            || request.Filter.After != null)
        {
            return null;
        }

        await InitializeAsync(cancellationToken);
        var filter = new RowFilter(request.Filter);
        var aggregation = new Aggregation(request);
        foreach (var shard in ShardsFor(request.Filter))
        {
            var (hot, segments) = shard.Snapshot();
            foreach (var logEvent in hot)
            {
                if (filter.Matches(logEvent))
                {
                    aggregation.Add(new EventValues(logEvent), 0);
                }
            }

            foreach (var segment in segments.Where(filter.MayMatch))
            {
                foreach (var block in segment.Blocks.Where(filter.MayMatch))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (aggregation.Full)
                    {
                        return aggregation.Result();
                    }

                    if (filter.WholeBlock(block) && aggregation.TryAddWhole(block))
                    {
                        continue;
                    }

                    var rows = new BlockValues(new BlockRows(segment, block), segment.Index);
                    for (var i = 0; i < rows.Rows.Count; i++)
                    {
                        if (filter.Matches(rows.Rows, i))
                        {
                            aggregation.Add(rows, i);
                        }
                    }
                }
            }
        }

        return aggregation.Result();
    }

    /// <summary>A row's time, ID and fields, as the query engine sees them.</summary>
    private interface IRowValues
    {
        long Ticks(int i);

        string Id(int i);

        /// <summary>The field's value as an engine row holds it, or null where it has none.</summary>
        object? Value(FieldRef field, int i);
    }

    /// <summary>A field resolved once: a built-in column, or a custom field after aliases.</summary>
    private sealed record FieldRef(string? Builtin, string Name)
    {
        public static FieldRef For(string name) => new(AggregateFields.Builtin(name), AggregateFields.CustomName(name));
    }

    private sealed class EventValues(LogEvent logEvent) : IRowValues
    {
        public long Ticks(int i) => logEvent.Timestamp.Ticks;

        public string Id(int i) => logEvent.Id;

        public object? Value(FieldRef field, int i) => field.Builtin switch
        {
            "host" => logEvent.Host ?? string.Empty,
            "source_type" => logEvent.SourceType,
            "index" => logEvent.Index,
            "severity" => (int)logEvent.Severity,
            "source_file" => logEvent.SourceFile ?? string.Empty,
            _ => logEvent.Fields.TryGetValue(field.Name, out var value) ? value : null,
        };
    }

    private sealed class BlockValues(BlockRows rows, string index) : IRowValues
    {
        public BlockRows Rows => rows;

        public long Ticks(int i) => rows.Ticks[i];

        public string Id(int i) => rows.Ids[i];

        public object? Value(FieldRef field, int i) => field.Builtin switch
        {
            "host" => rows.Hosts[i] ?? string.Empty,
            "source_type" => rows.SourceTypes[i],
            "index" => index,
            "severity" => (int)rows.Severities[i],
            "source_file" => rows.SourceFile(i) ?? string.Empty,
            _ => rows.Field(field.Name)?[i],
        };
    }

    /// <summary>The groups of one aggregate request, filled a row at a time.</summary>
    private sealed class Aggregation
    {
        private readonly AggregateRequest _request;
        private readonly FieldRef[] _groupBy;
        private readonly FieldRef?[] _fields;
        private readonly Dictionary<(long Bucket, string Key), Group> _groups = [];
        private readonly bool _countsOnly;
        private long _events;
        private bool _unanswerable;

        public Aggregation(AggregateRequest request)
        {
            _request = request;
            _groupBy = request.GroupBy.Select(FieldRef.For).ToArray();
            _fields = request.Aggregates.Select(spec => spec.Field == null ? null : FieldRef.For(spec.Field)).ToArray();
            _countsOnly = request.GroupBy.Count == 0 && request.Aggregates.All(spec => spec.Kind == AggregateKind.Count && spec.Field == null);
        }

        /// <summary>Whether there are already more groups than the query may have.</summary>
        public bool Full => _groups.Count > _request.MaxGroups;

        /// <summary>
        /// Counts a block every row of which matches, from its summary, when the answer doesn't
        /// depend on what's in the rows: counting events with no grouping, in a single bucket.
        /// </summary>
        public bool TryAddWhole(BlockInfo block)
        {
            if (!_countsOnly)
            {
                return false;
            }

            var bucket = 0L;
            if (_request.Bucket is { } span)
            {
                bucket = block.MinTicks / span.Ticks * span.Ticks;
                if (block.MaxTicks / span.Ticks * span.Ticks != bucket)
                {
                    return false;
                }
            }

            var group = GroupFor(bucket, string.Empty);
            if (group == null)
            {
                return true;
            }

            group.Observe(block.MaxTicks, string.Empty, null);
            foreach (var accumulator in group.Accumulators)
            {
                accumulator.AddCount(block.Rows);
            }

            _events += block.Rows;
            return true;
        }

        public void Add(IRowValues row, int i)
        {
            var ticks = row.Ticks(i);
            long bucket = 0;
            string key;
            object?[]? values = null;
            if (_request.Bucket is { } span)
            {
                // A timechart's split field is text, and an event without one goes under ""
                bucket = ticks / span.Ticks * span.Ticks;
                key = _groupBy.Length == 0 ? string.Empty : Text(row.Value(_groupBy[0], i)) ?? string.Empty;
            }
            else
            {
                values = new object?[_groupBy.Length];
                var builder = new StringBuilder();
                for (var g = 0; g < _groupBy.Length; g++)
                {
                    var value = row.Value(_groupBy[g], i);
                    if (value == null)
                    {
                        // As in SPL, an event without every by-field isn't counted
                        return;
                    }

                    values[g] = value;
                    var text = Text(value) ?? string.Empty;
                    builder.Append(text.Length).Append(':').Append(text);
                }

                key = builder.ToString();
            }

            var group = GroupFor(bucket, key);
            if (group == null)
            {
                return;
            }

            group.Observe(ticks, row.Id(i), values);
            for (var a = 0; a < group.Accumulators.Length; a++)
            {
                group.Accumulators[a].Add(_fields[a] is { } field ? row.Value(field, i) : null);
            }

            _events++;
        }

        private Group? GroupFor(long bucket, string key)
        {
            if (_groups.TryGetValue((bucket, key), out var group))
            {
                return group;
            }

            if (Full)
            {
                return null;
            }

            group = new Group(key, _request.Aggregates.Select(spec => new Accumulator(spec)).ToArray());
            _groups[(bucket, key)] = group;
            return group;
        }

        public AggregateResult? Result()
        {
            var rows = new List<Dictionary<string, object?>>();

            // With no grouping, stats has a row even when nothing matched
            if (_request.Bucket == null && _groupBy.Length == 0 && _groups.Count == 0)
            {
                _groups[(0, string.Empty)] = new Group(string.Empty, _request.Aggregates.Select(spec => new Accumulator(spec)).ToArray());
            }

            var ordered = _groups.OrderBy(pair => _request.Bucket == null ? 0 : pair.Key.Bucket)
                .ThenByDescending(pair => pair.Value.NewestTicks)
                .ThenByDescending(pair => pair.Value.NewestId, StringComparer.Ordinal);
            foreach (var ((bucket, _), group) in ordered)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (_request.Bucket != null)
                {
                    row["_time"] = new DateTime(bucket, DateTimeKind.Utc);
                    if (_groupBy.Length > 0)
                    {
                        row[_request.GroupBy[0]] = group.Key;
                    }
                }
                else
                {
                    for (var g = 0; g < _groupBy.Length; g++)
                    {
                        row[_request.GroupBy[g]] = group.Values![g];
                    }
                }

                for (var a = 0; a < group.Accumulators.Length; a++)
                {
                    if (!group.Accumulators[a].TryResult(out var value))
                    {
                        _unanswerable = true;
                    }

                    row[_request.Aggregates[a].OutputName] = value;
                }

                rows.Add(row);
            }

            return _unanswerable ? null : new AggregateResult { Rows = rows, EventsScanned = _events };
        }

        /// <summary>A value as text, the way the engine groups by it.</summary>
        private static string? Text(object? value) => value?.ToString();
    }

    private sealed class Group(string key, Accumulator[] accumulators)
    {
        public string Key { get; } = key;

        public Accumulator[] Accumulators { get; } = accumulators;

        public object?[]? Values { get; private set; }

        public long NewestTicks { get; private set; } = long.MinValue;

        public string NewestId { get; private set; } = string.Empty;

        /// <summary>Notes a row, whose values become the group's if it's the newest yet.</summary>
        public void Observe(long ticks, string id, object?[]? values)
        {
            if (ticks > NewestTicks || (ticks == NewestTicks && string.CompareOrdinal(id, NewestId) > 0))
            {
                NewestTicks = ticks;
                NewestId = id;
                Values = values ?? Values;
            }
        }
    }

    /// <summary>One aggregate of one group, computed as the query engine's accumulator does.</summary>
    private sealed class Accumulator(AggregateSpec spec)
    {
        private long _count;
        private double _sum;
        private double _mean;
        private double _squaredDeviations;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private bool _sawText;
        private HashSet<string>? _distinct;

        public void AddCount(long rows) => _count += rows;

        public void Add(object? value)
        {
            switch (spec.Kind)
            {
                case AggregateKind.Count:
                    if (spec.Field == null || value != null)
                    {
                        _count++;
                    }

                    return;

                case AggregateKind.DistinctCount:
                    if (value?.ToString() is { } text)
                    {
                        (_distinct ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(text);
                    }

                    return;
            }

            if (Number(value) is not { } number)
            {
                _sawText |= value != null;
                return;
            }

            _count++;
            _sum += number;
            _min = Math.Min(_min, number);
            _max = Math.Max(_max, number);
            var delta = number - _mean;
            _mean += delta / _count;
            _squaredDeviations += delta * (number - _mean);
        }

        /// <summary>The value, or false where only the engine can give it: min or max of text alone.</summary>
        public bool TryResult(out object? value)
        {
            value = null;
            switch (spec.Kind)
            {
                case AggregateKind.Count:
                    value = _count <= int.MaxValue ? (object)(int)_count : _count;
                    return true;
                case AggregateKind.DistinctCount:
                    value = _distinct?.Count ?? 0;
                    return true;
            }

            if (_count == 0)
            {
                return !(_sawText && spec.Kind is AggregateKind.Min or AggregateKind.Max);
            }

            double result = spec.Kind switch
            {
                AggregateKind.Sum => _sum,
                AggregateKind.Average => _sum / _count,
                AggregateKind.Min => _min,
                AggregateKind.Max => _max,
                AggregateKind.SampleDeviation => _count == 1 ? double.NaN : Math.Sqrt(_squaredDeviations / (_count - 1)),
                AggregateKind.PopulationDeviation => Math.Sqrt(_squaredDeviations / _count),
                AggregateKind.SampleVariance => _count == 1 ? double.NaN : _squaredDeviations / (_count - 1),
                AggregateKind.PopulationVariance => _squaredDeviations / _count,
                _ => double.NaN,
            };

            value = double.IsFinite(result) ? result : null;
            return true;
        }

        /// <summary>A number as the engine reads one: numeric values, or text that parses as a plain number.</summary>
        private static double? Number(object? value) => value switch
        {
            null => null,
            double d => d,
            long l => l,
            int i => i,
            _ => double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        };
    }
}
