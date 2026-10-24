using System.Text;
using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Options;
using YDotNet.Document.Transactions;
using YDotNet.Document.Types.XmlTexts;

namespace Spike.Crdt;

/// <summary>
/// <see cref="ICrdtCodec"/> through YDotNet (yrs, in process). With <paramref name="garbageCollect"/> off,
/// deleted content is kept in snapshots (Yjs' <c>gc: false</c>).
/// </summary>
public sealed class YrsCodec(bool garbageCollect = true) : ICrdtCodec
{
    /// <summary>The XmlFragment name TipTap's Collaboration extension uses.</summary>
    public const string Field = "default";

    // An empty state vector (zero clients) asks for the whole document.
    private static readonly byte[] EmptyStateVector = [0];

    public byte[] Merge(IEnumerable<byte[]> updates)
    {
        using var doc = Load(updates);
        using var read = doc.ReadTransaction();
        return read.StateDiffV1(EmptyStateVector);
    }

    public byte[] Diff(byte[] state, byte[] stateVector)
    {
        using var doc = Load([state]);
        using var read = doc.ReadTransaction();
        return read.StateDiffV1(stateVector);
    }

    public byte[] StateVector(byte[] state)
    {
        using var doc = Load([state]);
        using var read = doc.ReadTransaction();
        return read.StateVectorV1();
    }

    /// <summary>The text of every text block (a node whose children are text), separated by a newline.</summary>
    public string ExtractText(byte[] state)
    {
        using var doc = Load([state]);
        var fragment = doc.XmlFragment(Field);
        using var read = doc.ReadTransaction();

        var blocks = new List<string>();
        for (var child = fragment.FirstChild(read); child is not null; child = Next(read, child))
        {
            Collect(read, child, blocks);
        }

        return string.Join('\n', blocks);
    }

    private Doc Load(IEnumerable<byte[]> updates)
    {
        var doc = new Doc(new DocOptions { SkipGarbageCollection = !garbageCollect });
        // One transaction for the whole log. One per update (as Yjs' applyUpdate does) was measured
        // too: it is slower and gives a snapshot that is not byte-stable when merged again.
        using var write = doc.WriteTransaction();
        foreach (var update in updates)
        {
            var result = write.ApplyV1(update);
            if (result != TransactionUpdateResult.Ok)
            {
                throw new InvalidDataException($"yrs rejected an update: {result}");
            }
        }

        write.Commit();
        return doc;
    }

    private static void Collect(Transaction read, Output node, List<string> blocks)
    {
        if (node.Tag != OutputTag.XmlElement)
        {
            return;
        }

        var element = node.XmlElement;
        var first = element.FirstChild(read);
        if (first is null)
        {
            // An empty text block (e.g. an empty paragraph) still counts; leaf nodes like a rule do not.
            if (element.Tag(read) is "paragraph" or "heading" or "codeBlock")
            {
                blocks.Add(string.Empty);
            }

            return;
        }

        if (first.Tag == OutputTag.XmlText)
        {
            var text = new StringBuilder();
            for (var child = first; child is not null; child = Next(read, child))
            {
                if (child.Tag == OutputTag.XmlText)
                {
                    AppendChunks(read, child.XmlText, text);
                }
            }

            blocks.Add(text.ToString());
            return;
        }

        for (var child = first; child is not null; child = Next(read, child))
        {
            Collect(read, child, blocks);
        }
    }

    private static void AppendChunks(Transaction read, XmlText node, StringBuilder text)
    {
        // Chunks carry the raw text plus formatting attributes; String() would add mark tags.
        foreach (var chunk in node.Chunks(read))
        {
            if (chunk.Data.Tag == OutputTag.String)
            {
                text.Append(chunk.Data.String);
            }
        }
    }

    private static Output? Next(Transaction read, Output node) => node.Tag switch
    {
        OutputTag.XmlElement => node.XmlElement.NextSibling(read),
        OutputTag.XmlText => node.XmlText.NextSibling(read),
        _ => null,
    };
}
