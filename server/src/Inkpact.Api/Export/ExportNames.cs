using System.Text;

namespace Inkpact.Api.Export;

/// <summary>
/// Names for the entries of an export: valid on Windows, macOS and Linux alike, and the same for the same tree every time.
/// A title (or a file's name) loses the characters a file name cannot hold, Windows' reserved names get an underscore, and a
/// name is cut at <see cref="MaxLength"/> characters.
/// </summary>
public static class ExportNames
{
    /// <summary>Longest name, extension included, so a few levels of long titles still fit Windows' 260-character paths.</summary>
    public const int MaxLength = 100;

    public const string Untitled = "Untitled";

    private const int MaxExtensionLength = 16;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A title as the name of a file (without its extension) or a folder: the characters of <c>/ \ : * ? " &lt; &gt; |</c>, control
    /// characters and lone surrogates become a dash, white space is trimmed, a leading dot (a hidden file) and a trailing dot or
    /// space (which Windows drops) are removed, a reserved name gets a <c>_</c>. Nothing left: <see cref="Untitled"/>.
    /// </summary>
    public static string Stem(string? title) => Clean(title, extensionLength: 0);

    /// <summary>
    /// A file's name with its extension kept: the stem is cut so that the whole is at most <see cref="MaxLength"/> characters.
    /// </summary>
    public static string FileName(string? name)
    {
        var cleaned = Clean(name, extensionLength: 0, cut: false);
        var (stem, extension) = Split(cleaned);
        return Clean(stem, extension.Length, cut: true) + extension;
    }

    /// <summary>A file name without the characters that break a header or a path, for the download's name.</summary>
    public static string DownloadName(string? workspaceName)
    {
        var stem = Stem(workspaceName == null ? null : workspaceName.Replace(' ', '-'));
        return stem == Untitled ? "workspace" : stem;
    }

    private static string Clean(string? text, int extensionLength, bool cut = true)
    {
        var builder = new StringBuilder();
        var value = WithoutLoneSurrogates(text ?? "").Normalize(NormalizationForm.FormC);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c) || char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(c);
            }
        }

        var limit = Math.Max(1, MaxLength - extensionLength);
        var result = Trim(builder.ToString());
        if (cut && result.Length > limit)
        {
            var end = limit;
            // Not between the two halves of a character outside the basic plane.
            if (char.IsHighSurrogate(result[end - 1]))
            {
                end--;
            }

            result = Trim(result[..end]);
        }

        if (result.Length == 0)
        {
            return Untitled;
        }

        // "CON", and "CON.md" and "CON.txt" with it, cannot be made on Windows.
        var firstDot = result.IndexOf('.', StringComparison.Ordinal);
        var beforeDot = firstDot < 0 ? result : result[..firstDot];
        return Reserved.Contains(beforeDot.TrimEnd()) ? "_" + result : result;
    }

    /// <summary>Half of a character outside the basic plane cannot be normalised, nor written in a name: it becomes a dash.</summary>
    private static string WithoutLoneSurrogates(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(value[i]).Append(value[i + 1]);
                i++;
            }
            else
            {
                builder.Append(char.IsSurrogate(value[i]) ? '-' : value[i]);
            }
        }

        return builder.ToString();
    }

    /// <summary>White space at both ends, dots at the start and the end, until none is left: <c>". .notes"</c> is <c>notes</c>, never <c>.notes</c>.</summary>
    private static string Trim(string value)
    {
        string before;
        do
        {
            before = value;
            value = value.Trim().Trim('.');
        }
        while (value != before);

        return value;
    }

    private static (string Stem, string Extension) Split(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || name.Length - dot > MaxExtensionLength)
        {
            return (name, "");
        }

        return (name[..dot], name[dot..]);
    }

    /// <summary>
    /// Hands out names that are free in one folder. Windows and macOS compare names without regard to case (and macOS in
    /// another Unicode form), so two names that differ only there count as one. A page takes both its file and its folder, so
    /// that a page that gets children later does not need to move.
    /// </summary>
    public sealed class Folder
    {
        private readonly HashSet<string> _taken = [];

        /// <summary>The stem for a page: <c>Stem.md</c> and the folder <c>Stem</c> are both free; the second of two is <c>Stem (2)</c>.</summary>
        public string TakeStem(string title)
        {
            var stem = Stem(title);
            for (var n = 1; ; n++)
            {
                var candidate = n == 1 ? stem : Numbered(stem, n);
                if (!_taken.Contains(Key(candidate)) && !_taken.Contains(Key(candidate + ".md")))
                {
                    _taken.Add(Key(candidate));
                    _taken.Add(Key(candidate + ".md"));
                    return candidate;
                }
            }
        }

        /// <summary>The name for a file: its own, or with <c>(2)</c> before the extension when that name is taken.</summary>
        public string TakeFile(string name)
        {
            var clean = FileName(name);
            for (var n = 1; ; n++)
            {
                var candidate = n == 1 ? clean : Numbered(clean, n, keepExtension: true);
                if (_taken.Add(Key(candidate)))
                {
                    return candidate;
                }
            }
        }

        /// <summary>Marks a name as used without making it (the README at the top of the export).</summary>
        public void Reserve(string name) => _taken.Add(Key(name));

        private static string Key(string name) => name.Normalize(NormalizationForm.FormC).ToUpperInvariant();

        private static string Numbered(string name, int n, bool keepExtension = false)
        {
            var (stem, extension) = keepExtension ? Split(name) : (name, "");
            var suffix = $" ({n})";
            // The number must fit in the length too, or the name is cut after it is added.
            if (stem.Length + suffix.Length + extension.Length > MaxLength)
            {
                var end = MaxLength - suffix.Length - extension.Length;
                if (char.IsHighSurrogate(stem[end - 1]))
                {
                    end--;
                }

                stem = stem[..end].TrimEnd('.', ' ');
            }

            return stem + suffix + extension;
        }
    }
}
