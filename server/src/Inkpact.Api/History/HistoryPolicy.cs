using Inkpact.Data.Entities;

namespace Inkpact.Api.History;

internal sealed record HistoryPolicy(bool Enabled, int GranularHours, int DailyDays, int ExpiryDays, long? CapBytes)
{
    internal const int MaximumDays = 3_650;
    internal const long Mebibyte = 1024 * 1024;

    internal bool IsValid =>
        GranularHours > 0
        && GranularHours <= MaximumDays * 24
        && DailyDays > 0
        && DailyDays <= MaximumDays
        && (long)DailyDays * 24 >= GranularHours
        && ExpiryDays >= DailyDays
        && ExpiryDays <= MaximumDays
        && (CapBytes is null || (CapBytes >= Mebibyte && CapBytes % Mebibyte == 0));

    internal static HistoryPolicy From(Workspace workspace) =>
        new(workspace.HistoryEnabled, workspace.HistoryGranularHours, workspace.HistoryDailyDays,
            workspace.HistoryExpiryDays, workspace.HistoryCapBytes);
}
