using System.Text;
using Cabinet.Agent.Monitoring;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Agent;

public class LineReaderTests
{
    private static async Task<List<TailLine>> ReadAll(LineReader reader)
    {
        var lines = new List<TailLine>();
        await foreach (var line in reader.ReadLinesAsync())
        {
            lines.Add(line);
        }

        return lines;
    }

    private static MemoryStream Stream(string content) => new(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task ReadsLinesWithExactOffsets()
    {
        var reader = new LineReader(Stream("one\ntwo\n"), 0, 1024);

        var lines = await ReadAll(reader);

        lines.Should().Equal(new TailLine("one", 0, 4), new TailLine("two", 4, 8));
        reader.Position.Should().Be(8);
    }

    [Fact]
    public async Task UnterminatedLine_WaitsForItsLineEnding()
    {
        var stream = new MemoryStream();
        var reader = new LineReader(stream, 0, 1024);

        stream.Write("par"u8);
        (await ReadAll(reader)).Should().BeEmpty();
        reader.Position.Should().Be(0, "a partial line isn't delivered, so a crash re-reads it");

        stream.Seek(0, SeekOrigin.End);
        stream.Write("tial\n"u8);
        (await ReadAll(reader)).Should().Equal(new TailLine("partial", 0, 8));
    }

    [Fact]
    public async Task MultiByteCharacters_KeepByteOffsets()
    {
        var reader = new LineReader(Stream("héllo\nwörld\n"), 0, 1024);

        var lines = await ReadAll(reader);

        lines[0].Should().Be(new TailLine("héllo", 0, 7));
        lines[1].StartOffset.Should().Be(7);
    }

    [Fact]
    public async Task MultiByteCharacterAcrossReads_StaysIntact()
    {
        var text = new string('x', (64 * 1024) - 1) + "é\nnext\n";
        var reader = new LineReader(Stream(text), 0, 1024 * 1024);

        var lines = await ReadAll(reader);

        lines[0].Text.Should().EndWith("xé");
        lines[1].Text.Should().Be("next");
    }

    [Fact]
    public async Task WindowsLineEndings_AreRemoved()
    {
        var lines = await ReadAll(new LineReader(Stream("one\r\ntwo\r\n"), 0, 1024));

        lines.Select(l => l.Text).Should().Equal("one", "two");
    }

    [Fact]
    public async Task ByteOrderMark_IsRemovedFromFirstLine()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat("one\n"u8.ToArray()).ToArray();

        var lines = await ReadAll(new LineReader(new MemoryStream(bytes), 0, 1024));

        lines.Single().Should().Be(new TailLine("one", 0, 7));
    }

    [Fact]
    public async Task LongLine_IsTruncatedAndNextLineUnaffected()
    {
        var long_ = new string('x', 300_000);
        var reader = new LineReader(Stream($"{long_}\nnext\n"), 0, 100);

        var lines = await ReadAll(reader);

        lines[0].Text.Should().HaveLength(100);
        lines[0].EndOffset.Should().Be(300_001, "the offset still covers the whole line");
        lines[1].Text.Should().Be("next");
    }

    [Fact]
    public async Task StartsFromGivenOffset()
    {
        var lines = await ReadAll(new LineReader(Stream("one\ntwo\n"), 4, 1024));

        lines.Should().Equal(new TailLine("two", 4, 8));
    }
}
