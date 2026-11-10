using System.Text;

namespace Inkpact.Data.Entities;

public enum PageVersionKind
{
    Automatic,
    Rename,
    Restore,
    BeforeRestore,
}

/// <summary>A self-contained historical Yjs state and the page metadata needed to restore it.</summary>
public class PageVersion
{
    private byte[] _state = [];

    private PageVersion()
    {
    }

    public PageVersion(
        Guid pageId,
        byte[] state,
        string title,
        string fileText,
        DateTimeOffset createdAt,
        Guid? createdBy,
        PageVersionKind kind,
        DateTimeOffset? protectedUntil = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(fileText);

        PageId = pageId;
        State = state;
        Title = title;
        FileText = fileText;
        CreatedAt = createdAt;
        CreatedBy = createdBy;
        Kind = kind;
        ProtectedUntil = protectedUntil;
        PayloadBytes = checked(state.LongLength + Encoding.UTF8.GetByteCount(fileText));
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid PageId { get; private set; }

    public byte[] State
    {
        get => _state.ToArray();
        private set => _state = value.ToArray();
    }

    public string Title { get; private set; } = "";

    public string FileText { get; private set; } = "";

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public PageVersionKind Kind { get; private set; }

    public DateTimeOffset? ProtectedUntil { get; private set; }

    /// <summary>Bytes counted against the workspace cap: the Yjs state and the file-note search text.</summary>
    public long PayloadBytes { get; private set; }
}
