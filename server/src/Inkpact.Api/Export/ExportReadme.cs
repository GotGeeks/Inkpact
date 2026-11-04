using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inkpact.Api.Export;

/// <summary>
/// The <c>README.md</c> at the top of an export: what it is, when it was made and how it is laid out, in the language of the
/// person who asked. The words are the client's own (<c>export.readme.*</c> in <c>en.json</c> and <c>nl.json</c>, built into the
/// server), so the server writes no language of its own. English is the fallback.
/// </summary>
internal static partial class ExportReadme
{
    public const string Fallback = "en";

    private static readonly string[] Languages = ["en", "nl"];

    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Texts = new(() =>
        Languages.ToDictionary(language => language, Load));

    /// <summary>The language to write in: the one asked for when there is a text in it, otherwise English.</summary>
    public static string LanguageOf(string? requested) =>
        Languages.FirstOrDefault(l => string.Equals(l, requested, StringComparison.OrdinalIgnoreCase)) ?? Fallback;

    /// <param name="language">As <see cref="LanguageOf"/> gives it.</param>
    /// <param name="workspace">The workspace's name.</param>
    /// <param name="date">The day of the export, as <c>yyyy-MM-dd</c>.</param>
    /// <param name="pages">How many pages are in it.</param>
    /// <param name="files">How many files are in it.</param>
    /// <param name="missingFiles">Files a page uses whose bytes the store did not have, by the path they should have had.</param>
    /// <param name="unreadablePages">Pages whose content could not be read, by their path: they are there with their title and dates only.</param>
    public static string Write(
        string language,
        string workspace,
        string date,
        int pages,
        int files,
        IReadOnlyList<string> missingFiles,
        IReadOnlyList<string> unreadablePages)
    {
        string T(string key, params (string Name, string Value)[] values)
        {
            var text = Text(language, key) ?? Text(Fallback, key) ?? key;
            // One pass, so that a value with a placeholder in it (a workspace called "{date}") is not filled in again.
            return Placeholder().Replace(text, m => values.FirstOrDefault(v => v.Name == m.Groups[1].Value) is { Name: not null } found ? found.Value : m.Value);
        }

        var markdown = new StringBuilder();
        markdown.Append("# ").Append(T("title")).Append("\n\n");
        // The workspace's name is someone's text: escaped, so that its Markdown or HTML shows as it is written.
        markdown.Append(T("intro", ("workspace", MarkdownInline.Plain(workspace)), ("date", date))).Append("\n\n");
        markdown.Append(T("counts", ("pages", pages.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("files", files.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Append("\n\n");
        markdown.Append("- ").Append(T("pages")).Append('\n');
        markdown.Append("- ").Append(T("files")).Append('\n');
        markdown.Append("- ").Append(T("links")).Append('\n');
        markdown.Append("- ").Append(T("left")).Append('\n');

        Section(markdown, T("missingFiles"), missingFiles);
        Section(markdown, T("unreadablePages"), unreadablePages);
        return markdown.ToString();
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    private static void Section(StringBuilder markdown, string heading, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        markdown.Append('\n').Append(heading).Append("\n\n");
        foreach (var path in paths)
        {
            // In a code span, so that a name with Markdown in it is shown as it is: the fence longer than any run of backticks in
            // it, and a space inside the fence when the name starts or ends with one (CommonMark takes one such space off).
            var fence = new string('`', MarkdownInline.LongestBacktickRun(path) + 1);
            var pad = path.StartsWith('`') || path.EndsWith('`') ? " " : "";
            markdown.Append("- ").Append(fence).Append(pad).Append(path).Append(pad).Append(fence).Append('\n');
        }
    }

    private static string? Text(string language, string key)
    {
        var element = Texts.Value[language];
        foreach (var part in new[] { "export", "readme", key })
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static JsonElement Load(string language)
    {
        using var stream = typeof(ExportReadme).Assembly.GetManifestResourceStream($"Inkpact.Api.Export.{language}.json")
            ?? throw new InvalidOperationException($"The text file for '{language}' is not built into the server.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
