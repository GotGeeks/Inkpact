using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.History;
using Inkpact.Data.Entities;

namespace Inkpact.Api.Tests.History;

public class HistoryRetentionRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly HistoryPolicy Default = new(true, 24, 30, 365, null);

    [Fact]
    public void The_workspace_defaults_form_a_valid_policy()
    {
        HistoryPolicy.From(new Workspace()).ShouldBe(Default);
        Default.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 30, 365)]
    [InlineData(-1, 30, 365)]
    [InlineData(25, 1, 365)]
    [InlineData(24, 0, 365)]
    [InlineData(24, 30, 29)]
    [InlineData(24, 30, 3651)]
    [InlineData(87601, 3650, 3650)]
    public void Invalid_or_unordered_windows_are_refused(int hours, int days, int expiry)
    {
        var policy = Default with { GranularHours = hours, DailyDays = days, ExpiryDays = expiry };

        policy.IsValid.ShouldBeFalse();
        Should.Throw<ArgumentException>(() => new HistoryRetentionRule(policy, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1048575)]
    [InlineData(1048577)]
    public void A_cap_must_be_a_positive_whole_number_of_mebibytes(long bytes)
    {
        (Default with { CapBytes = bytes }).IsValid.ShouldBeFalse();
        (Default with { CapBytes = HistoryPolicy.Mebibyte }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Every_version_younger_than_the_granular_window_is_kept()
    {
        var rule = new HistoryRetentionRule(Default, Now);

        rule.ShouldDelete(Now.AddMinutes(1), null).ShouldBeFalse();
        rule.ShouldDelete(Now, null).ShouldBeFalse();
        rule.ShouldDelete(Now.AddHours(-24).AddTicks(1), null).ShouldBeFalse();
    }

    [Fact]
    public void At_the_granular_boundary_only_the_newest_version_of_a_utc_day_is_kept()
    {
        var rule = new HistoryRetentionRule(Default, Now);

        rule.ShouldDelete(Now.AddHours(-24), null).ShouldBeFalse();
        rule.ShouldDelete(Now.AddHours(-25), null).ShouldBeTrue();
        rule.ShouldDelete(Now.AddHours(-37), null).ShouldBeFalse();
    }

    [Fact]
    public void At_the_daily_boundary_the_newest_version_of_the_utc_iso_week_is_kept()
    {
        var rule = new HistoryRetentionRule(Default, Now);

        rule.ShouldDelete(Now.AddDays(-30), null).ShouldBeFalse();
        rule.ShouldDelete(Now.AddDays(-30).AddHours(-1), null).ShouldBeTrue();
        rule.ShouldDelete(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero), null).ShouldBeFalse();
    }

    [Fact]
    public void At_the_expiry_boundary_a_version_is_removed_but_one_just_younger_is_kept()
    {
        var rule = new HistoryRetentionRule(Default, Now);

        rule.ShouldDelete(Now.AddDays(-365).AddTicks(1), null).ShouldBeFalse();
        rule.ShouldDelete(Now.AddDays(-365), null).ShouldBeTrue();
        rule.ShouldDelete(Now.AddDays(-365).AddTicks(-1), null).ShouldBeTrue();
    }

    [Fact]
    public void Protected_versions_survive_thinning_and_expiry_until_the_exact_protection_boundary()
    {
        var rule = new HistoryRetentionRule(Default, Now);
        var day = Now.AddDays(-2);

        rule.ShouldDelete(day, Now.AddMinutes(1)).ShouldBeFalse();
        rule.ShouldDelete(day.AddHours(-1), null).ShouldBeTrue();
        rule.ShouldDelete(day.AddHours(-2), Now.AddMinutes(1)).ShouldBeFalse();
        rule.ShouldDelete(Now.AddDays(-400), Now.AddTicks(1)).ShouldBeFalse();
        rule.ShouldDelete(Now.AddDays(-400), Now).ShouldBeTrue();
    }

    [Fact]
    public void Calendar_buckets_use_utc_even_when_a_timestamp_has_another_offset()
    {
        var rule = new HistoryRetentionRule(Default, Now);
        var first = new DateTimeOffset(2026, 11, 8, 0, 30, 0, TimeSpan.FromHours(2));
        var second = new DateTimeOffset(2026, 11, 7, 22, 0, 0, TimeSpan.Zero);

        rule.ShouldDelete(first, null).ShouldBeFalse();
        rule.ShouldDelete(second, null).ShouldBeTrue();
    }

    [Fact]
    public void Disabling_new_capture_does_not_disable_retention_of_existing_versions()
    {
        var rule = new HistoryRetentionRule(Default with { Enabled = false }, Now);

        rule.ShouldDelete(Now.AddDays(-365), null).ShouldBeTrue();
    }

    [Fact]
    public void Generated_schedules_keep_each_bucket_winner_and_all_protected_versions()
    {
        var timestamps = Gen.Sized(size => Gen.Choose(0, Math.Min(size + 1, 200))
            .SelectMany(count => Gen.ListOf(Gen.Choose(0, 10_000), count))
            .Select(items => items.ToArray()));
        var config = Config.QuickThrowOnFailure.WithMaxTest(500).WithEndSize(200).WithQuietOnSuccess(true);

        Check.One(config, Prop.ForAll(Arb.From(timestamps), values =>
        {
            var versions = values.Select((value, id) => new
            {
                Id = id,
                Created = Now.AddHours(-(value % 9_000)),
                Protected = value % 11 == 0,
            }).OrderByDescending(v => v.Created).ThenByDescending(v => v.Id).ToList();
            var days = versions.Where(v => Now - v.Created >= TimeSpan.FromHours(24) && Now - v.Created < TimeSpan.FromDays(30))
                .GroupBy(v => DateOnly.FromDateTime(v.Created.UtcDateTime))
                .Select(g => g.First().Id).ToHashSet();
            var weeks = versions.Where(v => Now - v.Created >= TimeSpan.FromDays(30) && Now - v.Created < TimeSpan.FromDays(365))
                .GroupBy(v => (ISOWeek.GetYear(v.Created.UtcDateTime), ISOWeek.GetWeekOfYear(v.Created.UtcDateTime)))
                .Select(g => g.First().Id).ToHashSet();
            var rule = new HistoryRetentionRule(Default, Now);
            foreach (var version in versions)
            {
                var age = Now - version.Created;
                var keep = version.Protected || age < TimeSpan.FromHours(24)
                    || (age < TimeSpan.FromDays(30) && days.Contains(version.Id))
                    || (age >= TimeSpan.FromDays(30) && age < TimeSpan.FromDays(365) && weeks.Contains(version.Id));
                if (rule.ShouldDelete(version.Created, version.Protected ? Now.AddHours(1) : null) == keep)
                {
                    return false;
                }
            }

            return true;
        }));
    }
}
