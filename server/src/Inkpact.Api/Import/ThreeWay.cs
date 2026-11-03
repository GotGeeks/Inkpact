namespace Inkpact.Api.Import;

/// <summary>What a three-way comparison of one field decides.</summary>
public enum FieldOutcome
{
    /// <summary>Nothing to do and nothing to say: the other app did not change it, or both now hold the same value.</summary>
    Same,

    /// <summary>Changed only in the other app: its value is taken over.</summary>
    Take,

    /// <summary>Changed here (or in both places, with different values): the value here stays, and the report says so.</summary>
    Keep,
}

/// <summary>
/// The rule for the fields of an item an earlier import made (architecture → "Joplin import mapping"). Each field has three
/// values: what the other app had when it was last imported (the base), what it has now, and what is here now. A field
/// changed only in the other app is taken; one changed here stays, so a re-import never takes away what a person did here.
/// </summary>
public static class ThreeWay
{
    /// <summary>
    /// Decides one field. Without a base (<paramref name="baseKnown"/> false: an item of run 1.15) nothing can be said about
    /// who changed it, so a difference keeps the value here.
    /// </summary>
    public static FieldOutcome Decide<T>(T baseValue, T joplin, T here, bool baseKnown)
    {
        var comparer = EqualityComparer<T>.Default;
        if (comparer.Equals(joplin, here))
        {
            return FieldOutcome.Same;
        }

        if (!baseKnown)
        {
            return FieldOutcome.Keep;
        }

        if (comparer.Equals(joplin, baseValue))
        {
            // Only changed here: nothing is said, the person knows.
            return FieldOutcome.Same;
        }

        return comparer.Equals(here, baseValue) ? FieldOutcome.Take : FieldOutcome.Keep;
    }

    /// <summary>
    /// The tags of a page, as a set: what the other app added since the base is added (unless the page has it), what it
    /// removed is removed (if the page still has it), and a tag only here is never touched. Without a base nothing changes.
    /// </summary>
    public static (HashSet<Guid> Add, HashSet<Guid> Remove) Tags(IReadOnlySet<Guid>? baseTags, IReadOnlySet<Guid> joplin, IReadOnlySet<Guid> here)
    {
        if (baseTags is null)
        {
            return ([], []);
        }

        return ([.. joplin.Where(tag => !baseTags.Contains(tag) && !here.Contains(tag))],
                [.. baseTags.Where(tag => !joplin.Contains(tag) && here.Contains(tag))]);
    }
}
