namespace Inkpact.Api.Attachments;

/// <summary>Where attachments are kept and how large one may be, from the <c>Attachments</c> configuration section.</summary>
public sealed class AttachmentOptions
{
    public const string SectionName = "Attachments";

    /// <summary>The root of the local store. The image points it at the data volume; a second volume can be mounted here instead.</summary>
    public string Directory { get; set; } = "data/attachments";

    /// <summary>The largest file one upload may be, in bytes.</summary>
    public long MaxFileSize { get; set; } = 50L * 1024 * 1024;
}
