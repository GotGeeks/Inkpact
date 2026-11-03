using FsCheck;
using FsCheck.Fluent;
using Inkpact.Api.Import;

namespace Inkpact.Api.Tests.Import;

/// <summary>The rule for the fields of an item imported before (run 1.16b): the other app's value is taken only if nothing changed here.</summary>
public sealed class ThreeWayTests
{
    // base, Joplin, here, base known -> outcome
    [Theory]
    [InlineData("a", "a", "a", true, FieldOutcome.Same)] // nothing changed
    [InlineData("a", "b", "a", true, FieldOutcome.Take)] // changed only in Joplin
    [InlineData("a", "a", "c", true, FieldOutcome.Same)] // changed only here: left alone, nothing to say
    [InlineData("a", "b", "c", true, FieldOutcome.Keep)] // changed in both, differently: here stays, and it is said
    [InlineData("a", "b", "b", true, FieldOutcome.Same)] // changed in both to the same value
    [InlineData("a", "b", "b", false, FieldOutcome.Same)] // the same value needs no base
    [InlineData("a", "b", "c", false, FieldOutcome.Keep)] // no base: a difference keeps what is here
    [InlineData("a", "a", "c", false, FieldOutcome.Keep)] // no base: not even "Joplin did not change" can be told
    public void Each_combination_of_the_three_values_has_its_outcome(string baseValue, string joplin, string here, bool baseKnown, FieldOutcome expected) =>
        ThreeWay.Decide(baseValue, joplin, here, baseKnown).ShouldBe(expected);

    [Fact]
    public void A_missing_value_is_a_value()
    {
        // A due date that is taken away in Joplin is taken away here, when it was not touched here.
        ThreeWay.Decide<long?>(5, null, 5, true).ShouldBe(FieldOutcome.Take);
        ThreeWay.Decide<long?>(null, 5, null, true).ShouldBe(FieldOutcome.Take);
        ThreeWay.Decide<long?>(null, 5, 7, true).ShouldBe(FieldOutcome.Keep);
    }

    [Fact]
    public void Tags_are_a_set_that_only_Joplins_changes_move()
    {
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (add, remove) = ThreeWay.Tags(
            baseTags: new HashSet<Guid> { a, b },
            joplin: new HashSet<Guid> { b, c },
            here: new HashSet<Guid> { a, b, d });

        add.ShouldBe([c]);
        remove.ShouldBe([a]);
    }

    [Fact]
    public void A_tag_added_here_is_never_removed_and_one_the_page_has_already_is_not_added()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var (add, remove) = ThreeWay.Tags(new HashSet<Guid>(), new HashSet<Guid> { a }, new HashSet<Guid> { a, b });
        add.ShouldBeEmpty();
        remove.ShouldBeEmpty();
    }

    [Fact]
    public void Without_a_base_no_tag_changes()
    {
        var (add, remove) = ThreeWay.Tags(null, new HashSet<Guid> { Guid.NewGuid() }, new HashSet<Guid> { Guid.NewGuid() });
        add.ShouldBeEmpty();
        remove.ShouldBeEmpty();
    }

    [Fact]
    public void Whatever_the_values_a_value_that_differs_from_the_base_here_is_never_taken_over()
    {
        var values = Gen.Choose(0, 3);
        var property = Prop.ForAll(Arb.From(Gen.Zip(Gen.Zip(values, values), Gen.Zip(values, Gen.Elements(true, false)))), t =>
        {
            var ((baseValue, joplin), (here, known)) = t;
            var outcome = ThreeWay.Decide(baseValue, joplin, here, known);

            // Taken only when Joplin changed it and here it is as it was.
            var taken = outcome == FieldOutcome.Take;
            var allowed = known && joplin != baseValue && here == baseValue;
            // A value that is the same needs nothing; one that differs and is not taken is kept.
            var settled = joplin == here ? outcome == FieldOutcome.Same : taken || outcome is FieldOutcome.Same or FieldOutcome.Keep;
            return (taken == (allowed && joplin != here)) && settled;
        });

        Check.One(Config.QuickThrowOnFailure.WithQuietOnSuccess(true), property);
    }

    [Fact]
    public void Tags_never_remove_what_Joplin_did_not_have_in_the_base_nor_add_what_it_does_not_have_now()
    {
        var ids = Gen.Choose(0, 5).Select(i => new Guid(i, 0, 0, new byte[8]));
        var sets = Gen.ListOf(ids, 4).Select(list => list.ToHashSet());
        var property = Prop.ForAll(Arb.From(Gen.Zip(sets, Gen.Zip(sets, sets))), t =>
        {
            var (before, (now, here)) = t;
            var (add, remove) = ThreeWay.Tags(before, now, here);
            return add.All(tag => now.Contains(tag) && !before.Contains(tag) && !here.Contains(tag))
                && remove.All(tag => before.Contains(tag) && !now.Contains(tag) && here.Contains(tag));
        });

        Check.One(Config.QuickThrowOnFailure.WithQuietOnSuccess(true), property);
    }
}
