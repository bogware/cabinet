using System.Globalization;
using System.Text;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage.Engine;

/// <summary>
/// Aggregates where the data is, giving the query engine's answer exactly: an event without a
/// by-field is left out of <c>stats</c>, groups take the values of their newest event and come
/// out newest first, timechart buckets come out oldest first, and numbers held as text count as
/// numbers. Blocks every row of which matches are counted from their summaries, unread, and the
/// rest are read in parallel, each worker aggregating on its own before the results are merged.
/// </summary>
public sealed partial class EngineLogStorage : IAggregatingLogStorage
{
    /// <inheritdoc />
    public async Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.GroupBy.Any(field => !AggregateFields.CanPush(field))
            || request.Aggregates.Any(spec => spec.Field != null && !AggregateFields.CanPush(spec.Field))
            || request.Bucket is { Ticks: <= 0 }
            || request.Filter.After != null)
        {
            return null;
        }

        await InitializeAsync(cancellationToken);
        var snapshots = ShardsFor(request.Filter).Select(shard => shard.Snapshot()).ToList();
        try
        {
            return Aggregate(request, snapshots, cancellationToken);
        }
        finally
        {
            snapshots.ForEach(snapshot => snapshot.Dispose());
        }
    }

    private static AggregateResult? Aggregate(AggregateRequest request, List<ShardSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var filter = new RowFilter(request.Filter);
        var aggregation = new Aggregation(request);
        var blocks = new List<(SegmentReader Segment, BlockInfo Block)>();
        foreach (var (hot, segments) in snapshots)
        {
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
                    if (!(filter.WholeBlock(block) && aggregation.TryAddWhole(block)))
                    {
                        blocks.Add((segment, block));
                    }
                }
            }
        }

        var partials = new List<Aggregation>();
        Parallel.ForEach(
            blocks,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => new Aggregation(request),
            (work, state, local) =>
            {
                if (local.Full || local.Unanswerable)
                {
                    state.Stop();
                    return local;
                }

                var rows = new BlockValues(new BlockRows(work.Segment, work.Block), work.Segment.Index);
                for (var i = 0; i < rows.Rows.Count; i++)
                {
                    if (filter.Matches(rows.Rows, i))
                    {
                        local.Add(rows, i);
                    }
                }

                return local;
            },
            local =>
            {
                lock (partials)
                {
                    partials.Add(local);
                }
            });

        foreach (var partial in partials)
        {
            aggregation.Merge(partial);
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
        private long _retained;
        private bool _unanswerable;

        /// <summary>Most values percentiles and dc may hold in all, past which the pipeline answers instead.</summary>
        private const long MaxRetainedValues = 10_000_000;

        public Aggregation(AggregateRequest request)
        {
            _request = request;
            _groupBy = request.GroupBy.Select(FieldRef.For).ToArray();
            _fields = request.Aggregates.Select(spec => spec.Field == null ? null : FieldRef.For(spec.Field)).ToArray();
            _countsOnly = request.GroupBy.Count == 0 && request.Aggregates.All(spec => spec.Kind == AggregateKind.Count && spec.Field == null);
        }

        /// <summary>Whether there are already more groups than the query may have.</summary>
        public bool Full => _groups.Count > _request.MaxGroups;

        /// <summary>Whether only the pipeline can give the answer, this having kept too many values.</summary>
        public bool Unanswerable => _unanswerable;

        /// <summary>Adds another worker's groups to these.</summary>
        public void Merge(Aggregation other)
        {
            _events += other._events;
            _retained += other._retained;
            _unanswerable |= other._unanswerable || _retained > MaxRetainedValues;
            foreach (var ((bucket, key), theirs) in other._groups)
            {
                if (_groups.TryGetValue((bucket, key), out var ours))
                {
                    ours.Merge(theirs);
                }
                else
                {
                    _groups[(bucket, key)] = theirs;
                }
            }
        }

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
                if (group.Accumulators[a].Add(_fields[a] is { } field ? row.Value(field, i) : null) && ++_retained > MaxRetainedValues)
                {
                    _unanswerable = true;
                }
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

        /// <summary>Takes in the same group from another worker.</summary>
        public void Merge(Group other)
        {
            Observe(other.NewestTicks, other.NewestId, other.Values);
            for (var a = 0; a < Accumulators.Length; a++)
            {
                Accumulators[a].Merge(other.Accumulators[a]);
            }
        }

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
        private List<double>? _values;

        public void AddCount(long rows) => _count += rows;

        /// <summary>Adds a row's value. Returns true when it had to be kept, as for dc or a percentile.</summary>
        public bool Add(object? value)
        {
            switch (spec.Kind)
            {
                case AggregateKind.Count:
                    if (spec.Field == null || value != null)
                    {
                        _count++;
                    }

                    return false;

                case AggregateKind.DistinctCount:
                    return value?.ToString() is { } text && (_distinct ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(text);
            }

            if (Number(value) is not { } number)
            {
                _sawText |= value != null;
                return false;
            }

            _count++;
            _sum += number;
            _min = Math.Min(_min, number);
            _max = Math.Max(_max, number);
            var delta = number - _mean;
            _mean += delta / _count;
            _squaredDeviations += delta * (number - _mean);
            if (spec.Kind == AggregateKind.Percentile)
            {
                (_values ??= []).Add(number);
                return true;
            }

            return false;
        }

        /// <summary>Takes in another worker's values for the same group (Chan's method for the deviations).</summary>
        public void Merge(Accumulator other)
        {
            if (other._distinct != null)
            {
                (_distinct ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).UnionWith(other._distinct);
            }

            if (other._values != null)
            {
                (_values ??= []).AddRange(other._values);
            }

            _sawText |= other._sawText;
            if (spec.Kind is AggregateKind.Count or AggregateKind.DistinctCount)
            {
                _count += other._count;
                return;
            }

            if (other._count == 0)
            {
                return;
            }

            var count = _count + other._count;
            var delta = other._mean - _mean;
            _squaredDeviations += other._squaredDeviations + (delta * delta * _count * other._count / count);
            _mean += delta * other._count / count;
            _count = count;
            _sum += other._sum;
            _min = Math.Min(_min, other._min);
            _max = Math.Max(_max, other._max);
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
                AggregateKind.Percentile => Percentile(),
                _ => double.NaN,
            };

            value = double.IsFinite(result) ? result : null;
            return true;
        }

        /// <summary>
        /// The percentile of every value, interpolated between the nearest two as the pipeline does;
        /// for the 50th that's the mean of the middle two, its median. The pipeline samples past
        /// its exact limit, where this stays exact, as PostgreSQL does.
        /// </summary>
        private double Percentile()
        {
            var sorted = _values!;
            sorted.Sort();
            var index = spec.Percentile / 100.0 * (sorted.Count - 1);
            var lower = Math.Clamp((int)Math.Floor(index), 0, sorted.Count - 1);
            var upper = Math.Clamp((int)Math.Ceiling(index), 0, sorted.Count - 1);
            return lower == upper ? sorted[lower] : sorted[lower] + ((index - lower) * (sorted[upper] - sorted[lower]));
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
