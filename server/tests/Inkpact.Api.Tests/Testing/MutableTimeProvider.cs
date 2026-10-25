namespace Inkpact.Api.Tests.Testing;

/// <summary>A clock that only moves when a test says so.</summary>
public sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
