using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cabinet.Core.Models;

namespace Cabinet.Storage.Engine;

/// <summary>CRC-32 (IEEE), to tell a torn or corrupt log record from a good one.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary>
/// A stable 64-bit hash (FNV-1a with a final mix). Unlike <see cref="string.GetHashCode()"/>, it's
/// the same in every process, which Bloom filters written to disk need.
/// </summary>
internal static class StableHash
{
    public static ulong Of(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        // Spread the bits, since FNV's low bits are weak for short strings
        hash ^= hash >> 33;
        hash *= 0xff51afd7ed558ccdUL;
        hash ^= hash >> 33;
        hash *= 0xc4ceb9fe1a85ec53UL;
        hash ^= hash >> 33;
        return hash;
    }
}

/// <summary>
/// A Bloom filter: says a value is certainly absent, or maybe present. Blocks keep one for their
/// words and one for their event IDs, so searches and duplicate checks skip most blocks.
/// </summary>
internal sealed class BloomFilter
{
    private readonly ulong[] _bits;
    private readonly int _hashes;

    private BloomFilter(ulong[] bits, int hashes)
    {
        _bits = bits;
        _hashes = hashes;
    }

    /// <summary>A filter for about this many values: about 1% false positives, or about 0.07% if <paramref name="precise"/>.</summary>
    /// <remarks>
    /// Duplicate checks probe every segment's ID filter for every new event, and each false positive
    /// decodes a block, so ID filters are the precise kind.
    /// </remarks>
    public static BloomFilter ForCount(int count, bool precise = false)
    {
        var bits = Math.Max(512, count * (precise ? 16 : 10));
        return new BloomFilter(new ulong[(bits + 63) / 64], precise ? 7 : 4);
    }

    public static BloomFilter Read(BinaryReader reader)
    {
        var hashes = reader.ReadByte();
        var words = reader.ReadInt32();
        if (words < 0 || (long)words * sizeof(ulong) > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException($"A filter claims {words} words, more than is left to read");
        }

        var bits = new ulong[words];
        for (var i = 0; i < words; i++)
        {
            bits[i] = reader.ReadUInt64();
        }

        return new BloomFilter(bits, hashes);
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write((byte)_hashes);
        writer.Write(_bits.Length);
        foreach (var word in _bits)
        {
            writer.Write(word);
        }
    }

    private int BitCount => _bits.Length * 64;

    public void Add(string value)
    {
        var hash = StableHash.Of(value);
        var h1 = (uint)hash;
        var h2 = (uint)(hash >> 32) | 1;
        for (uint i = 0; i < _hashes; i++)
        {
            var bit = (int)((h1 + i * h2) % (uint)BitCount);
            _bits[bit >> 6] |= 1UL << (bit & 63);
        }
    }

    public bool MayContain(string value)
    {
        var hash = StableHash.Of(value);
        var h1 = (uint)hash;
        var h2 = (uint)(hash >> 32) | 1;
        for (uint i = 0; i < _hashes; i++)
        {
            var bit = (int)((h1 + i * h2) % (uint)BitCount);
            if ((_bits[bit >> 6] & (1UL << (bit & 63))) == 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>How a field value is written: a tag byte, then the value.</summary>
internal static class FieldValues
{
    public const byte Absent = 0;
    public const byte Null = 1;
    public const byte Text = 2;
    public const byte Integer = 3;
    public const byte Real = 4;
    public const byte True = 5;
    public const byte False = 6;
    public const byte Time = 7;

    /// <summary>
    /// A value as the backends hold it: whole numbers as long, others as double, times as UTC, and
    /// anything else as text, as a JSON round trip on the other backends would give.
    /// </summary>
    public static object? Normalize(object? value) => value switch
    {
        null => null,
        string or long or double or bool => value,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        uint u => (long)u,
        float f => (double)f,
        decimal m => (double)m,
        DateTime t => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t,
        DateTimeOffset o => o.UtcDateTime,
        JsonElement element => JsonFieldValue.Unwrap(element),
        // Lists and objects as the JSON text the other backends store, not the type's name
        System.Collections.IEnumerable or System.Collections.IDictionary => JsonFieldValue.Unwrap(JsonSerializer.SerializeToElement(value)),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    public static void Write(BinaryWriter writer, object? value)
    {
        switch (Normalize(value))
        {
            case null:
                writer.Write(Null);
                break;
            case string text:
                writer.Write(Text);
                writer.Write(text);
                break;
            case long number:
                writer.Write(Integer);
                writer.Write(number);
                break;
            case double number:
                writer.Write(Real);
                writer.Write(number);
                break;
            case bool flag:
                writer.Write(flag ? True : False);
                break;
            case DateTime time:
                writer.Write(Time);
                writer.Write(time.Ticks);
                writer.Write((byte)time.Kind);
                break;
        }
    }

    /// <summary>Moves past one value without making it.</summary>
    public static void Skip(BinaryReader reader)
    {
        var tag = reader.ReadByte();
        var stream = reader.BaseStream;
        switch (tag)
        {
            case Text:
                stream.Seek(reader.Read7BitEncodedInt(), SeekOrigin.Current);
                break;
            case Integer or Real:
                stream.Seek(8, SeekOrigin.Current);
                break;
            case Time:
                stream.Seek(9, SeekOrigin.Current);
                break;
            case Absent or Null or True or False:
                break;
            default:
                throw new InvalidDataException($"Unknown field tag {tag}");
        }
    }

    /// <summary>Reads a value after its tag. Returns false for an absent field.</summary>
    public static bool Read(BinaryReader reader, out object? value)
    {
        var tag = reader.ReadByte();
        value = tag switch
        {
            Absent or Null => null,
            Text => reader.ReadString(),
            Integer => reader.ReadInt64(),
            Real => reader.ReadDouble(),
            True => true,
            False => false,
            Time => new DateTime(reader.ReadInt64(), (DateTimeKind)reader.ReadByte()),
            _ => throw new InvalidDataException($"Unknown field tag {tag}"),
        };
        return tag != Absent;
    }
}

/// <summary>Writes whole events, for the write-ahead log.</summary>
internal static class EventCodec
{
    public static void Write(BinaryWriter writer, LogEvent logEvent)
    {
        writer.Write(logEvent.Id);
        writer.Write(logEvent.Timestamp.Ticks);
        writer.Write((byte)logEvent.Timestamp.Kind);
        writer.Write(logEvent.IndexTime.Ticks);
        writer.Write(logEvent.SourceType ?? string.Empty);
        WriteNullable(writer, logEvent.Host);
        writer.Write((int)logEvent.Severity);
        writer.Write(logEvent.RawLine ?? string.Empty);
        WriteNullable(writer, logEvent.SourceFile);
        writer.Write(logEvent.LineNumber);
        WriteNullable(writer, logEvent.ParserName);
        writer.Write(logEvent.Index ?? "main");
        var tags = logEvent.Tags.ToList();
        writer.Write(tags.Count);
        foreach (var tag in tags)
        {
            writer.Write(tag);
        }

        var fields = logEvent.Fields.ToList();
        writer.Write(fields.Count);
        foreach (var (name, value) in fields)
        {
            writer.Write(name);
            FieldValues.Write(writer, value);
        }
    }

    public static LogEvent Read(BinaryReader reader)
    {
        var logEvent = new LogEvent
        {
            Id = reader.ReadString(),
            RawLine = string.Empty,
        };
        logEvent.Timestamp = new DateTime(reader.ReadInt64(), (DateTimeKind)reader.ReadByte());
        logEvent.IndexTime = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        logEvent.SourceType = reader.ReadString();
        logEvent.Host = ReadNullable(reader);
        logEvent.Severity = (LogSeverity)reader.ReadInt32();
        logEvent.RawLine = reader.ReadString();
        logEvent.SourceFile = ReadNullable(reader);
        logEvent.LineNumber = reader.ReadInt64();
        logEvent.ParserName = ReadNullable(reader);
        logEvent.Index = reader.ReadString();
        var tags = reader.ReadInt32();
        for (var i = 0; i < tags; i++)
        {
            logEvent.Tags.Add(reader.ReadString());
        }

        var fields = reader.ReadInt32();
        for (var i = 0; i < fields; i++)
        {
            var name = reader.ReadString();
            FieldValues.Read(reader, out var value);
            logEvent.Fields[name] = value;
        }

        return logEvent;
    }

    public static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value != null)
        {
            writer.Write(value);
        }
    }

    public static string? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
}
