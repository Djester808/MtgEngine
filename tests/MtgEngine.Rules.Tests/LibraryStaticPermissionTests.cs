using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Tests;

/// <summary>Library permissions must work from a live permanent, not just an injected ActiveEffect.</summary>
public sealed class LibraryStaticPermissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_phased_out_provider_cannot_grant_library_play_permission(bool land)
    {
        var table = new CastingTable();
        var card = table.Add(land ? TestFaces.Land() : TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        table.Add(TestFaces.Artifact(), CastingTable.A, Zone.Battlefield);
        var provider = table.Add(TestFaces.Creature(abilities: [new StaticAbility(
            new GrantAbility(new StaticAbility(new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true))),
            To: new Each(ObjectFilter.Any with { Types = CardType.Artifact }))]), CastingTable.A, Zone.Battlefield);
        var engine = Subsystems.Wave2();
        Assert.Contains(card, land ? engine.Legal.For(table.State, CastingTable.A).PlayableLands
            : engine.Legal.For(table.State, CastingTable.A).Castable);
        table.Apply(EventLogSerializer.Read(EventLogSerializer.Write([new ObjectChanged(provider, PhasedOut: true)])));
        Assert.NotNull(land ? engine.Casting.Check(table.State, new PlayLand(CastingTable.A, card))
            : engine.Casting.Check(table.State, new CastSpell(CastingTable.A, card)));
        Assert.DoesNotContain(card, land ? engine.Legal.For(table.State, CastingTable.A).PlayableLands
            : engine.Legal.For(table.State, CastingTable.A).Castable);
        table.Apply([new ObjectChanged(provider, PhasedOut: false)]);
        Assert.Contains(card, land ? engine.Legal.For(table.State, CastingTable.A).PlayableLands
            : engine.Legal.For(table.State, CastingTable.A).Castable);
    }

    [Fact]
    public void Different_faces_do_not_share_a_library_permission_slot()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, Zone.Library);
        var spell = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        var permission = new StaticAbility(new MayPlayFromZone(new You(), Zone.Library,
            TopOfLibraryOnly: true, OncePerTurn: true));
        var definition = new CardDefinition("TEST-LIBRARY-FACES", "Library Faces", Layout.Transform, [
            new Face("Library Front", FaceRole.Main, new TypeLine(CardType.Artifact), Abilities: [permission]),
            new Face("Library Back", FaceRole.Back, new TypeLine(CardType.Artifact), Abilities: [permission]),
        ]);
        var source = table.Add(definition, CastingTable.A, Zone.Battlefield);
        var engine = Subsystems.Wave2();
        var played = engine.Casting.PlayLand(table.State, new PlayLand(CastingTable.A, land));
        table.Apply(EventLogSerializer.Read(EventLogSerializer.Write(played.Events)));
        Assert.NotNull(engine.Casting.Check(table.State, new CastSpell(CastingTable.A, spell)));
        var turned = GameReducer.Apply(table.State, EventLogSerializer.Read(EventLogSerializer.Write(
            [new ObjectTurnedOver(source, 1, TurnKind.Transform)])));
        Assert.Null(engine.Casting.Check(turned, new CastSpell(CastingTable.A, spell)));
        var cast = engine.Casting.Cast(turned, new CastSpell(CastingTable.A, spell));
        var replayed = GameReducer.Apply(turned, EventLogSerializer.Read(EventLogSerializer.Write(cast.Events)));
        var identity = Assert.Single(replayed.History.SpellsCast).StaticPermission;
        Assert.Equal(source, identity?.Source);
        Assert.Equal(1, identity?.Origin?.Face);
        Assert.NotEqual(Assert.Single(replayed.History.LandsPlayed).StaticPermission, identity);
    }

    [Fact]
    public void A_copied_permission_stays_spent_after_an_earlier_keyword_is_removed()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, Zone.Library);
        var spell = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        table.Add(TestFaces.Artifact(abilities: [
            new KeywordAbility(KeywordKind.Indestructible),
            new StaticAbility(new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true, OncePerTurn: true)),
        ]), CastingTable.B, Zone.Battlefield);
        var copy = table.Add(TestFaces.Creature(), CastingTable.A, Zone.Battlefield);
        table.Apply([new MtgEngine.Rules.Interpreter.ContinuousEffectCreated(new ActiveEffect(
            81, new BecomesCopyOf(new Each(ObjectFilter.Any with { Types = CardType.Artifact })),
            null, CastingTable.A, [copy], [], new UntilEndOfTurn(), table.State.NextTimestamp, false))]);
        var engine = Subsystems.Wave2();
        var played = engine.Casting.PlayLand(table.State, new PlayLand(CastingTable.A, land));
        table.Apply(EventLogSerializer.Read(EventLogSerializer.Write(played.Events)));
        var identity = Assert.Single(table.State.History.LandsPlayed).StaticPermission;
        Assert.Equal(copy, identity?.Source);
        Assert.Equal("copy", identity?.Origin?.Category);
        Assert.Equal(81, identity?.Origin?.Parent?.EffectId);
        table.Apply([new MtgEngine.Rules.Interpreter.ContinuousEffectCreated(new ActiveEffect(
            82, new LoseAbility(KeywordKind.Indestructible), null, CastingTable.A,
            [copy], [], new UntilEndOfTurn(), table.State.NextTimestamp, false))]);
        Assert.Single(engine.Characteristics.Of(table.State, copy).Abilities);
        Assert.NotNull(engine.Casting.Check(table.State, new CastSpell(CastingTable.A, spell)));
    }

    [Fact]
    public void Two_granting_permanents_keep_separate_permissions_when_one_leaves()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, Zone.Library);
        var spell = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        var recipient = table.Add(TestFaces.Artifact(), CastingTable.A, Zone.Battlefield);
        var grant = new StaticAbility(new GrantAbility(new StaticAbility(new MayPlayFromZone(
            new You(), Zone.Library, TopOfLibraryOnly: true, OncePerTurn: true))),
            To: new Each(ObjectFilter.Any with { Types = CardType.Artifact }));
        var definition = TestFaces.Creature(abilities: [grant]);
        var first = table.Add(definition, CastingTable.A, Zone.Battlefield);
        var second = table.Add(definition, CastingTable.A, Zone.Battlefield);
        var engine = Subsystems.Wave2();
        var play = engine.Casting.PlayLand(table.State, new PlayLand(CastingTable.A, land));
        table.Apply(EventLogSerializer.Read(EventLogSerializer.Write(play.Events)));
        var used = Assert.Single(table.State.History.LandsPlayed).StaticPermission;
        Assert.Equal(recipient, used?.Source);
        Assert.Equal(first, used?.Origin?.Source);
        var cast = engine.Casting.Cast(table.State, new CastSpell(CastingTable.A, spell));
        table.Apply(EventLogSerializer.Read(EventLogSerializer.Write(cast.Events)));
        Assert.Equal(second, Assert.Single(table.State.History.SpellsCast).StaticPermission?.Origin?.Source);
        var stack = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Stack)));
        table.Apply(engine.Zones.Move(table.State, stack, ZoneKey.Of(Zone.Graveyard, CastingTable.A)).Events);
        table.Apply(engine.Zones.Move(table.State, first, ZoneKey.Of(Zone.Graveyard, CastingTable.A)).Events);
        var next = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        Assert.Single(engine.Characteristics.Of(table.State, recipient).Abilities);
        Assert.NotNull(engine.Casting.Check(table.State, new CastSpell(CastingTable.A, next)));
    }

    [Fact]
    public void Independent_grants_keep_their_identity_when_an_earlier_grant_expires()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, Zone.Library);
        var spell = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        var source = table.Add(TestFaces.Artifact(), CastingTable.A, Zone.Battlefield);
        var ability = new StaticAbility(new MayPlayFromZone(new You(), Zone.Library,
            TopOfLibraryOnly: true, OncePerTurn: true));
        foreach (var id in new[] { 81, 82 })
            table.Apply([new MtgEngine.Rules.Interpreter.ContinuousEffectCreated(new ActiveEffect(
                id, new GrantAbility(ability), null, CastingTable.A, [source], [],
                new UntilEndOfTurn(), table.State.NextTimestamp, false))]);
        var engine = Subsystems.Wave2();
        var played = engine.Casting.PlayLand(table.State, new PlayLand(CastingTable.A, land));
        table.Apply(EventLogSerializer.Read(EventLogSerializer.Write(played.Events)));
        Assert.Equal(81, Assert.Single(table.State.History.LandsPlayed).StaticPermission?.Origin?.EffectId);
        Assert.Null(engine.Casting.Check(table.State, new CastSpell(CastingTable.A, spell)));
        var cast = engine.Casting.Cast(table.State, new CastSpell(CastingTable.A, spell));
        table.Apply(EventLogSerializer.Read(EventLogSerializer.Write(cast.Events)));
        Assert.Equal(82, Assert.Single(table.State.History.SpellsCast).StaticPermission?.Origin?.EffectId);
        var next = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        var stack = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Stack)));
        table.Apply(engine.Zones.Move(table.State, stack, ZoneKey.Of(Zone.Graveyard, CastingTable.A)).Events);
        var expired = table.State with { ActiveEffects = table.State.ActiveEffects.RemoveAll(effect => effect.Id == 81) };
        Assert.NotNull(engine.Casting.Check(expired, new CastSpell(CastingTable.A, next)));
    }

    [Fact]
    public void Removing_an_earlier_keyword_does_not_refresh_a_spent_library_permission()
    {
        var table = new CastingTable();
        var land = table.Add(TestFaces.Land(), CastingTable.A, Zone.Library);
        var spell = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        var source = table.Add(TestFaces.Artifact(abilities: [
            new KeywordAbility(KeywordKind.Indestructible),
            new StaticAbility(new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true, OncePerTurn: true)),
        ]), CastingTable.A, Zone.Battlefield);
        var engine = Subsystems.Wave2();
        table.Apply(engine.Casting.PlayLand(table.State, new PlayLand(CastingTable.A, land)).Events);
        Assert.NotNull(engine.Casting.Check(table.State, new CastSpell(CastingTable.A, spell)));
        table.Apply([new MtgEngine.Rules.Interpreter.ContinuousEffectCreated(new ActiveEffect(
            81, new LoseAbility(KeywordKind.Indestructible), null, CastingTable.A,
            [source], [], new UntilEndOfTurn(), table.State.NextTimestamp, false))]);
        Assert.Single(engine.Characteristics.Of(table.State, source).Abilities);
        Assert.NotNull(engine.Casting.Check(table.State, new CastSpell(CastingTable.A, spell)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_static_permission_applies_only_to_cards_named_by_its_selector(bool land, bool matches)
    {
        var table = new CastingTable();
        var card = table.Add(land ? TestFaces.Land() : TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        var wanted = matches ? (land ? CardType.Land : CardType.Sorcery) : CardType.Creature;
        table.Add(TestFaces.Artifact(abilities: [new StaticAbility(
            new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true),
            To: new Each(ObjectFilter.Any with { In = Zone.Library, Types = wanted }))]),
            CastingTable.A, Zone.Battlefield);
        var engine = Subsystems.Wave2();
        var refusal = land
            ? engine.Casting.Check(table.State, new PlayLand(CastingTable.A, card))
            : engine.Casting.Check(table.State, new CastSpell(CastingTable.A, card));
        Assert.Equal(matches, refusal is null);
        var offered = engine.Legal.For(table.State, CastingTable.A);
        Assert.Equal(matches, (land ? offered.PlayableLands : offered.Castable).Contains(card));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_static_permission_spent_by_either_kind_of_play_survives_replay_and_is_source_specific(bool land)
    {
        var table = new CastingTable();
        var first = table.Add(land ? TestFaces.Land() : TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        var second = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        var definition = TestFaces.Artifact(abilities:
            [new StaticAbility(new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true, OncePerTurn: true))]);
        var source = table.Add(definition, CastingTable.A, Zone.Battlefield);
        var engine = Subsystems.Wave2();
        var played = land
            ? engine.Casting.PlayLand(table.State, new PlayLand(CastingTable.A, first))
            : engine.Casting.Cast(table.State, new CastSpell(CastingTable.A, first));
        table.Apply(EventLogSerializer.Read(EventLogSerializer.Write(played.Events)));
        var identity = land ? Assert.Single(table.State.History.LandsPlayed).StaticPermission
            : Assert.Single(table.State.History.SpellsCast).StaticPermission;
        Assert.Equal(new StaticPlayPermission(source, 0), identity);
        if (!land)
        {
            var stack = Assert.Single(table.State.In(ZoneKey.Shared(Zone.Stack)));
            table.Apply(engine.Zones.Move(table.State, stack, ZoneKey.Of(Zone.Graveyard, CastingTable.A)).Events);
        }
        Assert.NotNull(engine.Casting.Check(table.State, new CastSpell(CastingTable.A, second)));
        table.Add(definition, CastingTable.A, Zone.Battlefield);
        Assert.Null(engine.Casting.Check(table.State, new CastSpell(CastingTable.A, second)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Static_permission_reads_controller_condition_and_card_filter(bool wrongController, bool wrongFilter)
    {
        var table = new CastingTable();
        var card = table.Add(TestFaces.Sorcery("{0}"), CastingTable.A, Zone.Library);
        table.Add(TestFaces.Artifact(abilities: [new StaticAbility(
            new MayPlayFromZone(new You(), Zone.Library,
                What: ObjectFilter.Any with { Types = wrongFilter ? CardType.Creature : CardType.Sorcery }, TopOfLibraryOnly: true),
            While: new Compare(new Fixed(1), ComparisonOp.Eq, new Fixed(wrongController || wrongFilter ? 1 : 0)))]),
            wrongController ? CastingTable.B : CastingTable.A, Zone.Battlefield);
        Assert.NotNull(Subsystems.Wave2().Casting.Check(table.State, new CastSpell(CastingTable.A, card)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legal_actions_include_a_library_card_only_when_the_casting_engine_allows_it(bool land)
    {
        var table = new CastingTable();
        var card = land ? TestFaces.Land() : TestFaces.Sorcery("{0}");
        var top = table.Add(card, CastingTable.A, Zone.Library);
        var below = table.Add(card, CastingTable.A, Zone.Library);
        var engine = Subsystems.Wave2();
        var without = engine.Legal.For(table.State, CastingTable.A);
        Assert.Empty(land ? without.PlayableLands : without.Castable);
        table.Apply([CastingTestsPermissions.ToPlayFromTop(table.State)]);
        Assert.Null(land
            ? engine.Casting.Check(table.State, new PlayLand(CastingTable.A, top))
            : engine.Casting.Check(table.State, new CastSpell(CastingTable.A, top)));
        var offered = engine.Legal.For(table.State, CastingTable.A);
        Assert.Equal(top, Assert.Single(land ? offered.PlayableLands : offered.Castable));
        Assert.DoesNotContain(below, land ? offered.PlayableLands : offered.Castable);
        table.Priority(CastingTable.B);
        var noPriority = engine.Legal.For(table.State, CastingTable.A);
        Assert.Empty(land ? noPriority.PlayableLands : noPriority.Castable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_live_permanent_offers_only_its_controllers_top_library_card(bool land)
    {
        var table = new CastingTable();
        var card = land ? TestFaces.Land() : TestFaces.Sorcery("{0}");
        var top = table.Add(card, CastingTable.A, Zone.Library);
        var below = table.Add(card, CastingTable.A, Zone.Library);
        var opponent = table.Add(card, CastingTable.B, Zone.Library);
        var source = table.Add(TestFaces.Artifact(abilities:
            [new StaticAbility(new MayPlayFromZone(new You(), Zone.Library, TopOfLibraryOnly: true))]),
            CastingTable.A, Zone.Battlefield);
        var engine = Subsystems.Wave2();

        Assert.Empty(table.State.ActiveEffects);
        var legal = engine.Legal.For(table.State, CastingTable.A);
        Assert.Contains(top, land ? legal.PlayableLands : legal.Castable);
        Assert.DoesNotContain(below, land ? legal.PlayableLands : legal.Castable);
        Assert.DoesNotContain(opponent, land ? legal.PlayableLands : legal.Castable);

        foreach (var unavailable in new[]
        {
            new ObjectChanged(source, FaceDown: true).Apply(table.State),
            new ObjectChanged(source, PhasedOut: true).Apply(table.State),
            engine.Zones.Move(table.State, source, ZoneKey.Of(Zone.Graveyard, CastingTable.A)).State,
        })
        {
            var denied = engine.Legal.For(unavailable, CastingTable.A);
            Assert.DoesNotContain(top, land ? denied.PlayableLands : denied.Castable);
        }

        var played = land
            ? engine.Casting.PlayLand(table.State, new PlayLand(CastingTable.A, top))
            : engine.Casting.Cast(table.State, new CastSpell(CastingTable.A, top));
        Assert.DoesNotContain(top, played.State.In(ZoneKey.Of(Zone.Library, CastingTable.A)));
        Assert.Equal(below, played.State.In(ZoneKey.Of(Zone.Library, CastingTable.A))[0]);
        var restored = GameReducer.Apply(table.State,
            EventLogSerializer.Read(EventLogSerializer.Write(played.Events)));
        Assert.Equal(played.State.In(ZoneKey.Of(Zone.Library, CastingTable.A)),
            restored.In(ZoneKey.Of(Zone.Library, CastingTable.A)));
    }
}
