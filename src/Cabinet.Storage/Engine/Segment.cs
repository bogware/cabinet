using System.IO.Compression;
using System.Text;
using Cabinet.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Cabinet.Storage.Engine;

/// <summary>
/// What a block's footer entry holds: enough to skip the block without reading it, and where each
/// column's compressed bytes are.
/// </summary>
internal sealed class BlockInfo
{
    public int Rows { get; init; }
    public long MinTicks { get; init; }
    public long MaxTicks { get; init; }
    public int MinSeverity { get; init; }
    public int MaxSeverity { get; init; }

    /// <summary>Distinct hosts and source types, lower-cased, when there are few; null when many.</summary>
    public HashSet<string>? Hosts { get; init; }
    public HashSet<string>? SourceTypes { get; init; }

    /// <summary>Events per source type and severity, for statistics without reading rows.</summary>
    public Dictionary<string, long> SourceTypeCounts { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<int, long> SeverityCounts { get; init; } = [];

    public required BloomFilter Words { get; init; }
    public required BloomFilter Ids { get; init; }

    /// <summary>Column name to (offset, compressed length). Field columns are named "f:" + field name.</summary>
    public Dictionary<string, (long Offset, int Length)> Columns { get; } = new(StringComparer.Ordinal);
}

/// <summary>Writes an immutable segment: a sorted run of one index's events, in column blocks.</summary>
internal static class SegmentWriter
{
    public const string Magic = "CABSEG01";
    public const int BlockRows = 4096;
    private const int MaxDistinct = 64;

    /// <summary>
    /// Writes the events (sorted here by time, then ID) to <paramref name="path"/>, through a
    /// temporary file renamed into place, so a segment is either whole or absent.
    /// </summary>
    public static void Write(string path, string index, IReadOnlyList<LogEvent> events, IReadOnlyList<string>? replaces = null)
    {
        var sorted = events.OrderBy(e => e.Timestamp.Ticks).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        WriteSorted(path, index, sorted, sorted.Count, replaces);
    }

    /// <summary>
    /// Writes events already in (time, ID) order, holding only a batch of blocks at a time, so a
    /// merge of large segments needs no more memory than a small one. Returns how many it wrote.
    /// </summary>
    /// <param name="atMost">An upper bound on the count, which sizes the segment's ID filter.</param>
    public static int WriteSorted(
        string path,
        string index,
        IEnumerable<LogEvent> sorted,
        int atMost,
        IReadOnlyList<string>? replaces = null,
        CancellationToken cancellationToken = default)
    {
        const int BlocksPerBatch = 32;
        var temporary = path + ".tmp";
        var blocks = new List<BlockInfo>();
        var ids = BloomFilter.ForCount(atMost, precise: true);
        var count = 0;
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var pending = new List<List<LogEvent>>();
                var current = new List<LogEvent>(BlockRows);

                // Blocks are independent, so a batch is encoded and compressed in parallel, then written in order
                void WritePending()
                {
                    var built = new (BlockInfo Block, MemoryStream Body)[pending.Count];
                    Parallel.For(0, built.Length, b => built[b] = BuildBlock(pending[b]));
                    foreach (var (block, body) in built)
                    {
                        var blockStart = file.Position;
                        body.Position = 0;
                        body.CopyTo(file);
                        body.Dispose();
                        foreach (var name in block.Columns.Keys.ToList())
                        {
                            var (offset, length) = block.Columns[name];
                            block.Columns[name] = (blockStart + offset, length);
                        }

                        blocks.Add(block);
                    }

                    pending.Clear();
                }

                foreach (var logEvent in sorted)
                {
                    ids.Add(logEvent.Id);
                    count++;
                    current.Add(logEvent);
                    if (current.Count == BlockRows)
                    {
                        pending.Add(current);
                        current = new List<LogEvent>(BlockRows);
                        if (pending.Count == BlocksPerBatch)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            WritePending();
                        }
                    }
                }

                if (current.Count > 0)
                {
                    pending.Add(current);
                }

                WritePending();

                var footerStart = file.Position;
                using (var footer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
                {
                    footer.Write(index);
                    footer.Write(count);
                    footer.Write(replaces?.Count ?? 0);
                    foreach (var name in replaces ?? [])
                    {
                        footer.Write(name);
                    }

                    footer.Write(blocks.Count);
                    foreach (var block in blocks)
                    {
                        WriteBlockInfo(footer, block);
                    }

                    // One filter over every ID, so a duplicate check skips the segment in one probe
                    ids.Write(footer);
                    footer.Write(file.Position - footerStart + sizeof(long) + Magic.Length);
                    footer.Write(Encoding.ASCII.GetBytes(Magic));
                }

                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return count;
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Encodes a block in memory, its column offsets relative to the block's start.</summary>
    private static (BlockInfo Block, MemoryStream Body) BuildBlock(List<LogEvent> rows)
    {
        var file = new MemoryStream();
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var word in SearchTerms.Parse(row.RawLine))
            {
                words.Add(word);
            }
        }

        var wordFilter = BloomFilter.ForCount(words.Count);
        foreach (var word in words)
        {
            wordFilter.Add(word);
        }

        var idFilter = BloomFilter.ForCount(rows.Count, precise: true);
        foreach (var row in rows)
        {
            idFilter.Add(row.Id);
        }

        var hosts = rows.Select(r => (r.Host ?? string.Empty).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var sourceTypes = rows.Select(r => (r.SourceType ?? string.Empty).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var block = new BlockInfo
        {
            Rows = rows.Count,
            MinTicks = rows[0].Timestamp.Ticks,
            MaxTicks = rows[^1].Timestamp.Ticks,
            MinSeverity = rows.Min(r => (int)r.Severity),
            MaxSeverity = rows.Max(r => (int)r.Severity),
            Hosts = hosts.Count <= MaxDistinct ? hosts : null,
            SourceTypes = sourceTypes.Count <= MaxDistinct ? sourceTypes : null,
            Words = wordFilter,
            Ids = idFilter,
        };
        foreach (var group in rows.GroupBy(r => r.SourceType ?? string.Empty, StringComparer.Ordinal))
        {
            block.SourceTypeCounts[group.Key] = group.Count();
        }

        foreach (var group in rows.GroupBy(r => (int)r.Severity))
        {
            block.SeverityCounts[group.Key] = group.Count();
        }

        Column(file, block, "ts", w =>
        {
            long previous = 0;
            foreach (var row in rows)
            {
                w.Write7BitEncodedInt64(row.Timestamp.Ticks - previous);
                previous = row.Timestamp.Ticks;
            }
        });
        Column(file, block, "kind", w => { foreach (var row in rows) w.Write((byte)row.Timestamp.Kind); });
        Column(file, block, "id", w => { foreach (var row in rows) w.Write(row.Id); });
        Column(file, block, "host", w => { foreach (var row in rows) EventCodec.WriteNullable(w, row.Host); });
        Column(file, block, "st", w => { foreach (var row in rows) w.Write(row.SourceType ?? string.Empty); });
        Column(file, block, "sev", w => { foreach (var row in rows) w.Write((byte)row.Severity); });
        Column(file, block, "raw", w => { foreach (var row in rows) w.Write(row.RawLine ?? string.Empty); });
        Column(file, block, "meta", w =>
        {
            foreach (var row in rows)
            {
                w.Write(row.IndexTime.Ticks);
                EventCodec.WriteNullable(w, row.SourceFile);
                w.Write(row.LineNumber);
                EventCodec.WriteNullable(w, row.ParserName);
                var tags = row.GetTagSnapshot();
                w.Write(tags.Length);
                foreach (var tag in tags)
                {
                    w.Write(tag);
                }
            }
        });

        // One column per field name in the block, so a query reads only the fields it needs
        var names = rows.SelectMany(r => r.Fields.Keys).Distinct(StringComparer.Ordinal).ToList();
        foreach (var name in names)
        {
            Column(file, block, "f:" + name, w =>
            {
                foreach (var row in rows)
                {
                    if (row.Fields.TryGetValue(name, out var value))
                    {
                        FieldValues.Write(w, value);
                    }
                    else
                    {
                        w.Write(FieldValues.Absent);
                    }
                }
            });
        }

        return (block, file);
    }

    private static void Column(Stream file, BlockInfo block, string name, Action<BinaryWriter> write)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        var offset = file.Position;
        using (var compressor = new ZLibStream(file, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(raw.GetBuffer(), 0, (int)raw.Length);
        }

        block.Columns[name] = (offset, (int)(file.Position - offset));
    }

    private static void WriteBlockInfo(BinaryWriter writer, BlockInfo block)
    {
        writer.Write(block.Rows);
        writer.Write(block.MinTicks);
        writer.Write(block.MaxTicks);
        writer.Write(block.MinSeverity);
        writer.Write(block.MaxSeverity);
        WriteSet(writer, block.Hosts);
        WriteSet(writer, block.SourceTypes);
        writer.Write(block.SourceTypeCounts.Count);
        foreach (var (key, count) in block.SourceTypeCounts)
        {
            writer.Write(key);
            writer.Write(count);
        }

        writer.Write(block.SeverityCounts.Count);
        foreach (var (key, count) in block.SeverityCounts)
        {
            writer.Write(key);
            writer.Write(count);
        }

        block.Words.Write(writer);
        block.Ids.Write(writer);
        writer.Write(block.Columns.Count);
        foreach (var (name, (offset, length)) in block.Columns)
        {
            writer.Write(name);
            writer.Write(offset);
            writer.Write(length);
        }
    }

    private static void WriteSet(BinaryWriter writer, HashSet<string>? set)
    {
        writer.Write(set?.Count ?? -1);
        foreach (var value in set ?? [])
        {
            writer.Write(value);
        }
    }
}

/// <summary>An open segment: its footer in memory, and its column blocks read on demand.</summary>
internal sealed class SegmentReader : IDisposable
{
    private readonly SafeFileHandle _handle;

    private SegmentReader(string path, SafeFileHandle handle)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        _handle = handle;
    }

    public string Path { get; }
    public string Name { get; }
    public string Index { get; private set; } = string.Empty;
    public int Rows { get; private set; }
    public List<BlockInfo> Blocks { get; } = [];
    public List<string> Replaces { get; } = [];

    /// <summary>A filter over every event ID in the segment.</summary>
    public BloomFilter Ids { get; private set; } = BloomFilter.ForCount(0);
    public long MinTicks => Blocks.Count == 0 ? long.MaxValue : Blocks[0].MinTicks;
    public long MaxTicks => Blocks.Count == 0 ? long.MinValue : Blocks[^1].MaxTicks;
    public long SizeBytes { get; private set; }

    public static SegmentReader Open(string path)
    {
        // Shared for deleting, so retention can remove a segment a query still has open
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        var reader = new SegmentReader(path, handle);
        try
        {
            reader.ReadFooter();
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private void ReadFooter()
    {
        var length = RandomAccess.GetLength(_handle);
        SizeBytes = length;
        var tail = new byte[sizeof(long) + SegmentWriter.Magic.Length];
        if (length < tail.Length || RandomAccess.Read(_handle, tail, length - tail.Length) != tail.Length
            || Encoding.ASCII.GetString(tail, sizeof(long), SegmentWriter.Magic.Length) != SegmentWriter.Magic)
        {
            throw new InvalidDataException($"{Path} isn't a Cabinet segment");
        }

        var footerLength = BitConverter.ToInt64(tail, 0);
        var footer = new byte[footerLength - tail.Length];
        RandomAccess.Read(_handle, footer, length - footerLength);
        using var reader = new BinaryReader(new MemoryStream(footer), Encoding.UTF8);
        Index = reader.ReadString();
        Rows = reader.ReadInt32();
        var replaces = reader.ReadInt32();
        for (var i = 0; i < replaces; i++)
        {
            Replaces.Add(reader.ReadString());
        }

        var blocks = reader.ReadInt32();
        for (var i = 0; i < blocks; i++)
        {
            Blocks.Add(ReadBlockInfo(reader));
        }

        Ids = BloomFilter.Read(reader);
    }

    private static BlockInfo ReadBlockInfo(BinaryReader reader)
    {
        var rows = reader.ReadInt32();
        var minTicks = reader.ReadInt64();
        var maxTicks = reader.ReadInt64();
        var minSeverity = reader.ReadInt32();
        var maxSeverity = reader.ReadInt32();
        var hosts = ReadSet(reader);
        var sourceTypes = ReadSet(reader);
        var sourceTypeCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = reader.ReadInt32(); i > 0; i--)
        {
            sourceTypeCounts[reader.ReadString()] = reader.ReadInt64();
        }

        var severityCounts = new Dictionary<int, long>();
        for (var i = reader.ReadInt32(); i > 0; i--)
        {
            severityCounts[reader.ReadInt32()] = reader.ReadInt64();
        }

        var block = new BlockInfo
        {
            Rows = rows,
            MinTicks = minTicks,
            MaxTicks = maxTicks,
            MinSeverity = minSeverity,
            MaxSeverity = maxSeverity,
            Hosts = hosts,
            SourceTypes = sourceTypes,
            SourceTypeCounts = sourceTypeCounts,
            SeverityCounts = severityCounts,
            Words = BloomFilter.Read(reader),
            Ids = BloomFilter.Read(reader),
        };
        for (var i = reader.ReadInt32(); i > 0; i--)
        {
            block.Columns[reader.ReadString()] = (reader.ReadInt64(), reader.ReadInt32());
        }

        return block;
    }

    private static HashSet<string>? ReadSet(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            set.Add(reader.ReadString());
        }

        return set;
    }

    /// <summary>A column's decompressed bytes, or null if the block has no such column.</summary>
    public BinaryReader? Column(BlockInfo block, string name)
    {
        if (!block.Columns.TryGetValue(name, out var location))
        {
            return null;
        }

        var compressed = new byte[location.Length];
        RandomAccess.Read(_handle, compressed, location.Offset);
        var output = new MemoryStream();
        using (var decompressor = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            decompressor.CopyTo(output);
        }

        output.Position = 0;
        return new BinaryReader(output, Encoding.UTF8);
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Finding a lower-case ASCII word in UTF-8 text, ignoring case, without decoding it.</summary>
internal static class AsciiWord
{
    public const int Absent = 0;
    public const int Present = 1;

    /// <summary>A match touches a non-ASCII character, so only the decoded text can say.</summary>
    public const int Unsure = 2;

    public static int Find(ReadOnlySpan<byte> line, byte[] term)
    {
        if (term.Length == 0)
        {
            return Present;
        }

        var first = term[0];
        var upper = first is >= (byte)'a' and <= (byte)'z' ? (byte)(first - 32) : first;
        var unsure = false;
        var start = 0;
        while (start <= line.Length - term.Length)
        {
            var offset = line[start..].IndexOfAny(first, upper);
            if (offset < 0)
            {
                break;
            }

            var found = start + offset;
            if (found > line.Length - term.Length)
            {
                break;
            }

            if (System.Text.Ascii.EqualsIgnoreCase(line.Slice(found, term.Length), term))
            {
                var before = found == 0 ? (byte)' ' : line[found - 1];
                var after = found + term.Length >= line.Length ? (byte)' ' : line[found + term.Length];
                if (before >= 0x80 || after >= 0x80)
                {
                    unsure = true;
                }
                else if (!IsWordByte(before) && !IsWordByte(after))
                {
                    return Present;
                }
            }

            start = found + 1;
        }

        return unsure ? Unsure : Absent;
    }

    private static bool IsWordByte(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9';
}

/// <summary>A block's rows, decoded column by column as a query needs them.</summary>
internal sealed class BlockRows
{
    private readonly SegmentReader _segment;
    private readonly BlockInfo _block;
    private long[]? _ticks;
    private DateTimeKind[]? _kinds;
    private string[]? _ids;
    private string?[]? _hosts;
    private string[]? _sourceTypes;
    private byte[]? _severities;
    private string[]? _raw;
    private readonly Dictionary<string, object?[]?> _fields = new(StringComparer.OrdinalIgnoreCase);

    public BlockRows(SegmentReader segment, BlockInfo block)
    {
        _segment = segment;
        _block = block;
    }

    public int Count => _block.Rows;
    public BlockInfo Info => _block;

    public long[] Ticks => _ticks ??= ReadTicks();
    public string[] Ids => _ids ??= ReadStrings("id");
    public string?[] Hosts => _hosts ??= ReadNullableStrings("host");
    public string[] SourceTypes => _sourceTypes ??= ReadStrings("st");
    public byte[] Severities => _severities ??= ReadBytes("sev");
    public string[] Raw => _raw ??= ReadStrings("raw");

    private bool[]? _termMatches;

    // The raw column as matching decompressed it, for reading a matched line without doing it again
    private (byte[] Buffer, int Length)? _rawBytes;
    private IReadOnlyList<string>? _matchedTerms;

    /// <summary>
    /// Which rows have every search term as a word, as <see cref="SearchTerms.Matches"/> decides.
    /// ASCII terms are found in the stored UTF-8 bytes without making a string per line; only a
    /// line where a match sits next to a non-ASCII character is decoded, to judge the boundary
    /// exactly as the rule does.
    /// </summary>
    public bool[] TermMatches(IReadOnlyList<string> terms)
    {
        if (_termMatches != null && ReferenceEquals(_matchedTerms, terms))
        {
            return _termMatches;
        }

        var matches = new bool[Count];
        if (_raw != null || terms.Any(t => !System.Text.Ascii.IsValid(t)))
        {
            for (var i = 0; i < Count; i++)
            {
                matches[i] = SearchTerms.Matches(Raw[i], terms);
            }
        }
        else
        {
            using var reader = _segment.Column(_block, "raw")!;
            var stream = (MemoryStream)reader.BaseStream;
            _rawBytes = (stream.GetBuffer(), (int)stream.Length);
            var bytes = _rawBytes.Value.Buffer.AsSpan(0, _rawBytes.Value.Length);
            var termBytes = terms.Select(t => Encoding.ASCII.GetBytes(t)).ToArray();
            var at = 0;
            for (var i = 0; i < Count; i++)
            {
                // A BinaryWriter string: a 7-bit encoded byte length, then UTF-8
                var length = 0;
                var shift = 0;
                byte b;
                do
                {
                    b = bytes[at++];
                    length |= (b & 0x7F) << shift;
                    shift += 7;
                }
                while ((b & 0x80) != 0);

                var line = bytes.Slice(at, length);
                at += length;
                var all = true;
                foreach (var term in termBytes)
                {
                    var found = AsciiWord.Find(line, term);
                    if (found == AsciiWord.Unsure)
                    {
                        all = SearchTerms.Matches(Encoding.UTF8.GetString(line), terms);
                        break;
                    }

                    if (found == AsciiWord.Absent)
                    {
                        all = false;
                        break;
                    }
                }

                matches[i] = all;
            }
        }

        _matchedTerms = terms;
        return _termMatches = matches;
    }
    private DateTimeKind[] Kinds => _kinds ??= ReadBytes("kind").Select(b => (DateTimeKind)b).ToArray();

    private long[] ReadTicks()
    {
        using var reader = _segment.Column(_block, "ts")!;
        var ticks = new long[Count];
        long previous = 0;
        for (var i = 0; i < Count; i++)
        {
            previous += reader.Read7BitEncodedInt64();
            ticks[i] = previous;
        }

        return ticks;
    }

    private string[] ReadStrings(string name)
    {
        using var reader = _segment.Column(_block, name)!;
        var values = new string[Count];
        for (var i = 0; i < Count; i++)
        {
            values[i] = reader.ReadString();
        }

        return values;
    }

    private string?[] ReadNullableStrings(string name)
    {
        using var reader = _segment.Column(_block, name)!;
        var values = new string?[Count];
        for (var i = 0; i < Count; i++)
        {
            values[i] = EventCodec.ReadNullable(reader);
        }

        return values;
    }

    private byte[] ReadBytes(string name)
    {
        using var reader = _segment.Column(_block, name)!;
        return reader.ReadBytes(Count);
    }

    /// <summary>
    /// A field's values: null for rows without it. Field names match ignoring case, as the engine
    /// does; when several stored names differ only in case, the first one a row has wins.
    /// </summary>
    public object?[]? Field(string name)
    {
        if (_fields.TryGetValue(name, out var cached))
        {
            return cached;
        }

        object?[]? values = null;
        bool[]? present = null;
        foreach (var column in _block.Columns.Keys.Where(c => c.StartsWith("f:", StringComparison.Ordinal) && c.Length - 2 == name.Length
                     && string.Equals(c[2..], name, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c, StringComparer.Ordinal))
        {
            using var reader = _segment.Column(_block, column)!;
            values ??= new object?[Count];
            present ??= new bool[Count];
            for (var i = 0; i < Count; i++)
            {
                if (FieldValues.Read(reader, out var value) && !present[i])
                {
                    values[i] = value;
                    present[i] = true;
                }
            }
        }

        _fields[name] = values;
        _present[name] = present;
        return values;
    }

    private readonly Dictionary<string, bool[]?> _present = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether row i has the field at all, even with a null value.</summary>
    public bool HasField(string name, int i)
    {
        Field(name);
        return _present[name] is { } present && present[i];
    }

    /// <summary>Every row as an event, for returning results.</summary>
    /// <summary>
    /// How many of this block's rows a query matched, when it knows. A search whose matches are
    /// scattered reads a row or two from many blocks, which is cheapest one row at a time; the
    /// newest 100 read many rows from one block, which is cheapest decoded whole.
    /// </summary>
    public int? Matched { get; set; }

    private int _singleReads;

    public LogEvent Materialize(int i)
    {
        if (_raw == null && Matched is <= 2 && ++_singleReads <= 2)
        {
            return MaterializeOne(i);
        }

        var logEvent = new LogEvent
        {
            Id = Ids[i],
            RawLine = Raw[i],
            Timestamp = new DateTime(Ticks[i], Kinds[i]),
            Host = Hosts[i],
            SourceType = SourceTypes[i],
            Severity = (LogSeverity)Severities[i],
            Index = _segment.Index,
        };

        var meta = Meta();
        var (indexTicks, sourceFile, lineNumber, parserName, tags) = meta[i];
        logEvent.IndexTime = new DateTime(indexTicks, DateTimeKind.Utc);
        logEvent.SourceFile = sourceFile;
        logEvent.LineNumber = lineNumber;
        logEvent.ParserName = parserName;
        foreach (var tag in tags)
        {
            logEvent.Tags.Add(tag);
        }

        foreach (var (name, values) in AllFields())
        {
            if (values[i] is var (present, value) && present)
            {
                logEvent.Fields[name] = value;
            }
        }

        return logEvent;
    }

    /// <summary>The file a row came from, if any.</summary>
    public string? SourceFile(int i) => Meta()[i].Item2;

    private (long IndexTicks, string? SourceFile, long LineNumber, string? ParserName, string[] Tags)[]? _meta;

    private (long, string?, long, string?, string[])[] Meta()
    {
        if (_meta != null)
        {
            return _meta;
        }

        using var reader = _segment.Column(_block, "meta")!;
        var meta = new (long, string?, long, string?, string[])[Count];
        for (var i = 0; i < Count; i++)
        {
            var indexTicks = reader.ReadInt64();
            var sourceFile = EventCodec.ReadNullable(reader);
            var lineNumber = reader.ReadInt64();
            var parser = EventCodec.ReadNullable(reader);
            var tags = new string[reader.ReadInt32()];
            for (var t = 0; t < tags.Length; t++)
            {
                tags[t] = reader.ReadString();
            }

            meta[i] = (indexTicks, sourceFile, lineNumber, parser, tags);
        }

        return _meta = meta;
    }

    private List<(string Name, (bool Present, object? Value)[] Values)>? _allFields;

    /// <summary>One row as an event, skipping over the other rows in each column rather than decoding them.</summary>
    private LogEvent MaterializeOne(int i)
    {
        var logEvent = new LogEvent
        {
            Id = Ids[i],
            RawLine = StringAt("raw", i),
            Timestamp = new DateTime(Ticks[i], Kinds[i]),
            Host = NullableAt("host", i),
            SourceType = StringAt("st", i),
            Severity = (LogSeverity)Severities[i],
            Index = _segment.Index,
        };

        using (var meta = _segment.Column(_block, "meta")!)
        {
            for (var k = 0; k < i; k++)
            {
                meta.BaseStream.Seek(sizeof(long), SeekOrigin.Current);
                SkipNullable(meta);
                meta.BaseStream.Seek(sizeof(long), SeekOrigin.Current);
                SkipNullable(meta);
                var tagCount = meta.ReadInt32();
                for (var t = 0; t < tagCount; t++)
                {
                    SkipString(meta);
                }
            }

            logEvent.IndexTime = new DateTime(meta.ReadInt64(), DateTimeKind.Utc);
            logEvent.SourceFile = EventCodec.ReadNullable(meta);
            logEvent.LineNumber = meta.ReadInt64();
            logEvent.ParserName = EventCodec.ReadNullable(meta);
            var tags = meta.ReadInt32();
            for (var t = 0; t < tags; t++)
            {
                logEvent.Tags.Add(meta.ReadString());
            }
        }

        foreach (var column in _block.Columns.Keys.Where(c => c.StartsWith("f:", StringComparison.Ordinal)))
        {
            using var reader = _segment.Column(_block, column)!;
            for (var k = 0; k < i; k++)
            {
                FieldValues.Skip(reader);
            }

            if (FieldValues.Read(reader, out var value))
            {
                logEvent.Fields[column[2..]] = value;
            }
        }

        return logEvent;
    }

    private string StringAt(string column, int i)
    {
        using var reader = column == "raw" && _rawBytes is { } raw
            ? new BinaryReader(new MemoryStream(raw.Buffer, 0, raw.Length, writable: false), Encoding.UTF8)
            : _segment.Column(_block, column)!;
        for (var k = 0; k < i; k++)
        {
            SkipString(reader);
        }

        return reader.ReadString();
    }

    private string? NullableAt(string column, int i)
    {
        using var reader = _segment.Column(_block, column)!;
        for (var k = 0; k < i; k++)
        {
            SkipNullable(reader);
        }

        return EventCodec.ReadNullable(reader);
    }

    internal static void SkipString(BinaryReader reader) => reader.BaseStream.Seek(reader.Read7BitEncodedInt(), SeekOrigin.Current);

    private static void SkipNullable(BinaryReader reader)
    {
        if (reader.ReadBoolean())
        {
            SkipString(reader);
        }
    }

    private List<(string, (bool, object?)[])> AllFields()
    {
        if (_allFields != null)
        {
            return _allFields;
        }

        var all = new List<(string, (bool, object?)[])>();
        foreach (var column in _block.Columns.Keys.Where(c => c.StartsWith("f:", StringComparison.Ordinal)))
        {
            using var reader = _segment.Column(_block, column)!;
            var values = new (bool, object?)[Count];
            for (var i = 0; i < Count; i++)
            {
                var present = FieldValues.Read(reader, out var value);
                values[i] = (present, value);
            }

            all.Add((column[2..], values));
        }

        return _allFields = all;
    }
}
