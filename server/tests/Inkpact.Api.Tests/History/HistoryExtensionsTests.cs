using Inkpact.Api.History;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inkpact.Api.Tests.History;

public class HistoryExtensionsTests
{
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("50.00:00:00")]
    public void The_retention_interval_must_be_supported_by_the_timer(string interval)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["History:RetentionInterval"] = interval,
        }).Build();

        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddHistory(config));
    }

    [Fact]
    public void The_retention_worker_can_be_disabled_and_uses_the_configured_interval()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["History:RetentionEnabled"] = "false",
            ["History:RetentionInterval"] = "00:30:00",
        }).Build();
        var services = new ServiceCollection().AddHistory(config);
        var options = services.Single(s => s.ServiceType == typeof(HistoryOptions)).ImplementationInstance.ShouldBeOfType<HistoryOptions>();

        options.RetentionEnabled.ShouldBeFalse();
        options.RetentionInterval.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Capture_and_retention_are_on_unless_configured_with_the_cadence_of_the_plan()
    {
        var services = new ServiceCollection().AddHistory(new ConfigurationBuilder().Build());
        var options = services.Single(s => s.ServiceType == typeof(HistoryOptions)).ImplementationInstance.ShouldBeOfType<HistoryOptions>();

        // Retention is on from the release that first writes versions (run B.5b).
        options.RetentionEnabled.ShouldBeTrue();
        options.RetentionInterval.ShouldBe(TimeSpan.FromHours(1));
        options.CaptureEnabled.ShouldBeTrue();
        options.CaptureInterval.ShouldBe(TimeSpan.FromSeconds(30));
        options.CapturesPerRound.ShouldBe(20);
        options.CaptureIdleAfter.ShouldBe(TimeSpan.FromMinutes(2));
        options.CaptureAtLatestAfter.ShouldBe(TimeSpan.FromMinutes(5));
        options.CaptureMaxLiveBytes.ShouldBe(2 * 1024 * 1024);
        options.CaptureLargeLogAfter.ShouldBe(TimeSpan.FromHours(1));
        options.PreviewWait.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("History:CaptureInterval", "00:00:00")]
    [InlineData("History:CaptureInterval", "50.00:00:00")]
    [InlineData("History:CapturesPerRound", "0")]
    [InlineData("History:CaptureIdleAfter", "-00:00:01")]
    [InlineData("History:CaptureAtLatestAfter", "-00:00:01")]
    [InlineData("History:CaptureMaxLiveBytes", "-1")]
    [InlineData("History:CaptureLargeLogAfter", "-00:00:01")]
    [InlineData("History:PreviewWait", "-00:00:01")]
    public void A_capture_setting_that_cannot_work_stops_the_app_at_start(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddHistory(config));
    }
}
