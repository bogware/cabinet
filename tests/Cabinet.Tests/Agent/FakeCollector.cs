using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Cabinet.Tests.Agent;

/// <summary>
/// Stands in for the HEC endpoint, recording what it accepted and failing on demand. Like the
/// real server, it stores an event ID once however many times it's sent.
/// </summary>
internal sealed class FakeCollector : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, string> _stored = new();
    private int _requests;
    private int _sends;

    /// <summary>Decides the response for a request's lines. Null means accept.</summary>
    public Func<IReadOnlyList<string>, HttpResponseMessage?>? Respond { get; set; }

    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Accepted events, counting repeats. More than stored means retries happened.</summary>
    public int EventsSent => Volatile.Read(ref _sends);

    /// <summary>Event text as stored: one entry per unique event ID.</summary>
    public IReadOnlyCollection<string> Stored => _stored.Values.ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);

        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var response = Respond?.Invoke(lines);
        if (response != null)
        {
            return response;
        }

        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var text = doc.RootElement.GetProperty("event").GetString()!;
            var id = doc.RootElement.TryGetProperty("id", out var value) ? value.GetString()! : Guid.NewGuid().ToString();
            Interlocked.Increment(ref _sends);
            _stored.TryAdd(id, text);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string description, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {description}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }
}
