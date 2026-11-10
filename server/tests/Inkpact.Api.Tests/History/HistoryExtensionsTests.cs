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
}
