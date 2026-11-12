using System.Net;
using Cabinet.Alerts;
using Cabinet.AWS.Parsers;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class AlertServiceTests
{
    private static Alert NewAlert() => new() { Title = "Disk full", Message = "web-1 is out of space", Severity = AlertSeverity.Critical };

    private static Mock<IAlertChannel> Channel(string name, Exception? failure = null)
    {
        var channel = new Mock<IAlertChannel>();
        channel.SetupGet(c => c.Name).Returns(name);
        var send = channel.Setup(c => c.SendAsync(It.IsAny<Alert>(), It.IsAny<CancellationToken>()));
        if (failure != null)
        {
            send.ThrowsAsync(failure);
        }
        else
        {
            send.Returns(Task.CompletedTask);
        }

        return channel;
    }

    [Fact]
    public async Task AFailingChannel_DoesntStopTheOthers()
    {
        var broken = Channel("broken", new HttpRequestException("down"));
        var working = Channel("working");
        var service = new AlertService(NullLogger<AlertService>.Instance, [broken.Object, working.Object]);

        await service.SendAlertAsync(NewAlert());

        working.Verify(c => c.SendAsync(It.IsAny<Alert>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void RegisterChannel_KeepsOnePerName()
    {
        var service = new AlertService(NullLogger<AlertService>.Instance, []);

        service.RegisterChannel(Channel("slack").Object);
        service.RegisterChannel(Channel("slack").Object);
        service.RegisterChannel(Channel("email").Object);

        service.GetChannels().Select(c => c.Name).Should().Equal("slack", "email");
    }

    [Fact]
    public async Task RegisteringWhileSending_IsSafe()
    {
        var service = new AlertService(NullLogger<AlertService>.Instance, [Channel("first").Object]);

        var sending = Task.WhenAll(Enumerable.Range(0, 200).Select(_ => service.SendAlertAsync(NewAlert())));
        for (var i = 0; i < 200; i++)
        {
            service.RegisterChannel(Channel($"c{i}").Object);
        }

        await sending;
        service.GetChannels().Should().HaveCount(201);
    }
}

public sealed class WebhookAlertChannelTests
{
    private sealed class Recorder(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _calls;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var status = statuses[Math.Min(_calls++, statuses.Length - 1)];
            return new HttpResponseMessage(status);
        }
    }

    [Fact]
    public async Task Sends_TheAlertAsJson_AndRetriesAServerError()
    {
        var handler = new Recorder(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var channel = new WebhookAlertChannel(
            new HttpClient(handler),
            new WebhookConfiguration { Url = "https://hooks.example.com/abc", RetryCount = 2 },
            NullLogger<WebhookAlertChannel>.Instance);

        await channel.SendAsync(new Alert { Title = "Disk full", Message = "web-1", Severity = AlertSeverity.Warning });

        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[^1].Should().Contain("Disk full");
    }

    [Fact]
    public async Task WithoutAUrl_SendsNothing()
    {
        var handler = new Recorder(HttpStatusCode.OK);
        var channel = new WebhookAlertChannel(new HttpClient(handler), new WebhookConfiguration(), NullLogger<WebhookAlertChannel>.Instance);

        await channel.SendAsync(new Alert { Title = "x", Message = "y" });

        handler.Bodies.Should().BeEmpty();
    }
}

public sealed class AwsParserTests
{
    private const string CloudTrailRecord =
        """{"eventVersion":"1.08","userIdentity":{"type":"IAMUser","userName":"alice","arn":"arn:aws:iam::123456789012:user/alice"},"eventTime":"2026-03-04T05:06:07Z","eventSource":"signin.amazonaws.com","eventName":"ConsoleLogin","awsRegion":"us-east-1","sourceIPAddress":"203.0.113.9","additionalEventData":{"MFAUsed":"No"},"responseElements":{"ConsoleLogin":"Success"}}""";

    [Fact]
    public async Task CloudTrail_ReadsTheRecordsFields()
    {
        var parser = new CloudTrailParser();

        parser.CanParse(CloudTrailRecord).Should().BeTrue();
        var logEvent = (await parser.ParseAsync(CloudTrailRecord))!;

        logEvent.SourceType.Should().Be(SourceTypes.CloudTrail);
        logEvent.Timestamp.Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        logEvent.GetField<string>("EventName").Should().Be("ConsoleLogin");
        logEvent.GetField<string>("EventSource").Should().Be("signin.amazonaws.com");
    }

    [Theory]
    [InlineData("plain text line")]
    [InlineData("{\"message\":\"json but not CloudTrail\"}")]
    [InlineData("{not json}")]
    public void CloudTrail_LeavesOtherLinesAlone(string line) =>
        new CloudTrailParser().CanParse(line).Should().BeFalse();

    [Fact]
    public async Task CloudWatch_TimestampPrefix_IsUtc_WhateverTheServersZone()
    {
        // Parsed as local time, it moved by the server's offset
        const string line = "[2026-03-04 05:06:07.000] REPORT RequestId: 3f1e2d6c-0000-4000-8000-000000000001\tDuration: 12.34 ms";
        var parser = new CloudWatchParser();

        parser.CanParse(line).Should().BeTrue();
        var logEvent = (await parser.ParseAsync(line))!;

        logEvent.SourceType.Should().Be(SourceTypes.CloudWatch);
        logEvent.Timestamp.Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        logEvent.GetField<string>("RequestId").Should().Be("3f1e2d6c-0000-4000-8000-000000000001");
    }
}
