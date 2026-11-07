using Cabinet.API.Services;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public class RetentionServiceTests
{
    private readonly Mock<ILogStorage> _storage = new();

    private RetentionService CreateService(int retentionDays)
    {
        var config = new CabinetConfiguration();
        config.Storage.RetentionDays = retentionDays;
        return new RetentionService(
            NullLogger<RetentionService>.Instance,
            _storage.Object,
            Options.Create(config));
    }

    [Fact]
    public async Task ApplyRetentionPoliciesAsync_DefaultConfiguration_DeletesNothing()
    {
        var service = new RetentionService(
            NullLogger<RetentionService>.Instance,
            _storage.Object,
            Options.Create(new CabinetConfiguration()));

        await service.ApplyRetentionPoliciesAsync();

        _storage.Verify(
            s => s.PurgeOldEventsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ApplyRetentionPoliciesAsync_NonPositiveDays_DeletesNothing(int days)
    {
        await CreateService(days).ApplyRetentionPoliciesAsync();

        _storage.Verify(
            s => s.PurgeOldEventsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(365)]
    public async Task ApplyRetentionPoliciesAsync_ConfiguredDays_PurgesAtThatCutoff(int days)
    {
        DateTime? cutoff = null;
        _storage
            .Setup(s => s.PurgeOldEventsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<DateTime, CancellationToken>((before, _) => cutoff = before)
            .ReturnsAsync(0);

        await CreateService(days).ApplyRetentionPoliciesAsync();

        cutoff.Should().BeCloseTo(DateTime.UtcNow.AddDays(-days), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ApplyRetentionPoliciesAsync_StorageFails_DoesNotThrow()
    {
        _storage
            .Setup(s => s.PurgeOldEventsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage offline"));

        var act = () => CreateService(30).ApplyRetentionPoliciesAsync();

        await act.Should().NotThrowAsync();
    }
}
