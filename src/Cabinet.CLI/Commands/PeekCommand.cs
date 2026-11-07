using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

public class PeekCommand : Command
{
    public PeekCommand() : base("peek", "Quickly view the beginning or end of log files")
    {
        var pathArgument = new Argument<string>("path", "Path to the log file");
        var linesOption = new Option<int>("--lines", () => 50, "Number of lines to display");
        var reverseOption = new Option<bool>("--reverse", "Read from the end of the file");
        var followOption = new Option<bool>("--follow", "Follow file changes (tail -f)");

        AddArgument(pathArgument);
        AddOption(linesOption);
        AddOption(reverseOption);
        AddOption(followOption);

        this.SetHandler(HandleCommand, pathArgument, linesOption, reverseOption, followOption);
    }

    private async Task<int> HandleCommand(string path, int lines, bool reverse, bool follow)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {path}");
            return 1;
        }

        try
        {
            if (reverse)
            {
                await ReadReverse(path, lines);
            }
            else
            {
                await ReadForward(path, lines);
            }

            if (follow)
            {
                await FollowFile(path);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task ReadForward(string path, int lines)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
        var lineCount = 0;

        while (lineCount < lines && await reader.ReadLineAsync() is { } line)
        {
            DisplayLine(line, lineCount + 1);
            lineCount++;
        }
    }

    private async Task ReadReverse(string path, int lines)
    {
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0) return;

        var result = new List<string>();
        var buffer = new byte[4096];
        var leftover = string.Empty;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var position = fs.Length;

            while (position > 0 && result.Count < lines)
            {
                var readSize = (int)Math.Min(buffer.Length, position);
                position -= readSize;
                fs.Seek(position, SeekOrigin.Begin);
                await fs.ReadExactlyAsync(buffer, 0, readSize);

                var text = Encoding.UTF8.GetString(buffer, 0, readSize) + leftover;
                var textLines = text.Split('\n');

                // Handle partial line at the beginning
                if (position > 0)
                {
                    leftover = textLines[0];
                    textLines = textLines.Skip(1).ToArray();
                }
                else
                {
                    leftover = string.Empty;
                }

                // Add lines in reverse order
                for (int i = textLines.Length - 1; i >= 0 && result.Count < lines; i--)
                {
                    if (!string.IsNullOrEmpty(textLines[i]))
                    {
                        result.Add(textLines[i].TrimEnd('\r'));
                    }
                }
            }

            // Add any leftover as the first line if we haven't reached the limit
            if (!string.IsNullOrEmpty(leftover) && result.Count < lines)
            {
                result.Add(leftover.TrimEnd('\r'));
            }
        }

        // Display in correct order
        result.Reverse();
        for (int i = 0; i < result.Count; i++)
        {
            DisplayLine(result[i], i + 1);
        }
    }

    private async Task FollowFile(string path)
    {
        AnsiConsole.MarkupLine("\n[yellow]Following file changes... Press Ctrl+C to stop[/]\n");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        // Move to end of file
        fs.Seek(0, SeekOrigin.End);

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line != null)
            {
                DisplayLine(line, 0, highlight: true);
            }
            else
            {
                await Task.Delay(100);
            }
        }
    }

    private void DisplayLine(string line, int lineNumber, bool highlight = false)
    {
        // Detect severity and colorize
        var color = DetectSeverityColor(line);
        var prefix = lineNumber > 0 ? $"[dim]{lineNumber,6}:[/] " : "";

        if (highlight)
        {
            AnsiConsole.MarkupLine($"{prefix}[bold {color}]{Markup.Escape(line)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"{prefix}[{color}]{Markup.Escape(line)}[/]");
        }
    }

    private string DetectSeverityColor(string line)
    {
        var lower = line.ToLowerInvariant();

        if (lower.Contains("error") || lower.Contains("fail") || lower.Contains("fatal"))
            return "red";
        if (lower.Contains("warn") || lower.Contains("warning"))
            return "yellow";
        if (lower.Contains("debug") || lower.Contains("trace"))
            return "dim";
        if (lower.Contains("info"))
            return "cyan";

        return "white";
    }
}