namespace Inkpact.Api.History;

/// <summary>Thins one page's versions, visited newest first with id as the descending tie-break.</summary>
internal sealed class HistoryRetentionRule
{
    private readonly DateTimeOffset _now;
    private readonly TimeSpan _granular;
    private readonly TimeSpan _daily;
    private readonly TimeSpan _expiry;
    private DateOnly? _lastDay;
    private DateOnly? _lastWeek;

    internal HistoryRetentionRule(HistoryPolicy policy, DateTimeOffset now)
    {
        if (!policy.IsValid)
        {
            throw new ArgumentException("The history policy is invalid.", nameof(policy));
        }

        _now = now.ToUniversalTime();
        _granular = TimeSpan.FromHours(policy.GranularHours);
        _daily = TimeSpan.FromDays(policy.DailyDays);
        _expiry = TimeSpan.FromDays(policy.ExpiryDays);
    }

    internal bool ShouldDelete(DateTimeOffset createdAt, DateTimeOffset? protectedUntil)
    {
        var created = createdAt.ToUniversalTime();
        var age = _now - created;
        var isProtected = protectedUntil > _now;
        if (age < _granular)
        {
            return false;
        }

        if (age >= _expiry)
        {
            return !isProtected;
        }

        var date = DateOnly.FromDateTime(created.UtcDateTime);
        if (age < _daily)
        {
            var duplicate = _lastDay == date;
            _lastDay = date;
            return duplicate && !isProtected;
        }

        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        var week = date.AddDays(-daysSinceMonday);
        var sameWeek = _lastWeek == week;
        _lastWeek = week;
        return sameWeek && !isProtected;
    }
}
