using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The second canonical pass, from the re-scored pilot's remaining first differences. Same shape as
/// the first: every rule has a test that the two spellings agree and a test that the neighbouring
/// difference, which means something else, is still a difference. Nothing here names a real card
/// and nothing touches the network or the corpus.
/// </summary>
public sealed partial class CanonicalTests
{
    private static ReplacementAbility Replacing(ReplacedEvent instead, bool selfOnly) =>
        new(instead, new ReplaceWith(new Exile(new Triggering())), SelfOnly: selfOnly);

    private static bool SelfOnlyOf(CardDefinition definition) =>
        ((ReplacementAbility)Canonical.Of(definition).Faces[0].AbilityList[0]).SelfOnly;

    /// <summary>
    /// CR 614.15: a replacement whose event already names this permanent replaces only its own
    /// source whether or not the reading said so, so the flag is derived from the event.
    /// </summary>
    [Fact]
    public void Self_only_is_derived_when_the_replaced_event_names_this_permanent()
    {
        // "This creature enters with two +1/+1 counters on it" — an unconstrained
        // WouldEnterBattlefield printed on a permanent is that permanent entering, CR 614.1c.
        var entering = new WouldEnterBattlefield(ObjectFilter.Any);
        Same(Card(Replacing(entering, selfOnly: false)), Card(Replacing(entering, selfOnly: true)));
        Assert.True(SelfOnlyOf(Card(Replacing(entering, selfOnly: false))));

        // "If this creature would die, exile it instead" — the filter says so outright.
        var thisDies = new WouldDie(new ObjectFilter(Also: new IsThisObject()));
        Same(Card(Replacing(thisDies, selfOnly: false)), Card(Replacing(thisDies, selfOnly: true)));
        Assert.True(SelfOnlyOf(Card(Replacing(thisDies, selfOnly: false))));
    }

    /// <summary>The flag is only derived where the event names this permanent; elsewhere it is a reading.</summary>
    [Fact]
    public void Self_only_stays_as_written_when_the_replaced_event_names_other_objects()
    {
        // "If a creature would die" is not "if this creature would die": one replaces every
        // creature's death and the other only its own, and no rewrite may decide which was meant.
        var anyCreatureDies = new WouldDie(ObjectFilter.Creature);
        Differs(Card(Replacing(anyCreatureDies, selfOnly: true)), Card(Replacing(anyCreatureDies, selfOnly: false)));
        Assert.False(SelfOnlyOf(Card(Replacing(anyCreatureDies, selfOnly: false))));

        // A constrained entry is another card's entry, not this one's.
        var creatureEnters = new WouldEnterBattlefield(ObjectFilter.Creature);
        Differs(Card(Replacing(creatureEnters, selfOnly: true)), Card(Replacing(creatureEnters, selfOnly: false)));
    }

    /// <summary>CR 205.4c: Basic is a supertype only lands have, so "basic" and "basic land" are one filter.</summary>
    [Fact]
    public void A_basic_filter_gains_the_land_type()
    {
        var basic = new ObjectFilter(Supertypes: Supertype.Basic);
        var basicLand = new ObjectFilter(Supertypes: Supertype.Basic, Types: CardType.Land);
        Same(Spell(new Search(basic, Zone.Battlefield)), Spell(new Search(basicLand, Zone.Battlefield)));

        // Filled in rather than struck out, so the engine's type check does not have to infer it.
        var searched = (Search)Do(Canonical.Of(Spell(new Search(basic, Zone.Battlefield))));
        Assert.Equal(CardType.Land, searched.For.Types);
        Assert.Equal(Supertype.Basic, searched.For.Supertypes);
    }

    /// <summary>Every other supertype belongs to more than one type, and a stated type is never overwritten.</summary>
    [Fact]
    public void Only_basic_implies_land_and_a_stated_type_is_left_alone()
    {
        // Legendary creatures, artifacts and lands all exist; Legendary implies nothing.
        var legendary = new ObjectFilter(Supertypes: Supertype.Legendary);
        Differs(
            Spell(new Search(legendary, Zone.Hand)),
            Spell(new Search(new ObjectFilter(Supertypes: Supertype.Legendary, Types: CardType.Land), Zone.Hand)));

        // A basic land creature card is a real filter; the rule adds Land, it does not replace one.
        var snowCreature = new ObjectFilter(Supertypes: Supertype.Basic, Types: CardType.Creature);
        var reading = (Search)Do(Canonical.Of(Spell(new Search(snowCreature, Zone.Hand))));
        Assert.Equal(CardType.Creature, reading.For.Types);
    }

    /// <summary>CR 701.23b: the shuffle belongs to the search, so the search that shuffles is the spelling.</summary>
    [Fact]
    public void A_search_followed_by_a_shuffle_is_the_search_that_shuffles()
    {
        var land = new ObjectFilter(Types: CardType.Land);
        var spelledOut = Spell(new Sequence([new Search(land, Zone.Battlefield, ShuffleAfter: false), new Shuffle()]));
        var folded = Spell(new Search(land, Zone.Battlefield, ShuffleAfter: true));

        Same(spelledOut, folded);

        // The sequence is left with one step, which is that step.
        var written = Assert.IsType<Search>(Do(Canonical.Of(spelledOut)));
        Assert.True(written.ShuffleAfter);

        // A search with more to do afterwards keeps its sequence, and only the shuffle folds in.
        var longer = Spell(new Sequence(
        [
            new Search(land, Zone.Battlefield, ShuffleAfter: false),
            new Shuffle(),
            new Draw(new Fixed(1)),
        ]));
        Same(longer, Spell(new Sequence([new Search(land, Zone.Battlefield, ShuffleAfter: true), new Draw(new Fixed(1))])));
    }

    /// <summary>A search that does not shuffle, and a shuffle that is not the search's, both survive.</summary>
    [Fact]
    public void A_search_that_does_not_shuffle_and_someone_elses_shuffle_are_left_alone()
    {
        var land = new ObjectFilter(Types: CardType.Land);

        // "Search your library ... you don't shuffle" is a different card from one that shuffles.
        Differs(
            Spell(new Search(land, Zone.Battlefield, ShuffleAfter: false)),
            Spell(new Search(land, Zone.Battlefield, ShuffleAfter: true)));

        // An opponent's shuffle after your search is a second instruction, not the search's own.
        Differs(
            Spell(new Sequence([new Search(land, Zone.Battlefield, ShuffleAfter: false), new Shuffle(new EachOpponent())])),
            Spell(new Search(land, Zone.Battlefield, ShuffleAfter: true)));

        // A search of a graveyard is not a search of a library; nothing folds into it.
        Differs(
            Spell(new Sequence([new Search(land, Zone.Hand, From: Zone.Graveyard, ShuffleAfter: false), new Shuffle()])),
            Spell(new Search(land, Zone.Hand, From: Zone.Graveyard, ShuffleAfter: true)));
    }

    /// <summary>CR 701.23a: you search your own library, so an owner there says nothing.</summary>
    [Fact]
    public void Owned_by_you_is_dropped_inside_a_search_of_your_own_library()
    {
        var yours = new ObjectFilter(Types: CardType.Creature, OwnedBy: new You());
        var bare = new ObjectFilter(Types: CardType.Creature);
        Same(Spell(new Search(yours, Zone.Hand)), Spell(new Search(bare, Zone.Hand)));
        Assert.Null(((Search)Do(Canonical.Of(Spell(new Search(yours, Zone.Hand))))).For.OwnedBy);
    }

    /// <summary>Every other zone has an owner worth naming, and so does every other filter.</summary>
    [Fact]
    public void Owned_by_you_is_kept_outside_a_search_of_your_own_library()
    {
        var yours = new ObjectFilter(Types: CardType.Creature, OwnedBy: new You());
        var bare = new ObjectFilter(Types: CardType.Creature);

        // "Search a graveyard for a creature card you own" is not "search a graveyard".
        Differs(
            Spell(new Search(yours, Zone.Hand, From: Zone.Graveyard)),
            Spell(new Search(bare, Zone.Hand, From: Zone.Graveyard)));

        // And outside a search entirely, an owner is a plain constraint.
        Differs(Spell(new Destroy(new Target(yours))), Spell(new Destroy(new Target(bare))));
    }

    /// <summary>Where the record's own default is one, a written Fixed(1) is the same instruction.</summary>
    [Fact]
    public void A_count_of_one_is_dropped_where_leaving_it_out_already_means_one()
    {
        var creature = new ObjectFilter(Types: CardType.Creature);

        // "Discard a card."
        Same(Spell(new Discard(new Fixed(1))), Spell(new Discard()));

        // "Destroy target creature."
        Same(Spell(new Destroy(new Target(creature, Count: new Fixed(1)))), Spell(new Destroy(new Target(creature))));

        // "As an additional cost, sacrifice a creature."
        Same(
            Card(new ActivatedAbility([new SacrificeCost(creature, new Fixed(1))], new Draw(new Fixed(1)))),
            Card(new ActivatedAbility([new SacrificeCost(creature)], new Draw(new Fixed(1)))));

        Assert.Null(((Discard)Do(Canonical.Of(Spell(new Discard(new Fixed(1)))))).N);
    }

    /// <summary>Two is not one, a required amount is not optional, and UpTo is not a count.</summary>
    [Fact]
    public void A_count_that_does_not_mean_one_is_left_exactly_as_written()
    {
        var creature = new ObjectFilter(Types: CardType.Creature);

        // The rule is about one, not about counts.
        Differs(Spell(new Discard(new Fixed(2))), Spell(new Discard()));

        // Draw.N and Mill.N have no default: absent there is a broken reading, not "one".
        Assert.Equal(new Fixed(1), ((Draw)Do(Canonical.Of(Spell(new Draw(new Fixed(1)))))).N);
        Assert.Equal(new Fixed(1), ((Mill)Do(Canonical.Of(Spell(new Mill(new Fixed(1)))))).N);

        // "Up to one" is a choice the player makes; "one" is not.
        Differs(
            Spell(new Search(creature, Zone.Hand, UpTo: new Fixed(1))),
            Spell(new Search(creature, Zone.Hand)));
        Assert.Equal(new Fixed(1), ((Search)Do(Canonical.Of(Spell(new Search(creature, Zone.Hand, UpTo: new Fixed(1)))))).UpTo);
    }

    /// <summary>
    /// What the reflection actually covers, so the rule's reach is read off the vocabulary rather
    /// than remembered. Every entry is a record whose count parameter defaults to null.
    /// </summary>
    [Fact]
    public void The_records_whose_count_means_one_are_derived_from_the_vocabulary()
    {
        var covered = Canonical.CountsThatMeanOne;

        foreach (var (record, property) in new[]
        {
            (nameof(Discard), "n"), (nameof(Sacrifice), "n"),
            (nameof(Target), "count"), (nameof(Choose), "count"),
            (nameof(SacrificeCost), "count"), (nameof(DiscardCost), "count"), (nameof(TapOther), "count"),
            (nameof(RemoveCountersCost), "count"), (nameof(PutCountersCost), "count"),
            (nameof(ExileFromZoneCost), "count"), (nameof(ReturnToHandCost), "count"),
            (nameof(RevealCost), "count"), (nameof(TapUntappedCost), "count"),
        })
        {
            Assert.True(covered.TryGetValue(record, out var properties), record);
            Assert.Contains(property, properties!);
        }

        // A required amount is not in the set: stripping it would write a definition with no count.
        Assert.DoesNotContain(nameof(Draw), covered.Keys);
        Assert.DoesNotContain(nameof(Mill), covered.Keys);
        Assert.DoesNotContain(nameof(MillCost), covered.Keys);
    }

    /// <summary>
    /// Two readings that both gave up in the same place understood the card the same way: the
    /// vocabulary is short a primitive and the prose each chose for that hole is not a reading.
    /// </summary>
    [Fact]
    public void Two_unsupported_readings_agree_whatever_their_prose_and_the_file_keeps_the_reason()
    {
        var first = Spell(new Unsupported("No way to express this in the vocabulary."));
        var second = Spell(new Unsupported("The vocabulary has no way to say this."));

        var result = AgreeStage.Agree(new AgreeRequest(OracleId, "Canonical Scribe", "sha256:cccc", "prompt-v1",
            [new ModelReading("model-a", first), new ModelReading("model-b", second)]));

        Assert.Equal(ImplementationStatus.Translated, result.Status);
        Assert.True(result.Agreed);
        Assert.Null(result.Difference);

        // The marker survives, prose and all, and L001 still records it — only the status is honest.
        Assert.Equal("No way to express this in the vocabulary.", ((Unsupported)Do(result.File!.Definition)).Reason);
        Assert.Contains(result.Lint["model-a"]!.Findings, f => f.Code == "L001");
        Assert.Empty(result.CleanModels);
    }

    /// <summary>An Unsupported marker never agrees with a reading that expressed the card.</summary>
    [Fact]
    public void An_unsupported_reading_against_a_real_one_is_still_disputed()
    {
        var result = AgreeStage.Agree(new AgreeRequest(OracleId, "Canonical Scribe", "sha256:cccc", "prompt-v1",
            [
                new ModelReading("model-a", Spell(new Unsupported("No way to express this."))),
                new ModelReading("model-b", Spell(new Draw(new Fixed(1)))),
            ]));

        Assert.Equal(ImplementationStatus.Disputed, result.Status);
        Assert.NotNull(result.Difference);

        // And two markers in different places are two different readings of the card.
        var elsewhere = AgreeStage.Agree(new AgreeRequest(OracleId, "Canonical Scribe", "sha256:cccc", "prompt-v1",
            [
                new ModelReading("model-a", Spell(new Sequence([new Draw(new Fixed(2)), new Unsupported("a")]))),
                new ModelReading("model-b", Spell(new Sequence([new Draw(new Fixed(3)), new Unsupported("b")]))),
            ]));
        Assert.Equal(ImplementationStatus.Disputed, elsewhere.Status);
    }

    /// <summary>
    /// The differences this pass deliberately leaves standing. Each is a real reading of the card
    /// that the vocabulary can tell apart, and the honest outcome is a disputed card, not a guess.
    /// </summary>
    [Fact]
    public void The_second_pass_canonicalises_nothing_semantic_away_either()
    {
        // "Add one mana of any color" is not "add one mana of a color this says".
        Differs(
            Spell(new AddMana(new ManaProduction(AnyColor: true))),
            Spell(new AddMana(new ManaProduction(AnyColor: false))));

        // "Any target" still has no spelling of its own; both readings stay as written.
        Differs(
            Spell(new DealDamage(new Fixed(3), new Target(new ObjectFilter(AnyTypes: CardType.Creature | CardType.Planeswalker | CardType.Battle)), new TargetPlayer(PlayerFilter.Any))),
            Spell(new DealDamage(new Fixed(3), new Target(ObjectFilter.Any), new TargetPlayer(PlayerFilter.Any))));

        // Remembering a target and targeting again are different instructions: the remembered
        // object is the one that was targeted even after it moves.
        var creature = new ObjectFilter(Types: CardType.Creature);
        Differs(
            Spell(new Sequence([new Remember("k", new Target(creature)), new Destroy(new Remembered("k"))])),
            Spell(new Sequence([new Remember("k", new Target(creature)), new Destroy(new Target(creature))])));
    }

    /// <summary>The report carries the pair and the card, so the next reader does not open a card file.</summary>
    [Fact]
    public void The_report_carries_one_example_pair_and_the_card_it_came_from()
    {
        var report = AgreeReport.Of(
        [
            Judged(Spell(new Draw(new Fixed(1))), Spell(new Mill(new Fixed(1)))),
            Judged(Spell(new Draw(new Fixed(2))), Spell(new GainLife(new Fixed(2)))),
        ]);

        var group = Assert.Single(report.Differences);
        Assert.Equal("kind", group.Property);
        Assert.Equal(2, group.Cards);
        Assert.Equal("\"Draw\"", group.Left);
        Assert.Equal("\"Mill\"", group.Right);
        Assert.Equal("Canonical Scribe", group.ExampleCard);
        Assert.Contains("on Canonical Scribe", report.Text(), StringComparison.Ordinal);
    }
}
