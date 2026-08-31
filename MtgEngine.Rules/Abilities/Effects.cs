using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Abilities;

/// <summary>What a target is (CR 115.1).</summary>
public enum TargetKind
{
    Permanent,
    Player,
    SpellOnStack,
    CardInGraveyard,

    /// <summary>
    /// "Any target": a creature, a planeswalker, a battle, or a player (CR 115.4).
    /// </summary>
    /// <remarks>
    /// Its own kind rather than a union of the others, because it is what the cards actually say
    /// and it is the commonest targeting line there is. A spec that could only name one kind
    /// would force every burn spell to be written twice.
    /// </remarks>
    Any,
}

/// <summary>One chosen target (CR 115.1).</summary>
public readonly record struct Target(TargetKind Kind, ObjectId Subject, Guid Player)
{
    public static Target ToPermanent(ObjectId id) => new(TargetKind.Permanent, id, Guid.Empty);

    public static Target ToPlayer(Guid id) => new(TargetKind.Player, default, id);

    public static Target ToSpell(ObjectId id) => new(TargetKind.SpellOnStack, id, Guid.Empty);

    public static Target ToCard(ObjectId id) => new(TargetKind.CardInGraveyard, id, Guid.Empty);
}

/// <summary>
/// What a spell or ability may target (CR 115.1).
/// </summary>
/// <remarks>
/// The legality question is asked twice: when targets are chosen, as the spell is cast
/// (CR 601.2c), and again when it resolves (CR 608.2b). A spell whose only target has become
/// illegal in between does not resolve at all. That second check is why this is a rule the
/// engine keeps rather than something checked once at the point of casting.
/// </remarks>
public sealed record TargetSpec
{
    public required TargetKind Kind { get; init; }

    /// <summary>Reads naturally in an error: "target creature you control".</summary>
    public required string Description { get; init; }

    /// <summary>Which objects qualify. Null accepts any object of the right kind.</summary>
    public Func<GameState, IAbilitySource, GameObject, Guid, bool>? ObjectFilter { get; init; }

    /// <summary>
    /// A further test that also gets to see what is doing the choosing, or null.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ObjectFilter"/> because most phrases do not need it and every
    /// one of them constructs that delegate: "target creature" is the same question whoever
    /// asks. A handful are not - "enchanted creature" means the one this Aura is on, "another
    /// target creature" means not this one, and "creature with lesser power" is lesser than
    /// whose? Those are all the same missing argument.
    /// <para>
    /// A caller that cannot say what its source is skips this test rather than guessing, exactly
    /// as the protection check does.
    /// </para>
    /// </remarks>
    public Func<GameState, IAbilitySource, GameObject, GameObject?, Guid, bool>? SourceFilter
    {
        get;
        init;
    }

    /// <summary>
    /// A further test that gets to see another <em>target of the same effect</em>, or null.
    /// </summary>
    /// <remarks>
    /// The third thing a filter can be about, and the one neither of the others can say. An
    /// <see cref="ObjectFilter"/> sees only the candidate; a <see cref="SourceFilter"/> adds the
    /// permanent whose ability is asking. Neither can answer "each other creature that shares a
    /// color with <em>it</em>" (radiance, 10 cards) or "all other creatures with the same name as
    /// <em>that creature</em>" (Bile Blight and 18 more), because "it" is the creature this same
    /// spell targeted, and no delegate had it.
    /// <para>
    /// The sibling is a target and the group is not. Targets are chosen as the spell is cast
    /// (CR 601.2c) and checked again on resolution (CR 608.2b); the group is found while the
    /// spell resolves and is not targeted at all, so hexproof does not protect it and it does not
    /// fizzle. That asymmetry is the whole shape of these cards and is why this is a filter and
    /// not a second target slot.
    /// </para>
    /// <para>
    /// The peer argument is <em>not</em> nullable, deliberately. A filter that could be handed
    /// null would have to decide what to do about it, and the convenient answer — pass — matches
    /// every permanent on the battlefield, which on these cards is a one-sided board wipe. The
    /// decision is made once, in <see cref="Accepts"/>, and it is to refuse: CR 608.2b says that
    /// if part of an effect requires information about an illegal target it fails to determine
    /// that information, and any part of the effect requiring it does not happen.
    /// </para>
    /// </remarks>
    public Func<GameState, IAbilitySource, GameObject, GameObject, Guid, bool>? PeerFilter
    {
        get;
        init;
    }

    /// <summary>
    /// A further test that gets to see the value announced for X, or null.
    /// </summary>
    /// <remarks>
    /// The fourth thing a filter can be about, and the one none of the others can say: "target
    /// creature with mana value X or less" is a filter over a number that is not printed on the
    /// card at all. X is chosen as the spell is cast or the ability activated (CR 601.2b,
    /// 602.2b), so the compiler has no number to close over — the same shape
    /// <see cref="VariableTargets"/> already answers for "how many targets", one question along.
    /// <para>
    /// The announced value is <em>nullable</em> at every call site and a spec carrying this
    /// refuses when it is absent, which is the same decision <see cref="PeerFilter"/> makes and
    /// is made for a sharper reason. A missing X defaulted to zero is not merely wrong: on "with
    /// mana value X or greater" it admits the entire battlefield, and on "X or less" it admits
    /// nothing and the card becomes uncastable. Neither is the printed card, so a caller that
    /// cannot say what X is gets no targets rather than the wrong ones.
    /// </para>
    /// <para>
    /// The compiler will only build one where the ability announces an X — <c>{X}</c> in the
    /// spell's mana cost, or in the activation cost of the ability the line prints. "Where X is
    /// the number of Faeries you control" names a different X entirely and is left unread.
    /// </para>
    /// </remarks>
    public Func<GameState, IAbilitySource, GameObject, Guid, int, bool>? VariableFilter
    {
        get;
        init;
    }

    /// <summary>Which players qualify. Null accepts any player still in the game.</summary>
    public Func<GameState, Guid, Guid, bool>? PlayerFilter { get; init; }

    /// <summary>
    /// Which <em>earlier</em> target of the same announcement this one is measured against, or
    /// null when it stands alone (CR 601.2c).
    /// </summary>
    /// <remarks>
    /// <see cref="PeerFilter"/> could always say what the comparison was and never say which
    /// sibling to make it against, so it ran only where an effect named the index itself — the
    /// sweep of "each other creature that shares a color with it", which happens on resolution
    /// with the target long since chosen. Nothing ran it while targets were <em>being</em>
    /// chosen, which is where two whole families of card live: "exile up to four target cards
    /// from a single graveyard" and "…and 1 damage to any other target". This is the missing
    /// half — the index — and it is on the spec rather than on an effect because the restriction
    /// is about the announcement and not about what the spell then does.
    /// <para>
    /// CR 601.2c announces every target at once, so "single" and "other" are restrictions on
    /// that announcement: an announcement violating one is <em>illegal</em>, not legal-and-inert.
    /// That is why this is checked in <see cref="IsLegal"/> beside hexproof and protection, and
    /// why the whole cast is refused rather than the offending part quietly skipped.
    /// </para>
    /// <para>
    /// Carrying an index means two things at once, because both families want both. The target
    /// may not <em>be</em> the peer — CR 115.3 says the same target cannot be chosen twice for
    /// one instance of the word "target", which is the whole of what "other" adds — and it must
    /// additionally pass <see cref="PeerFilter"/> where the spec has one. A spec with an index
    /// whose peer cannot be found accepts nothing, which is the same decision
    /// <see cref="PeerFilter"/> and <see cref="VariableFilter"/> already make: a restriction that
    /// cannot be evaluated is not a restriction that passes.
    /// </para>
    /// <para>
    /// The index always points <em>backwards</em>. Targets are chosen in order, and a trigger
    /// chooses them one question at a time (CR 603.3d), so a spec pointing forwards would be
    /// asked about a target that does not exist yet and would refuse every option — a card that
    /// cannot be put on the stack at all.
    /// </para>
    /// </remarks>
    public int? PeerIndex { get; init; }

    /// <summary>
    /// The same specs with every <see cref="PeerIndex"/> moved along by an offset.
    /// </summary>
    /// <remarks>
    /// The twin of <c>EffectTargets.Shift</c>, and needed for the same reason. A mode's specs and
    /// a spliced card's specs are compiled against their own list and then concatenated onto the
    /// spell's, so a peer index numbered from that list points at the wrong target the moment
    /// anything sits in front of it. The effects have always been shifted; nothing on a spec ever
    /// carried an index before, so nothing was.
    /// </remarks>
    public static ImmutableList<TargetSpec> ShiftPeers(
        IEnumerable<TargetSpec> specs, int offset)
    {
        ArgumentNullException.ThrowIfNull(specs);

        return offset == 0
            ? [.. specs]
            : [.. specs.Select(spec => spec.PeerIndex is { } sibling
                ? spec with { PeerIndex = sibling + offset }
                : spec)];
    }

    /// <summary>Whether the given target is currently legal for the given controller.</summary>
    /// <summary>
    /// Whether this target may be left unchosen — "up to one target creature" (CR 601.2c).
    /// </summary>
    /// <remarks>
    /// The phrase was being read and then ignored: the engine required a target for every spec it
    /// had, so "up to one" meant "one". That is a real difference — a card with no legal target
    /// could not be cast at all when it should have been castable for none of its effect.
    /// </remarks>
    public bool Optional { get; init; }

    /// <summary>
    /// Whether this one spec stands for <em>any number</em> of targets (CR 601.2c).
    /// </summary>
    /// <remarks>
    /// "Any number of target creatures" names no ceiling, and a fixed list of specs cannot say
    /// that: giving it an arbitrary one would be a different card the moment the ceiling
    /// mattered, which is why this family was left unread for so long. So the count is not in
    /// the definition at all. CR 601.2c has the player announce how many targets they will
    /// choose <em>before</em> choosing them, and once announced "that number doesn't change" —
    /// so the announcement is the length of the target list the caster sends, and one spec
    /// marked this way is expanded against that length by
    /// <see cref="VariableTargets.Expand(ImmutableList{TargetSpec}, ImmutableList{IEffect}, int)"/>.
    /// <para>
    /// Zero is a legal announcement, and the rules say so outright: CR 601.2c's example under
    /// Loaming Shaman has the ability resolve with "no cards are targeted". So the expansion of
    /// an unchosen block is no targets and no effects, not a refusal.
    /// </para>
    /// <para>
    /// A spec carrying this must be the <em>last</em> in its list. Everything downstream — the
    /// effects' target indices, the slices modes and splice take — is positional, and a block
    /// that grew in the middle would move every index after it. The compiler refuses to emit one
    /// anywhere else and the expansion refuses to run on one, which is a card left unread rather
    /// than a card that quietly aims its effects at the wrong creature.
    /// </para>
    /// </remarks>
    public bool AnyNumber { get; init; }

    /// <summary>
    /// Whether one object passes every filter this spec carries.
    /// </summary>
    /// <remarks>
    /// The single place that asks all of them, and it exists because forgetting one has been a
    /// live bug twice: two consumers asked <see cref="ObjectFilter"/> and not
    /// <see cref="SourceFilter"/>, so "another creature" offered the source's own name and a
    /// sweeper that said to leave itself out put a counter on itself. A third delegate makes a
    /// hand-written conjunction at each call site three chances to be wrong instead of two, so
    /// there is now one conjunction and every caller uses it.
    /// </remarks>
    /// <param name="source">
    /// The permanent doing the asking, when the caller knows it. Null skips
    /// <see cref="SourceFilter"/> rather than guessing at an answer.
    /// </param>
    /// <param name="peer">
    /// Another target of the same spell or ability, for <see cref="PeerFilter"/>. Null when the
    /// caller has none to offer or when the one it had can no longer be found, and a spec that
    /// carries a peer filter then accepts nothing (CR 608.2b).
    /// </param>
    public bool Accepts(
        GameState state,
        IAbilitySource abilities,
        GameObject candidate,
        Guid controllerId,
        GameObject? source = null,
        GameObject? peer = null,
        int? announced = null)
    {
        if (ObjectFilter?.Invoke(state, abilities, candidate, controllerId) == false)
            return false;

        if (SourceFilter?.Invoke(state, abilities, candidate, source, controllerId) == false)
            return false;

        if (VariableFilter is { } variable)
        {
            // Fail closed. An unknown X is not zero: read that way "with mana value X or
            // greater" would admit every permanent on the board, which is strictly better than
            // the printed card and is the one class of error this compiler refuses outright.
            if (announced is not { } chosen)
                return false;

            if (!variable(state, abilities, candidate, controllerId, chosen))
                return false;
        }

        if (PeerFilter is not { } comparison)
            return true;

        // CR 608.2b: "If part of the effect requires information about an illegal target, it
        // fails to determine any such information. Any part of the effect that requires that
        // information won't happen." Matching everything would be the opposite reading, and on
        // the cards that want this it is the difference between a two-damage ping and a wipe.
        return peer is not null && comparison(state, abilities, candidate, peer, controllerId);
    }

    /// <param name="source">
    /// What is doing the targeting, when it is known. Protection is a question about the source
    /// (CR 702.16b), so a caller that cannot say what the source is gets no protection check
    /// rather than a wrong one.
    /// </param>
    /// <param name="peers">
    /// The already-chosen targets this one is measured against, the one <see cref="PeerIndex"/>
    /// names first (CR 601.2c). <em>Targets</em> and not objects, because "any other target" has
    /// to be able to say that a player already chosen may not be chosen again, and a player is
    /// not an object. Empty where the spec names no peer — and empty where it names one the
    /// caller cannot supply, which refuses every target rather than accepting every target: a
    /// card that cannot be cast instead of a card that does the wrong thing.
    /// <para>
    /// A list rather than one, because CR 115.3 forbids a repeat anywhere within one instance of
    /// the word "target": "exile up to four target cards from a single graveyard" is one
    /// instance, and the fourth pick has to differ from all three before it and not merely from
    /// the first. The comparison a <see cref="PeerFilter"/> makes is against the first alone,
    /// which is enough — everything in the group agrees with it, so everything agrees.
    /// </para>
    /// </param>
    /// <param name="announced">
    /// The value chosen for X, when this is a spell or ability that announced one (CR 601.2b).
    /// Null everywhere else, and a spec carrying a <see cref="VariableFilter"/> then refuses.
    /// </param>
    public bool IsLegal(
        GameState state,
        IAbilitySource abilities,
        Target target,
        Guid controllerId,
        GameObject? source = null,
        IReadOnlyList<Target>? peers = null,
        int? announced = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        GameObject? sibling = null;

        if (PeerIndex is not null)
        {
            // Fail closed. A restriction the caller cannot evaluate is not a restriction that
            // passes: with no peer to compare against, "any other target" would be "any target"
            // and "from a single graveyard" would be "from any graveyard", and both of those are
            // strictly better than the printed card.
            if (peers is not { Count: > 0 })
                return false;

            // CR 115.3: the same target cannot be chosen twice for one instance of the word
            // "target", and CR 601.2c says the same. That is the whole of what "other" adds, and
            // it is asked of the Target rather than of an object so that the two shapes "any
            // target" comes in - a permanent and a player - are both covered.
            for (var i = 0; i < peers.Count; i++)
            {
                if (peers[i] == target)
                    return false;
            }

            // A player is not an object, so a spec that also carries a PeerFilter finds nothing
            // to compare and refuses below. That is right: every printed peer comparison is
            // about a card, and one aimed at a player is a phrase this does not read.
            if (peers[0].Kind != TargetKind.Player
                && state.TryGetObject(peers[0].Subject, out var live))
            {
                sibling = live;
            }
        }

        // A spec that takes any target accepts both shapes; anything else has to match exactly.
        if (Kind == TargetKind.Any)
        {
            if (target.Kind is not (TargetKind.Player or TargetKind.Permanent))
                return false;
        }
        else if (target.Kind != Kind)
        {
            return false;
        }

        if (target.Kind == TargetKind.Player)
        {
            if (!state.Players.ContainsKey(target.Player)
                || state.GetPlayer(target.Player).HasLost)
            {
                return false;
            }

            // Two of the three refusals the permanent arm below makes, asked of a player - which
            // this could not ask until players had computed abilities at all. CR 702.11c and
            // CR 702.18a put hexproof and shroud on a player in as many words, and until they
            // were askable "You have hexproof" was a line the compiler could not read and a rule
            // the engine could not enforce, so an opponent's Lightning Bolt could name a player
            // sitting behind Leyline of Sanctity.
            var quality = PlayerCharacteristics.Of(state, abilities, target.Player);

            // CR 702.18a: shroud stops everybody, the player themselves included.
            if (quality.Has(KeywordAbility.Shroud))
                return false;

            // CR 702.11c: hexproof stops only opponents, so a player may still target themselves.
            if (quality.Has(KeywordAbility.Hexproof) && target.Player != controllerId)
                return false;

            // Protection is the third refusal the permanent arm makes and is not made here: no
            // printed card gives a player a protection this engine can express — see the note on
            // ComputedPlayerCharacteristics — so the check would be unreachable.
            return PlayerFilter?.Invoke(state, target.Player, controllerId) ?? true;
        }

        if (!state.TryGetObject(target.Subject, out var obj))
            return false;

        var expectedZone = Kind switch
        {
            TargetKind.SpellOnStack => Zone.Stack,
            TargetKind.CardInGraveyard => Zone.Graveyard,
            _ => Zone.Battlefield,
        };

        if (obj.Zone != expectedZone)
            return false;

        // CR 702.11b: hexproof means it cannot be the target of spells or abilities an opponent
        // controls. CR 702.18b: shroud means nobody may target it, including its controller.
        // Both are checked here, where every target passes, rather than at each spell.
        if (Kind is TargetKind.Permanent or TargetKind.Any && obj.Zone == Zone.Battlefield)
        {
            var computed = Characteristics.Of(state, abilities, obj);

            if (computed.Has(KeywordAbility.Shroud))
                return false;

            if (computed.Has(KeywordAbility.Hexproof) && obj.ControllerId != controllerId)
                return false;

            // CR 702.16b: protection from a quality also stops a permanent being targeted by
            // anything with that quality. It is a question about the source rather than about the
            // target, which is why the signature carries one — and why a caller that does not
            // know its source simply skips the check instead of guessing.
            if (source is not null
                && computed.IsProtectedFrom(Characteristics.Of(state, abilities, source)))
            {
                return false;
            }

            // CR 702.11d and every "can't be the target of …" the corpus prints: a prohibition
            // whose parameter describes the *source*, which no keyword flag can carry. Asked
            // here beside the other three refusals so that a card printing one is enforced by
            // the same code path a printed hexproof is, rather than at each spell.
            //
            // Skipped when the caller cannot say what the source is, exactly as protection is
            // skipped. Every restriction here asks a question about the source, so with none to
            // ask about the honest answer is no restriction rather than a guessed one — and the
            // generous direction is the right one for a *board* that does not know: the engine
            // refuses when the real cast arrives with its source.
            if (source is not null)
            {
                foreach (var restriction in computed.TargetRestrictions)
                {
                    if (!restriction(state, abilities, obj, source, controllerId))
                        return false;
                }
            }
        }

        return Accepts(state, abilities, obj, controllerId, source, sibling, announced);
    }
}

/// <summary>
/// The comparisons a <see cref="TargetSpec.PeerFilter"/> is built from (CR 608.2h).
/// </summary>
/// <remarks>
/// Two questions between them cover every card the corpus prints in this shape, measured rather
/// than guessed: "shares a color with it" is 10 cards, all of them radiance, and "with the same
/// name as that [noun]" is 19 across creatures, permanents, lands, artifacts and enchantments.
/// They are kept apart from the "other" exclusion because that is a third question — the printed
/// line says "each <em>other</em>", and a comparison that hid the exclusion inside itself could
/// not be reused by anything that does not.
/// <para>
/// Each is handed the object it is judging and the sibling to judge it against, and asks nothing
/// about where either one is. That is what lets the sibling be a card the same resolution has
/// already exiled: CR 608.2h says an effect needing information about an object that has left the
/// zone it was expected to be in uses its last known information, and Sever the Bloodline exiles
/// its target before the group it describes is gathered.
/// </para>
/// </remarks>
public static class PeerFilters
{
    /// <summary>"…that shares a color with it" (CR 105.2).</summary>
    /// <remarks>
    /// Colour is read from the computed characteristics on both sides, because layer 5 changes it
    /// (CR 613.1e) and a creature painted white by an effect shares a colour with a white spell's
    /// target. The one thing this does not reproduce is last known <em>colour</em>: a sibling that
    /// has left the battlefield is asked where it is now, so a layer-5 effect that was on it there
    /// is gone. No printed card reaches that — the only mid-resolution sibling in the corpus is
    /// compared by name, and a colour comparison whose target has left fizzles first (CR 608.2b).
    /// </remarks>
    public static bool SharesAColour(
        GameState state,
        IAbilitySource abilities,
        GameObject candidate,
        GameObject peer,
        Guid controllerId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(peer);

        var theirs = Characteristics.Of(state, abilities, peer).Colors;

        // CR 105.2c: a colourless object has no colour, so it shares one with nothing — not even
        // with another colourless object. An empty intersection says that without a special case.
        return theirs.Count != 0
            && Characteristics.Of(state, abilities, candidate).Colors.Any(theirs.Contains);
    }

    /// <summary>"…with the same name as that creature" (CR 201.2a).</summary>
    /// <remarks>
    /// The name is read off the card rather than off the computed characteristics because nothing
    /// in this engine changes a name — face-down is the one thing that does, and CR 707.2 makes a
    /// face-down permanent nameless, which is exactly what the second half of CR 201.2a is about.
    /// </remarks>
    public static bool HasTheSameName(
        GameState state,
        IAbilitySource abilities,
        GameObject candidate,
        GameObject peer,
        Guid controllerId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(peer);

        // CR 707.2: a face-down permanent has no name.
        if (candidate.Permanent is { IsFaceDown: true } || peer.Permanent is { IsFaceDown: true })
            return false;

        // CR 201.2a: "An object with no name doesn't have the same name as any other object,
        // including another object with no name." Two nameless tokens are not each other's twin,
        // and string equality alone would have said they were.
        return !string.IsNullOrEmpty(candidate.Card.Name)
            && string.Equals(candidate.Card.Name, peer.Card.Name, StringComparison.Ordinal);
    }

    /// <summary>"…from a single graveyard" (CR 404.1, 404.3).</summary>
    /// <remarks>
    /// The one comparison here that is asked while targets are being <em>chosen</em> rather than
    /// as an effect resolves, and the one that is about where a card is rather than what it is.
    /// "Exile up to four target cards from a single graveyard" does not say which graveyard, so
    /// there is nothing for an <see cref="TargetSpec.ObjectFilter"/> to test: the restriction is
    /// that the picks agree with each other, and the first pick is what they agree with.
    /// <para>
    /// A graveyard is identified by its owner. CR 404.1 gives each player one, and CR 404.3 puts
    /// a card into its <em>owner's</em> graveyard however it got there — so two cards are in a
    /// single graveyard exactly when they have the same owner, and no lookup through the zone
    /// lists can disagree with that. Owner and not controller: a card in a graveyard has no
    /// controller (CR 108.4), and the player who cast it has nothing to do with where it went.
    /// </para>
    /// </remarks>
    public static bool InTheSameGraveyard(
        GameState state,
        IAbilitySource abilities,
        GameObject candidate,
        GameObject peer,
        Guid controllerId)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(peer);

        return candidate.OwnerId == peer.OwnerId;
    }

    /// <summary>The same comparison with the sibling itself left out — "each other …".</summary>
    /// <remarks>
    /// Every one of the 29 cards measured for this says "other", because each pairs the group
    /// with a targeted effect on the sibling and would otherwise hit it twice. It is a wrapper
    /// rather than a flag on the comparison so that the two questions stay separable: a card that
    /// said "each creature with the same name" would want one and not the other.
    /// </remarks>
    public static Func<GameState, IAbilitySource, GameObject, GameObject, Guid, bool> Other(
        Func<GameState, IAbilitySource, GameObject, GameObject, Guid, bool> comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        return (state, abilities, candidate, peer, controller) =>
            candidate.Id != peer.Id && comparison(state, abilities, candidate, peer, controller);
    }
}

/// <summary>
/// How much of something an effect does: a printed number, or X (CR 601.2b).
/// </summary>
/// <remarks>
/// The value chosen for X has been carried from the cast all the way to
/// <see cref="ResolutionContext.VariableValue"/> since the engine was built, and no effect ever
/// read it — so "deals X damage" and "draw X cards" compiled to nothing and every X spell in the
/// game was unplayable.
/// <para>
/// It converts implicitly from <see langword="int"/> so that the hundred existing call sites that
/// pass a plain number keep reading as they did. An effect asks <see cref="In"/> for the number
/// rather than storing one, because X is not known until the spell is cast and the same
/// definition is shared by every casting of that card.
/// </para>
/// </remarks>
public readonly record struct Amount(int Fixed, bool IsVariable = false)
{
    /// <summary>The value the caster chose for X (CR 601.2b).</summary>
    public static readonly Amount X = new(0, IsVariable: true);

    public static implicit operator Amount(int value) => new(value);

    /// <summary>Whether this amount is counted the other way — how life loss is written.</summary>
    /// <remarks>
    /// A flag rather than a negative <see cref="Fixed"/>, because negating the fixed part only
    /// works when the fixed part <em>is</em> the amount. It is not when X was chosen by the caster
    /// and it is not when the amount is a count: X's fixed part is zero, so negating it produced
    /// zero and left the variable flag alone, and "each opponent loses X life" resolved to each
    /// opponent <em>gaining</em> X life. Nothing failed and no line went unread.
    /// </remarks>
    public bool Negated { get; init; }

    /// <summary>The same amount, counted the other way — how life loss is written.</summary>
    public static Amount operator -(Amount amount) => amount with { Negated = !amount.Negated };

    /// <summary>Named alternative to the negation operator, for callers that want words.</summary>
    public static Amount Negate(Amount amount) => -amount;

    /// <summary>
    /// What to multiply by, when the amount is "for each" something (CR 107.3).
    /// </summary>
    /// <remarks>
    /// "Gain 2 life for each creature you control" is two per creature, and "draw cards equal to
    /// the number of Islands you control" is one per Island — the same shape with the multiplier
    /// left implicit. Both are this: a fixed part times a count taken when the effect resolves.
    /// <para>
    /// It lives on the amount rather than on each effect because "for each" attaches to numbers
    /// generally, not to any one thing a card does. Every effect that already takes an Amount can
    /// be counted this way without knowing about it.
    /// </para>
    /// </remarks>
    public Func<ResolutionContext, int>? Counter { get; init; }

    /// <summary>The number this comes to for the spell or ability now resolving.</summary>
    public int In(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var size = Counter is { } counted
            ? Fixed * counted(context)
            : IsVariable ? context.VariableValue : Fixed;

        return Negated ? -size : size;
    }

    public override string ToString() =>
        Counter is not null ? $"{Fixed} for each"
            : IsVariable ? "X"
            : Fixed.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Everything an effect needs to know while it resolves (CR 608.2).</summary>
public sealed record ResolutionContext
{
    public required GameState State { get; init; }

    public required IAbilitySource Abilities { get; init; }

    /// <summary>Who controls the spell or ability, and so who "you" means (CR 608.2).</summary>
    public required Guid ControllerId { get; init; }

    /// <summary>The object on the stack that is resolving.</summary>
    public required ObjectId SourceId { get; init; }

    public ImmutableList<Target> Targets { get; init; } = [];

    /// <summary>
    /// The player the triggering event was about, for <see cref="PlayerScope.TriggerSubject"/>.
    /// </summary>
    /// <remarks>
    /// Null for a spell and for an activated ability, because neither has one — nothing about
    /// casting a spell picks out a player the way "whenever this deals damage to a player" does.
    /// </remarks>
    public Guid? SubjectPlayer { get; init; }

    /// <summary>The object the triggering event was about, for a trigger that says "it".</summary>
    public ObjectId? SubjectObject { get; init; }

    /// <summary>How much the triggering event was about — "that many" (CR 603.2).</summary>
    public int? SubjectAmount { get; init; }

    /// <summary>The value chosen for X as the spell was cast (CR 601.2b).</summary>
    public int VariableValue { get; init; }

    /// <summary>
    /// What the effects before this one in this same resolution did — "this way" (CR 608.2).
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="SubjectAmount"/>, which has carried "that much" forward since the
    /// day it was built. That one is a magnitude and this is the things themselves, and the
    /// printed sentences want both: "each opponent loses 2 life and you gain that much life"
    /// against "destroy all creatures, then draw a card for each creature destroyed this way".
    /// <para>
    /// Empty on a context built without one, and on a deferred branch that runs after its own
    /// resolution is over — callers treat an empty record as "nothing was done", which is why the
    /// compiler refuses every verb whose events arrive at the settle rather than in the
    /// resolution.
    /// </para>
    /// </remarks>
    public ResolutionRecord Record { get; init; } = ResolutionRecord.Empty;

    /// <summary>How much each target was assigned, by target index (CR 601.2d).</summary>
    /// <remarks>
    /// Not "how much damage". The same announcement carries a distribution of counters, and the
    /// rule it comes from is written over dividing or distributing anything at all.
    /// </remarks>
    public ImmutableList<int> Division { get; init; } = [];

    /// <summary>
    /// Who controlled an object, whether or not that object still exists (CR 400.7).
    /// </summary>
    /// <remarks>
    /// Effects in one resolution each see what the previous one left behind (CR 608.2c), so a
    /// second sentence that says "its controller" is asking about something the first sentence
    /// has already destroyed, countered or exiled — which made it a different object under a new
    /// id, and <see cref="GameState.TryGetObject"/> finds nothing. This follows the id forward
    /// through the log the way the engine's own deferred questions do.
    /// <para>
    /// Null when nothing was ever known about the id, and null on a context built without one —
    /// callers fall back rather than assume, because an unanswerable question is not a broken
    /// game.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Which of the source's abilities is resolving, when one is (CR 405.4).
    /// </summary>
    /// <remarks>
    /// Carried here rather than looked up from the state, because the two disagree exactly when
    /// it matters. An effect that has to be found again later - one that asks a question and is
    /// located by (source, ability, index) when the answer arrives - runs from a *deferred*
    /// branch after the ability that raised it has left the stack. Asking the state then gives
    /// the permanent, which has no ability on it, so the locator searched the card's spell
    /// effects instead of the ability's, found nothing, and the request was dropped in silence:
    /// no question, no error, the branch simply did not happen.
    /// </remarks>
    public string? AbilityId { get; init; }

    public Func<ObjectId, Guid?>? ControllerBehind { get; init; }

    /// <summary>
    /// The object an id became, whether or not that id still names anything (CR 400.7).
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="ControllerBehind"/>, for effects that need the object rather than
    /// who controlled it. A death trigger's source is the permanent that died, and by the time
    /// the trigger resolves that permanent is a card in a graveyard under a new id - so an effect
    /// that wants to move it has to be able to follow the id forward.
    /// </remarks>
    public Func<ObjectId, GameObject?>? ObjectBehind { get; init; }

    public Target? TargetAt(int index) =>
        index >= 0 && index < Targets.Count ? Targets[index] : null;

    /// <summary>
    /// The object a target of this same spell or ability names, wherever it has got to
    /// (CR 608.2h).
    /// </summary>
    /// <remarks>
    /// What "it" and "that creature" mean to a group filter in the same sentence — "each other
    /// creature that shares a color with <em>it</em>". <see cref="TargetAt"/> answers with the
    /// chosen target; this answers with the object, and it has to keep answering after an earlier
    /// effect of the same resolution has moved it. Sever the Bloodline exiles its target and then
    /// describes a group by that target's name, and a permanent that leaves the battlefield does
    /// so under a new id (CR 400.7), so asking the state alone returns nothing on exactly the card
    /// the mechanism exists for.
    /// <para>
    /// Null when the target names a player, when the id cannot be followed, and on a context built
    /// without <see cref="ObjectBehind"/>. Callers must treat null as "cannot be determined" and
    /// do nothing (CR 608.2b) rather than as "no restriction".
    /// </para>
    /// </remarks>
    public GameObject? PeerAt(int index)
    {
        if (TargetAt(index) is not { } target || target.Kind == TargetKind.Player)
            return null;

        return State.TryGetObject(target.Subject, out var live)
            ? live
            : ObjectBehind?.Invoke(target.Subject);
    }

    /// <summary>
    /// The object that is the <em>source</em> of what this effect does (CR 608.2, 609.7).
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="SourceId"/>. What resolves is a spell or an ability, and an
    /// ability on the stack is its own object — but the source of damage an ability deals is the
    /// permanent whose ability it is, not the ability. The difference is invisible until something
    /// asks about the source: lifelink on the creature, deathtouch on the creature, and every
    /// "whenever this creature deals damage" trigger all read this and would all miss.
    /// <para>
    /// For a spell the two are the same object, which is why the fallback is <see cref="SourceId"/>
    /// rather than a failure.
    /// </para>
    /// </remarks>
    public ObjectId PhysicalSourceId =>
        State.TryGetObject(SourceId, out var onStack) && onStack.Ability is { } ability
            ? ability.SourceId
            : SourceId;
}

/// <summary>
/// One thing a spell or ability does when it resolves.
/// </summary>
/// <remarks>
/// The vocabulary cards are built from. An effect reports the events it wants to happen; it does
/// not apply them, so it cannot mutate state behind the reducer's back and everything it does
/// lands in the log like everything else.
/// </remarks>
public interface IEffect
{
    IReadOnlyList<GameEvent> Resolve(ResolutionContext context);
}

/// <summary>Deals damage to a target creature or player (CR 119.3, 120).</summary>
/// <remarks>
/// The subject is here for the same reason destroy, exile and tap have one: "~ deals 1 damage to
/// that creature" is the same verb aimed at something that was never chosen. Damage was the last
/// of the four without it, and the nine cards whose entire text is that sentence were unread for
/// exactly that field.
/// </remarks>
public sealed record DealDamage(
    Amount Amount,
    int TargetIndex = 0,
    bool Deathtouch = false,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    /// <summary>
    /// Whether the damage beyond lethal goes to the permanent's controller (CR 120.4a).
    /// </summary>
    /// <remarks>
    /// "Excess damage is dealt to that creature's controller instead" — a rider printed on the
    /// sentence that deals the damage, which is why it is a field on this effect and not an
    /// effect of its own. CR 120.4a makes it the <em>first</em> of the four steps damage is
    /// processed in: the damage event is modified before any replacement or prevention sees it,
    /// so the split has to happen where the event is built rather than anywhere downstream.
    /// <para>
    /// Two events come out instead of one, and both name the same source — so the creature's
    /// share is still marked by this permanent, and the player's share is damage rather than life
    /// loss, which is what protection, prevention and lifelink all read.
    /// </para>
    /// </remarks>
    public bool ExcessToTargetsController { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = context.PhysicalSourceId;

        // A pronoun names an object and never a player, so the subject arms resolve one and stop
        // rather than going through the target kinds below. The battlefield check is the same
        // CR 120.1 rule the permanent arm applies: a creature that has left in the meantime takes
        // no damage, and a subject that resolved to nothing is nothing to damage at all.
        if (Subject != EffectSubject.Target)
        {
            if (Subjects.Resolve(context, Subject, TargetIndex) is not { } struck)
                return [];

            return context.State.TryGetObject(struck, out var burned)
                && burned.Zone == Zone.Battlefield
                    ? Dealt(context, burned, source)
                    : [];
        }

        if (context.TargetAt(TargetIndex) is not { } target)
            return [];

        return target.Kind switch
        {
            TargetKind.Player =>
                [new PlayerDamaged(target.Player, source, Amount.In(context), IsCombat: false)],
            // CR 120.1: damage is dealt to a permanent, and a permanent is on the battlefield.
            // A creature that died or was bounced in response is an illegal target and is skipped
            // (CR 608.2b) - the engine's own damage record refuses anything else, so this has to
            // be asked here rather than discovered there.
            TargetKind.Permanent =>
                context.State.TryGetObject(target.Subject, out var hit)
                && hit.Zone == Zone.Battlefield
                    ? Dealt(context, hit, source)
                    : [],
            _ => [],
        };
    }

    /// <summary>The events one hit produces, split at lethal when the card says so (CR 120.4a).</summary>
    private IReadOnlyList<GameEvent> Dealt(
        ResolutionContext context, GameObject struck, ObjectId source)
    {
        var amount = Amount.In(context);

        if (!ExcessToTargetsController)
            return [new DamageMarked(struck.Id, amount, Deathtouch, source)];

        var excess = ExcessDamage.Over(context, struck, amount, Deathtouch, source);
        if (excess <= 0)
            return [new DamageMarked(struck.Id, amount, Deathtouch, source)];

        // Whose it is, computed rather than stored: control is layer 2 (CR 613.1b), and a stolen
        // creature's excess belongs to whoever controls it now.
        var owner = Characteristics.ControllerOf(context.State, context.Abilities, struck);

        return
        [
            new DamageMarked(struck.Id, amount - excess, Deathtouch, source),
            new PlayerDamaged(owner, source, excess, IsCombat: false),
        ];
    }
}

/// <summary>
/// How much of a damage event is beyond what would have been lethal (CR 120.4a, 120.6).
/// </summary>
/// <remarks>
/// The number the excess-damage family is written around, and the engine had every part of it
/// except this: damage marked, computed toughness, loyalty and defence counters were all here,
/// and nothing had ever subtracted one from the other.
/// <para>
/// It is a <em>question about the moment the damage is dealt</em>, which is why it lives beside
/// the effect rather than in the state-based actions. CR 120.4a is step one of four and runs
/// before replacement and prevention effects, so a creature whose damage is later prevented was
/// still dealt the excess this says it was.
/// </para>
/// </remarks>
public static class ExcessDamage
{
    /// <summary>
    /// The amount of <paramref name="amount"/> in excess of lethal for this permanent.
    /// </summary>
    /// <remarks>
    /// CR 120.4a spells out three measures and one tie-break, and all four are here rather than
    /// only the creature arm, because a card reading "target creature or planeswalker" hands this
    /// either kind and a redirect that answered nought for one of them would be a card that
    /// silently does half of what it says.
    /// </remarks>
    public static int Over(
        ResolutionContext context, GameObject struck, int amount, bool deathtouch, ObjectId source)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(struck);

        if (amount <= 0)
            return 0;

        var now = Characteristics.Of(context.State, context.Abilities, struck);

        // CR 120.4a: with deathtouch, everything past the first point is excess - and it is the
        // *source's* deathtouch, not the effect's, so a Prodigal Pyromancer that has been given
        // deathtouch counts the same as an effect that prints the word.
        var deadly = deathtouch
            || (context.State.TryGetObject(source, out var dealer)
                && Characteristics.HasKeyword(
                    context.State, context.Abilities, dealer, KeywordAbility.Deathtouch));

        var lethal = (int?)null;

        if (now.IsCreature)
        {
            lethal = deadly
                ? 1
                : Math.Max(0, (now.Toughness ?? 0) - (struck.Permanent?.DamageMarked ?? 0));
        }

        // A planeswalker's loyalty and a battle's defence are the same measure one counter along
        // (CR 120.3c, 120.3h), and deathtouch has nothing to say about either.
        lethal = Smallest(lethal, now.CardTypes.HasFlag(CardType.Planeswalker), struck, CounterKinds.Loyalty);
        lethal = Smallest(lethal, now.CardTypes.HasFlag(CardType.Battle), struck, CounterKinds.Defense);

        // "The greatest of the calculated amounts for each of the card types it has" - the most
        // excess, which is the least lethal. A permanent that is none of the three has no measure
        // at all and takes the whole hit.
        return lethal is { } past ? Math.Max(0, amount - past) : 0;
    }

    private static int? Smallest(int? lethal, bool applies, GameObject struck, string counter)
    {
        if (!applies)
            return lethal;

        var here = struck.Permanent?.Counters.GetValueOrDefault(counter) ?? 0;
        return lethal is { } already ? Math.Min(already, here) : here;
    }
}

/// <summary>Destroys a target permanent (CR 701.8).</summary>
/// <remarks>
/// Destruction is a move to the graveyard, which indestructible replaces and regeneration can
/// replace (CR 701.8c). It goes through the same event as any other zone change, so those
/// replacements see it.
/// </remarks>
public sealed record DestroyTarget(
    int TargetIndex = 0,
    bool NoRegeneration = false,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } victim)
            return [];

        if (!context.State.TryGetObject(victim, out var permanent))
            return [];

        // CR 701.7a: destroy applies to a permanent, and a permanent is on the battlefield.
        // A pronoun can now name an object in a graveyard - "whenever a creature dies, destroy
        // that creature" is a card nobody prints, but the grammar allows the sentence - and
        // destroying something that has already left does nothing rather than moving it again.
        if (permanent.Zone != Zone.Battlefield)
            return [];

        // CR 702.12b: a permanent with indestructible is not destroyed by an effect that says
        // "destroy". The spell still resolves and the target was legal — nothing happens to it.
        // The state-based actions already knew this about lethal damage; a Murder did not, so
        // it killed a Darksteel creature outright.
        if (Characteristics.HasKeyword(
                context.State, context.Abilities, permanent, KeywordAbility.Indestructible))
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                victim,
                ObjectId.New(),
                Zone.Battlefield,
                Zone.Graveyard,
                permanent.ControllerId,
                NoRegeneration ? MoveCause.DestroyNoRegeneration : MoveCause.Destroy),
        ];
    }
}

/// <summary>Exiles a target permanent (CR 406.2).</summary>
public sealed record ExileTarget(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } exiled)
            return [];

        if (!context.State.TryGetObject(exiled, out var permanent))
            return [];

        // The zone it is actually in, not the battlefield. Exile reaches other zones and always
        // did on the cards - "exile that creature" on a dies trigger means the card now in the
        // graveyard (CR 400.7) - but this had the origin hardcoded, so the move described a
        // journey the object was not on and the reducer refused it outright. It was invisible
        // while every pronoun this effect could see pointed at the battlefield.
        return
        [
            new ObjectMoved(
                exiled, ObjectId.New(), permanent.Zone, Zone.Exile,
                permanent.ControllerId, MoveCause.Exile),
        ];
    }
}

/// <summary>
/// Exiles a permanent and returns it to the battlefield at once — a flicker (CR 400.7).
/// </summary>
/// <remarks>
/// One effect and not two, because the card that comes back is a <em>different object</em> and
/// nothing could name it in between: an exile followed by a separate return would have to find a
/// card by an id that stopped existing the moment it was exiled.
/// <para>
/// What comes back has no counters, no Auras, no damage and none of the abilities anything gave
/// it, and it is summoning-sick again. None of that is arranged here — it is simply what changing
/// zones means, and it is the whole reason these cards are played: the permanent enters, so
/// everything that triggers on entering triggers again.
/// </para>
/// <para>
/// "Under its owner's control" is the ordinary case and what a new object gets anyway. The cards
/// that say "under your control" are a theft, and they are not read here — reading them as this
/// would quietly hand the permanent back to the player it was taken from.
/// </para>
/// </remarks>
/// <summary>
/// Exiles the permanent this ability belongs to and returns it at once (CR 400.7).
/// </summary>
/// <remarks>
/// <see cref="FlickerTarget"/> written about the source instead of a target - and the difference
/// is not only which object: a card that blinks *itself* is usually doing it to arrive as its
/// other face, which is what <paramref name="Transformed" /> is for.
/// <para>
/// What comes back is a new object (CR 400.7), so nothing it had before travels with it - no
/// counters, no auras, no damage. That is the point of every card printed this way.
/// </para>
/// </remarks>
public sealed record FlickerSource(bool Transformed = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();
        var returning = ObjectId.New();

        var events = new List<GameEvent>
        {
            new ObjectMoved(
                subject, exiled, Zone.Battlefield, Zone.Exile,
                permanent.OwnerId, MoveCause.Exile),
            new ObjectMoved(
                exiled, returning, Zone.Exile, Zone.Battlefield,
                permanent.OwnerId, MoveCause.Return),
        };

        // A card with nothing to turn over folds this to nothing, which is the right answer for
        // a sentence that cannot apply rather than a reason to refuse the line.
        if (Transformed)
            events.Add(new PermanentTransformed(returning, 1));

        return events;
    }
}

/// <summary>
/// Defeats the Siege this ability belongs to: exile it, then its controller may cast it
/// transformed without paying its mana cost (CR 310.12b).
/// </summary>
/// <remarks>
/// The resolution half of the intrinsic trigger every Siege has — see <c>SiegeRules</c>. The
/// exile and the offer are one effect for the reason <see cref="FlickerSource"/> is one: the
/// exiled card is a different object (CR 400.7), and only the effect that names the new id can
/// put the offer on it. The offer itself is the standing free-cast machinery — permission on the
/// exiled card, taken through the ordinary cast path, revoked when its window passes — with
/// <c>Transformed</c> set, so taking it puts the back face on the stack (CR 712.11a).
/// <para>
/// A Siege that is no longer on the battlefield folds to nothing: there is nothing to exile, and
/// the flip side is not offered off a battle that already left some other way.
/// </para>
/// </remarks>
public sealed record DefeatSiege : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var battle)
            || battle.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();

        return
        [
            new ObjectMoved(
                subject, exiled, Zone.Battlefield, Zone.Exile,
                battle.OwnerId, MoveCause.Exile,
                LeavingControllerId: Characteristics.Of(
                    context.State, context.Abilities, battle).ControllerId),
            new FreeCastOffered(exiled, context.ControllerId, Transformed: true),
        ];
    }
}

/// <summary>
/// Offers the card this permanent hid away, free, to its controller (CR 702.75a, CR 601.2b).
/// </summary>
/// <remarks>
/// Hideaway's other half. The keyword itself only buries a card in exile; every card that prints
/// it also prints a second line saying when the buried card may be played, and until now that
/// line was the one thing on those cards nothing read — which made hideaway a keyword that
/// compiled and could never pay out.
/// <para>
/// "The exiled card" is the card <em>this</em> permanent's own ability put there, so the link is
/// read off <see cref="GameObject.ExiledBy"/> rather than by hunting exile for something the
/// controller owns. Two hideaway permanents on one board each have their own card, and a hunt
/// would hand over whichever came first.
/// </para>
/// <para>
/// The offer is the standing free-cast machinery cascade and a defeated Siege already use:
/// permission on the exiled card, taken on a later priority through the ordinary casting path,
/// revoked when its owner next passes. The printed sentence gives the window inside the
/// resolution and this gives one that ends at the next pass — the deviation the whole
/// offer-a-cast family shares, written down under <see cref="GameObject.MayCastFree"/>.
/// </para>
/// <para>
/// A permanent that hid nothing — its trigger countered, its card already played — offers
/// nothing. That is the sentence finding no card, not a reason to refuse the line.
/// </para>
/// </remarks>
public sealed record OfferHiddenCard : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hidden = context.State.Exile
            .Select(context.State.GetObject)
            .FirstOrDefault(card => card.ExiledBy == context.PhysicalSourceId);

        return hidden is null ? [] : [new FreeCastOffered(hidden.Id, context.ControllerId)];
    }
}

/// <summary>
/// Exiles a permanent and brings it back at the next end step (CR 603.7b).
/// </summary>
/// <remarks>
/// The slow flicker. Its immediate twin below does both moves at once; this one has to name the
/// exiled card afterwards, and a card that changes zones is a new object (CR 400.7) - so the
/// delayed trigger is created here, where the id the exile produced is still in hand.
/// <para>
/// The trigger is filed under the card's owner, because it returns "under its owner's control"
/// and the dispatch moves it for whoever the trigger belongs to.
/// </para>
/// </remarks>
public sealed record ExileAndReturnAtEndStep(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();

        return
        [
            new ObjectMoved(
                target.Subject, exiled, Zone.Battlefield, Zone.Exile,
                permanent.OwnerId, MoveCause.Exile),

            new DelayedTriggerCreated(
                Guid.NewGuid(),
                permanent.OwnerId,
                exiled,
                State.TurnStep.End,
                "return-to-battlefield",
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Runs an exile and brings back whatever it exiled at the beginning of the next end step.
/// </summary>
/// <remarks>
/// A wrapper rather than a variant of each exile, because the sentence before this one takes
/// many shapes - a target, a group, the source itself - and every one of them ends with the same
/// clause. It reads its own inner effect's events to learn which object was exiled, which is the
/// only place that answer exists: the card that left the battlefield is a new object now
/// (CR 400.7) and nothing else in the resolution knows its new id.
/// </remarks>
public sealed record ReturnAtNextEndStep(IEffect Inner) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var produced = Inner.Resolve(context);
        var armed = new List<GameEvent>(produced);

        foreach (var moved in produced.OfType<ObjectMoved>())
        {
            if (moved.To != Zone.Exile)
                continue;

            armed.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                moved.ControllerId,
                moved.NewId,
                State.TurnStep.End,
                "return-to-battlefield",
                context.State.TurnNumber));
        }

        return armed;
    }
}

public sealed record FlickerTarget(int TargetIndex = 0, bool Tapped = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();
        var returning = ObjectId.New();

        var events = new List<GameEvent>
        {
            new ObjectMoved(
                target.Subject, exiled, Zone.Battlefield, Zone.Exile,
                permanent.OwnerId, MoveCause.Exile),
            new ObjectMoved(
                exiled, returning, Zone.Exile, Zone.Battlefield,
                permanent.OwnerId, MoveCause.Return),
        };

        if (Tapped)
            events.Add(new PermanentTapped(returning));

        return events;
    }
}

/// <summary>Draws cards (CR 121.3). "You" is the controller unless a target says otherwise.</summary>
/// <summary>
/// Returns a target permanent to its owner's hand (CR 400.3).
/// </summary>
/// <remarks>
/// Its <em>owner's</em> hand, not its controller's. The two are the same in almost every game
/// and different in exactly the ones that matter — a creature you have taken control of goes
/// home when it is bounced, and a rule written against the controller would quietly steal it.
/// </remarks>
public sealed record ReturnToHand(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } going)
            return [];

        if (!context.State.TryGetObject(going, out var permanent))
            return [];

        // CR 608.2b: a target that has stopped being legal is skipped, and the spell does as much
        // as it can with the rest. A creature bounced or killed in response to this is exactly
        // that, and describing a move out of the battlefield it has already left was refused by
        // the reducer rather than quietly doing nothing.
        //
        // For a target only. A pronoun the trigger answered names a card that is *expected* to
        // have left: "when enchanted creature dies, return that card to its owner's hand" is
        // about the card now in a graveyard under a new id (CR 400.7), and eight corpus cards -
        // Squee's Embrace, Demonic Vigor and the six Zendikons - are that one sentence. Holding
        // them to the battlefield rule compiled every one of them into an effect that did
        // nothing at all, which is a worse card than the unread line it replaced.
        if (Subject == EffectSubject.Target && permanent.Zone != Zone.Battlefield)
            return [];

        // Wherever it actually is, so the event describes a move that can happen. A card already
        // in a hand or a library is left alone rather than moved from a zone it is not in.
        if (permanent.Zone is not (Zone.Battlefield or Zone.Graveyard or Zone.Exile))
            return [];

        return
        [
            new ObjectMoved(
                going, ObjectId.New(), permanent.Zone, Zone.Hand,
                permanent.OwnerId, MoveCause.Return),
        ];
    }
}

/// <summary>
/// Puts a target permanent into its owner's library (CR 400.7).
/// </summary>
/// <remarks>
/// A harder bounce than <see cref="ReturnToHand"/>, and the difference is the whole reason cards
/// print it: the permanent is gone for at least a draw, and on the bottom it is gone for good.
/// Which end it goes to is the entire point, so it is a parameter rather than a default.
/// </remarks>
public sealed record PutTargetOnLibrary(
    int TargetIndex = 0, ZonePosition Position = ZonePosition.Top) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A permanent on the battlefield and a card in a graveyard are both put onto a library
        // by this line, and the zone the card leaves has to be the one it is actually in: naming
        // the battlefield unconditionally meant a graveyard target moved nowhere at all.
        if (context.TargetAt(TargetIndex) is not
            { Kind: TargetKind.Permanent or TargetKind.CardInGraveyard } target)
        {
            return [];
        }

        if (!context.State.TryGetObject(target.Subject, out var card)
            || card.Zone is not (Zone.Battlefield or Zone.Graveyard))
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                target.Subject,
                ObjectId.New(),
                card.Zone,
                Zone.Library,
                card.OwnerId,
                MoveCause.Return,
                Position),
        ];
    }
}

/// <summary>
/// Shows a player's hand to everybody (CR 701.16a).
/// </summary>
/// <remarks>
/// Whose hand is a target when the card names one and a scope when it names a group, which is
/// why both are here: "target opponent reveals their hand" and "each opponent reveals their
/// hand" are the same act asked of different people.
/// </remarks>
/// <summary>"Look at target opponent's hand" (CR 701.19a).</summary>
public sealed record LookAtHand(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Player } target
            || !context.State.Players.ContainsKey(target.Player))
        {
            return [];
        }

        var hand = context.State.GetPlayer(target.Player).Hand;

        return hand.IsEmpty
            ? []
            : [new HandLookedAt(context.ControllerId, target.Player, hand)];
    }
}

public sealed record RevealHand(int? TargetIndex = null, PlayerScope Scope = PlayerScope.You)
    : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IEnumerable<Guid> who = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } target ? [target.Player] : []
            : PlayerScopes.Resolve(Scope, context);

        return
        [
            .. who
                .Select(id => (Player: id, Hand: context.State.GetPlayer(id).Hand))
                .Where(shown => !shown.Hand.IsEmpty)
                .Select(shown => new CardsRevealed(shown.Player, shown.Hand)),
        ];
    }
}

/// <summary>
/// A target permanent sits out its controller's next untap step (CR 502.3).
/// </summary>
/// <remarks>
/// The tail of "tap target creature. That creature doesn't untap during its controller's next
/// untap step", which is why it names no target of its own - it reads the one the sentence
/// before it chose.
/// </remarks>
public sealed record SkipNextUntap(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } held
            || !context.State.TryGetObject(held, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return [new UntapSkipped(held, Skipping: true)];
    }
}

/// <summary>
/// The source itself skips its controller's next untap step (CR 502.3).
/// </summary>
/// <remarks>
/// The tail of a mana ability far more often than a spell: "{T}: Add {C}{C}. This land doesn't
/// untap during your next untap step" is a land that pays double and then sits out a turn. It
/// names no target because there is nothing to aim at - the permanent that produced the ability
/// is the one that stays tapped, and asking would offer a choice the card does not give.
/// </remarks>
public sealed record SkipNextUntapSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        // Gone from the battlefield before the ability resolved: nothing to keep tapped, and an
        // event naming an object that is not there would replay into a state that has no room
        // for it.
        if (!context.State.TryGetObject(subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return [new UntapSkipped(subject, Skipping: true)];
    }
}

/// <summary>
/// Does the same thing to every target of an "any number of target ..." block (CR 601.2c).
/// </summary>
/// <remarks>
/// The other half of <see cref="TargetSpec.AnyNumber"/>. One spec stands for a block of targets
/// whose length is announced as the spell is cast, so one of these stands for the effects that
/// happen to each of them — and both are turned into the flat, counted lists everything else
/// reads by <see cref="VariableTargets.Expand(ImmutableList{TargetSpec}, ImmutableList{IEffect}, int)"/>.
/// <para>
/// <see cref="Effects"/> is written against <see cref="FirstIndex"/>, the position of the block's
/// first target — not against zero. That is deliberate: the corpus invariant that every chosen
/// target is read by something walks the effect tree and asks which indices it uses, and a
/// template numbered from zero would report the block's own target as chosen and ignored on
/// every card whose block is not first.
/// </para>
/// <para>
/// It resolves on its own as well, doing each target in turn, so that an unexpanded one is a
/// correct effect rather than a silent no-op. Expansion is still what normally happens, and for
/// a reason the loop here cannot reproduce: <c>RunEffects</c> applies each effect's events before
/// running the next, so an expanded block sees the state its previous target left behind
/// (CR 608.2c) exactly as "up to three target creatures" already does.
/// </para>
/// </remarks>
public sealed record ToEachChosenTarget(ImmutableList<IEffect> Effects, int FirstIndex = 0)
    : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        for (var index = FirstIndex; index < context.Targets.Count; index++)
        {
            foreach (var effect in Effects)
                events.AddRange(EffectTargets.Shift(effect, index - FirstIndex).Resolve(context));
        }

        return events;
    }
}

/// <summary>
/// An effect that spends a quantity announced as a division among its targets (CR 601.2d).
/// </summary>
/// <remarks>
/// Division is not a fact about damage. CR 601.2d is written over a spell or ability that
/// "requires a player to divide or distribute an effect", and the two families printed on cards
/// are damage and counters — the same announcement, the same "each target must be assigned at
/// least one", the same refusal to redistribute a lost target's share. This is what the
/// announcement check is keyed on, so a second divided verb costs a record rather than a second
/// copy of the rule.
/// <para>
/// <see cref="Total"/> is an <see cref="Amount"/> and not an <c>int</c> because "deals X damage
/// divided as you choose" is a real card. X is announced before the division is (CR 601.2b before
/// CR 601.2d), so the number the announcement is checked against is known by the time it is
/// needed — but only for an amount that is fixed or is X. An amount counted off the board is
/// refused rather than guessed at, in <c>Game.RequireDivision</c>.
/// </para>
/// </remarks>
public interface IDividedEffect
{
    /// <summary>How much is divided, in total, across every target this reaches.</summary>
    Amount Total { get; }

    /// <summary>How many of the spell or ability's targets the division covers.</summary>
    int TargetCount { get; }

    /// <summary>The first of those targets, as an index into what was chosen.</summary>
    int FirstIndex { get; }
}

/// <summary>
/// Deals damage split among the targets in the amounts announced as the spell was cast
/// (CR 601.2d).
/// </summary>
/// <remarks>
/// The division is not made here. It was made as the spell was cast, is public from that moment,
/// and rides on the stack object — so this effect only spends what is already decided. An effect
/// that asked on resolution would be a different card: an opponent who let the spell resolve did
/// so knowing exactly where the damage was going.
/// <para>
/// A target that has become illegal is skipped and its share is simply not dealt. The damage is
/// not moved to the others, because the division named that target and CR 608.2b does not
/// redistribute what a lost target was owed.
/// </para>
/// </remarks>
public sealed record DealDividedDamage(Amount Total, int TargetCount, int FirstIndex = 0)
    : IEffect, IDividedEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var (slot, amount) in Division.Shares(this, context))
            events.AddRange(new DealDamage(new Amount(amount), slot).Resolve(context));

        return events;
    }
}

/// <summary>
/// Puts counters on the targets in the numbers announced as the spell was cast (CR 121.2,
/// CR 601.2d).
/// </summary>
/// <remarks>
/// "Distribute three +1/+1 counters among one, two, or three target creatures" is divided damage
/// with a different verb, and it is built as one: the same announcement rides on the same field of
/// the stack object, and the same three rules are enforced against it. They are identical because
/// CR 601.2d never mentions damage — it is about dividing or distributing anything, and both
/// printings say "as you choose" and require every chosen target to be assigned at least one.
/// <para>
/// The battlefield check is <see cref="PutCounters"/>'s, for <see cref="PutCounters"/>'s reason:
/// the reducer refuses a counter on anything that is not a permanent, so a target that has since
/// died has to be found here rather than discovered there. Its share is not moved to the others
/// (CR 608.2b).
/// </para>
/// </remarks>
public sealed record DistributeCounters(
    string Kind,
    Amount Total,
    int TargetCount,
    int FirstIndex = 0) : IEffect, IDividedEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var (slot, amount) in Division.Shares(this, context))
        {
            if (Subjects.Resolve(context, EffectSubject.Target, slot) is not { } on)
                continue;

            if (!context.State.TryGetObject(on, out var subject)
                || subject.Zone != Zone.Battlefield)
            {
                continue;
            }

            events.Add(new CountersChanged(on, Kind, amount));
        }

        return events;
    }
}

/// <summary>Reading an announced division back out, for the effects that spend one.</summary>
/// <remarks>
/// One reader rather than one per divided verb. Every line of the loop is a rule — which slots
/// this effect owns, that a slot the announcement never reached gets nothing, and that a zero
/// share does nothing at all — so two copies would be two places for those rules to drift.
/// Damage and counters differ only in what they do with the number.
/// </remarks>
public static class Division
{
    /// <summary>Each of this effect's target slots that was assigned anything, and how much.</summary>
    public static IEnumerable<(int Slot, int Amount)> Shares(
        IDividedEffect effect, ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(context);

        for (var i = 0; i < effect.TargetCount; i++)
        {
            var slot = effect.FirstIndex + i;
            if (slot >= context.Division.Count)
                break;

            var amount = context.Division[slot];
            if (amount > 0)
                yield return (slot, amount);
        }
    }
}

/// <summary>Taps a target permanent (CR 701.26a).</summary>
/// <remarks>
/// Tapping something already tapped does nothing at all — it is not an error and the spell is
/// not countered, so this returns no event rather than emitting one that would read as a second
/// tap in the log.
/// </remarks>
/// <summary>
/// "Return that card to the battlefield under your control" - reanimating what the trigger was
/// about (CR 400.7).
/// </summary>
/// <remarks>
/// The subject is a creature that died, so the id the trigger carries names the permanent it was
/// and not the card it has become. Followed forward to the card, which is the only thing that can
/// be returned - the permanent stopped existing the moment it left.
/// </remarks>
public sealed record ReturnSubjectCardToBattlefield(bool UnderYourControl = true) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.SubjectObject is not { } was)
            return [];

        var card = context.State.TryGetObject(was, out var present)
            ? present
            : context.ObjectBehind?.Invoke(was);

        // Only from a graveyard: something that has since been exiled or shuffled away is gone,
        // and a card that has moved on is not "that card" any more.
        if (card is not { Zone: Zone.Graveyard })
            return [];

        var newId = ObjectId.New();

        return
        [
            new ObjectMoved(
                card.Id,
                newId,
                Zone.Graveyard,
                Zone.Battlefield,
                UnderYourControl ? context.ControllerId : card.OwnerId,
                MoveCause.Other),
        ];
    }
}

/// <summary>
/// Taps something and keeps it down through its controller's next untap step (CR 302.6).
/// </summary>
/// <remarks>
/// One effect for two printed clauses, because the second names what the first just tapped and
/// an effect never sees the events of the one before it. The freeze is emitted even when the
/// permanent was already tapped: "tap that creature and it doesn't untap" is two instructions,
/// and the second does not depend on the first having changed anything.
/// </remarks>
public sealed record TapAndFreeze(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } frozen
            || !context.State.TryGetObject(frozen, out var caught)
            || caught.Zone != Zone.Battlefield)
        {
            return [];
        }

        return caught.Permanent is { IsTapped: true }
            ? [new UntapSkipped(frozen, true)]
            : [new PermanentTapped(frozen), new UntapSkipped(frozen, true)];
    }
}

public sealed record TapTarget(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } tapping)
            return [];

        if (!context.State.TryGetObject(tapping, out var permanent))
            return [];

        if (permanent.Permanent?.IsTapped != false)
            return [];

        return [new PermanentTapped(tapping)];
    }
}

/// <summary>
/// Which permanents leave with one that phases out (CR 702.26g).
/// </summary>
/// <remarks>
/// Shared with the untap step's own phasing sweep rather than written twice. The keyword sweep
/// had this fan-out and an effect that phased one permanent would not have: an Aura on a creature
/// sent away by Reality Ripple would have stayed on the battlefield attached to nothing, which is
/// CR 704.5m's state-based action putting it in a graveyard - the creature comes back naked and
/// the opponent is a card up on the exchange.
/// <para>
/// It runs to a fixed point because attachment chains: an Aura on an Equipment on a creature goes
/// whole. And what is written down is <em>whose</em> untap step returns each one, taken from the
/// permanent that is actually phasing, so an opponent's Aura returns on its host's schedule
/// (CR 702.26h) rather than its own.
/// </para>
/// </remarks>
public static class Phasing
{
    /// <summary>The events that phase <paramref name="leaving"/> out, hangers-on included.</summary>
    public static IReadOnlyList<GameEvent> Out(
        GameState state, IReadOnlyDictionary<ObjectId, Guid> leaving)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(leaving);

        var going = new Dictionary<ObjectId, Guid>(leaving);
        bool grew;

        do
        {
            grew = false;

            foreach (var id in state.Battlefield)
            {
                if (going.ContainsKey(id))
                    continue;

                if (state.GetObject(id).Permanent?.AttachedTo is { } host
                    && going.TryGetValue(host, out var withHost))
                {
                    going[id] = withHost;
                    grew = true;
                }
            }
        }
        while (grew);

        return [.. going.Select(pair => new PermanentPhasedOut(pair.Key, pair.Value))];
    }
}

/// <summary>
/// A permanent phases out (CR 702.26b) — "target creature phases out", "~ phases out".
/// </summary>
/// <remarks>
/// The one-shot half of the keyword. Nothing else is needed in the state: the untap step's sweep
/// already returns whatever <see cref="GameState.PhasedOut"/> holds for the active player, so an
/// effect that writes the same entry gets the phasing-in half for free (CR 702.26c).
/// <para>
/// Who it returns for is the permanent's <em>computed</em> controller rather than the id it was
/// created with, because a stolen creature phases in under whoever holds it now (CR 702.26c, and
/// control is layer 2 — CR 613.1b).
/// </para>
/// </remarks>
public sealed record PhaseOutPermanent(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } leaving)
            return [];

        if (!context.State.TryGetObject(leaving, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        // Already gone. A phased-out permanent does not exist for this purpose (CR 702.26b), so
        // there is nothing to phase out and no event describing one.
        if (context.State.PhasedOut.ContainsKey(leaving))
            return [];

        return Phasing.Out(
            context.State,
            new Dictionary<ObjectId, Guid>
            {
                [leaving] =
                    Characteristics.ControllerOf(context.State, context.Abilities, permanent),
            });
    }
}

/// <summary>Untaps a target permanent (CR 701.26b).</summary>
/// <remarks>
/// Reuses the untap step's event rather than adding a singular one. There is no such thing as
/// untapping "one at a time" in the rules — the untap step untaps a whole set simultaneously —
/// and a second event meaning the same thing is a second thing every reducer and replay has to
/// know about.
/// <para>
/// It is the last of <c>EffectPhrase.ItLine</c>'s five verbs to get a subject, and the comment
/// beside the tap-or-untap reader had said so for months: a pronoun there was refused outright
/// because untapping had nowhere to put an answer. No corpus card reaches the trigger-subject
/// form yet - every printing of "untap it" whose sentence names nothing sits behind a trigger
/// the allow-list does not admit - so what this buys today is that one sentence has one reader
/// and one ladder, rather than four verbs with an answer and a fifth with a special case.
/// </para>
/// </remarks>
public sealed record UntapTarget(
    int TargetIndex = 0, EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } waking)
            return [];

        if (!context.State.TryGetObject(waking, out var permanent))
            return [];

        if (permanent.Permanent?.IsTapped != true)
            return [];

        return StunCounters.Untapping(context.State, [waking]);
    }
}

/// <summary>
/// The replacement every untap goes through while stun counters exist (CR 122.1c).
/// </summary>
/// <remarks>
/// "If a permanent with a stun counter on it would become untapped, remove a stun counter from
/// it instead." Nothing read the counter. It compiled - "put a stun counter on it" is a named
/// counter like any other and the counter reader has taken any name for months - so every card
/// in the family was counted as covered, went onto the battlefield, put its counters on, and
/// then watched the creature untap on schedule. Eighty-seven corpus cards mention one.
/// <para>
/// A helper rather than a <c>ReplacementEffectDefinition</c>, because there is no permanent to
/// hang one on: this is part of the game rather than something a card grants, and it applies to
/// an untap from any source. Every place that emits <see cref="PermanentsUntapped"/> asks here
/// instead, which is what stops the next untap that gets written from missing it.
/// </para>
/// <para>
/// The removals go in the log beside the untap rather than in place of it silently. A replay
/// has to reach the same board, and "these untapped, that one spent a counter" is the whole of
/// what happened.
/// </para>
/// </remarks>
public static class StunCounters
{
    /// <summary>What actually happens when these permanents would untap (CR 122.1c).</summary>
    public static IReadOnlyList<GameEvent> Untapping(
        GameState state, IReadOnlyList<ObjectId> ids)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ids);

        var waking = ImmutableList.CreateBuilder<ObjectId>();
        var spent = new List<GameEvent>();

        foreach (var id in ids)
        {
            if (state.TryGetObject(id, out var obj)
                && obj.Permanent is { } permanent
                && permanent.Counters.GetValueOrDefault(CounterKinds.Stun) > 0)
            {
                // One counter, however many the permanent carries: the rule replaces this
                // untap and the next one is replaced again by the next counter.
                spent.Add(new CountersChanged(id, CounterKinds.Stun, -1));
                continue;
            }

            waking.Add(id);
        }

        // An untap of nobody is not emitted at all. "Untap target creature" aimed at a stunned
        // creature untaps nothing, and an event saying so is a line every replay and every log
        // reader has to know to ignore.
        return waking.Count > 0
            ? [new PermanentsUntapped(waking.ToImmutable()), .. spent]
            : spent;
    }
}

public sealed record DrawCards(
    Amount Count,
    int? TargetIndex = null,
    PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A named target wins outright over a scope, the same way it does for a discard: the
        // sentence named one player and the scope named none.
        if (TargetIndex is { } index)
        {
            var aimed = context.TargetAt(index)?.Player ?? context.ControllerId;
            return Drawing.From(context, aimed, Count.In(context));
        }

        var drawn = new List<GameEvent>();
        var count = Count.In(context);

        foreach (var who in PlayerScopes.Resolve(Scope, context))
            drawn.AddRange(Drawing.From(context, who, count));

        return drawn;
    }
}

/// <summary>Gains or loses life (CR 119.3).</summary>
public sealed record ChangeLife(Amount Amount, int? TargetIndex = null) : IEffect
{
    /// <summary>Whose life changes, when the sentence names a group rather than a target.</summary>
    public PlayerScope Scope { get; init; } = PlayerScope.You;

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A named target wins outright over a scope, the same way it does for drawing and
        // discarding: the sentence named one player and the scope named none.
        var amount = Amount.In(context);

        if (TargetIndex is { } index)
        {
            var aimed = context.TargetAt(index)?.Player ?? context.ControllerId;
            return [new LifeChanged(aimed, amount, context.State.GetPlayer(aimed).Life + amount)];
        }

        return
        [
            .. PlayerScopes.Resolve(Scope, context).Select(who =>
                new LifeChanged(who, amount, context.State.GetPlayer(who).Life + amount)),
        ];
    }
}

/// <summary>
/// Changes the life total of whoever controls a target (CR 608.2).
/// </summary>
/// <remarks>
/// The tail of a counterspell: "Counter target creature spell. Its controller loses 1 life."
/// "Its" is the target the sentence before chose, so this reads a target slot like any other
/// effect — what is different is that it wants the target's <em>controller</em> rather than the
/// target itself.
/// <para>
/// A target that is already a player is used directly, so the same effect reads "target player
/// loses 2 life" if a card ever phrases it that way.
/// </para>
/// </remarks>
public sealed record ChangeLifeOfTargetsController(Amount Amount, int TargetIndex = 0) : IEffect
{
    /// <summary>
    /// Whether the sentence said "its owner" rather than "its controller" (CR 108.3).
    /// </summary>
    /// <remarks>
    /// A flag on the one effect rather than a second effect beside it, because the two sentences
    /// differ in one word and in nothing else - and two effects would be two places to keep the
    /// id-following right.
    /// </remarks>
    public bool ToOwner { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TargetOwnership.WhoseTarget(context, TargetIndex, ToOwner) is not { } who)
            return [];

        var amount = Amount.In(context);
        return [new LifeChanged(who, amount, context.State.GetPlayer(who).Life + amount)];
    }
}

/// <summary>
/// The controller of a target draws cards — "its controller draws a card" (CR 121.3).
/// </summary>
/// <remarks>
/// The same player <see cref="ChangeLifeOfTargetsController"/> finds, and found the same way,
/// including the case that makes it awkward: the target may have been countered or destroyed a
/// sentence ago and be a card in a graveyard under a new id (CR 400.7). Whose it was is still a
/// fact about the game, so the controller is followed back rather than given up on.
/// </remarks>
public sealed record DrawForTargetsController(Amount Count, int TargetIndex = 0) : IEffect
{
    /// <summary>Whether the sentence said "its owner" (CR 108.3).</summary>
    public bool ToOwner { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TargetOwnership.WhoseTarget(context, TargetIndex, ToOwner) is { } who
            ? Drawing.From(context, who, Count.In(context))
            : [];
    }
}

/// <summary>
/// The controller of a target gets tokens - "its controller creates a Treasure token" (CR 111.1).
/// </summary>
/// <remarks>
/// The same player <see cref="ChangeLifeOfTargetsController"/> and
/// <see cref="DrawForTargetsController"/> find, found the same way, and here for the same reason
/// none of them is a <see cref="PlayerScope"/>: a scope names a relation to the controller of the
/// ability, and this names a relation to something the ability <em>targeted</em>. Nothing in the
/// scope vocabulary can say it.
/// <para>
/// Kept apart from <see cref="CreateToken"/>'s own <c>TargetIndex</c>, which means a targeted
/// <em>player</em>. Folding the two would put two different questions behind one field, and the
/// index shifter could no longer say what either one pointed at.
/// </para>
/// <para>
/// The target is nearly always gone by the time this runs - the sentence in front of it destroyed
/// or exiled the permanent whose controller this is - which is exactly what
/// <see cref="TargetOwnership"/> is for: whose it was is still a fact about the game, so the
/// controller is followed back rather than given up on (CR 400.7, 608.2h).
/// </para>
/// </remarks>
public sealed record CreateTokenForTargetsController(
    Domain.Models.CardDefinition Token,
    Amount Count = default,
    int TargetIndex = 0,
    bool Tapped = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TargetOwnership.ControllerOf(context, TargetIndex) is not { } owner)
            return [];

        // The same reading of an unset count that CreateToken makes: no number printed means
        // one, and a number that counts to nothing means none.
        var count = Count.Equals(default(Amount)) ? 1 : Math.Max(0, Count.In(context));

        var made = new List<GameEvent>();

        foreach (var _ in Enumerable.Range(0, count))
        {
            var id = ObjectId.New();

            made.Add(new ObjectCreated(id, Token, owner, owner, Zone.Battlefield));

            if (Tapped)
                made.Add(new PermanentTapped(id));
        }

        return made;
    }
}

/// <summary>
/// Damages whoever controls a target — "and 2 damage to that creature's controller" (CR 119.3).
/// </summary>
/// <remarks>
/// The third verb of the clause <see cref="ChangeLifeOfTargetsController"/> and
/// <see cref="DrawForTargetsController"/> already read, and it arrives late for a reason worth
/// keeping: those two are printed as their own sentence after a counterspell, while this one is
/// almost always the tail of the sentence that did the damage — "~ deals 4 damage to target
/// creature <em>and 2 damage to that creature's controller</em>" — so the words never reached a
/// reader that begins at a subject.
/// <para>
/// Damage rather than life loss, and the difference is not cosmetic: it is dealt by the physical
/// source, so it can be prevented, it is what lifelink and "whenever this deals damage" watch,
/// and a player with protection from the source is not touched. A card of this shape compiled as
/// life loss would be a different card in all four ways.
/// </para>
/// </remarks>
public sealed record DamageTargetsController(Amount Amount, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The creature this damage is measured against may already be dead — the first half of
        // the same sentence often kills it — so the controller is followed back through the log
        // rather than looked up on a battlefield that no longer holds it (CR 400.7).
        return TargetOwnership.ControllerOf(context, TargetIndex) is { } who
            ? [new PlayerDamaged(
                who, context.PhysicalSourceId, Amount.In(context), IsCombat: false)]
            : [];
    }
}

/// <summary>Which player a target belongs to (CR 608.2).</summary>
/// <remarks>
/// One question asked by more than one effect — "its controller loses 2 life", "its controller
/// draws a card" — and worth a name of its own because the awkward part is shared too: the target
/// may have been countered or destroyed a sentence ago and be a card in a graveyard under a new
/// id (CR 400.7). Whose it was is still a fact about the game, so the controller is followed back
/// rather than given up on. Written twice, the second copy is where that arm gets forgotten.
/// </remarks>
public static class TargetOwnership
{
    public static Guid? ControllerOf(ResolutionContext context, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(targetIndex) is not { } target)
            return null;

        if (target.Kind == TargetKind.Player)
            return target.Player;

        if (context.State.TryGetObject(target.Subject, out var owner))
            return owner.ControllerId;

        return context.ControllerBehind?.Invoke(target.Subject);
    }

    /// <summary>Who <em>owns</em> a target, which is a different player (CR 108.3).</summary>
    /// <remarks>
    /// Owner is where the card came from and never changes; controller is layer 2 and does. On
    /// Path of Peace - "Destroy target creature. Its owner gains 4 life" - the two are the same
    /// player until somebody steals the creature, and then the life goes to the player who lost
    /// the card rather than to the one who took it. Reading "owner" as "controller" would have
    /// paid the wrong player on exactly the board where the distinction is the point, which is
    /// why the sentence stayed unread until there was something honest to compile it to.
    /// <para>
    /// Like its sibling it follows the id forward: the creature is in a graveyard under a new id
    /// by the time this runs (CR 400.7), and who owned it is still a fact about the game.
    /// </para>
    /// </remarks>
    public static Guid? OwnerOf(ResolutionContext context, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(targetIndex) is not { } target)
            return null;

        if (target.Kind == TargetKind.Player)
            return target.Player;

        if (context.State.TryGetObject(target.Subject, out var live))
            return live.OwnerId;

        return context.ObjectBehind?.Invoke(target.Subject)?.OwnerId;
    }

    /// <summary>Whichever of the two the sentence named.</summary>
    public static Guid? WhoseTarget(ResolutionContext context, int targetIndex, bool owner) =>
        owner ? OwnerOf(context, targetIndex) : ControllerOf(context, targetIndex);
}

/// <summary>Drawing cards, and what happens when there are none (CR 121.3, 121.4).</summary>
/// <remarks>
/// Shared because the empty-library arm is the part that matters and the part a second copy
/// would omit: the draw does not happen, the attempt is remembered, and the player loses to a
/// state-based action later rather than here — so the rest of the effect still resolves.
/// </remarks>
public static class Drawing
{
    public static IReadOnlyList<GameEvent> From(ResolutionContext context, Guid who, int count)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();
        var library = context.State.GetPlayer(who).Library;

        for (var i = 0; i < count; i++)
        {
            if (i >= library.Count)
            {
                events.Add(new DrawFromEmptyLibraryAttempted(who));
                break;
            }

            events.Add(new ObjectMoved(
                library[i], ObjectId.New(), Zone.Library, Zone.Hand, who, MoveCause.Draw));
        }

        return events;
    }
}

/// <summary>
/// Gives poison counters to the players a scope names (CR 122.1, 704.5c).
/// </summary>
/// <remarks>
/// Poison lives on the player rather than on a permanent, so it takes a scope where the other
/// counter effect takes a target. Ten of them and that player loses (CR 704.5c), which the
/// state-based actions already check.
/// </remarks>
public sealed record GivePoisonCounters(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var count = Count.In(context);
        return
        [
            .. PlayerScopes.Resolve(Scope, context)
                .Select(who => new PoisonCountersChanged(who, count)),
        ];
    }
}

/// <summary>
/// Counters the spell or ability the trigger was about (CR 701.5a).
/// </summary>
/// <remarks>
/// Ward's "counter it". It cannot be a <see cref="CounterTargetSpell"/> because a ward trigger
/// does not target — that is the point of ward, which would otherwise be stopped by the very
/// hexproof it sits beside. So it counters the trigger's subject instead.
/// <para>
/// A subject that has already left the stack is not an error: something else countered the spell
/// first, or it resolved while the trigger was still waiting. Countering nothing is what the rules
/// do there (CR 701.5b).
/// </para>
/// </remarks>
public sealed record CounterSubjectSpell : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.SubjectObject is not { } subject
            || !context.State.TryGetObject(subject, out var spell)
            || spell.Zone != Zone.Stack)
        {
            return [];
        }

        // CR 701.6a: a spell that can't be countered simply isn't. Through the one reader that
        // knows both spellings, because the keyword is only how a spell says it about itself and
        // a permanent can say it about a group.
        if (Bans.CannotBeCountered(context.State, context.Abilities, spell))
            return [];

        return
        [
            new ObjectMoved(
                subject, ObjectId.New(), Zone.Stack, Zone.Graveyard,
                spell.ControllerId, MoveCause.Other),
        ];
    }
}

/// <summary>
/// Creates a token and attaches the source to it (CR 702.90b).
/// </summary>
/// <remarks>
/// Living weapon, and the one shape that cannot be spelled as "create a token" followed by
/// "attach this to it": the second half needs the id of the thing the first half made, and an
/// effect only ever sees the events it returns itself. Two effects in sequence would each be
/// handed a context, and neither context can name a token that did not exist when it was built.
/// <para>
/// The id is generated here and used twice, which is exactly why the two belong in one effect.
/// </para>
/// </remarks>
public sealed record CreateTokenAndAttach(Domain.Models.CardDefinition Token) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // CR 701.3c: an Equipment can only be attached to a permanent that is still there, and
        // the source is the Equipment itself rather than whatever is resolving.
        var equipment = context.PhysicalSourceId;
        if (!context.State.TryGetObject(equipment, out var self) || self.Zone != Zone.Battlefield)
            return [];

        var germ = ObjectId.New();

        return
        [
            new ObjectCreated(germ, Token, context.ControllerId, context.ControllerId, Zone.Battlefield),
            new PermanentAttached(equipment, germ),
        ];
    }
}

/// <summary>
/// Runs effects against whatever the source is attached to (CR 701.3).
/// </summary>
/// <remarks>
/// "When this enters, tap enchanted creature." The enchanted creature is not a target — it was
/// chosen when the Aura was cast, and this is not choosing it again — so it cannot be an entry in
/// the ability's target list. But every verb that could apply to it already knows how to act on a
/// target, so rather than a tapping-the-attached effect and a destroying-the-attached effect and
/// four more, the inner effects are handed a context whose one target <em>is</em> the attached
/// permanent. Whatever the verb vocabulary learns, this learns with it.
/// <para>
/// Nothing attached means nothing happens, which is right: the Aura may have been moved, or the
/// creature may have died in response to the trigger.
/// </para>
/// </remarks>
public sealed record OnAttached(ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var self)
            || self.Permanent?.AttachedTo is not { } attached
            || !context.State.TryGetObject(attached, out _))
        {
            return [];
        }

        var inner = context with { Targets = [Target.ToPermanent(attached)] };
        return [.. Effects.SelectMany(effect => effect.Resolve(inner))];
    }
}

/// <summary>
/// Puts an emblem into a player's command zone (CR 114.2).
/// </summary>
/// <remarks>
/// "[Player] gets an emblem with [ability]" is the only way an emblem is ever made, and CR 114.2
/// says exactly what it means: that player puts an emblem with that ability into the command
/// zone, owning and controlling it. So this is one <see cref="ObjectCreated"/> per recipient into
/// <see cref="Zone.Command"/> — the same event a dungeon arrives on, because an emblem is the
/// same kind of thing: an object in that zone whose abilities work from it.
/// <para>
/// The emblem's abilities travel as the <see cref="Domain.Models.CardDefinition"/> holding their
/// text, exactly as a copy effect carries the copied card whole
/// (<see cref="Cards.GenerativeEffects"/>) and for the same reason: the log has to read back in a
/// later process, and an id pointing at something the compiler would have to be asked to rebuild
/// is an id that can stop meaning anything. <see cref="Emblems.CardFor"/> is a pure function of
/// the printed words, so the definition a replay rebuilds is the definition the game made.
/// </para>
/// <para>
/// A player who has left the game gets nothing; <see cref="PlayerScopes"/> already answers that
/// way, and a target that is no longer a player answers with no id at all.
/// </para>
/// </remarks>
public sealed record CreateEmblem(Domain.Models.CardDefinition Emblem, int? TargetIndex = null)
    : IEffect
{
    /// <summary>Who gets it, when the sentence names a group rather than a target.</summary>
    public PlayerScope Scope { get; init; } = PlayerScope.You;

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A named target wins outright over a scope, the same way it does for drawing and for
        // life: the sentence named one player and the scope named none.
        var recipients = TargetIndex is { } index
            ? context.TargetAt(index)?.Player is { } aimed ? new[] { aimed } : []
            : PlayerScopes.Resolve(Scope, context).ToArray();

        return
        [
            .. recipients.Select(who =>
                new ObjectCreated(ObjectId.New(), Emblem, who, who, Zone.Command)),
        ];
    }
}

/// <summary>Gives energy counters to the players a scope names (CR 107.4c).</summary>
public sealed record GainEnergy(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var count = Count.In(context);
        return
        [
            .. PlayerScopes.Resolve(Scope, context).Select(who => new EnergyChanged(who, count)),
        ];
    }
}

/// <summary>
/// "You get an experience counter" (CR 122.1).
/// </summary>
/// <remarks>
/// Energy's twin, and separate from it because the two are different resources that different
/// cards read back: sixteen commanders hand out experience and eighteen cards multiply by "the
/// number of experience counters you have", none of which would be satisfied by a pile of energy.
/// <para>
/// The printed line is always exactly one counter, but the count is an <see cref="Amount"/> like
/// every other number in the engine, so a card that ever prints two - or two for each of
/// something - needs no new effect.
/// </para>
/// </remarks>
public sealed record GainExperience(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var count = Count.In(context);
        return
        [
            .. PlayerScopes.Resolve(Scope, context)
                .Select(who => new ExperienceCountersChanged(who, count)),
        ];
    }
}

/// <summary>
/// Copies a spell on the stack some number of times (CR 707.10).
/// </summary>
/// <remarks>
/// One effect for both shapes the corpus prints. With a target index it copies what it is aimed
/// at — "copy target instant or sorcery spell". Without one it copies the spell that produced the
/// ability, which is what storm and replicate do, and there the source is a spell still on the
/// stack waiting below the trigger that is copying it.
/// <para>
/// The copies are created top-down so they resolve before the original, which is what makes storm
/// resolve as a stack of copies with the real spell underneath.
/// </para>
/// </remarks>
public sealed record CopySpell(Amount Count = default, int? TargetIndex = null) : IEffect
{
    /// <summary>
    /// How many copies, when the number has to be worked out from the game (CR 702.40a).
    /// </summary>
    /// <remarks>
    /// Storm's "for each spell cast before it this turn" is not a printed number and not X, which
    /// are the only two things an <see cref="Amount"/> can be. It lives here rather than widening
    /// Amount with a delegate, because one effect needing a computed count is not a reason to make
    /// every amount in the engine capable of holding code.
    /// </remarks>
    public Func<ResolutionContext, int>? CountFrom { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.SpellOnStack } aimed
                ? aimed.Subject
                : (ObjectId?)null
            : context.PhysicalSourceId;

        if (subject is not { } spellId
            || !context.State.TryGetObject(spellId, out var spell)
            || spell.Zone != Zone.Stack)
        {
            return [];
        }

        // "Copy it" with no number means once; a default Amount is zero. A computed count is
        // allowed to be zero, though — storm on the first spell of the turn copies nothing.
        var copies = CountFrom is { } counted ? Math.Max(0, counted(context)) : Math.Max(1, Count.In(context));
        if (copies == 0)
            return [];

        return
        [
            .. Enumerable.Range(0, copies).Select(_ => new SpellCopied(
                ObjectId.New(), spell.Card, context.ControllerId, spell.Targets)),
        ];
    }
}

/// <summary>
/// Creates a token that is a copy of a permanent (CR 707.2).
/// </summary>
/// <remarks>
/// What gets copied is the <em>copiable values</em> — the printed card, plus anything that copied
/// onto it already. Not its counters, not its damage, not what an Aura is doing to it: a copy of
/// a 2/2 bear wearing +3/+3 of Auras is a 2/2 bear. Taking the target's card is exactly that, and
/// it is right for the same reason a face-down permanent's card is left alone — the card is the
/// copiable part and the rest is effects on top of it.
/// <para>
/// The copy is marked a token so it stops existing when it leaves the battlefield (CR 111.7),
/// which is the one way it differs from the thing it copied.
/// </para>
/// </remarks>
/// <summary>
/// A bonus that grows with the number of blockers beyond the first (CR 702.23a) - rampage.
/// </summary>
/// <remarks>
/// The size is not known until it resolves, which is why it is not an ordinary pump: a pump is a
/// continuous effect whose name carries the numbers, so the name is generated here from a count
/// taken at resolution rather than baked in when the card was compiled.
/// <para>
/// The blockers are counted now rather than when the trigger fired. That is what the rule says -
/// the bonus is worked out on resolution - and it means a blocker removed in response makes the
/// bonus smaller, which is the interaction the card is played around.
/// </para>
/// </remarks>
/// <param name="BeyondTheFirst">
/// Whether the first blocker is free, which is what tells rampage from the plainer
/// "+N/+N for each creature blocking it" the same trigger also prints. One creature
/// blocking is worth nothing under rampage and worth one bonus under the other, so
/// folding the two shapes together would have made one family or the other bigger or
/// smaller than printed - five corpus lines say the second, twelve the first.
/// </param>
public sealed record RampageBonus(int PerBlocker, bool BeyondTheFirst = true) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        var blockers = context.State.Combat.BlockersOf(sourceId);
        var counted = BeyondTheFirst ? blockers.Count - 1 : blockers.Count;

        if (counted <= 0)
            return [];

        var bonus = PerBlocker * counted;

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.PumpId(bonus, bonus),
                [sourceId],
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Puts the card this ability is printed on onto the battlefield attacking (CR 702.49a).
/// </summary>
/// <remarks>
/// Ninjutsu, and the reason the ability carries an attack: the creature whose place the ninja
/// takes is back in its owner's hand by the time this resolves, so the defender it was attacking
/// has to have been written down when the cost was paid.
/// <para>
/// It arrives attacking without ever having been declared, so no "whenever this attacks" ability
/// of its own triggers - which is what <see cref="JoinedCombat"/> exists to express.
/// </para>
/// </remarks>
public sealed record PutSourceOntoBattlefieldAttacking : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var card)
            || card.Zone != Zone.Hand)
        {
            return [];
        }

        if (!context.State.TryGetObject(context.SourceId, out var onStack)
            || onStack.Ability?.JoiningAgainst is not { } joining)
        {
            return [];
        }

        var arriving = ObjectId.New();

        return
        [
            new ObjectMoved(
                context.PhysicalSourceId, arriving, Zone.Hand, Zone.Battlefield,
                card.OwnerId, MoveCause.Other),
            new PermanentTapped(arriving),
            new JoinedCombat(arriving, joining),
        ];
    }
}

/// <summary>
/// A token copy of the source for each other opponent, attacking them (CR 702.115a) - myriad.
/// </summary>
/// <remarks>
/// Only reaches anything in a game of three or more: with one opponent, that opponent is the
/// defending player and there is nobody else to attack. That is what the card says rather than a
/// shortcoming, and it is the clearest thing in the engine that the N-player priority model was
/// worth building.
/// <para>
/// The printed "you may" is read as "you do". Declining is a choice the engine cannot ask for
/// mid-resolution, and it is one almost nobody takes; the tokens are exiled at end of combat
/// either way, so nothing survives the turn that would not have.
/// </para>
/// </remarks>
public sealed record MyriadCopies : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var original)
            || original.Zone != Zone.Battlefield
            || !context.State.Combat.Attackers.TryGetValue(sourceId, out var attacking))
        {
            return [];
        }

        // CR 707.3: the copiable values are what this permanent *is*, so a Clone that attacks
        // with myriad makes tokens of what it copied and not of Clone. Reading the printed card
        // made a myriad copy of a blank.
        var copied = TokenCards.AsToken(
            Characteristics.CardOf(context.State, context.Abilities, original));

        var events = new List<GameEvent>();

        foreach (var opponent in context.State.TurnOrder)
        {
            if (opponent == context.ControllerId
                || opponent == attacking.DefendingPlayer
                || context.State.GetPlayer(opponent).HasLost)
            {
                continue;
            }

            var token = ObjectId.New();

            events.Add(new ObjectCreated(
                token, copied, context.ControllerId, context.ControllerId, Zone.Battlefield));
            events.Add(new PermanentTapped(token));
            events.Add(new JoinedCombat(token, AttackTarget.Player(opponent)));
            events.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                token,
                State.TurnStep.EndOfCombat,
                "exile",
                context.State.TurnNumber));
        }

        return events;
    }
}

/// <summary>
/// Tokens created already attacking whoever the source is attacking (CR 702.180a) - mobilize.
/// </summary>
/// <remarks>
/// Its own effect rather than <see cref="CreateToken"/> with flags, because "attacking" is not a
/// property a token can be created with: it is a place in the combat, and the token has to be put
/// there by joining the combat the source is already in. Which player that is cannot be known
/// until the trigger resolves, so it is read off the combat rather than chosen at compile time.
/// <para>
/// The tokens attack the same player the source does, not one each. That is the difference from
/// myriad, which spreads across the other opponents, and it is why this reads
/// <c>attacking.DefendingPlayer</c> instead of walking the turn order.
/// </para>
/// <para>
/// Sacrificed at the beginning of the next end step, which is a delayed trigger created here
/// rather than a rule the tokens carry - they are ordinary tokens, and nothing about them says
/// they are temporary except the ability that made them.
/// </para>
/// </remarks>
public sealed record MobilizeTokens(int Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Nothing at all if the source has left combat or the battlefield by the time this
        // resolves. A token created attacking nobody would sit on the board as a 1/1 that never
        // attacked and never got sacrificed, which is worse than no token.
        if (!context.State.Combat.Attackers.TryGetValue(context.PhysicalSourceId, out var attacking))
            return [];

        var warrior = new Domain.Models.CardDefinition
        {
            OracleId = "token-red-warrior",
            Name = "Warrior",
            CardTypes = Domain.Enums.CardType.Creature | Domain.Enums.CardType.Token,
            Subtypes = ["Warrior"],
            Power = 1,
            Toughness = 1,
            ColorIdentity = [Domain.Enums.ManaColor.Red],
            Colors = [Domain.Enums.ManaColor.Red],
        };

        var events = new List<GameEvent>();

        foreach (var _ in Enumerable.Range(0, Math.Max(0, Count)))
        {
            var token = ObjectId.New();

            events.Add(new ObjectCreated(
                token, warrior, context.ControllerId, context.ControllerId, Zone.Battlefield));
            events.Add(new PermanentTapped(token));
            events.Add(new JoinedCombat(token, attacking));
            events.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                token,
                State.TurnStep.End,
                "sacrifice",
                context.State.TurnNumber));
        }

        return events;
    }
}

public sealed record CreateTokenCopy(
    Amount Count = default,
    int? TargetIndex = null,

    /// <summary>
    /// Whose card is copied, when it is neither a target nor this permanent.
    /// </summary>
    /// <remarks>
    /// "Whenever a creature dies, create a token that's a copy of that creature" names the
    /// creature the trigger was about. A null index used to mean this permanent, so there was no
    /// way to say that at all - and the pronoun branch beside it only worked when the sentence
    /// had already targeted something.
    /// </remarks>
    EffectSubject? Subject = null,

    /// <summary>
    /// What the sentence's "except" clause changes about the copy (CR 707.9b).
    /// </summary>
    /// <remarks>
    /// The same record the "enters as a copy" replacement holds, read by the same parser. It
    /// replaced a private <c>ExceptNotLegendary</c> flag, which was one of a dozen printed
    /// exceptions and refused the other eleven - the copy family's vocabulary written out twice,
    /// which is the bug this codebase keeps re-finding.
    /// </remarks>
    Cards.CopyException? Except = null,

    /// <summary>Whether the token arrives tapped (CR 111.6).</summary>
    bool Tapped = false,

    /// <summary>
    /// Whether what is copied is a card rather than a permanent (CR 707.2).
    /// </summary>
    /// <remarks>
    /// "Exile target artifact or creature card from your graveyard. Create a token that's a copy
    /// of it" copies a card, which has no copiable values worked out for it and is simply read as
    /// itself. It is a flag rather than something inferred from the target at resolution time
    /// because the two cases want opposite answers to the same question: a copy of a
    /// <em>permanent</em> that has since left the battlefield copies nothing, and a copy of a
    /// card is the one case where leaving is expected — the exile in the sentence before is what
    /// moved it.
    /// </remarks>
    bool CopiesACard = false) : IEffect
{
    /// <summary>What happens to each token later (CR 603.7b) - see
    /// <see cref="DelayedTokenAction"/>. Kiki-Jiki is the card this exists for.</summary>
    public DelayedTokenAction? Delayed { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var aimed = TargetIndex is { } index ? context.TargetAt(index) : null;

        var subject = Subject is { } named
            ? Subjects.Resolve(context, named, TargetIndex ?? 0)
            : TargetIndex is not null
            ? aimed is { Kind: TargetKind.Permanent or TargetKind.CardInGraveyard }
                ? aimed.Value.Subject
                : (ObjectId?)null
            : context.PhysicalSourceId;

        if (subject is not { } id)
            return [];

        // CR 608.2g: a copy of something that has already left uses last known information. That
        // is the ordinary case for "create a token that's a copy of that creature" on a death
        // trigger - by the time it resolves the creature is a card in a graveyard under a new id
        // (CR 400.7), so requiring it to still be on the battlefield made every such card do
        // nothing at all.
        var original = context.State.TryGetObject(id, out var present)
            ? present
            : context.ObjectBehind?.Invoke(id);

        if (original is null)
            return [];

        // A permanent that is still here has to be a permanent: "a copy of target creature" that
        // has since been exiled copies nothing, and only the trigger-subject reading and the
        // card reading are allowed to reach back for something that is not on the battlefield.
        if (!CopiesACard
            && Subject is not EffectSubject.TriggeringObject
            && original.Zone != Zone.Battlefield)
        {
            return [];
        }

        // CR 707.3: "a token that's a copy of target creature" copies what that permanent is
        // now, which is the copied card when something has already made it a copy of something
        // else. Only a permanent has copiable values worked out for it - a card in a graveyard
        // is read as itself, which is also the rule (CR 707.2).
        var copiable = original.Zone == Zone.Battlefield
            ? Characteristics.CardOf(context.State, context.Abilities, original)
            : original.Card;

        // CR 707.9b: the exception changes the card the copy is made from, so it is applied
        // before the token is minted rather than to the permanent afterwards - what it modifies
        // becomes one of the copy's own copiable values.
        var copied = TokenCards.AsToken(Cards.GenerativeEffects.Excepting(copiable, Except));

        // An unset count means one, and a count that was given is used as it stands - "create X
        // tokens that are copies of it" with X of zero makes none, and flooring that at one
        // would be a card doing something it does not say. The same rule CreateToken follows.
        var count = Count.Equals(default(Amount)) ? 1 : Math.Max(0, Count.In(context));

        var made = new List<GameEvent>();

        foreach (var _ in Enumerable.Range(0, count))
        {
            var token = ObjectId.New();

            made.Add(new ObjectCreated(
                token, copied, context.ControllerId, context.ControllerId, Zone.Battlefield));

            if (Tapped)
                made.Add(new PermanentTapped(token));

            if (Delayed is { } later)
            {
                made.Add(new DelayedTriggerCreated(
                    Guid.NewGuid(),
                    context.ControllerId,
                    token,
                    later.Step,
                    later.EffectId,
                    context.State.TurnNumber));
            }
        }

        return made;
    }
}

/// <summary>
/// The same card, marked as a token (CR 111.7).
/// </summary>
/// <remarks>
/// Written out field by field because <see cref="Domain.Models.CardDefinition"/> is a class and
/// not a record, so there is no <c>with</c>. Making it a record for this one call would change
/// equality across the whole application from reference to structural, on a type that carries
/// several collections — a much larger change than the one being asked for.
/// <para>
/// The images come along deliberately: a token copy is shown on the board as the thing it copied,
/// and a copy with no art is a copy the player cannot recognise.
/// </para>
/// </remarks>
internal static class TokenCards
{
    /// <param name="power">
    /// A size to print on the token instead of the card's own, for the few effects that make a
    /// copy of a different size - offspring's 1/1 is the reason this exists.
    /// </param>
    /// <remarks>
    /// It does <em>not</em> read exception clauses. "Except it isn't legendary" used to drop the
    /// supertype here, which put half of CR 707.9b in the minting of the token and the other half
    /// in <see cref="Cards.GenerativeEffects.Excepting"/>; the caller applies the whole clause to
    /// the card first, and this only marks the result a token.
    /// </remarks>
    public static Domain.Models.CardDefinition AsToken(
        Domain.Models.CardDefinition card,
        int? power = null,
        int? toughness = null,
        string? oracleId = null) => new()
        {
            OracleId = oracleId ?? card.OracleId,
            Name = card.Name,
            ManaCost = card.ManaCost,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes | Domain.Enums.CardType.Token,
            Subtypes = card.Subtypes,
            Supertypes = card.Supertypes,
            OracleText = card.OracleText,
            Power = power ?? card.Power,
            Toughness = toughness ?? card.Toughness,
            StartingLoyalty = card.StartingLoyalty,
            Keywords = card.Keywords,
            ColorIdentity = card.ColorIdentity,
            Colors = card.Colors,

            // CR 707.8a: a token that is a copy of a double-faced permanent is itself
            // double-faced and can transform. Dropped, the token came back with one face and no
            // back, so nothing about it looked wrong and it could never turn over — the same
            // field, and the same silence, as the game log that was once caught losing it.
            Faces = card.Faces,
            ImageUriNormal = card.ImageUriNormal,
            ImageUriLarge = card.ImageUriLarge,
            ImageUriSmall = card.ImageUriSmall,
            ImageUriArtCrop = card.ImageUriArtCrop,
            ImageUriNormalBack = card.ImageUriNormalBack,
        };

    /// <summary>
    /// The same card carrying rules text something granted it, under an id of its own.
    /// </summary>
    /// <remarks>
    /// A token's abilities come from its <see cref="Domain.Models.CardDefinition"/>, and the pool
    /// compiles that definition's text - so text a card grants a token it creates has exactly one
    /// place to live, which is the definition. That is how the plain
    /// <c>create a 1/1 Rat token with "~ can't block"</c> has always worked; what had nowhere to
    /// go was a grant onto a token whose definition is somebody else's card, because
    /// <c>CompiledPool</c> refuses two cards sharing an id with different text and a copy keeps
    /// the copied card's id on purpose.
    /// <para>
    /// So the grant re-keys the definition. The id is the copied card's plus a stable hash of the
    /// granted text, which makes the amended card a card of its own for the pool while leaving
    /// the id of an ungranted copy exactly where it was. The hash is
    /// <see cref="Cards.EffectPhrase.StableHash"/> - the same arithmetic the minted tokens fold
    /// into their own ids - because this id reaches the event log and a log replayed in a later
    /// process has to name the same card.
    /// </para>
    /// <para>
    /// Empty text returns the card untouched rather than an id nobody asked for: a copy with no
    /// exception, and a token with nothing quoted after it, must still be the definition they
    /// were.
    /// </para>
    /// </remarks>
    public static Domain.Models.CardDefinition Granting(
        Domain.Models.CardDefinition card,
        string granted)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (string.IsNullOrWhiteSpace(granted))
            return card;

        return new Domain.Models.CardDefinition
        {
            OracleId = card.OracleId + "-granted-" + Cards.EffectPhrase.StableHash(granted),
            Name = card.Name,
            ManaCost = card.ManaCost,
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes,
            Subtypes = card.Subtypes,
            Supertypes = card.Supertypes,
            OracleText = card.OracleText.Length == 0
                ? granted
                : card.OracleText + "\n" + granted,
            Power = card.Power,
            Toughness = card.Toughness,
            StartingLoyalty = card.StartingLoyalty,
            Keywords = card.Keywords,
            ColorIdentity = card.ColorIdentity,
            Colors = card.Colors,

            // Kept for the same reason AsToken keeps them: a copy that lost its faces comes back
            // with one side and can never turn over, and nothing about it looks wrong.
            Faces = card.Faces,
            ImageUriNormal = card.ImageUriNormal,
            ImageUriLarge = card.ImageUriLarge,
            ImageUriSmall = card.ImageUriSmall,
            ImageUriArtCrop = card.ImageUriArtCrop,
            ImageUriNormalBack = card.ImageUriNormalBack,
        };
    }
}

/// <summary>
/// Exiles a target and remembers which permanent did it (CR 400.7).
/// </summary>
/// <remarks>
/// The first half of the "exile it until this leaves the battlefield" family. The link has to be
/// made here rather than by the returning half, because the card in exile is a new object with a
/// new id and only the effect that moved it knows both ends.
/// </remarks>
public sealed record ExileUntilSourceLeaves(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var exiled = ObjectId.New();

        return
        [
            new ObjectMoved(
                target.Subject, exiled, Zone.Battlefield, Zone.Exile,
                permanent.OwnerId, MoveCause.Exile),
            new ExiledUntilLeaves(exiled, context.PhysicalSourceId),
        ];
    }
}

/// <summary>
/// Returns everything this permanent exiled (CR 400.7).
/// </summary>
/// <remarks>
/// Cards come back to the battlefield under their owner's control, and they come back as new
/// objects with nothing they had before — no counters, no auras, no damage. Nothing here has to
/// arrange that: it is what a zone change means.
/// </remarks>
public sealed record ReturnExiledBySource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = context.PhysicalSourceId;

        return
        [
            .. context.State.Exile
                .Select(context.State.GetObject)
                .Where(card => card.ExiledBy == source)
                .Select(card => new ObjectMoved(
                    card.Id, ObjectId.New(), Zone.Exile, Zone.Battlefield,
                    card.OwnerId, MoveCause.Other)),
        ];
    }
}

/// <summary>
/// A player reveals their hand and somebody else takes a card from it (CR 701.16).
/// </summary>
/// <remarks>
/// Three printed sentences and one effect, because the middle one is the whole of it: revealing
/// is what makes the choice legal, and the discard is what the choice was for. Split into three
/// effects, the second would have nothing to choose from and the third nothing to discard.
/// </remarks>
public sealed record RevealAndTake(
    int TargetIndex,
    string FilterId,
    Zone Destination,
    int EffectIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Player } target
            || !context.State.Players.ContainsKey(target.Player))
        {
            return [];
        }

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        return
        [
            new HandChoiceRequested(
                context.ControllerId,
                target.Player,
                context.PhysicalSourceId,
                abilityId,
                EffectIndex,
                string.Equals(FilterId, SearchFilters.AnyCard, StringComparison.Ordinal)
                    ? "a card"
                    : $"a {FilterId} card",
                FilterId),
        ];
    }
}

/// <summary>
/// "If that creature would die this turn, exile it instead" (CR 614.1c).
/// </summary>
/// <remarks>
/// A replacement effect the spell leaves behind rather than one printed on a permanent, and it
/// needs no new state at all: a floating effect is already "this thing applies to these objects
/// until the end of this turn", and the layer engine skips a definition id it does not know. The
/// replacement side recognises the name instead.
/// <para>
/// It is worth the trouble because it is not the same as destroying: a creature exiled this way
/// never reaches the graveyard, so nothing that watches for a death sees one and nothing can
/// bring it back.
/// </para>
/// </remarks>
public sealed record ExileInsteadOfDying(int TargetIndex = 0) : IEffect
{
    /// <summary>The name the replacement side looks for.</summary>
    public const string FloatingId = "dies-to-exile";

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                FloatingId,
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Gives the controller speed 1 if they have none at all (CR 702.179a).
/// </summary>
/// <remarks>
/// Does nothing to a player who is already moving, at any speed. That is the whole of "Start
/// your engines!" — everything after it is the automatic increase, which is a rule of the game
/// rather than anything printed on a card.
/// </remarks>
public sealed record StartYourEngines : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State.GetPlayer(context.ControllerId).Speed == 0
            ? [new SpeedChanged(context.ControllerId, 1)]
            : [];
    }
}

/// <summary>
/// No combat damage is dealt for the rest of the turn (CR 615.1) — Fog.
/// </summary>
/// <remarks>
/// A shield over the whole combat rather than over one creature, so it affects nothing in
/// particular and carries an empty affected list. The combat damage step reads it and assigns
/// nothing at all, which is what prevention means: the damage is never dealt, so nothing that
/// watches for damage being dealt — lifelink, "whenever this deals damage" — sees anything.
/// <para>
/// Only combat damage. A card preventing <em>all</em> damage this turn would have to filter
/// every damage event in the game rather than one step, and is left unread.
/// </para>
/// </remarks>
public sealed record PreventAllCombatDamage : IEffect
{
    /// <summary>The name the combat damage step looks for.</summary>
    public const string FloatingId = "fog";

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(), FloatingId, [], context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Two creatures each deal damage equal to their power to the other (CR 701.12a).
/// </summary>
/// <remarks>
/// Both halves are worked out before either is dealt, because the damage is simultaneous: a
/// creature that dies to the fight still dealt its own. Reading the second power after applying
/// the first would make the loser hit for less, which is the classic way to get this wrong.
/// <para>
/// CR 701.12b: if either has left the battlefield by the time this resolves, neither deals or is
/// dealt damage — a fight needs two fighters.
/// </para>
/// </remarks>
public sealed record Fight(
    int TheirIndex,
    int? MyIndex = null,
    bool BothWays = true,
    EffectSubject MySubject = EffectSubject.Source) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Who deals it. A target index when the sentence named one, and otherwise whatever the
        // pronoun resolved to - which defaults to the source and so leaves every "~ fights" card
        // reading exactly as it did. It could not be anything else before, and half a fight
        // written as "target creature you control gets +1/+0. It deals damage equal to its power
        // to target creature you don't control" then had the *instant* deal it: a spell is not on
        // the battlefield, so the whole effect returned nothing and Ambuscade did nothing at all.
        var mine = MyIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Permanent } chosen
                ? chosen.Subject
                : (ObjectId?)null
            : Subjects.Resolve(context, MySubject, 0);

        if (mine is not { } myId
            || context.TargetAt(TheirIndex) is not { Kind: TargetKind.Permanent } theirs
            || !context.State.TryGetObject(myId, out var me)
            || !context.State.TryGetObject(theirs.Subject, out var them)
            || me.Zone != Zone.Battlefield
            || them.Zone != Zone.Battlefield)
        {
            return [];
        }

        var mineNow = Characteristics.Of(context.State, context.Abilities, me);
        var theirsNow = Characteristics.Of(context.State, context.Abilities, them);
        var myPower = mineNow.Power ?? 0;
        var theirPower = theirsNow.Power ?? 0;

        var events = new List<GameEvent>();

        // The source is named on each half so lifelink and wither can see who dealt it — a fight
        // is ordinary damage from one creature to another (CR 701.12a).
        //
        // Deathtouch is not carried by the source, though: it rides on the damage event, because
        // CR 704.5h remembers "was dealt damage by a deathtouch source" separately from how much.
        // Naming the source and leaving the flag off meant a fight with a deathtouch creature was
        // ordinary damage, so a 1/1 assassin fighting a 6/6 did one damage and nothing else.
        if (myPower > 0)
        {
            events.Add(new DamageMarked(
                theirs.Subject, myPower, mineNow.Has(KeywordAbility.Deathtouch), myId));
        }

        // "Deals damage equal to its power to target creature" is a fight with one half: the
        // damage goes one way and nothing comes back. Everything else about it is the same
        // question - whose power, computed when it resolves, from a source named so deathtouch
        // and lifelink still see who dealt it - which is why it is a flag here and not a second
        // effect that would have to get all of that right again.
        if (BothWays && theirPower > 0)
        {
            events.Add(new DamageMarked(
                myId, theirPower, theirsNow.Has(KeywordAbility.Deathtouch), theirs.Subject));
        }

        return events;
    }
}

/// <summary>
/// Exiles every card in a player's graveyard (CR 701.13a).
/// </summary>
/// <remarks>
/// One effect for the whole zone rather than a card at a time, because the cards are not chosen
/// and not targeted — the graveyard is, and everything in it goes. A player whose graveyard is
/// empty is not an error; there is simply nothing to move.
/// </remarks>
public sealed record ExileGraveyard(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Player } target
            || !context.State.Players.ContainsKey(target.Player))
        {
            return [];
        }

        return
        [
            .. context.State.GetPlayer(target.Player).Graveyard.Select(card => new ObjectMoved(
                card, ObjectId.New(), Zone.Graveyard, Zone.Exile, target.Player, MoveCause.Exile)),
        ];
    }
}

/// <summary>
/// Cascade: exile until something cheaper turns up, and offer it (CR 702.85a).
/// </summary>
/// <remarks>
/// Deferred like every other question, and for one more reason than usual: the cards that are not
/// taken go to the bottom of the library <em>in a random order</em>, and randomness lives on the
/// game so that every random outcome in a match comes from one seeded source and lands in the log.
/// </remarks>
public sealed record Cascade : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // CR 702.85a: "lesser mana value" means less than the spell that cascaded, which is the
        // object this ability belongs to — still on the stack, underneath this trigger.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var spell))
            return [];

        return
        [
            new CascadeRequested(context.ControllerId, context.PhysicalSourceId, spell.Card.Cmc),
        ];
    }
}

/// <summary>
/// Offers to show the top N cards and cast the ones sharing this spell's name (CR 702.60a).
/// </summary>
/// <remarks>
/// Cascade's shape with the search replaced by a name match, and like cascade it records that the
/// question is owed rather than asking it: an effect returns events, and a decision halts the
/// whole game. What name to match is read from the spell underneath rather than carried here, so
/// one definition serves every card that has the keyword.
/// </remarks>
public sealed record Ripple(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Math.Max(0, Count.In(context));
        if (many == 0)
            return [];

        return [new RippleRequested(context.ControllerId, context.PhysicalSourceId, many)];
    }
}

/// <summary>Puts counters on a target permanent (CR 121.2).</summary>
public sealed record PutCounters(
    string Kind,
    Amount Count,
    int TargetIndex = 0,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } on)
            return [];

        // Counters live on permanents, and the engine says so: its reducer refuses a counter on
        // anything that is not on the battlefield. So an object that has left has to be checked
        // for here, not discovered there - "whenever a creature you control dies, put a +1/+1
        // counter on it" resolves with "it" meaning the card now in the graveyard (CR 400.7), and
        // that threw rather than doing nothing. Seven hundred and forty-six compiled effects were
        // one dies-trigger away from the same crash.
        if (!context.State.TryGetObject(on, out var subject) || subject.Zone != Zone.Battlefield)
            return [];

        return [new CountersChanged(on, Kind, Count.In(context))];
    }
}

/// <summary>
/// "Double the number of +1/+1 counters on target creature" (CR 121.3).
/// </summary>
/// <remarks>
/// Doubling is putting on as many as are already there, so it is one <see cref="CountersChanged"/>
/// per kind and needs no event of its own. It cannot be written as a <see cref="PutCounters"/>
/// with a counted <see cref="Amount"/>, and the reason is the un-named form: "double the number of
/// <em>each kind</em> of counter" is one instruction over a set the compiler cannot know, because
/// which kinds a permanent carries is a fact of the board at the moment it resolves.
/// <para>
/// The count is read off the permanent rather than off the computed characteristics, for the
/// reason every other counter reader here does: a counter is a thing sitting on an object
/// (CR 122.1), not a characteristic, and nothing in CR 613 puts one there.
/// </para>
/// <para>
/// A permanent with none of the named kind gets nothing rather than one - doubling zero is zero,
/// and the ordinary reading of "double" would otherwise quietly become "put one on".
/// </para>
/// </remarks>
public sealed record DoubleCounters(
    string? Kind = null,
    int TargetIndex = 0,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } on)
            return [];

        // Counters live on permanents and the reducer refuses one anywhere else, so an object
        // that has left the battlefield since the ability went on the stack is checked for here
        // rather than discovered there - the same guard PutCounters carries, for the same reason.
        if (!context.State.TryGetObject(on, out var subject)
            || subject.Zone != Zone.Battlefield
            || subject.Permanent is not { } permanent)
        {
            return [];
        }

        // Ordered, because the log is replayed and two events differing only in order would
        // make a game that does not fold back to itself.
        var doubled = new List<GameEvent>();

        foreach (var held in permanent.Counters.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (held.Value <= 0)
                continue;

            if (Kind is { } named && !string.Equals(held.Key, named, StringComparison.Ordinal))
                continue;

            doubled.Add(new CountersChanged(on, held.Key, held.Value));
        }

        return doubled;
    }
}

/// <summary>
/// Which object a sentence is about, for the effects that can be about more than one thing.
/// </summary>
/// <remarks>
/// One reader rather than a copy inside each effect. The order of preference is the whole
/// content of the rule — a pronoun means the target if the sentence named one, otherwise
/// whatever the trigger was about, otherwise the permanent with the ability — and a second copy
/// of it is a second chance to get that order wrong.
/// </remarks>
internal static class Subjects
{
    public static ObjectId? Resolve(
        ResolutionContext context, EffectSubject subject, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(context);

        return subject switch
        {
            EffectSubject.Target =>
                context.TargetAt(targetIndex) is { Kind: TargetKind.Permanent } target
                    ? target.Subject
                    : null,

            EffectSubject.AttachedHost => Attachment.HostOf(context),

            // "It" with no target before it means whatever the trigger was about. When the
            // trigger was about no object at all, an attached permanent means its host before it
            // means itself: "at the beginning of the upkeep of enchanted creature's controller,
            // put a -1/-1 counter on it" is about the creature, and an Aura that read it as
            // itself put the counter on the Aura. Only then does it fall back to the permanent
            // with the ability, which is what a card that is on nothing must mean.
            EffectSubject.TriggerSubject =>
                context.SubjectObject
                ?? Attachment.HostOf(context)
                ?? context.PhysicalSourceId,

            // No fallback, deliberately. If the event was about no object then "that creature"
            // names nothing, and doing nothing is the honest answer where guessing is not.
            EffectSubject.TriggeringObject => context.SubjectObject,

            _ => context.PhysicalSourceId,
        };
    }
}

/// <summary>
/// A pump whose size is not known until it resolves — the "+X/+X" of a card (CR 613.4c).
/// </summary>
/// <remarks>
/// Every ordinary pump <em>names</em> a continuous effect that was written when the card
/// compiled, because the size is printed on the card and the id literally contains the numbers.
/// "+X/+X" has no size until X does, so the id is built as the ability resolves and the layer
/// machinery receives a definition like any other — one that did not exist a moment earlier.
/// <para>
/// One record shared by every pump rather than a variable twin of each. The pump effects differ
/// only in <em>which</em> permanents they find — the source, a pronoun, a target, a group, an
/// Aura's host — and the size is the same question in all of them. That was the defect this
/// closed: "target creature gets +X/+X" read while "~ gets +X/+X", "it gets +X/+X" and
/// "creatures you control get +X/+X" did not, because only the targeted reader had ever been
/// taught the variable spelling, and the other four wrote the size out again with a digit in it.
/// </para>
/// <para>
/// Power and toughness are two amounts rather than one, because the cards separate them: "+X/+0"
/// is as common as "+X/+X", and reading the same X twice is a coincidence of wording.
/// </para>
/// </remarks>
public sealed record VariablePumpSize(Amount Power, Amount Toughness)
{
    /// <summary>The definition id this size comes to, for the resolution asking.</summary>
    public string DefinitionIdIn(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Cards.GenerativeEffects.PumpId(Power.In(context), Toughness.In(context));
    }
}

/// <summary>
/// Gives a target creature a bonus until end of turn — the pump effect (CR 611.2).
/// </summary>
/// <remarks>
/// Creates a continuous effect rather than editing the creature, so it applies in layer 7c and
/// ends during cleanup (CR 514.2) without anything having to remember to take it off.
/// </remarks>
public sealed record PumpUntilEndOfTurn(
    string DefinitionId,
    int TargetIndex = 0,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    /// <summary>
    /// Whether the bonus lasts "until your next turn" rather than until end of turn (CR 611.2b).
    /// </summary>
    /// <remarks>
    /// The same effect with a longer duration, so it is a flag rather than a second record: what
    /// differs is one field on the event, and a parallel type would have been a second place for
    /// the layer lookup and the subject resolution to be got wrong.
    /// </remarks>
    public bool UntilYourNextTurn { get; init; }

    /// <summary>
    /// Whether the effect ends at cleanup, or lasts for as long as the game does (CR 611.2).
    /// </summary>
    /// <remarks>
    /// False is for the few effects that change a permanent and say nothing about when they
    /// stop: awaken stands a land up as a creature and it stays one. The duration lives on the
    /// effect rather than in the definition id because the id names <em>what</em> the change is,
    /// and the same change can be temporary on one card and permanent on another. It is
    /// exclusive with <see cref="UntilYourNextTurn"/> - a card prints one duration or none.
    /// </summary>
    public bool ForTheTurn { get; init; } = true;

    /// <summary>The size, when the card wrote it as X rather than a number (CR 613.4c).</summary>
    /// <remarks>
    /// Null on every pump whose size was printed, which is nearly all of them: the id already
    /// carries the numbers. When it is set the id is built from it as the effect resolves, and
    /// <see cref="DefinitionId"/> is a placeholder nothing reads.
    /// </remarks>
    public VariablePumpSize? Size { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Through the shared subject resolver, like every other effect that can be aimed at
        // something other than a target. "That creature gets +1/+1" names the creature the
        // trigger was about, which is neither a target nor the permanent with the ability -
        // exalted is the card that shows the difference, because the creature attacking alone is
        // very often not the one with exalted on it.
        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } subject)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Size?.DefinitionIdIn(context) ?? DefinitionId,
                [subject],
                UntilYourNextTurn || !ForTheTurn ? null : context.State.TurnNumber)
            {
                UntilTurnOf = UntilYourNextTurn ? context.ControllerId : null,
            },
        ];
    }
}

/// <summary>
/// Gives the target's soulbond partner a bonus too, if it has one (CR 702.95b).
/// </summary>
/// <remarks>
/// "If it's paired with a creature, that creature also gets +2/+2 until end of turn" — the
/// pairing consulted from a resolving spell rather than from a static. The partner is read off
/// the status when the effect resolves: it is not a second target, so nothing about it was
/// chosen and hexproof on it does not apply. No partner means no event, which is the sentence's
/// own "if".
/// </remarks>
public sealed record PumpPairedPartner(string DefinitionId, int TargetIndex) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, EffectSubject.Target, TargetIndex) is not { } aimed
            || !context.State.TryGetObject(aimed, out var target)
            || context.State.PairedPartnerOf(target) is not { } partner)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(), DefinitionId, [partner.Id], context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Makes a permanent become a copy of a target permanent (CR 613.2a, 707.2).
/// </summary>
/// <remarks>
/// The one continuous effect whose name cannot be worked out until it resolves. Every other
/// generated effect in this engine is a family with two numbers in it — <c>pump:+3/+3</c> — and
/// the compiler can write the name down while reading the card. A copy's name carries the whole
/// copied card, and which card that is depends on what is on the battlefield at the moment the
/// ability resolves, so the name is built here.
/// <para>
/// CR 707.3 is why it reads the copiable values rather than the printed card: a permanent that
/// has already become a copy of something else is copied as the thing it became.
/// </para>
/// <para>
/// CR 707.2b fixes those values now. The effect that lands in the log holds the card itself, so
/// the permanent that was copied may leave, die or change into something else without the copy
/// noticing — which is what the rule says and what an id pointing at an object could not do.
/// </para>
/// </remarks>
/// <param name="TargetIndex">Which target names the permanent whose values are copied.</param>
/// <param name="UntilEndOfTurn">
/// Whether the copy wears off (CR 514.2). False is a permanent change: "becomes a copy" with no
/// duration is what that permanent now is, and nothing has to take it back off again — the effect
/// is a layer rather than something written into the object.
/// </param>
/// <param name="Subject">
/// Which permanent becomes the copy. The source is the printed form on every card in the corpus
/// that says this — "{2}: This artifact becomes a copy of target artifact until end of turn" —
/// and the shared resolver is what lets a target or a trigger's subject be named instead without
/// this effect learning how.
/// </param>
public sealed record BecomeCopyOfTarget(
    int TargetIndex = 0,
    bool UntilEndOfTurn = true,
    EffectSubject Subject = EffectSubject.Source) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } me)
            return [];

        // CR 608.2b: a target that is no longer there fails to determine the information the
        // effect needs, and the effect does not happen. A copy of nothing would be a permanent
        // with no name at all.
        if (context.PeerAt(TargetIndex) is not { Zone: Zone.Battlefield } original)
            return [];

        var copied = Characteristics.CardOf(context.State, context.Abilities, original);

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.CopyId(copied),
                [me],
                UntilEndOfTurn ? context.State.TurnNumber : null),
        ];
    }
}

/// <summary>
/// Gives the source itself +N/+N until end of turn (CR 613.4).
/// </summary>
/// <remarks>
/// The same effect as <see cref="PumpUntilEndOfTurn"/> with nothing to aim it at: "~ gets +2/+2
/// until end of turn" is a firebreathing ability on the creature that has it, and a targeted
/// pump would ask the player to choose the only legal answer.
/// </remarks>
public sealed record PumpSourceUntilEndOfTurn(string DefinitionId) : IEffect
{
    /// <summary>The size, when the card wrote it as X rather than a number (CR 613.4c).</summary>
    public VariablePumpSize? Size { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The ability is on the stack; what it pumps is the permanent that produced it.
        var subject = context.PhysicalSourceId;

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Size?.DefinitionIdIn(context) ?? DefinitionId,
                [subject],
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Pumps whatever the source is attached to, until end of turn (CR 701.3c).
/// </summary>
/// <remarks>
/// The activated twin of "enchanted creature gets +2/+2", which is a static. An Aura that can
/// pump on demand names no target - "enchanted creature" is whatever it is already on, and an
/// Aura attached to nothing is on its way to the graveyard anyway (CR 704.5m).
/// </remarks>
public sealed record PumpHostUntilEndOfTurn(string DefinitionId) : IEffect
{
    /// <summary>The size, when the card wrote it as X rather than a number (CR 613.4c).</summary>
    public VariablePumpSize? Size { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var aura)
            || aura.Permanent?.AttachedTo is not { } host)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(), DefinitionId, [host], context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Attaches the source permanent to a target permanent (CR 701.3).
/// </summary>
/// <remarks>
/// What an Equipment's equip ability does. The source is the permanent whose ability this is,
/// not the ability on the stack — the same distinction <see cref="PumpSourceUntilEndOfTurn"/>
/// makes, and for the same reason.
/// </remarks>
/// <summary>
/// Creates a token and attaches the source to it - job select (CR 702.182a).
/// </summary>
/// <remarks>
/// One effect rather than two, because the second half has to name the thing the first half
/// made and the subject vocabulary has no word for that: an effect returns events and the next
/// effect in the ability never sees them. Generating the id here is what lets the attachment be
/// written at all, and it is the same shape any "create a token, then attach this to it" line
/// needs.
/// </remarks>
public sealed record CreateTokenAndAttachSource(Domain.Models.CardDefinition Token) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The Equipment has to be on the battlefield to be attached to anything, and an enter
        // trigger whose source has already left attaches nothing rather than attaching a card
        // in a graveyard to a token.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var equipment)
            || equipment.Zone != Zone.Battlefield)
        {
            return [];
        }

        var token = ObjectId.New();

        return
        [
            new ObjectCreated(
                token, Token, context.ControllerId, context.ControllerId, Zone.Battlefield),
            new PermanentAttached(equipment.Id, token),
        ];
    }
}

/// <summary>Unattaches the source from whatever it is on - reconfigure (CR 702.151a).</summary>
/// <remarks>
/// The same event the attachment uses, with nothing to attach to. Held as its own effect rather
/// than an <see cref="AttachSourceTo"/> with no target, because "no target" already means "the
/// target was illegal and nothing happens" everywhere else.
/// </remarks>
public sealed record UnattachSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var equipment)
            || equipment.Permanent?.AttachedTo is null)
        {
            return [];
        }

        return [new PermanentAttached(equipment.Id, null)];
    }
}

public sealed record AttachSourceTo(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        var equipment = context.PhysicalSourceId;

        return [new PermanentAttached(equipment, target.Subject)];
    }
}

/// <summary>
/// Ends a continuous effect an earlier ability of this same permanent created (CR 611.2).
/// </summary>
/// <remarks>
/// The reverse half of an ability that changes its own permanent and offers to change it back —
/// "you may pay {W} to end this effect" on the Licids. It names the effect the way everything
/// else in this engine names one, by its <see cref="Cards.GenerativeEffects"/> id, and ends every
/// floating effect with that name that is applying to this permanent.
/// <para>
/// It has to match on the source as well as the name, or a second Licid paying to come back would
/// end the first one's effect too — they share a definition, because what the effect does is the
/// same on every card that prints the line. Matching on the pair is what keeps one player's
/// payment from undoing another's.
/// </para>
/// <para>
/// Nothing here says the effect is currently running: an ability that resolves with nothing to
/// end simply ends nothing, which is the ordinary CR 608.2 outcome and not a refusal. Whether the
/// ability may be <em>activated</em> at all is <see cref="ActivatedAbilityDefinition
/// .ActivateOnlyIf"/>'s question and is asked before any cost is paid (CR 602.5b).
/// </para>
/// </remarks>
public sealed record EndSourceEffect(string DefinitionId) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = context.PhysicalSourceId;

        return
        [
            .. context.State.FloatingEffects
                .Where(f => string.Equals(f.DefinitionId, DefinitionId, StringComparison.Ordinal)
                    && f.AffectedIds.Contains(source))
                .Select(f => new ContinuousEffectEnded(f.Id)),
        ];
    }
}

// "Creatures you control get +N/+N until end of turn" was compiled to a PumpCreaturesYouControl
// by a reader that nothing could reach - EffectPhrase.MassPumpLine claims the sentence first and
// builds a PumpGroup over a parsed spec. The effect went with its only producer, and it was worth
// going: it selected its creatures by reading obj.ControllerId, which is where control *started*
// rather than where it is (CR 613.1b), so a creature stolen this turn would have been pumped by
// its old controller's spell. The reader that survives asks the group vocabulary, which asks
// Game.ControllerOf. A second implementation nothing runs is a second implementation nothing
// tests.

/// <summary>
/// Puts a regeneration shield on the source (CR 701.19).
/// </summary>
/// <remarks>
/// Nothing visible happens when this resolves. The shield waits, and is spent the next time the
/// permanent would be destroyed this turn — which is why "regenerate" reads as doing nothing
/// until something tries to kill it.
/// </remarks>
public sealed record Regenerate(EffectSubject Subject, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Through the shared resolver rather than a third copy of the order of answers. This
        // effect had its own switch over the same three subjects, which is how it came to be the
        // one that could not take a pronoun.
        return Subjects.Resolve(context, Subject, TargetIndex) is { } shielded
            ? [new RegenerationShieldsChanged(shielded, +1)]
            : [];
    }
}

/// <summary>What an effect is about, when the same effect can be about three things.</summary>
/// <remarks>
/// "Regenerate this creature", "regenerate target creature" and "regenerate enchanted creature"
/// are one action asked of three subjects. Naming the subject keeps them one effect: three
/// effects would be three places to get regeneration itself right.
/// </remarks>
public enum EffectSubject
{
    /// <summary>The permanent whose ability this is.</summary>
    Source,

    /// <summary>A target the ability chose.</summary>
    Target,

    /// <summary>What the source is attached to — an Aura's or Equipment's host (CR 701.3c).</summary>
    AttachedHost,

    /// <summary>
    /// The object the triggering event was about — what "that creature" refers to (CR 603.2).
    /// </summary>
    TriggerSubject,

    /// <summary>
    /// The object the triggering event was about, and nothing else (CR 603.2).
    /// </summary>
    /// <remarks>
    /// <see cref="TriggerSubject"/> with the fallbacks taken off, and the difference is the whole
    /// point. That one ends at the permanent with the ability when the event was about no object,
    /// which is right for "it" in a sentence that could only mean this card - and catastrophic for
    /// "that creature", which then means the wrong creature rather than none. An earlier attempt at
    /// reading "that creature" as the trigger's subject was reverted for exactly that: "whenever ~
    /// blocks a creature, destroy that creature" destroyed the blocker.
    /// <para>
    /// So this resolves to the subject or to nothing, and a sentence using it is only compiled
    /// when the trigger is known to supply one. Both halves are required; either alone is the bug.
    /// </para>
    /// </remarks>
    TriggeringObject,
}

/// <summary>What an Aura or Equipment is attached to (CR 701.3c).</summary>
/// <remarks>
/// Shared, because more than one effect needs it and every one of them needs the same two
/// answers: the permanent it is on, or nothing at all when it is on nothing — which is not an
/// error, it is an Aura on its way to the graveyard (CR 704.5m).
/// </remarks>
public static class Attachment
{
    public static ObjectId? HostOf(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State.TryGetObject(context.PhysicalSourceId, out var attached)
            ? attached.Permanent?.AttachedTo
            : null;
    }
}

/// <summary>Scry N — look at the top N and put any of them on the bottom (CR 701.22).</summary>
public sealed record Scry(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new LookAtTopRequested(context.ControllerId, Count.In(context), ToGraveyard: false)];
    }
}

/// <summary>Surveil N — the same, but the unwanted cards are milled (CR 701.25).</summary>
public sealed record Surveil(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new LookAtTopRequested(context.ControllerId, Count.In(context), ToGraveyard: true)];
    }
}

/// <summary>Puts counters on the source itself (CR 122.1).</summary>
/// <remarks>
/// Renown, and every "put a +1/+1 counter on this creature" trigger. It aims at the permanent
/// that produced the ability rather than at a target, for the same reason the source pump does.
/// </remarks>
public sealed record PutCountersOnSource(string Kind, Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var target) || target.Permanent is null)
            return [];

        return [new CountersChanged(subject, Kind, Count.In(context))];
    }
}

/// <summary>
/// Removes a time counter, and sacrifices the permanent when it was the last (CR 702.63a).
/// </summary>
/// <remarks>
/// Vanishing. The rule makes the sacrifice its own triggered ability, watching for the last
/// counter to leave; here the removal and the sacrifice happen in one resolution, so nobody gets
/// a window in between. The difference is visible only to a card that wants to respond to the
/// counter leaving, which is the same deviation evoke's sacrifice carries.
/// <para>
/// A permanent that has somehow lost all its counters already is left alone rather than
/// sacrificed: the ability removes a counter and only then asks whether that was the last, and
/// there was none to remove.
/// </para>
/// </remarks>
public sealed record TickVanishing : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var permanent)
            || permanent.Permanent is not { } onBoard)
        {
            return [];
        }

        var left = onBoard.Counters.TryGetValue(CounterKinds.Time, out var many) ? many : 0;
        if (left <= 0)
            return [];

        var removed = new CountersChanged(subject, CounterKinds.Time, -1);

        return left > 1
            ? [removed]
            :
            [
                removed,
                new ObjectMoved(
                    subject, ObjectId.New(), Zone.Battlefield, Zone.Graveyard,
                    permanent.ControllerId, MoveCause.Sacrifice),
            ];
    }
}

/// <summary>
/// A +1/+1 counter, but only if the attack was on whoever is ahead (CR 702.105a) - dethrone.
/// </summary>
/// <remarks>
/// The condition is checked here rather than in the trigger because "the player with the most
/// life" is a fact about the board and the trigger grammar matches events. That makes it an
/// intervening-if in the wrong place: the real ability checks as it triggers and again as it
/// resolves (CR 603.4), and this checks only the second time, so a player who falls behind
/// between the declaration and the resolution stops the counter where the card would not.
/// <para>
/// Tied for most life counts, which is what the rule says and the opposite of what "the player
/// with the most life" reads like.
/// </para>
/// </remarks>
public sealed record DethroneCounter : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var attacker) || attacker.Permanent is null)
            return [];

        if (!context.State.Combat.Attackers.TryGetValue(subject, out var attacking))
            return [];

        if (!context.State.Players.ContainsKey(attacking.DefendingPlayer))
            return [];

        var most = context.State.TurnOrder
            .Select(id => context.State.GetPlayer(id))
            .Where(player => !player.HasLost)
            .Max(player => player.Life);

        if (context.State.GetPlayer(attacking.DefendingPlayer).Life < most)
            return [];

        return [new CountersChanged(subject, CounterKinds.PlusOnePlusOne, 1)];
    }
}

/// <summary>
/// Returns the source from its graveyard to the battlefield, with counters (CR 702.92a).
/// </summary>
/// <remarks>
/// Undying and persist, and the sentence the two keywords are shorthand for — "when this creature
/// dies, return it to the battlefield tapped under its owner's control". The trigger fires as the
/// creature dies, so by the time the ability resolves the card is already in the graveyard and has
/// a new identity there (CR 400.7) — which is why this finds it by the id the move produced rather
/// than by the id that died.
/// <para>
/// The counter is emitted only when there is one. The keywords always bring one and always did;
/// the printed sentence usually brings none, and a <c>CountersChanged</c> of zero is an event
/// saying nothing happened, which a log should not have to carry or replay.
/// </para>
/// </remarks>
public sealed record ReturnSourceFromGraveyard(string CounterKind, Amount Counters) : IEffect
{
    /// <summary>Whether it comes back tapped.</summary>
    /// <remarks>
    /// Emitted as the arrival's own second event, which is the pair the enters-tapped replacement
    /// already makes (<c>[moved, tapped]</c>) — one shape for "this permanent arrives tapped", so
    /// the two can never come to mean different things.
    /// </remarks>
    public bool Tapped { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The card is not where the ability says it is. A dies trigger fires from the
        // battlefield, and by the time it resolves the card has moved to the graveyard and taken
        // a new identity with it (CR 400.7) — so the id the ability remembers no longer names
        // anything. It is found again by what it is: the same card, in its owner's graveyard.
        if (!context.State.TryGetObject(context.SourceId, out var onStack))
            return [];

        var printed = onStack.Card;
        var owner = onStack.OwnerId;

        var found = context.State.GetPlayer(owner).Graveyard
            .Select(context.State.GetObject)
            .LastOrDefault(o => string.Equals(
                o.Card.OracleId, printed.OracleId, StringComparison.Ordinal));

        if (found is null)
            return [];

        var sourceId = found.Id;
        var arriving = ObjectId.New();

        var events = new List<GameEvent>
        {
            new ObjectMoved(
                sourceId, arriving, Zone.Graveyard, Zone.Battlefield,
                owner, MoveCause.Return),
        };

        if (Tapped)
            events.Add(new PermanentTapped(arriving));

        if (Counters.In(context) is var many && many != 0)
            events.Add(new CountersChanged(arriving, CounterKind, many));

        return events;
    }
}

/// <summary>
/// "The next N damage that would be dealt to ~ this turn is dealt to target creature you control
/// instead" (CR 614.1b).
/// </summary>
/// <remarks>
/// Redirection, not prevention: the damage still happens and something else takes it. The shield
/// beside this one would have the wrong effect entirely - a creature that was meant to soak a
/// blow instead watches it vanish.
/// </remarks>
public sealed record RedirectDamage(Amount Amount, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } destination
            || !context.State.TryGetObject(destination.Subject, out _)
            || !context.State.TryGetObject(context.PhysicalSourceId, out var from)
            || from.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new RedirectionChanged(
                from.Id, Math.Max(0, Amount.In(context)), destination.Subject),
        ];
    }
}

/// <summary>Prevents the next N damage to a target this turn (CR 615.1).</summary>
public sealed record PreventDamage(
    Amount Amount, int? TargetIndex = null, PlayerScope? Scope = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // "Dealt to you" names nobody to choose, so it is a scope rather than a target - the
        // same distinction every other effect here draws between the two.
        if (Scope is { } scope)
        {
            return
            [
                .. PlayerScopes.Resolve(scope, context)
                    .Select(id => new PlayerPreventionChanged(id, Amount.In(context))),
            ];
        }

        if (TargetIndex is not { } index || context.TargetAt(index) is not { } target)
            return [];

        // "Any target" is a permanent or a player, and the shield goes wherever it was aimed.
        // Only the permanent arm existed, so the spell resolved and left nothing behind whenever
        // a player was chosen.
        return target.Kind switch
        {
            TargetKind.Permanent => [new PreventionChanged(target.Subject, Amount.In(context))],
            TargetKind.Player => [new PlayerPreventionChanged(target.Player, Amount.In(context))],
            _ => [],
        };
    }
}

/// <summary>
/// Prevents damage to or from a described set of things (CR 615.1, 615.3).
/// </summary>
/// <remarks>
/// The other half of <see cref="PreventDamage"/>, and the half every prevention line that is not
/// "prevent the next N damage to target X" needs. Two things separate them:
/// <list type="bullet">
/// <item>
/// It shields by <em>description</em> rather than by target. "Prevent all damage that would be
/// dealt to creatures you control" names no target, so a countdown shield — which is created per
/// target — had nowhere at all to be put.
/// </item>
/// <item>
/// It can ask about the source. "Prevent all combat damage that would be dealt by creatures this
/// turn" is a question about who is dealing, and a shield sitting on the thing being hit cannot
/// answer it (CR 609.7).
/// </item>
/// </list>
/// <para>
/// <see cref="Amount"/> is CR 615.10's number, not CR 615.7's: it caps each damage event
/// separately and is never spent. Null means all of it, which is what the great majority print
/// and what the existing reader was faking with a shield of a million points.
/// </para>
/// </remarks>
public sealed record PreventDescribedDamage : IEffect
{
    /// <summary>The most prevented from any one damage event, or null for all of it.</summary>
    public int? Amount { get; init; }

    /// <summary>Whether it watches all damage, combat damage, or noncombat damage.</summary>
    public State.DamageKind Kind { get; init; }

    /// <summary>A chosen permanent or player it shields, for the targeted wordings.</summary>
    public int? TargetIndex { get; init; }

    /// <summary>
    /// Whether <see cref="TargetIndex"/> names the damage's <em>source</em> rather than its
    /// victim — "prevent all combat damage that would be dealt by target creature this turn".
    /// </summary>
    /// <remarks>
    /// One flag rather than a second index, and deliberately: <see cref="EffectTargets"/> keys
    /// on the one property an effect carries, and a second slot would be a second thing for the
    /// shifter and the invariant test to remember. A sentence never names both — it shields what
    /// a creature deals or what reaches it, and the eleven cards printing the first say nothing
    /// about the second.
    /// <para>
    /// A player is refused in this slot. CR 609.7a lets a source be a permanent or a spell and
    /// never a player, so a target that turns out to be one leaves the shield unmade rather than
    /// unbounded.
    /// </para>
    /// </remarks>
    public bool TargetIsSource { get; init; }

    /// <summary>Permanents answering this filter, in the shared vocabulary.</summary>
    public string? PermanentFilter { get; init; }

    /// <summary>Whose permanents those are, or null for anyone's.</summary>
    public PlayerScope? PermanentController { get; init; }

    /// <summary>A described set of players — "dealt to you", "dealt to players".</summary>
    public PlayerScope? Players { get; init; }

    /// <summary>A filter the damage's source has to answer — "dealt by creatures".</summary>
    public string? SourceFilter { get; init; }

    /// <summary>Whose sources those are, or null for anyone's.</summary>
    public PlayerScope? SourceController { get; init; }

    /// <summary>
    /// What the permanents it shields have to be doing in combat — "to attacking creatures".
    /// </summary>
    /// <remarks>
    /// Beside <see cref="PermanentFilter"/> rather than inside it, because a filter is a
    /// <see cref="SearchFilters"/> id asked of a printed card and no card says whether it is
    /// attacking. Both are carried through to the shield unchanged; the predicate that reads
    /// them is one copy in <see cref="State.Preventions"/>, shared with the static spelling.
    /// </remarks>
    public State.CombatRole? PermanentCombat { get; init; }

    /// <summary>
    /// What the damage's source has to be doing in combat — "by unblocked creatures".
    /// </summary>
    public State.CombatRole? SourceCombat { get; init; }

    /// <summary>
    /// Whether it ends as the turn does (CR 514.2), which is what "this turn" means.
    /// </summary>
    /// <remarks>
    /// False is for a prevention a permanent's static ability generates, which lasts as long as
    /// the permanent does rather than as long as the turn.
    /// </remarks>
    public bool ForTheTurn { get; init; } = true;

    /// <summary>
    /// Whether the source is named by the controller as this resolves — "a source of your
    /// choice" (CR 609.7b).
    /// </summary>
    /// <remarks>
    /// The fourth way a sentence names one source, and the only one whose answer is not in the
    /// sentence: a target is chosen as the spell is cast, "~" is the permanent that printed it,
    /// a filter describes a set, and this is a question. It is therefore the only one that cannot
    /// be answered inside <see cref="Resolve"/> — the effect emits
    /// <see cref="DamageSourceChoiceRequested"/> and the settle sweep asks, exactly as
    /// <see cref="AddChosenMana"/> does with a colour.
    /// <para>
    /// <strong>No answer means no shield.</strong> A shield with an empty
    /// <see cref="State.PreventionEffect.Source"/> prevents damage from <em>every</em> source,
    /// so the failure mode of dropping the question is not a card that does nothing — it is a
    /// card that fogs the table. That is why the request is the only thing this arm returns:
    /// there is no path from here to a created shield that has not been through an answer.
    /// </para>
    /// </remarks>
    public bool ChooseSource { get; init; }

    /// <summary>
    /// Whether the first damage this prevents spends it (CR 615.8).
    /// </summary>
    /// <remarks>
    /// "The next time a source of your choice would deal damage to you this turn" against
    /// "prevent all damage a source of your choice would deal this turn": the same shield around
    /// the same chosen object, and the difference is whether it survives its first use. Both are
    /// printed, on cards that are otherwise near-identical, so the flag is read from the sentence
    /// rather than implied by anything else about it.
    /// </remarks>
    public bool OnlyOnce { get; init; }

    /// <summary>
    /// Whether the object shielded is the permanent whose ability this is — "this creature".
    /// </summary>
    /// <remarks>
    /// The third way a sentence names a single object, beside a target and a description, and
    /// the one an <em>activated</em> ability uses: "{U}: Prevent all combat damage that would be
    /// dealt to and dealt by this creature this turn" names its own permanent and targets
    /// nothing. It is not <see cref="TargetIndex"/> with a reserved value, because a target is
    /// chosen, legality-checked twice and defeated by hexproof, and none of that is true of the
    /// permanent an ability is printed on.
    /// <para>
    /// It reads <see cref="ResolutionContext.PhysicalSourceId"/> rather than the resolving
    /// object: what resolves is the ability, and the shield goes round the creature.
    /// </para>
    /// </remarks>
    public bool AroundSource { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ObjectId? permanent = null;
        ObjectId? dealer = null;
        Guid? player = null;

        if (AroundSource)
        {
            // The same flag on both halves of a "to and dealt by" pair, told apart by
            // TargetIsSource exactly as a targeted pair is: one shield watches what reaches the
            // creature, the other what it deals.
            if (TargetIsSource)
                dealer = context.PhysicalSourceId;
            else
                permanent = context.PhysicalSourceId;
        }

        if (TargetIndex is { } index)
        {
            // A target that has gone leaves nothing to shield, and shielding everything instead
            // would be a strictly better card than the printed one.
            switch (context.TargetAt(index))
            {
                case { Kind: TargetKind.Permanent } aimedAtPermanent when TargetIsSource:
                    dealer = aimedAtPermanent.Subject;
                    break;
                case { Kind: TargetKind.Permanent } aimedAtPermanent:
                    permanent = aimedAtPermanent.Subject;
                    break;

                // CR 609.7a: a player is never a source of damage, so a shield told to watch one
                // watches nothing. Making it anyway would leave the source slot empty, which
                // means "any source" — the whole board fogged by a card that named one creature.
                case { Kind: TargetKind.Player } when TargetIsSource:
                    return [];
                case { Kind: TargetKind.Player } aimedAtPlayer:
                    player = aimedAtPlayer.Player;
                    break;
                default:
                    return [];
            }
        }

        var shield = new State.PreventionEffect
        {
            Id = Guid.NewGuid(),
            ControllerId = context.ControllerId,
            Amount = Amount,
            Kind = Kind,
            Permanent = permanent,
            PermanentFilter = PermanentFilter,
            PermanentController = PermanentController,
            PermanentCombat = PermanentCombat,
            Player = player,
            Players = Players,
            Source = dealer,
            SourceFilter = SourceFilter,
            SourceController = SourceController,
            SourceCombat = SourceCombat,
            UntilEndOfTurn = ForTheTurn ? context.State.TurnNumber : null,
            OnlyOnce = OnlyOnce,
        };

        if (!ChooseSource)
            return [new PreventionEffectCreated(shield)];

        var choices = SourcesToChooseFrom(context);

        // Nothing that answers the description is nothing to name, and CR 609.7b's shield is
        // built around a source that was named. No shield at all is the only safe answer: one
        // with an empty source slot means "any source", so the card that could not find a red
        // permanent to point at would fog the whole table instead.
        return choices.IsEmpty
            ? []
            : [new DamageSourceChoiceRequested(context.ControllerId, shield, choices)];
    }

    /// <summary>
    /// Every object that could be named as the source of damage (CR 609.7a).
    /// </summary>
    /// <remarks>
    /// "A source of damage is an object that dealt damage", and an object is on the battlefield
    /// or on the stack — a Circle of Protection held open until the burn spell is cast is the
    /// card, so the stack is not an optional half of this list. The resolving object itself is
    /// left out: it is on its way off the stack and naming it is naming nothing.
    /// <para>
    /// The properties are read off the printed card, as every other card-filter question at this
    /// level is, and the same deviation <see cref="State.Preventions.Covers"/> documents applies
    /// — an animated land is not offered to "a creature of your choice". Widening it here would
    /// need a second filter vocabulary over computed characteristics, and it would also have to
    /// be widened in <see cref="State.Preventions.Watches"/>, which rechecks the same properties
    /// when the damage arrives (CR 615.9). The two must agree, so neither moves alone.
    /// </para>
    /// </remarks>
    private ImmutableList<ObjectId> SourcesToChooseFrom(ResolutionContext context)
    {
        var found = ImmutableList.CreateBuilder<ObjectId>();

        foreach (var id in context.State.Battlefield.Concat(context.State.Stack))
        {
            if (id == context.SourceId || !context.State.TryGetObject(id, out var candidate))
                continue;

            if (SourceFilter is { } filter && !SearchFilters.Matches(filter, candidate.Card))
                continue;

            // The menu and the shield ask the same questions, which is CR 615.9's whole point:
            // the properties are rechecked when the damage would happen, so a source that was
            // offered because it was attacking and has since left combat is not prevented. Two
            // lists built from different questions would disagree about that.
            if (SourceCombat is { } role
                && !State.Preventions.InCombatState(role, context.State, id))
            {
                continue;
            }

            if (SourceController is { } scope
                && !PlayerScopes.Around(scope, context.State, context.ControllerId)
                    .Contains(Characteristics.ControllerOf(
                        context.State, context.Abilities, candidate)))
            {
                continue;
            }

            found.Add(id);
        }

        return found.ToImmutable();
    }
}

/// <summary>
/// Says that some damage can't be prevented (CR 615.12).
/// </summary>
/// <remarks>
/// The exact inverse of <see cref="PreventDescribedDamage"/> and built on the same record, for
/// the reason the state field is: the two sentences describe the same set of damage events in
/// the same words. What CR 615.12 adds is what happens when both are true at once — every
/// applicable prevention effect is still applied, prevents nothing, and, the clause that is
/// invisible unless it is stated, <em>does not spend the shield</em>. The engine buys all three
/// by skipping the prevention arms rather than by zeroing their amounts.
/// <para>
/// Nothing in this engine prints a prevention with an additional effect attached ("prevent that
/// damage; if you do, draw a card"), so the half of CR 615.12 that keeps those riders running is
/// vacuous here. It is written down rather than left implied, because the day a rider exists the
/// skip above becomes wrong and this is the note that says so.
/// </para>
/// </remarks>
public sealed record BanDamagePrevention : IEffect
{
    /// <summary>Whether it covers all damage or combat damage only.</summary>
    public State.DamageKind Kind { get; init; }

    /// <summary>
    /// True for "the damage can't be prevented" — the damage this very spell deals.
    /// </summary>
    /// <remarks>
    /// CR 609.7a fixes a source when the effect is created, so this is the resolving object's id
    /// and not a description. A ban with the slot left empty covers <em>every</em> source at the
    /// table, which is a wholly different card: Combust would stop the opponent's fog as well as
    /// its own, and the coverage number cannot see the difference.
    /// </remarks>
    public bool BySource { get; init; }

    /// <summary>
    /// Whether it ends as the turn does (CR 514.2), which is what "this turn" means.
    /// </summary>
    /// <remarks>
    /// Always true for the sentences a spell resolves — a ban printed with no duration at all is
    /// a permanent's static ability, which is read off the battlefield rather than resolved.
    /// The spell-scoped form keeps the duration too: its source has left the stack by the time
    /// the turn ends, so the entry would never be reachable again in any case, and letting it
    /// outlive the turn would leave the log holding rows nothing can ever match.
    /// </remarks>
    public bool ForTheTurn { get; init; } = true;

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new UnpreventableDamageDeclared(new State.PreventionEffect
            {
                Id = Guid.NewGuid(),
                ControllerId = context.ControllerId,
                Kind = Kind,
                Source = BySource ? context.PhysicalSourceId : null,
                UntilEndOfTurn = ForTheTurn ? context.State.TurnNumber : null,
            }),
        ];
    }
}

/// <summary>Stops a described set of players gaining life (CR 119.7).</summary>
/// <remarks>
/// The players are read off the scope when the ban is made rather than when it is asked, which
/// is the wrong half of CR 119.7 to get wrong in only one direction: a scope re-read later would
/// catch a player who joined the ban's set afterwards. It is stored as a scope for the same
/// reason <see cref="PreventionEffect.Players"/> is — "your opponents" is a description the
/// shared vocabulary already answers, and copying the answer into a list would be a second
/// place for it to disagree.
/// </remarks>
public sealed record BanLifeGain : IEffect
{
    /// <summary>Which players it stops — "players", "your opponents".</summary>
    public PlayerScope Players { get; init; } = PlayerScope.EachPlayer;

    /// <summary>Whether it ends as the turn does (CR 514.2).</summary>
    public bool ForTheTurn { get; init; } = true;

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new LifeGainBanned(new State.LifeGainBan
            {
                Id = Guid.NewGuid(),
                ControllerId = context.ControllerId,
                Players = Players,
                UntilEndOfTurn = ForTheTurn ? context.State.TurnNumber : null,
            }),
        ];
    }
}

/// <summary>Counters a target spell (CR 701.6).</summary>
/// <remarks>
/// A countered spell is put into its owner's graveyard from the stack; it does not resolve, so
/// none of its effects happen (CR 701.5a).
/// <para>
/// <paramref name="ToExile"/> is the rider a good many counterspells carry — "if that spell is
/// countered this way, exile it instead of putting it into its owner's graveyard". It changes
/// where the spell ends up and nothing else, which matters to every card that would otherwise
/// buy it back out of the graveyard.
/// </para>
/// </remarks>
public sealed record CounterTargetSpell(int TargetIndex = 0, bool ToExile = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.SpellOnStack } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var spell))
            return [];

        // CR 701.5a: countering a spell moves it from the stack to its owner's graveyard, so
        // there has to be a spell on the stack to move. A target can stop being legal between
        // being chosen and this resolving (CR 608.2b) - something else countered it, or it
        // resolved - and a counterspell that then described a move out of a zone the object had
        // already left was refused by the reducer rather than doing nothing.
        if (spell.Zone != Zone.Stack)
            return [];

        // CR 701.6a: a spell that can't be countered simply isn't. Through the one reader that
        // knows both spellings — the keyword a spell says about itself, and the group ban a
        // permanent on the battlefield says about somebody's spells.
        if (Bans.CannotBeCountered(context.State, context.Abilities, spell))
            return [];

        return
        [
            new ObjectMoved(
                target.Subject,
                ObjectId.New(),
                Zone.Stack,
                ToExile ? Zone.Exile : Zone.Graveyard,
                spell.ControllerId,
                ToExile ? MoveCause.Exile : MoveCause.Other),
        ];
    }
}

/// <summary>Who an effect is aimed at when it names a group rather than a target (CR 109.5).</summary>
public enum PlayerScope
{
    /// <summary>The controller of the spell or ability — "you".</summary>
    You,

    /// <summary>Every opponent of the controller.</summary>
    EachOpponent,

    /// <summary>Every player, the controller included.</summary>
    EachPlayer,

    /// <summary>
    /// Every player except the controller - "each other player".
    /// </summary>
    /// <remarks>
    /// The same set as <see cref="EachOpponent"/> at every table the engine plays today, and
    /// deliberately not folded into it: the two come apart the moment a card makes a player
    /// your teammate, and a card that said "other" would then be quietly wrong rather than
    /// unread.
    /// </remarks>
    EachOtherPlayer,

    /// <summary>
    /// Whoever controls the permanent the trigger was about - "that creature's controller".
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="TriggerSubject"/>, which is a player the event named directly.
    /// A land entering names no player at all; the one this asks for is read off the permanent.
    /// </remarks>
    SubjectController,

    /// <summary>
    /// The player the triggering event was about — "that player" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// Only a triggered ability has one. Anywhere else it names nobody, and an effect scoped to
    /// it does nothing rather than falling back to the controller: "that player discards a card"
    /// made to mean "you discard a card" is a different and much worse card.
    /// </remarks>
    TriggerSubject,

    /// <summary>
    /// The player this spell or ability has already named - "that player's hand" (CR 603.2).
    /// </summary>
    /// <remarks>
    /// A possessive points back at a player the same text has picked out, and text does that two
    /// ways: by targeting one, and by being a trigger whose event was about one. The target is
    /// asked first, because a target is named by the ability's own words and a trigger subject
    /// only by its condition - so on "target player mills three cards ... the number of instant
    /// and sorcery cards in that player's graveyard" the target is the later of the two and the
    /// one the pronoun means. Asked the other way round that card counts the graveyard of
    /// whoever the "when this enters" trigger was about, which is its own controller: a card
    /// that reads perfectly and counts the wrong pile.
    /// <para>
    /// Exactly one player among the targets, or none at all. Two would make the pronoun
    /// ambiguous, and naming nobody is the honest answer where naming the first would be a
    /// guess. Distinct from <see cref="TriggerSubject"/>, which is what a template that writes
    /// its own ability text uses when it knows the pronoun means the event: this one is what a
    /// <em>printed</em> pronoun compiles to, where the compiler has to work out which.
    /// </para>
    /// </remarks>
    NamedPlayer,

    /// <summary>
    /// The player the source is attacking - "defending player" (CR 506.2).
    /// </summary>
    /// <remarks>
    /// Read off the combat rather than off the trigger, because an attack trigger names the
    /// attacker as its subject and not the player being attacked. Names nobody outside combat,
    /// and an effect scoped to it then does nothing rather than picking someone.
    /// </remarks>
    DefendingPlayer,

    /// <summary>
    /// The opponent this spell's gift was promised to — "the chosen player" (CR 702.174a).
    /// </summary>
    /// <remarks>
    /// Chosen as the spell was cast and read off the source when the delivery resolves, which
    /// for a spell is the object on the stack and for a permanent's gift trigger is the
    /// permanent the fact rode in on. A scope rather than a target because the rules never
    /// target the recipient: nothing checks the choice twice, and hexproof does not refuse a
    /// present. Names nobody when no gift was promised — the delivery is gated on the promise
    /// anyway, so an empty answer is a second lock rather than a decision point.
    /// </remarks>
    GiftRecipient,

    /// <summary>
    /// The player the source is attached to — "enchanted player" (CR 303.4b).
    /// </summary>
    /// <remarks>
    /// A Curse is an Aura whose host is a player rather than a permanent, and every ability on
    /// one names that player: "enchanted player mills two cards", "~ deals damage to enchanted
    /// player". It is a scope rather than a target because nothing about the ability chooses it —
    /// it is decided once, when the Aura is attached, and CR 303.4m says the same word means the
    /// same player on any permanent attached to one, Aura or not.
    /// <para>
    /// Names nobody when the source is attached to nothing, which is the honest answer for an
    /// Aura that has come unattached rather than a reason to fall back to its controller. The
    /// state-based action puts it in the graveyard shortly afterwards anyway (CR 704.5n).
    /// </para>
    /// </remarks>
    EnchantedPlayer,
}

/// <summary>The players a scope names, in turn order (CR 101.4).</summary>
internal static class PlayerScopes
{
    public static IEnumerable<Guid> Resolve(PlayerScope scope, ResolutionContext context) =>
        scope switch
        {
            PlayerScope.You => [context.ControllerId],
            // Followed forward if the permanent has since moved on (CR 400.7): a creature that
            // died still names whoever controlled it.
            // The target first and the trigger's object only behind it, for the reason
            // NamedPlayer gives about players: "destroy target creature. Its controller
            // discards a card" names the creature this spell chose, and a spell has no
            // triggering object at all.
            PlayerScope.SubjectController =>
                (TargetedObject(context) ?? context.SubjectObject) is { } about
                && (context.State.TryGetObject(about, out var owner)
                    ? owner
                    : context.ObjectBehind?.Invoke(about)) is { } found
                ? [found.ControllerId]
                : [],

            PlayerScope.TriggerSubject => context.SubjectPlayer is { } subject
                && context.State.Players.ContainsKey(subject)
                ? [subject]
                : [],

            // The target first and the trigger's subject only behind it - see the enum member
            // for why that order is the whole of the rule.
            PlayerScope.NamedPlayer => TargetedPlayer(context) is { } named
                ? [named]
                : context.SubjectPlayer is { } behind
                    && context.State.Players.ContainsKey(behind)
                    ? [behind]
                    : [],

            // Read off the physical source — the spell on the stack while it resolves, or the
            // permanent its gift trigger belongs to — and followed behind if the object has
            // since moved on, because the promise was made to a player and not to a zone. A
            // recipient who has left the game is nobody: the gift is not delivered to an empty
            // seat, and CR 800.4a has already taken everything else of theirs with them.
            PlayerScope.GiftRecipient =>
                (context.State.TryGetObject(context.PhysicalSourceId, out var gifting)
                    ? gifting
                    : context.ObjectBehind?.Invoke(context.PhysicalSourceId))
                        is { GiftedTo: { } chosen }
                && context.State.Players.TryGetValue(chosen, out var recipient)
                && !recipient.HasLost
                    ? [chosen]
                    : [],
            PlayerScope.DefendingPlayer =>
                context.State.Combat.Attackers.TryGetValue(
                    context.PhysicalSourceId, out var attacking)
                && context.State.Players.ContainsKey(attacking.DefendingPlayer)
                    ? [attacking.DefendingPlayer]
                    : [],

            // The permanent rather than the ability, because an ability on the stack is its own
            // object and is attached to nothing (CR 303.4b). A player who has left the game is
            // no longer one to be enchanted, so the scope names nobody rather than an empty seat.
            PlayerScope.EnchantedPlayer =>
                context.State.TryGetObject(context.PhysicalSourceId, out var attachedSource)
                && attachedSource.Permanent?.AttachedToPlayer is { } enchanted
                && context.State.Players.TryGetValue(enchanted, out var host)
                && !host.HasLost
                    ? [enchanted]
                    : [],
            PlayerScope.EachOpponent or PlayerScope.EachOtherPlayer => context.State.ApnapOrder()
                .Where(id => id != context.ControllerId && !context.State.GetPlayer(id).HasLost),
            _ => context.State.ApnapOrder().Where(id => !context.State.GetPlayer(id).HasLost),
        };

    /// <summary>
    /// The one player among a spell's chosen targets, or null when there is not exactly one.
    /// </summary>
    /// <remarks>
    /// A permanent target names no player at all - its <see cref="Target.Player"/> is empty - so
    /// only the player-shaped targets are counted, "any target" among them when it was aimed at
    /// a seat rather than at a creature (CR 115.4). Two different players is not an answer: the
    /// pronoun would be ambiguous and this returns null rather than taking the first.
    /// </remarks>
    private static Guid? TargetedPlayer(ResolutionContext context)
    {
        Guid? only = null;

        foreach (var target in context.Targets)
        {
            if (target.Kind is not (TargetKind.Player or TargetKind.Any)
                || target.Player == Guid.Empty)
            {
                continue;
            }

            if (only is { } already && already != target.Player)
                return null;

            only = target.Player;
        }

        return only is { } chosen && context.State.Players.ContainsKey(chosen) ? chosen : null;
    }

    /// <summary>
    /// The one object among a spell's chosen targets, or null when there is not exactly one.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="TargetedPlayer"/>, and it counts the other three kinds: a
    /// permanent, a spell on the stack, a card in a graveyard, and "any target" when it was
    /// aimed at one of those rather than at a seat. Two different objects is not an answer for
    /// the same reason two players are not - "its" would be ambiguous, and the honest reply is
    /// nobody rather than the first one chosen.
    /// </remarks>
    private static ObjectId? TargetedObject(ResolutionContext context)
    {
        ObjectId? only = null;

        foreach (var target in context.Targets)
        {
            if (target.Kind is TargetKind.Player || target.Subject == default)
                continue;

            if (only is { } already && already != target.Subject)
                return null;

            only = target.Subject;
        }

        return only;
    }

    /// <summary>
    /// The same question asked of a permanent rather than of a resolving spell or ability.
    /// </summary>
    /// <remarks>
    /// A static ability has no resolution to be the controller of, so "you" is whoever controls
    /// the permanent the ability is printed on. The three scopes a board question can answer are
    /// the only ones offered here: the rest read a trigger's subject or a combat, and neither
    /// exists at the moment a cost is being worked out (CR 601.2f).
    /// </remarks>
    public static IEnumerable<Guid> Around(
        PlayerScope scope, State.GameState state, Guid controllerId)
    {
        ArgumentNullException.ThrowIfNull(state);

        return scope switch
        {
            PlayerScope.You => [controllerId],
            PlayerScope.EachOpponent or PlayerScope.EachOtherPlayer => state.ApnapOrder()
                .Where(id => id != controllerId && !state.GetPlayer(id).HasLost),
            PlayerScope.EachPlayer => state.ApnapOrder().Where(id => !state.GetPlayer(id).HasLost),
            _ => [],
        };
    }
}

/// <summary>
/// Each named player discards cards (CR 701.9).
/// </summary>
/// <remarks>
/// Which card a player discards is theirs to choose (CR 701.9a), and the engine cannot make that
/// choice for them — so this discards from the front of the hand only when there is no choice to
/// make, and otherwise raises one. That is why it emits a <see cref="ChoiceRequested"/> rather
/// than picking: a discard the engine chose is a different game from the one the card describes.
/// </remarks>
public sealed record DiscardCards(
    Amount Count,
    PlayerScope Scope = PlayerScope.You,
    bool AtRandom = false,
    int? TargetIndex = null,
    bool WholeHand = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        // "Target player discards a card" names one player and the scope names none, which is
        // why the target wins outright rather than being added to what the scope found. A target
        // that has gone is not a scope to fall back on: the effect simply does nothing.
        var told = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        foreach (var who in told)
        {
            var hand = context.State.GetPlayer(who).Hand;
            if (hand.IsEmpty)
                continue;

            // "Discard your hand" is a count of however many they are holding, which is a
            // different question per player and so cannot be a number the compiler worked out.
            // Everything after it is the same, including the part that makes this easy: with no
            // choice left to make the engine may act.
            var count = WholeHand ? hand.Count : Count.In(context);
            if (!AtRandom && hand.Count <= count)
            {
                foreach (var card in hand)
                {
                    events.Add(new ObjectMoved(
                        card, ObjectId.New(), Zone.Hand, Zone.Graveyard, who, MoveCause.Discard));
                }

                continue;
            }

            events.Add(new DiscardRequested(who, Math.Min(count, hand.Count), AtRandom));
        }

        return events;
    }
}

/// <summary>
/// Discards a whole hand and draws back as many as it held (CR 701.8, 121.3).
/// </summary>
/// <remarks>
/// One effect rather than a discard followed by a draw, because "that many" is a number that
/// stops existing the moment the first half happens: effects in an ability resolve one at a time
/// against the state the one before it left (CR 608.2), so by the time a draw could ask how big
/// the hand was, it is empty. The count is taken once, here, before either instruction runs.
/// <para>
/// The draw reads the library and the discard reads the hand, so both sets of events can be
/// worked out from the same state without either seeing the other's — which is what lets this be
/// one resolution rather than a deferred question.
/// </para>
/// </remarks>
public sealed record DiscardHandThenDraw(PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var who in PlayerScopes.Resolve(Scope, context))
        {
            var hand = context.State.GetPlayer(who).Hand;

            // Nothing to choose: the whole hand goes, so there is no discard to ask about and
            // the cards can move here the way an emptied hand does everywhere else.
            foreach (var card in hand)
            {
                events.Add(new ObjectMoved(
                    card, ObjectId.New(), Zone.Hand, Zone.Graveyard, who, MoveCause.Discard));
            }

            events.AddRange(Drawing.From(context, who, hand.Count));
        }

        return events;
    }
}

/// <summary>
/// Puts the top cards of a library into its owner's graveyard (CR 701.17).
/// </summary>
/// <remarks>
/// Milling is not drawing: a player who cannot mill as many cards as the effect says mills as
/// many as they can and does not lose the game for it (CR 701.17b). That is why this stops at the
/// end of the library rather than emitting the empty-draw attempt that <see cref="DrawCards"/>
/// does.
/// </remarks>
/// <summary>A permanent connives - draw, then discard, then maybe grow (CR 701.50a).</summary>
/// <remarks>
/// The draw is done here and the discard is not, because which card goes is the player's to
/// choose and whether it was a land decides the counter. Both halves have to happen even when
/// the library is empty: drawing from nothing is a loss the player takes later (CR 704.5b), not
/// a reason for the rest of the ability to stop.
/// </remarks>
public sealed record Connive(
    Amount Count, int TargetIndex = 0, EffectSubject Subject = EffectSubject.Source) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Math.Max(0, Count.In(context));
        if (many == 0)
            return [];

        // "Target creature you control connives" - the permanent that connives is the one the
        // sentence names, and it is *its* controller who draws and discards and *it* that grows.
        // Defaulting both to the source would have drawn the right cards onto the wrong player
        // at any table where the target was not the caster's own.
        //
        // Through the shared subject resolver rather than the target list alone, because the
        // sentence has a third spelling: "target attacking creature can't be blocked this turn.
        // It connives" names the creature two words earlier and never targets again, and reading
        // that pronoun as the source made Kamiz and Doctor Doom connive *themselves*.
        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } conniver)
            return [];

        if (!context.State.TryGetObject(conniver, out var permanent))
            return [];

        var who = permanent.Zone == Zone.Battlefield
            ? Characteristics.Of(context.State, context.Abilities, permanent).ControllerId
            : context.ControllerId;

        var events = new List<GameEvent>(Drawing.From(context, who, many));

        // One request per card: CR 701.50a is written for one, and a permanent that connives
        // twice asks twice - each discard is judged on its own for the counter.
        for (var i = 0; i < many; i++)
            events.Add(new ConniveRequested(who, conniver));

        return events;
    }
}

/// <summary>"Becomes the creature type of your choice" (CR 205.1b).</summary>
/// <remarks>
/// The colour choice's twin. A null index means the source is choosing for itself, which is what
/// every card with this line says - "{1}: this creature becomes the creature type of your
/// choice until end of turn".
/// </remarks>
public sealed record ChooseCreatureTypeForTarget(int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var affected = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Permanent } aimed
                ? ImmutableList.Create(aimed.Subject)
                : ImmutableList<ObjectId>.Empty
            : ImmutableList.Create(context.PhysicalSourceId);

        return affected.IsEmpty
            ? []
            : [new CreatureTypeChoiceRequested(
                context.ControllerId, context.PhysicalSourceId, affected)];
    }
}

/// <summary>"Discover N" - cascade with a hand fallback (CR 701.57a).</summary>
/// <remarks>
/// The exiling is not a decision, so nothing is asked here: what the player gets is the offer at
/// the end, taken by casting the card like any other. The only two things that separate this
/// from cascade are the comparison - N or less, against cascade's strictly less - and where a
/// declined card ends up.
/// </remarks>
public sealed record Discover(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new DiscoverRequested(
                context.ControllerId, context.PhysicalSourceId, Count.In(context)),
        ];
    }
}

/// <summary>Creates an Incubator token with counters on it (CR 701.53a, 111.10i).</summary>
/// <remarks>
/// The token is double-faced, which the engine already models: a card with two faces transforms
/// through <c>PermanentTransformed</c>, and nothing about that cares whether the card came from
/// a printing or from here. The counters go on in the same breath as the token is made, because
/// CR 701.53a says it enters with them - a creature that arrived at 0/0 and grew a moment later
/// would already have died to state-based actions.
/// </remarks>
public sealed record Incubate(Amount Count) : IEffect
{
    /// <summary>The token CR 111.10i describes, front face first.</summary>
    internal static Domain.Models.CardDefinition Token { get; } = new()
    {
        OracleId = "token-incubator",
        Name = "Incubator",
        CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
        Subtypes = ["Incubator"],
        OracleText = "{2}: Transform this artifact.",
        Faces =
        [
            new Domain.Models.CardFace
            {
                Name = "Incubator",
                TypeLine = "Artifact Token — Incubator",
                CardTypes = Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Token,
                Subtypes = ["Incubator"],
                OracleText = "{2}: Transform this artifact.",
            },
            new Domain.Models.CardFace
            {
                Name = "Phyrexian Token",
                TypeLine = "Artifact Creature Token — Phyrexian",
                CardTypes = Domain.Enums.CardType.Artifact
                    | Domain.Enums.CardType.Creature
                    | Domain.Enums.CardType.Token,
                Subtypes = ["Phyrexian"],
                Power = 0,
                Toughness = 0,
            },
        ],
    };

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var counters = Math.Max(0, Count.In(context));
        var id = ObjectId.New();

        return
        [
            new ObjectCreated(
                id, Token, context.ControllerId, context.ControllerId, Zone.Battlefield),
            new CountersChanged(id, State.CounterKinds.PlusOnePlusOne, counters),
        ];
    }
}

/// <summary>"Venture into the dungeon" (CR 701.49), or into a dungeon named by the card.</summary>
/// <remarks>
/// The dungeon is a real object in the command zone, so its room abilities are found by the
/// ordinary trigger scan rather than being run from inside this resolution — which is what lets
/// a room target something. The cases of the rule itself live in
/// <see cref="Dungeons.VentureEvents"/>, shared with the initiative's inherent abilities so the
/// printed keyword action and the engine's own hooks cannot drift apart.
/// </remarks>
/// <param name="Into">
/// The dungeon CR 701.49d's variant names — "venture into Undercity" — or null for the plain
/// instruction. A named dungeon decides only what is <em>started</em>: a player already in any
/// dungeon follows its arrows exactly as a plain venture would (CR 701.49d).
/// </param>
public sealed record VentureIntoTheDungeon(string? Into = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Dungeons.VentureEvents(context.State, context.ControllerId, Into);
    }
}

/// <summary>"The Ring tempts you" (CR 701.54a).</summary>
/// <remarks>
/// Two things in order: the temptation is counted, then a Ring-bearer is chosen. The count goes
/// up first because the emblem's abilities are "as long as the Ring has tempted you N or more
/// times", and the creature about to be chosen should already be under the ability it earns.
/// </remarks>
public sealed record TheRingTemptsYou : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new RingTempted(context.ControllerId),
            new RingBearerRequested(context.ControllerId),
        ];
    }
}

/// <summary>Encore: a token copy per opponent, each aimed at one of them (CR 702.141a).</summary>
/// <remarks>
/// The tokens are not created attacking - encore is sorcery-speed, so there is no combat yet.
/// They are created with a requirement instead: attack, and attack that opponent. Both halves
/// travel in the continuous effect's id, because a token has nowhere else to keep them.
/// </remarks>
public sealed record EncoreCopies : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The cost exiled this card before the effect resolved, so the source id names nothing
        // any more - a card that changes zones is a new object (CR 400.7). The object it became
        // is found by the link the move left behind, which is the same recovery a dies-trigger
        // needs to know what "another" excludes.
        var card = context.State.TryGetObject(context.PhysicalSourceId, out var original)
            ? original.Card
            : context.State.Objects.Values
                .FirstOrDefault(o => o.PreviousId == context.PhysicalSourceId)?.Card;

        if (card is null)
            return [];

        var copied = TokenCards.AsToken(card, oracleId: "token-encore-" + card.OracleId);
        var events = new List<GameEvent>();

        foreach (var opponent in context.State.ApnapOrder())
        {
            if (opponent == context.ControllerId || context.State.GetPlayer(opponent).HasLost)
                continue;

            var token = ObjectId.New();

            events.Add(new ObjectCreated(
                token, copied, context.ControllerId, context.ControllerId, Zone.Battlefield));

            events.Add(new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.MustAttackPlayerId(opponent),
                [token],
                context.State.TurnNumber));

            // CR 702.141a: sacrificed at the beginning of the next end step, whatever happened
            // to them in between - which is what keeps encore from being a permanent army.
            events.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                token,
                State.TurnStep.End,
                "sacrifice",
                context.State.TurnNumber));
        }

        return events;
    }
}

/// <summary>Offers the exploit sacrifice (CR 702.110a).</summary>
/// <remarks>
/// The offer and the choice are one question rather than a yes-or-no followed by a which-one:
/// "you may sacrifice a creature" is answered by naming one or by declining, and splitting it
/// would ask the player twice for one decision.
/// </remarks>
public sealed record OfferExploit : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return [new ExploitRequested(context.ControllerId, context.PhysicalSourceId)];
    }
}

/// <summary>Offers the soulbond pairing (CR 702.95a).</summary>
/// <remarks>
/// Exploit's shape: the offer and the choice are one question, answered by naming a creature or
/// declining. Which question depends on the arm. "When this creature enters" chooses among every
/// unpaired creature its controller has; "whenever another creature you control enters" pairs
/// the newcomer with this creature or nobody, so <see cref="WithEnteringCreature"/> reads that
/// newcomer off the trigger's subject — the one thing the resolution still knows that the board
/// no longer says. CR 702.95c's re-check happens where the question is asked, against the state
/// as it stands then.
/// </remarks>
public sealed record OfferSoulbondPair(bool WithEnteringCreature) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!WithEnteringCreature)
            return [new SoulbondPairRequested(context.ControllerId, context.PhysicalSourceId, null)];

        // A subject the trigger never recorded is a card this effect was wired to wrongly;
        // doing nothing is the failure that cannot mispair anybody.
        return context.SubjectObject is { } entered
            ? [new SoulbondPairRequested(context.ControllerId, context.PhysicalSourceId, entered)]
            : [];
    }
}

/// <summary>Copies a creature token its controller chooses (CR 701.36a).</summary>
/// <remarks>
/// Not a target: populate chooses on resolution, so nothing is picked when the spell is cast and
/// nothing about it can be made illegal in between. That is the difference between this and the
/// ordinary "create a token that's a copy of target creature".
/// </remarks>
public sealed record Populate(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Math.Max(0, Count.In(context));

        // "Populate twice" is two separate choices, not one choice copied twice - the second may
        // well pick the token the first just made.
        return [.. Enumerable.Range(0, many).Select(_ => new PopulateRequested(context.ControllerId))];
    }
}

/// <summary>Looks at the top two and manifests one of them (CR 701.62a).</summary>
/// <remarks>
/// Nothing moves here: which card is manifested is the player's to choose, and the other goes
/// to the graveyard only once that is settled. Fewer than two cards is not a failure - the
/// player looks at what there is and manifests one of those.
/// </remarks>
public sealed record ManifestDread : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var looked = context.State.GetPlayer(context.ControllerId).Library.Take(2).ToImmutableArray();
        if (looked.IsEmpty)
            return [];

        return [new ManifestDreadRequested(context.ControllerId, looked)];
    }
}

/// <summary>
/// Exiles the top cards of your library and lets you play them this turn (CR 601.3e).
/// </summary>
/// <remarks>
/// One effect for two printed sentences, because the second names what the first produced and an
/// effect never sees the events of the one before it. The same reason job select creates its
/// token and attaches to it in one place.
/// </remarks>
public sealed record ExileTopAndMayPlay(
    Amount Count, bool ThroughOwnersNextTurn = false) : IEffect
{
    /// <summary>
    /// Whether the permission is to play it for nothing rather than for its cost (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// The same sentence with four words on the end, and a different mechanism behind it: a play
    /// permission grants the zone and charges the printed price, while this grants both and so is
    /// the standing free-cast offer instead. That swaps the window as well as the price — the
    /// offer lapses when its owner next passes, where "until end of turn" would have lasted the
    /// turn — which is the offer-a-cast family's usual deviation and the safe direction for it.
    /// </remarks>
    public bool Free { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Math.Max(0, Count.In(context));
        if (many == 0)
            return [];

        var events = new List<GameEvent>();
        var library = context.State.GetPlayer(context.ControllerId).Library;

        foreach (var card in library.Take(many))
        {
            var exiled = ObjectId.New();

            events.Add(new ObjectMoved(
                card, exiled, Zone.Library, Zone.Exile, context.ControllerId, MoveCause.Exile));

            if (Free)
                events.Add(new FreeCastOffered(exiled, context.ControllerId));
            else
                events.Add(new CardMayBePlayed(
                    exiled, context.State.TurnNumber, ThroughOwnersNextTurn));
        }

        return events;
    }
}

/// <summary>Manifests the top card of a library (CR 701.40a).</summary>
/// <remarks>
/// The card arrives face down and becomes a 2/2 with no text, which the characteristics already
/// know how to compute for a face-down permanent - so nothing here describes a 2/2, and turning
/// it face up needs no remembered "real" card because the card was never changed.
/// <para>
/// CR 701.40e: several are manifested one at a time, which is what the loop is - each takes the
/// card that is on top once the one before it has gone.
/// </para>
/// </remarks>
public sealed record Manifest(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var who in PlayerScopes.Resolve(Scope, context))
        {
            var library = context.State.GetPlayer(who).Library;

            foreach (var card in library.Take(Math.Max(0, Count.In(context))))
            {
                var arrived = ObjectId.New();

                events.Add(new ObjectMoved(
                    card, arrived, Zone.Library, Zone.Battlefield, who, MoveCause.Other));
                events.Add(new CardManifested(arrived));
            }
        }

        return events;
    }
}

public sealed record MillCards(
    Amount Count, PlayerScope Scope = PlayerScope.You, int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        // A named target wins outright over the scope, the same way a targeted discard does: the
        // card says who, and a target that has gone is not a scope to fall back on.
        var told = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        foreach (var who in told)
            events.AddRange(Milling.From(context, who, Count.In(context)));

        return events;
    }
}

/// <summary>Putting the top cards of a library into its graveyard (CR 701.13a).</summary>
/// <remarks>
/// Shared for the reason <see cref="Drawing"/> is: a second copy is where the empty-library
/// arm gets forgotten. Milling more cards than a player has mills what they have and is not
/// a loss - that only comes later, and only from a draw (CR 704.5b).
/// </remarks>
public static class Milling
{
    public static IReadOnlyList<GameEvent> From(
        ResolutionContext context, Guid who, int count)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. context.State.GetPlayer(who).Library.Take(count).Select(card =>
                new ObjectMoved(
                    card, ObjectId.New(), Zone.Library, Zone.Graveyard, who,
                    MoveCause.Mill)),
        ];
    }
}

/// <summary>
/// The controller of a target mills cards - "Counter target spell. Its controller mills two
/// cards" (CR 701.13a).
/// </summary>
/// <remarks>
/// The third verb of the clause <see cref="ChangeLifeOfTargetsController"/> and
/// <see cref="DrawForTargetsController"/> already read, and it finds its player the same way
/// - through <see cref="TargetOwnership"/>, which follows a spell that has been countered
/// into the graveyard it now sits in (CR 400.7). Whose spell it was is still a fact about
/// the game after it stops being a spell, and every card printing this sentence counters
/// something first.
/// </remarks>
public sealed record MillForTargetsController(Amount Count, int TargetIndex = 0) : IEffect
{
    /// <summary>Whether the sentence said "its owner" (CR 108.3).</summary>
    public bool ToOwner { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return TargetOwnership.WhoseTarget(context, TargetIndex, ToOwner) is { } who
            ? Milling.From(context, who, Count.In(context))
            : [];
    }
}

/// <summary>Exiles the top cards of a library (CR 701.19).</summary>
public sealed record ExileFromTopOfLibrary(Amount Count, PlayerScope Scope = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var who in PlayerScopes.Resolve(Scope, context))
        {
            var library = context.State.GetPlayer(who).Library;
            foreach (var card in library.Take(Count.In(context)))
            {
                events.Add(new ObjectMoved(
                    card, ObjectId.New(), Zone.Library, Zone.Exile, who, MoveCause.Exile));
            }
        }

        return events;
    }
}

/// <summary>
/// Every player in a group gains or loses life (CR 119.3).
/// </summary>
/// <remarks>
/// Separate from <see cref="ChangeLife"/>, which is about one player named by a target. "Each
/// opponent loses 2 life" targets nobody at all, so it cannot be expressed as a target index —
/// and a card that targeted every opponent would be a different card, stopped by hexproof.
/// </remarks>
public sealed record ChangeLifeOfEach(Amount Amount, PlayerScope Scope) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. PlayerScopes.Resolve(Scope, context).Select(who =>
                new LifeChanged(
                    who, Amount.In(context), context.State.GetPlayer(who).Life + Amount.In(context))),
        ];
    }
}

/// <summary>
/// "You lose half your life, rounded up" (CR 119.3, 107.15).
/// </summary>
/// <remarks>
/// It cannot be an <see cref="Amount"/>, and that is the whole reason this is an effect of its
/// own. An amount is one number handed to every player the sentence names; half a life total is
/// a different number for each of them, taken when the effect resolves. "Each player loses half
/// their life" at 20 and 7 is ten and four, and any single number is wrong for one of them.
/// <para>
/// The rounding is read from the card rather than assumed, because CR 107.15 leaves it to the
/// card to say and the two answers differ on every odd life total - which is most of them. A
/// sentence that does not say is left unread.
/// </para>
/// <para>
/// A player already at or below nothing loses nothing rather than gaining some back. Half of a
/// negative total is a negative loss, and a life <em>gain</em> is not what this sentence says;
/// the state-based action has that player anyway (CR 704.5a).
/// </para>
/// </remarks>
public sealed record LoseHalfLife(
    PlayerScope Scope = PlayerScope.You,
    bool RoundUp = true,
    int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A named target wins outright over a scope, the same way it does for drawing and
        // discarding: the sentence named one player and the scope named none.
        var told = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        var lost = new List<GameEvent>();

        foreach (var who in told)
        {
            var life = context.State.GetPlayer(who).Life;
            var half = RoundUp ? (life + 1) / 2 : life / 2;

            if (half <= 0)
                continue;

            lost.Add(new LifeChanged(who, -half, life - half));
        }

        return lost;
    }
}

/// <summary>
/// Shuffles a library, optionally taking a graveyard with it (CR 701.24a).
/// </summary>
/// <remarks>
/// Asks rather than acts. The resulting order is the game's own decision and must come from the
/// seeded source so a replay reproduces it, and an effect has no access to that - so this emits
/// a request the engine answers at the next settle, the way cascade and search already do.
/// </remarks>
/// <summary>Shuffles the card this spell is into its owner's library (CR 701.24a).</summary>
/// <remarks>
/// The beacons: a sorcery that goes back into the deck instead of to the graveyard. The card is
/// still on the stack as this resolves and reaches the graveyard a moment later under a new id
/// (CR 400.7), so the shuffle names the id it has now and the settle follows it forward - the
/// same way a deferred branch finds the permanent its spell became.
/// </remarks>
public sealed record ShuffleSourceIntoLibrary : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var card))
            return [];

        return [new ShuffleRequested(card.OwnerId, [context.PhysicalSourceId])];
    }
}

/// <param name="TargetIndex">
/// The one player the sentence named, or null when it named a group instead.
/// </param>
/// <remarks>
/// A named target wins outright over the scope, the way it does for drawing and discarding: the
/// sentence named one player and the scope named none. "Target player shuffles their graveyard
/// into their library" is the same instruction as the untargeted one pointed somewhere else, and
/// a target that has gone leaves nothing to shuffle rather than falling back to the controller.
/// </remarks>
public sealed record ShuffleLibrary(
    PlayerScope Whose = PlayerScope.You,
    bool GraveyardFirst = false,
    int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A named target wins outright over a scope, the same way it does for drawing and
        // discarding: the sentence named one player and the scope named none. "Target player
        // shuffles their graveyard into their library" is five corpus cards whose only unread
        // line was the subject - the shuffle itself had worked since the tutors were built.
        if (TargetIndex is { } index)
        {
            var aimed = context.TargetAt(index)?.Player ?? context.ControllerId;
            return [Requested(context, aimed)];
        }

        return [.. PlayerScopes.Resolve(Whose, context).Select(who => Requested(context, who))];
    }

    /// <summary>Whether the hand goes in with it (CR 701.24a).</summary>
    /// <remarks>
    /// Timetwister and everything printed after it: "each player shuffles their hand and
    /// graveyard into their library, then draws seven cards". A flag beside the graveyard one
    /// rather than a second effect, because the two zones are named by one sentence and go in
    /// together - the shuffle is one act, and two requests would be two shuffles in the log for
    /// something the card does once.
    /// </remarks>
    public bool HandFirst { get; init; }

    /// <summary>Which graveyard cards go in, or null for all of them.</summary>
    /// <remarks>
    /// "Shuffle all nonland cards from your graveyard into your library" - the same instruction
    /// over part of a graveyard. The filter is the one the tutors name their card with, asked
    /// the way <c>MoveGraveyardGroup</c> asks it, so a kind that can be searched for can be
    /// shuffled back and neither reader has a vocabulary of its own.
    /// </remarks>
    public TargetSpec? OnlyCards { get; init; }

    private ShuffleRequested Requested(ResolutionContext context, Guid who)
    {
        var player = context.State.GetPlayer(who);
        var going = ImmutableList.CreateBuilder<ObjectId>();

        if (GraveyardFirst)
        {
            foreach (var id in player.Graveyard)
            {
                if (OnlyCards is { ObjectFilter: { } wanted }
                    && (!context.State.TryGetObject(id, out var card)
                        || !wanted(context.State, context.Abilities, card, who)))
                {
                    continue;
                }

                going.Add(id);
            }
        }

        // The hand after the graveyard, which is the order the sentence names them in. Both are
        // read off the player whose shuffle this is rather than off the controller: "each player
        // shuffles their hand and graveyard into their library" is one instruction carried out
        // once per player, over that player's own zones.
        if (HandFirst)
            going.AddRange(player.Hand);

        return new ShuffleRequested(who, going.ToImmutable());
    }
}

/// <summary>
/// Every player in a group draws (CR 121.3).
/// </summary>
/// <remarks>
/// Not the same as several <see cref="DrawCards"/> in a row: the draws happen in turn order
/// starting with the active player (CR 101.4), and a player who runs out of library still
/// records the attempt so the rest of the effect resolves and the loss is a state-based action
/// afterwards.
/// </remarks>
public sealed record DrawEach(Amount Count, PlayerScope Scope) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();
        var count = Count.In(context);

        foreach (var who in PlayerScopes.Resolve(Scope, context))
        {
            // Each player's own library, and each player's own run of draws: the drawn cards
            // leave the library as the events are folded, so the indices below are read against
            // a library that has not moved yet and every draw names a distinct card.
            var library = context.State.GetPlayer(who).Library;

            for (var i = 0; i < count; i++)
            {
                if (i >= library.Count)
                {
                    events.Add(new DrawFromEmptyLibraryAttempted(who));
                    break;
                }

                events.Add(new ObjectMoved(
                    library[i], ObjectId.New(), Zone.Library, Zone.Hand, who, MoveCause.Draw));
            }
        }

        return events;
    }
}

/// <summary>
/// The source deals damage to each player in a group (CR 119.3).
/// </summary>
/// <remarks>
/// Damage rather than life loss, which is a real distinction: damage can be prevented and it
/// triggers lifelink on the source (CR 702.15b), while life loss does neither.
/// </remarks>
public sealed record DamageEach(Amount Amount, PlayerScope Scope) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. PlayerScopes.Resolve(Scope, context).Select(who =>
                new PlayerDamaged(
                    who, context.PhysicalSourceId, Amount.In(context), IsCombat: false)),
        ];
    }
}

/// <summary>Returns the source card from its owner's graveyard to their hand (CR 701.19).</summary>
public sealed record ReturnSourceToHand : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The source may be the card in the graveyard already, or the permanent that died and
        // became it a moment ago - a death trigger sees the second (CR 400.7). Both have to
        // reach the same card, so the id is followed forward when it no longer names anything.
        var card = context.State.TryGetObject(context.PhysicalSourceId, out var live)
            ? live
            : context.ObjectBehind?.Invoke(context.PhysicalSourceId);

        if (card is not { Zone: Zone.Graveyard })
            return [];

        return
        [
            new ObjectMoved(
                card.Id, ObjectId.New(), Zone.Graveyard, Zone.Hand, card.OwnerId,
                MoveCause.Return),
        ];
    }
}

/// <summary>Returns the source itself from the battlefield to its owner's hand (CR 400.7).</summary>
/// <remarks>
/// The tail of a great many enters triggers: the permanent arrives, does something, and bounces
/// itself. It finds the source through the ability on the stack, because by the time an ability
/// resolves the object resolving is the ability and not the permanent that made it.
/// </remarks>
/// <summary>
/// "Put this creature on top of its owner's library" (CR 400.7).
/// </summary>
/// <remarks>
/// The source moving itself, which is why it is a source effect and not a targeted one: the
/// sentence names no target, and a permanent that puts itself back is choosing nothing.
/// <para>
/// Owner rather than controller, because a library is a player's own zone (CR 400.3) and a
/// stolen permanent goes home to the deck it came from.
/// </para>
/// </remarks>
public sealed record PutSourceOnLibrary(ZonePosition Position = ZonePosition.Top) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                sourceId,
                ObjectId.New(),
                Zone.Battlefield,
                Zone.Library,
                permanent.OwnerId,
                MoveCause.Return,
                Position),
        ];
    }
}

/// <summary>"Remove it from combat" - the source steps out (CR 506.4).</summary>
/// <remarks>
/// Aimed at the source rather than a target because every corpus line that says it says
/// it about the permanent whose ability it is. It does nothing off the battlefield and
/// nothing outside combat, which is the honest answer rather than an event describing a
/// removal that is not happening.
/// </remarks>
public sealed record RemoveSourceFromCombat : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        var combat = context.State.Combat;

        return combat.Attackers.ContainsKey(sourceId)
            || combat.Blockers.Any(pair => pair.Value.Contains(sourceId))
                ? [new RemovedFromCombat(sourceId)]
                : [];
    }
}

public sealed record ReturnSourceFromBattlefield : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                sourceId, ObjectId.New(), Zone.Battlefield, Zone.Hand, permanent.OwnerId,
                MoveCause.Return),
        ];
    }
}

/// <summary>
/// Weakens every creature blocking the source, except those with a given keyword (CR 702.25a).
/// </summary>
/// <remarks>
/// Flanking. The creatures it affects are neither targets nor the source: they are whatever
/// happened to block, which is a question only the combat state can answer. That is why this is
/// its own effect rather than a pump with a target index — there is no target to index, and the
/// set is not known until blockers are declared.
/// </remarks>
/// <summary>Every creature blocking the source is sacrificed at end of combat (CR 701.54c).</summary>
/// <remarks>
/// The Ring's third ability. Delayed rather than immediate, and that is the whole of it: the
/// blocker deals and takes its combat damage first, and only then goes. Sacrificed by its own
/// controller, which the delayed trigger records so it is their permanent that leaves.
/// </remarks>
public sealed record SacrificeBlockersAtEndOfCombat : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        if (!context.State.Combat.Blockers.TryGetValue(sourceId, out var blockers))
            return [];

        var events = new List<GameEvent>();

        foreach (var id in blockers)
        {
            if (!context.State.TryGetObject(id, out var blocker))
                continue;

            events.Add(new DelayedTriggerCreated(
                Guid.NewGuid(),
                Characteristics.Of(context.State, context.Abilities, blocker).ControllerId,
                id,
                State.TurnStep.EndOfCombat,
                "sacrifice",
                context.State.TurnNumber));
        }

        return events;
    }
}

public sealed record PumpBlockersOfSource(string DefinitionId, KeywordAbility Except) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        if (!context.State.Combat.Blockers.TryGetValue(sourceId, out var blockers))
            return [];

        var affected = blockers
            .Where(id => context.State.TryGetObject(id, out var blocker)
                // CR 702.25a: a blocker that has the keyword itself is spared, which is what
                // makes flanking creatures able to block each other.
                && !Characteristics.Of(context.State, context.Abilities, blocker).Has(Except))
            .ToImmutableList();

        return affected.IsEmpty
            ? []
            :
            [
                new ContinuousEffectCreated(
                    Guid.NewGuid(), DefinitionId, affected, context.State.TurnNumber),
            ];
    }
}

/// <summary>
/// "You may pay [cost]. If you do, ... If you don't, ..." (CR 601.2b).
/// </summary>
/// <remarks>
/// The one effect whose <em>rest</em> depends on an answer, which is why it is built the way it
/// is. It does not ask; it records that the question is owed, and the engine asks at the next
/// settle and then runs whichever branch the answer names.
/// <para>
/// <see cref="EffectIndex"/> is how the branch is found again. The engine cannot hold a
/// continuation — a game has to replay from its log, and a closure is not in a log — so the
/// answer carries a locator back to this effect inside the card's own definition, and the branch
/// is read from the card a second time. That is also why the branches are data on the record
/// rather than something captured: they have to survive being looked up rather than remembered.
/// </para>
/// <para>
/// The cost is mana, life, energy — or a selection, when <see cref="ChosenKind"/> is set. That
/// last one turns the question from a yes/no into a pick, because "unless that player discards a
/// card" cannot be answered without naming the card. It stays <em>one</em> question: asking
/// "will you pay?" and then "with what?" lets a player answer yes and then have nothing legal to
/// name, and gives the log two answers to keep in step where the rules have one decision.
/// </para>
/// <para>
/// <b>Every part of the price scales with <see cref="TimesCounter"/>, not only the mana.</b> The
/// mana does it by repeating the printed text, which is what the player is shown; the life and
/// the count of chosen objects cannot be said in text, so the multiplier itself travels on
/// <see cref="OptionalPaymentRequested.Times"/> and the engine multiplies at the point of asking.
/// Without it "Cumulative upkeep—Pay 1 life" charged 1 on its fifth upkeep instead of 5, and
/// "Cumulative upkeep—Sacrifice a land" asked for one land forever — a card strictly better than
/// the one printed, which is the direction this compiler may never be wrong in.
/// </para>
/// </remarks>
/// <param name="AskTargetController">
/// When set, the offer goes to the controller of that target rather than to this spell's
/// controller — "counter target spell unless its controller pays {2}". The branches still belong
/// to this spell, so "if you don't" is what happens when <em>they</em> decline.
/// </param>
/// <param name="ChosenKind">
/// When set, the cost is a selection rather than a price: sacrifice a permanent, discard a card,
/// return a permanent to its owner's hand (CR 118.12a). The kinds that may be asked for are the
/// ones <c>Game.PayableFor</c> can offer and <c>Game.TakeChosenPayment</c> can move; a kind
/// neither knows is declined rather than charged, which is the safe direction — a cost that
/// cannot be paid is not paid (CR 118.3).
/// </param>
/// <param name="ChosenCount">
/// How many objects the chosen cost takes. Partial payments are not payments (CR 601.2h), so
/// fewer picks than this is a decline and not a discount.
/// </param>
/// <param name="ChosenWhat">
/// Which objects qualify — "target creature you control" for "sacrifice a creature". Null accepts
/// anything, which is what "discard a card" means.
/// <para>
/// A <see cref="TargetSpec"/> and not a <see cref="SearchFilters"/> id, because that is what an
/// <em>activation</em> cost already says (<see cref="ChosenCost.What"/>) and one offer speaking a
/// smaller vocabulary than the other is how the two drift. The id could only ask about a printed
/// card, so "sacrifice a permanent with mana value 1 or greater" and "an untapped creature" had
/// nowhere to go and the whole keyword line stayed unread.
/// </para>
/// </param>
public sealed record MayPay(
    Mana.ManaCostSpec Cost,
    ImmutableList<IEffect> IfYouDo,
    ImmutableList<IEffect> IfYouDont,
    int EffectIndex = 0,
    int? AskTargetController = null,
    bool AskSubjectPlayer = false,
    string? YesLabel = null,
    string? NoLabel = null,
    string? TimesCounter = null,
    int EnergyCost = 0,
    int LifeCost = 0,
    ChosenCostKind? ChosenKind = null,
    int ChosenCount = 1,
    TargetSpec? ChosenWhat = null) : IEffect
{
    /// <summary>
    /// Which seat the offer goes to when the sentence <em>names</em> a player rather than
    /// pointing at a target's controller — "that player loses 2 life unless they pay {2}".
    /// </summary>
    /// <remarks>
    /// <see cref="AskTargetController"/> answers one relation, "whoever controls the thing this
    /// is aimed at", which is the counterspell tax and nothing else. A punisher names the player
    /// outright, and the two ways cards do that — the player a trigger was about, and the player
    /// a target slot chose — are already one word in the shared vocabulary
    /// (<see cref="PlayerScope.NamedPlayer"/>). So the offer takes a scope rather than growing a
    /// flag per relation, and a word that vocabulary learns is a word this offer can address.
    /// <para>
    /// A scope naming nobody is <em>not paid</em> rather than not asked: a cost no player can pay
    /// is not paid (CR 118.3), so the "unless" fails and the printed consequence happens. The
    /// other direction — doing nothing at all — is a punisher that never punishes, which is a
    /// strictly better card than the one printed and the one way this compiler may not be wrong.
    /// </para>
    /// <para>
    /// A scope naming <em>several</em> players is refused at compile time and never reaches here.
    /// "Each opponent loses 3 life unless they pay {2}" is a separate question per player and
    /// this record asks one; charging the first opponent for all of them would be worse than
    /// leaving the line unread.
    /// </para>
    /// </remarks>
    public PlayerScope? AskScope { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        var asked = context.ControllerId;

        // Ward: the offer goes to whoever cast the spell that targeted this, which the trigger
        // recorded as its subject because the event naming them is long past by now.
        if (AskSubjectPlayer)
        {
            if (context.SubjectPlayer is not { } subject
                || !context.State.Players.ContainsKey(subject))
            {
                return [];
            }

            asked = subject;
        }

        if (AskScope is { } scope)
        {
            // Nobody to ask means nobody paid, so the decline branch runs. See the remarks on
            // AskScope: the alternative is a card that quietly stops working.
            if (PlayerScopes.Resolve(scope, context).ToList() is not [var named]
                || !context.State.Players.ContainsKey(named))
            {
                return [.. IfYouDont.SelectMany(e => e.Resolve(context))];
            }

            asked = named;
        }

        if (AskTargetController is { } index)
        {
            // The offer belongs to whoever controls the thing this is aimed at. If that has gone
            // — the spell was countered by something else first — there is nobody to ask and the
            // effect does nothing rather than asking the wrong player.
            if (context.TargetAt(index) is not { } target
                || !context.State.TryGetObject(target.Subject, out var aimedAt))
            {
                return [];
            }

            asked = aimedAt.ControllerId;
        }

        // Cumulative upkeep asks for its cost once per counter (CR 702.24a). The mana travels
        // to the player as printed text, so charging it several times is the printed cost written
        // out several times - which is also how the card reads it aloud.
        var asking = Cost.ToString();
        var times = 1;

        if (TimesCounter is { } kind)
        {
            var counters = context.State.TryGetObject(context.PhysicalSourceId, out var counted)
                ? counted.Permanent?.Counters.GetValueOrDefault(kind, 0) ?? 0
                : 0;

            times = Math.Max(0, counters);
            asking = string.Concat(Enumerable.Repeat(asking, times));
        }

        return
        [
            new OptionalPaymentRequested(
                asked,
                context.PhysicalSourceId,
                abilityId,
                EffectIndex,
                asking)
            {
                Targets = context.Targets,
                SubjectObject = context.SubjectObject,

                // Carried for the reason the object beside it is: the branch this offer defers
                // runs against the permanent, long after the ability that knew whose trigger it
                // was has left the stack.
                SubjectPlayer = context.SubjectPlayer,
                YesLabel = YesLabel,
                NoLabel = NoLabel,

                // Worked out here and carried, for the same reason the mana text is: by the time
                // the answer comes back the counter may have moved, and a replay has to reach the
                // price that was actually offered rather than the price today's board implies.
                Times = times,
            },
        ];
    }
}

/// <summary>
/// Adds mana to its controller's pool (CR 106.1).
/// </summary>
/// <remarks>
/// Distinct from a mana <em>ability</em>, which does not use the stack (CR 605.3b) and is
/// modelled on <see cref="ActivatedAbilityDefinition.Produces"/>. This is for the cases that are
/// not mana abilities at all: a triggered ability that adds mana, or an ability whose mana comes
/// alongside something else. Those do use the stack, and the difference is visible — an opponent
/// can respond to one and not to the other.
/// </remarks>
public sealed record AddMana(ImmutableList<ManaProduction> Produces) : IEffect
{
    /// <summary>Whose pool it goes into (CR 106.4).</summary>
    /// <remarks>
    /// "Whenever a player taps a land for mana, that player adds {G}" puts the mana in
    /// somebody else's pool, and until this existed every reader of the sentence had to
    /// assume the controller's. Defaults to <see cref="PlayerScope.You"/>, which is what
    /// every existing caller meant and what an imperative "Add {G}" says (CR 608.2).
    /// </remarks>
    public PlayerScope Who { get; init; } = PlayerScope.You;

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            .. PlayerScopes.Resolve(Who, context).SelectMany(
                player => Produces.Select(
                    p => new ManaAdded(player, p.Color, p.Amount, p.Restriction))),
        ];
    }
}

/// <summary>Where the mana types an effect offers come from (CR 106.1b).</summary>
public enum ManaPalette
{
    /// <summary>
    /// "One mana of any color" - the five colours, and only those (CR 106.1a).
    /// </summary>
    /// <remarks>
    /// Colourless is a type of mana but not a colour (CR 106.1b), so it is deliberately off this
    /// menu: a card that meant to include it says "any type" and gets the other palette.
    /// </remarks>
    AnyColor,

    /// <summary>
    /// "One mana of any type that land produced" - read off the permanent the trigger was about.
    /// </summary>
    /// <remarks>
    /// The types come from the land's own mana abilities (CR 106.7) rather than from the single
    /// production that fired the trigger, because a resolution context carries the object an
    /// event was about and not the mana it made. On the lands this is printed against - a Forest,
    /// a Swamp, anything with one mana ability - the two answers are the same and there is no
    /// question to ask at all. They come apart only on a land that could have made something
    /// else, where the engine offers the wider menu; that is a stated divergence rather than a
    /// reading of the card.
    /// </remarks>
    TypesTheSubjectProduces,
}

/// <summary>
/// Adds mana whose colour is chosen while this resolves (CR 106.1a).
/// </summary>
/// <remarks>
/// The half of the mana vocabulary that could not be said. <see cref="AddMana"/> needs its
/// colours decided when the card is compiled, so "add one mana of any color" was left unread
/// outside a mana ability - the mana-ability path answers the same question by splitting itself
/// into one ability per colour, and an effect has nothing to split.
/// <para>
/// The answer is a <see cref="Events.ManaColorChoiceRequested"/> and a
/// <see cref="State.ChoiceKind"/>, the shape every question the game must ask takes here, so a
/// replay reaches the same offer rather than needing a continuation the log cannot rebuild.
/// </para>
/// <para>
/// Not a mana ability and never one: an ability that adds mana as part of doing something else,
/// or a trigger that adds it, uses the stack (CR 605.1a) - which is exactly why there is a
/// resolution to ask a question during.
/// </para>
/// </remarks>
/// <param name="Amount">How much mana, all of it one colour unless split below.</param>
/// <param name="Palette">Which types are on the menu.</param>
/// <param name="Who">Whose pool it goes into - "that player adds" is not always "you add".</param>
/// <param name="EachSeparately">
/// True for "in any combination of colors", where each mana has its own colour and so its own
/// question; false for "N mana of any one color", which is one question for all of it.
/// </param>
public sealed record AddChosenMana(
    int Amount = 1,
    ManaPalette Palette = ManaPalette.AnyColor,
    PlayerScope Who = PlayerScope.You,
    bool EachSeparately = false) : IEffect
{
    /// <summary>The five colours in the order the rules name them (CR 105.1).</summary>
    private static readonly ImmutableList<ManaColor> Colours =
    [
        ManaColor.White, ManaColor.Blue, ManaColor.Black, ManaColor.Red, ManaColor.Green,
    ];

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = OptionsFor(context);

        // CR 106.5: an ability that would produce mana of an undefined type produces none at
        // all. A land that has left the battlefield, or one with no mana ability, leaves nothing
        // to choose between - so nothing is added rather than a colour being invented.
        if (options.IsEmpty || Amount <= 0)
            return [];

        var events = ImmutableList.CreateBuilder<GameEvent>();

        foreach (var player in PlayerScopes.Resolve(Who, context))
        {
            // CR 118.3, and the rule this whole mechanism turns on: a question with one possible
            // answer is not a question. A Forest can only make green, and stopping the game to
            // ask which colour it made is how a game stalls.
            if (options.Count == 1)
            {
                events.Add(new ManaAdded(
                    player, Colour(options[0]), Amount, null, context.PhysicalSourceId));

                continue;
            }

            // "Two mana in any combination of colors" is two questions, because each mana has a
            // colour of its own; "two mana of any one color" is one question that pays out both.
            var asks = EachSeparately ? Amount : 1;
            var each = EachSeparately ? 1 : Amount;

            for (var i = 0; i < asks; i++)
            {
                events.Add(new ManaColorChoiceRequested(
                    player, options, each, context.PhysicalSourceId));
            }
        }

        return events.ToImmutable();
    }

    /// <summary>
    /// Colourless mana, which the pool stores as an absent colour rather than as a sixth one.
    /// </summary>
    /// <remarks>
    /// <see cref="ManaColor"/> has a <c>Colorless</c> member and <see cref="ManaAdded"/> uses a
    /// null colour to mean the same thing, so a menu built from the enum has to be translated on
    /// the way out. Getting this wrong adds a sixth kind of mana to the pool that nothing spends.
    /// </remarks>
    internal static ManaColor? Colour(ManaColor type) =>
        type == ManaColor.Colorless ? null : type;

    /// <summary>The types on the menu, read as this resolves (CR 106.7).</summary>
    private ImmutableList<ManaColor> OptionsFor(ResolutionContext context)
    {
        if (Palette != ManaPalette.TypesTheSubjectProduces)
            return Colours;

        if (context.SubjectObject is not { } about
            || !context.State.TryGetObject(about, out var land))
        {
            return [];
        }

        var types = new HashSet<ManaColor>();

        // The computed abilities rather than the printed ones, because a land can be granted a
        // mana ability and a face-down permanent has none of its own (CR 613.1f, 707.2).
        foreach (var ability in Engine.Game.ActivatedAbilitiesOf(
            context.State, context.Abilities, land))
        {
            if (!ability.IsManaAbility)
                continue;

            foreach (var production in ability.Produces)
            {
                // "Add one mana of the chosen color" on a land that has not named one yet is not
                // a type this land could produce (CR 106.5), so it contributes nothing.
                if (production.FromChosenColor)
                    continue;

                types.Add(production.Color ?? ManaColor.Colorless);
            }
        }

        // In the rules' own order, so the menu is the same on a replay as it was in the game.
        return [.. Colours.Add(ManaColor.Colorless).Where(types.Contains)];
    }
}

/// <summary>
/// Sets up something to happen to the source at a later step (CR 603.7).
/// </summary>
/// <remarks>
/// "Sacrifice it at the beginning of the next end step" — the tail of every temporary-theft and
/// temporary-token effect there is. It creates a delayed triggered ability rather than doing
/// anything now, and that ability fires once and is gone (CR 603.7b).
/// </remarks>
public sealed record DelaySourceAction(string EffectId, State.TurnStep Step) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                context.PhysicalSourceId,
                Step,
                EffectId,
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Sets up something to happen at a later step to a permanent the sentence named (CR 603.7).
/// </summary>
/// <remarks>
/// <see cref="DelaySourceAction"/> aimed somewhere other than the source: "destroy that creature
/// at end of combat" on a basilisk is about the creature the trigger was about, and "target
/// creature you control gets +X/+X until end of turn. Destroy it at the beginning of the next end
/// step" is about the creature the spell targeted. Neither is the permanent whose ability it is.
/// <para>
/// A separate record rather than a subject on the existing one, and the subject has no default
/// here on purpose. The two differ only in which permanent they name, so a forgotten argument
/// would not fail — it would silently aim a destruction at the wrong permanent, which is the
/// direction this whole family has to fail away from.
/// </para>
/// <para>
/// The subject is resolved <em>now</em>, as the ability that created this resolves, and the id it
/// found is what the delayed ability carries. That is what CR 603.7d asks for: the delayed
/// ability is about the object the effect was about, and if that object has left the battlefield
/// by the time the step arrives, CR 603.7c says the ability does nothing at all.
/// </para>
/// </remarks>
public sealed record DelayObjectAction(
    string EffectId, State.TurnStep Step, EffectSubject Subject, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } about)
            return [];

        return
        [
            new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                about,
                Step,
                EffectId,
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Its controller loses the game (CR 104.3e).
/// </summary>
/// <remarks>
/// The one thing an effect can do that no state-based action is checking for. CR 704 catches a
/// player at zero life, drawing from an empty library, or holding ten poison counters; this is
/// the arm for an effect that simply says the words, and the pacts are what say them.
/// <para>
/// It emits the same <see cref="PlayerLost"/> that conceding and every state-based loss emit, so
/// nothing downstream has to learn a second way for a player to be out — the last player left
/// wins by the rule that was already there (CR 104.2a).
/// </para>
/// <para>
/// "You" is the effect's controller, which for the branch of a delayed payment is the player who
/// controlled the spell as it resolved (CR 603.7d) — the controller stored on the card the spell
/// became. It is deliberately not the player who was asked to pay: those are the same person on
/// every card that prints this, and reading it off the question rather than off the ability would
/// make the first card that separates them lose the wrong game.
/// </para>
/// </remarks>
public sealed record LoseTheGame : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return [new PlayerLost(context.ControllerId, "an effect said so", "104.3e")];
    }
}

/// <summary>
/// Sets one of the card's own abilities to go on the stack at a later step (CR 603.7).
/// </summary>
/// <remarks>
/// The delayed vocabulary's other half, and the half <c>Game.FireDelayedTriggers</c>'s own note
/// asked for: everything the word-based delays do is a zone change performed inline, "because
/// nothing in the game can profitably respond to that — but a delayed ability that drew a card
/// would be wrong here, and should go through the trigger machinery instead."
/// <para>
/// A pact is exactly that card. "At the beginning of your next upkeep, pay {2}{B}. If you don't,
/// you lose the game" has to reach the stack, because the whole of what the player does about it
/// happens while it is there: mana empties between steps (CR 500.4), so a payment asked at the
/// moment the upkeep begins is a payment nobody can ever make, and every pact would kill its
/// caster. Put on the stack, the ability is answered the way the cards are actually played —
/// priority, lands tapped in response, and then the question.
/// </para>
/// <para>
/// What it names is an ability id on its own card rather than an effect list, for the reason the
/// whole delayed family carries strings: a delayed ability lives in the state, the state folds
/// from a log, and a log holds no closures. The body is looked up again from the compiled card
/// through the same <c>EffectsOfAbility</c> that resolves every other triggered ability.
/// </para>
/// </remarks>
public sealed record DelayAbility(string AbilityId, State.TurnStep Step) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                context.PhysicalSourceId,
                Step,
                DelayedActions.Ability + AbilityId,
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Something that happens to a token at a later step, folded into the effect that made it
/// (CR 603.7b).
/// </summary>
/// <remarks>
/// "Create a 2/1 red Elemental creature token with trample and haste. Sacrifice it at the
/// beginning of the next end step" is two sentences about one token, and the second has nothing
/// to name it with. A delayed ability is set up against an object id, and the only place that
/// knows the token's id is the effect that minted it - so the delay travels with the creation
/// rather than standing beside it, which is exactly how mobilize's own sacrifice is built.
/// <para>
/// Both alternatives were measured and both are worse. Aiming the delay at the <em>source</em>
/// is what eleven fully compiled cards were doing: Lagomos, Hand of Hatred sacrificed itself at
/// the beginning of every end step instead of the Elemental it had just made, and Rakdos
/// Guildmage exiled itself instead of the Goblin. Aiming it at a <em>target</em> is the Kiki-Jiki
/// reading - "create a token that's a copy of target nonlegendary creature you control ...
/// sacrifice it" would sacrifice the creature that was copied, which is a strictly different card
/// that reads perfectly.
/// </para>
/// <para>
/// It carries no id of its own because it needs none: the creating effect emits one delayed
/// ability per token as it mints them, so "create two tokens ... sacrifice them" would reach both
/// without the sentence having to name either. What the reader will not do is fold a delay onto a
/// creation it cannot see as a sibling - that leaves the line unread rather than guessing.
/// </para>
/// </remarks>
public sealed record DelayedTokenAction(string EffectId, State.TurnStep Step);

/// <summary>The words the delayed vocabulary understands, and what each one does.</summary>
/// <remarks>
/// A delayed ability carries one string and nothing else, so the instruction has to be a word.
/// Named here rather than spelled at each site because two places have to agree about them: the
/// compiler that writes one and <c>Game.FireDelayedTriggers</c> that reads it. That switch's last
/// arm is a sacrifice, so a word the two ends spell differently does not fail — it destroys.
/// </remarks>
public static class DelayedActions
{
    /// <summary>Sacrifice it (CR 701.17a) — the vocabulary's default, and the harshest default.</summary>
    public const string Sacrifice = "sacrifice";

    /// <summary>Exile it (CR 406.2).</summary>
    public const string Exile = "exile";

    /// <summary>Return it to its owner's hand (CR 701.9a).</summary>
    public const string ReturnToHand = "return-to-hand";

    /// <summary>
    /// Destroy it (CR 701.7a), which is not the same instruction as sacrificing it.
    /// </summary>
    /// <remarks>
    /// CR 701.21a: sacrificing a permanent does not destroy it, so nothing that replaces
    /// destruction sees a sacrifice — not regeneration (CR 701.19b) and not indestructible
    /// (CR 702.12b). Reading "destroy it at end of combat" as the sacrifice the vocabulary
    /// already had would have made every one of those cards strictly harsher than printed, which
    /// is why the line stayed unread until this word existed.
    /// </remarks>
    public const string Destroy = "destroy";

    /// <summary>
    /// A prefix, not a word: what follows it is an ability id on the delayed trigger's own card.
    /// </summary>
    /// <remarks>
    /// The four words above are performed inline. This one is not performed at all — it names an
    /// ability, and <c>Game.FireDelayedTriggers</c> puts that ability on the stack so priority
    /// happens before it resolves. See <see cref="DelayAbility"/> for why a pact needs that and
    /// a delayed sacrifice does not.
    /// <para>
    /// The arm that reads it fires only on a turn of the delayed ability&apos;s own controller,
    /// because every card printing this shape says &quot;your next upkeep&quot;. A delayed
    /// ability that has to fire on anyone&apos;s turn will need a second prefix; no corpus card
    /// prints one, so there is not one here to be read by nothing.
    /// </para>
    /// </remarks>
    public const string Ability = "ability:";
}

/// <summary>
/// Changes the source's counters at a later step (CR 603.7, 122.1).
/// </summary>
/// <remarks>
/// The Clockwork cycle's wind-down — "whenever this creature attacks or blocks, remove a +1/+1
/// counter from it at end of combat" — and the two cards that put one on instead. A delayed
/// ability like <see cref="DelaySourceAction"/> and deliberately a separate effect rather than a
/// fourth verb in it: everything that one can be asked to do is a zone change, and the arm in
/// <c>Game</c> that reads its id falls through to a <em>sacrifice</em> for any word it does not
/// recognise. A counter change handed to that vocabulary would not remove a counter, it would
/// destroy the creature.
/// <para>
/// The kind and the delta ride in the ability's id, which is the same choice a generated
/// continuous effect makes and for the same reason: a delayed ability carries a string and
/// nothing else, and the string has to stay legible in a stored game.
/// </para>
/// </remarks>
public sealed record DelaySourceCounters(string Kind, int Delta, State.TurnStep Step) : IEffect
{
    /// <summary>The word that marks a delayed ability as a counter change.</summary>
    public const string Prefix = "counters:";

    /// <summary>The id a delayed counter change carries — "counters:+1/+1:-1".</summary>
    public static string IdFor(string kind, int delta) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{Prefix}{kind}:{delta}");

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new DelayedTriggerCreated(
                Guid.NewGuid(),
                context.ControllerId,
                context.PhysicalSourceId,
                Step,
                IdFor(Kind, Delta),
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Unearth: back from the graveyard, hasty, and exiled at end of turn (CR 702.83a).
/// </summary>
/// <remarks>
/// One effect rather than three, and that is forced rather than chosen: a card leaving the
/// graveyard becomes a new object with a new id (CR 400.7), so a second effect running afterwards
/// could not name the thing that just arrived. Only the effect that performs the move knows what
/// id it produced, so everything that has to be aimed at the returned permanent belongs here.
/// </remarks>
/// <summary>
/// Returns the source from the graveyard to the battlefield, and keeps it (CR 701.16a).
/// </summary>
/// <remarks>
/// Unearth's honest twin: the same move without the haste and without the exile at end of turn.
/// A card that recurs itself for a price is the whole of some cards, and the two differ only in
/// what happens afterwards - which is exactly why they are separate effects rather than one with
/// a flag, since forgetting to set the flag would turn one into the other silently.
/// </remarks>
public sealed record ReturnSourceToBattlefield(bool Tapped = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        if (!context.State.TryGetObject(sourceId, out var card) || card.Zone != Zone.Graveyard)
            return [];

        var arriving = ObjectId.New();

        // The permanent that arrives is a new object (CR 400.7), so the tap names the id the
        // move produced and never the one that was in the graveyard.
        return Tapped
            ?
            [
                new ObjectMoved(
                    sourceId, arriving, Zone.Graveyard, Zone.Battlefield, card.OwnerId,
                    MoveCause.Return),
                new PermanentTapped(arriving),
            ]
            :
            [
                new ObjectMoved(
                    sourceId, arriving, Zone.Graveyard, Zone.Battlefield, card.OwnerId,
                    MoveCause.Return),
            ];
    }
}

public sealed record UnearthSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;
        if (!context.State.TryGetObject(sourceId, out var card) || card.Zone != Zone.Graveyard)
            return [];

        var arriving = ObjectId.New();

        return
        [
            new ObjectMoved(
                sourceId, arriving, Zone.Graveyard, Zone.Battlefield, card.OwnerId,
                MoveCause.Return),
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.GrantId(KeywordAbility.Haste),
                [arriving],
                context.State.TurnNumber),
            new DelayedTriggerCreated(
                Guid.NewGuid(), context.ControllerId, arriving, State.TurnStep.End, "exile",
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Look at the top cards, keep one, and bury the rest (CR 701.20a).
/// </summary>
/// <remarks>
/// Records that the question is owed rather than asking it, for the same reason
/// <see cref="Scry"/> does: an effect returns events, and a question halts the whole game.
/// </remarks>
public sealed record LookAndTake(
    Amount Count,
    Zone Destination,
    Zone RestTo = Zone.Library,
    string FilterId = SearchFilters.AnyCard) : IEffect
{
    /// <summary>Whether the cards are revealed to everybody rather than looked at (CR 701.16a).</summary>
    public bool Reveal { get; init; }

    /// <summary>+1/+1 counters the taken card arrives with, when it lands on the battlefield.</summary>
    /// <remarks>
    /// Undercity's Throne of the Dead Three is why these three exist: "put a creature card from
    /// among them onto the battlefield with three +1/+1 counters on it, it gains hexproof until
    /// your next turn, then shuffle" is the same look with the taking dressed. They ride the
    /// request rather than becoming separate effects because only the resolution knows the id
    /// the taken card lands under (CR 400.7) — a second effect running afterwards could not name
    /// the thing that just arrived.
    /// </remarks>
    public int CountersOnTaken { get; init; }

    /// <summary>A generated continuous effect the taken card gains as it lands, or null.</summary>
    public string? TakenGrantId { get; init; }

    /// <summary>Whether the grant lasts until the taker's next turn rather than this one (CR 611.2b).</summary>
    public bool GrantUntilTakersNextTurn { get; init; }

    /// <summary>"Then shuffle" — the rest go back and the library is shuffled (CR 701.20a).</summary>
    public bool ShuffleAfter { get; init; }

    /// <summary>How many of what was seen may be taken, or null for as many as match.</summary>
    /// <remarks>
    /// "Put two of them into your hand", "you may reveal up to two creature cards", "put any
    /// number of permanent cards from among them onto the battlefield" — one look with a
    /// different ceiling on the answer. It is a ceiling rather than a quantity: taking fewer is
    /// allowed, as it has been for the one-card form since that form existed, and the engine
    /// clamps it to how many of the cards seen the filter actually admits.
    /// </remarks>
    public int? TakeLimit { get; init; } = 1;

    /// <summary>
    /// "Put all Goblin cards revealed this way into your hand" — every match, with no question.
    /// </summary>
    /// <remarks>
    /// A flag rather than an unlimited ceiling, because the two differ in whether the game stops:
    /// a ceiling is a question with a bound on the answer, and this sentence offers the player
    /// nothing to decide (CR 118.3). Asking anyway would raise a prompt whose only legal answer
    /// is every option printed on it.
    /// </remarks>
    public bool TakeAll { get; init; }

    /// <summary>
    /// "With mana value 3 or less" — a bound on what may be taken, beside the filter.
    /// </summary>
    /// <remarks>
    /// An <see cref="Amount"/> rather than a number for the reason a search's bounds are one:
    /// "with mana value X or less" is settled when the spell resolves and the compiled definition
    /// is shared by every casting of the card. It is settled here, before the request is made, so
    /// what reaches the log is the number the choice was actually offered against.
    /// </remarks>
    public Amount? MaxManaValue { get; init; }

    /// <summary>"Onto the battlefield tapped" — how the taken card arrives (CR 701.26a).</summary>
    public bool TappedOnTaken { get; init; }

    /// <summary>
    /// Whether the taken card remembers which permanent took it (CR 702.75a).
    /// </summary>
    /// <remarks>
    /// Hideaway is the one look whose result has to stay traceable: the second line every
    /// hideaway card prints says "the exiled card", meaning the one this permanent buried, and
    /// nothing else about a card sitting in exile tells it apart from one some other effect put
    /// there. Off by default, because every other look here is finished with the card the moment
    /// it has moved.
    /// </remarks>
    public bool LinksTakenToSource { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new LookAndTakeRequested(
                context.ControllerId, Count.In(context), Destination, RestTo, FilterId)
            {
                Source = LinksTakenToSource ? context.PhysicalSourceId : null,
                Reveal = Reveal,
                CountersOnTaken = CountersOnTaken,
                TakenGrantId = TakenGrantId,
                GrantUntilTakersNextTurn = GrantUntilTakersNextTurn,
                ShuffleAfter = ShuffleAfter,
                TakeLimit = TakeLimit,
                TakeAll = TakeAll,
                MaxManaValue = MaxManaValue?.In(context),
                TappedOnTaken = TappedOnTaken,
            },
        ];
    }
}

/// <summary>
/// Gives every permanent matching a filter a bonus until end of turn (CR 611.2).
/// </summary>
/// <remarks>
/// The set is worked out once, as it resolves, and the effect then applies to those permanents
/// for the rest of the turn — so a creature that arrives afterwards is not affected, which is
/// what "creatures get -2/-2 until end of turn" means and what a static ability would not do.
/// </remarks>
/// <param name="PeerIndex">
/// Which target of the same spell or ability the filter compares each candidate against, when it
/// does — Bile Blight's "all other creatures with the same name as that creature". Null on every
/// other group, and the group is still untargeted either way: the sibling is targeted, the
/// creatures found by looking at it are not.
/// </param>
public sealed record PumpGroup(string DefinitionId, TargetSpec What, int? PeerIndex = null)
    : IEffect
{
    /// <summary>The size, when the card wrote it as X rather than a number (CR 613.4c).</summary>
    public VariablePumpSize? Size { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The source is handed to the filter as well, so a group phrase can say "each other
        // creature" or "each creature this is attached to" and mean it.
        var source = context.State.TryGetObject(context.PhysicalSourceId, out var self)
            ? self
            : null;

        var peer = PeerIndex is { } sibling ? context.PeerAt(sibling) : null;

        var affected = context.State.Battlefield
            .Where(id => What.Accepts(
                context.State, context.Abilities, context.State.GetObject(id),
                context.ControllerId, source, peer, context.VariableValue))
            .ToImmutableList();

        return affected.IsEmpty
            ? []
            :
            [
                new ContinuousEffectCreated(
                    Guid.NewGuid(),
                    Size?.DefinitionIdIn(context) ?? DefinitionId,
                    affected,
                    context.State.TurnNumber),
            ];
    }
}

/// <summary>What a group effect does to each permanent it finds.</summary>
public enum GroupAction
{
    /// <summary>Destroy it (CR 701.8a). Indestructible survives.</summary>
    Destroy,

    /// <summary>Destroy it with no regeneration allowed (CR 701.19c).</summary>
    DestroyNoRegeneration,

    /// <summary>Exile it (CR 701.13a). Indestructible does not help.</summary>
    Exile,

    /// <summary>Put +1/+1 counters on it (CR 121.2).</summary>
    /// <remarks>
    /// Only +1/+1 and -1/-1 are worth a group action: they are what a card puts on a whole board
    /// at once. A named counter on each of something is rare enough to stay unread rather than
    /// widen this into a kind-and-count pair that one card in the corpus would use.
    /// </remarks>
    PlusOneCounters,

    /// <summary>Put -1/-1 counters on it (CR 121.2).</summary>
    MinusOneCounters,

    /// <summary>Tap it (CR 701.26a).</summary>
    Tap,

    /// <summary>Untap it (CR 701.26b).</summary>
    Untap,

    /// <summary>Return it to its owner's hand (CR 400.7).</summary>
    ReturnToHand,

    /// <summary>Have the source deal damage to it (CR 119.3).</summary>
    Damage,
}

/// <summary>
/// Does something to every permanent matching a filter (CR 609.2).
/// </summary>
/// <remarks>
/// A sweeper targets nothing, and that is not a detail: an untargeted effect is not stopped by
/// hexproof, does not fizzle, and finds the permanents that qualify <em>as it resolves</em>
/// rather than the ones that qualified when it was cast. So it takes a filter, never target
/// indices — and one effect covers every verb, because the only thing that differs between
/// "destroy all creatures" and "exile all creatures" is the event at the end.
/// <para>
/// <paramref name="PeerIndex"/> is the one thing here that <em>is</em> about a target, and it is
/// not a contradiction: "Cleansing Beam deals 2 damage to target creature and each other creature
/// that shares a color with it" targets one creature and finds the rest by looking at it. The
/// found ones are still untargeted — hexproof does not save them and none of them can make the
/// spell fizzle.
/// </para>
/// </remarks>
/// <param name="PeerIndex">
/// Which target of the same spell or ability <see cref="TargetSpec.PeerFilter"/> compares each
/// candidate against. Null on every ordinary sweeper.
/// </param>
public sealed record ToEachPermanent(
    GroupAction Action, TargetSpec What, Amount Amount = default, int? PeerIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        // Every one of the spec's filters, through the one method that asks them all. The
        // source-aware one is what "each *other* creature you control" is made of, and asking
        // only the plain one put a counter on the very permanent whose ability said to leave
        // itself out.
        var source = context.State.TryGetObject(context.PhysicalSourceId, out var self)
            ? self
            : null;

        var peer = PeerIndex is { } sibling ? context.PeerAt(sibling) : null;

        foreach (var id in context.State.Battlefield)
        {
            var obj = context.State.GetObject(id);

            // The announced X, for the sweepers whose filter is written around one: "destroy
            // each nonland permanent with mana value X or less" finds its set as it resolves
            // (CR 609.2) and the number it measures against was chosen as it was cast.
            if (!What.Accepts(
                context.State, context.Abilities, obj, context.ControllerId, source, peer,
                context.VariableValue))
            {
                continue;
            }

            var computed = Characteristics.Of(context.State, context.Abilities, obj);

            switch (Action)
            {
                case GroupAction.Destroy:
                case GroupAction.DestroyNoRegeneration:
                    // CR 702.12b: indestructible is not destroyed, and the rest still resolves.
                    if (!computed.Has(KeywordAbility.Indestructible))
                    {
                        events.Add(new ObjectMoved(
                            id,
                            ObjectId.New(),
                            Zone.Battlefield,
                            Zone.Graveyard,
                            obj.OwnerId,
                            Action is GroupAction.DestroyNoRegeneration
                                ? MoveCause.DestroyNoRegeneration
                                : MoveCause.Destroy));
                    }

                    break;

                case GroupAction.Exile:
                    events.Add(new ObjectMoved(
                        id, ObjectId.New(), Zone.Battlefield, Zone.Exile, obj.OwnerId,
                        MoveCause.Exile));
                    break;

                case GroupAction.ReturnToHand:
                    events.Add(new ObjectMoved(
                        id, ObjectId.New(), Zone.Battlefield, Zone.Hand, obj.OwnerId,
                        MoveCause.Return));
                    break;

                case GroupAction.PlusOneCounters:
                    events.Add(new CountersChanged(
                        id, CounterKinds.PlusOnePlusOne, Math.Max(1, Amount.In(context))));
                    break;

                case GroupAction.MinusOneCounters:
                    events.Add(new CountersChanged(
                        id, CounterKinds.MinusOneMinusOne, Math.Max(1, Amount.In(context))));
                    break;

                case GroupAction.Tap when obj.Permanent?.IsTapped == false:
                    events.Add(new PermanentTapped(id));
                    break;

                case GroupAction.Untap when obj.Permanent?.IsTapped == true:
                    events.AddRange(StunCounters.Untapping(context.State, [id]));
                    break;

                case GroupAction.Damage:
                    events.Add(new DamageMarked(
                        id, Amount.In(context), computed.Has(KeywordAbility.Deathtouch),
                        context.PhysicalSourceId));
                    break;

                default:
                    break;
            }
        }

        return events;
    }
}

/// <summary>
/// Moves every matching card out of a graveyard at once (CR 400.7).
/// </summary>
/// <remarks>
/// The mass form of <see cref="MoveTargetedCard"/>, and it targets nothing: "return all creature
/// cards from your graveyard to your hand" chooses no cards, so hexproof and protection never
/// come into it and there is nothing to fizzle.
/// <para>
/// The cards are read out of the graveyard before any of them moves, because every move gives
/// its card a new identity (CR 400.7) and a list gathered as it went would lose track of itself.
/// </para>
/// </remarks>
public sealed record MoveGraveyardGroup(
    TargetSpec What, Zone Destination, PlayerScope Whose = PlayerScope.You) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var who in PlayerScopes.Resolve(Whose, context))
        {
            foreach (var id in context.State.GetPlayer(who).Graveyard)
            {
                if (!context.State.TryGetObject(id, out var card))
                    continue;

                if (What.ObjectFilter?.Invoke(
                        context.State, context.Abilities, card, context.ControllerId) == false)
                {
                    continue;
                }

                events.Add(new ObjectMoved(
                    id, ObjectId.New(), Zone.Graveyard, Destination, who, MoveCause.Return));
            }
        }

        return events;
    }
}

/// <summary>
/// Moves a targeted card out of a graveyard (CR 400.7).
/// </summary>
/// <remarks>
/// The graveyard is a public zone (CR 404.2), so a card in it can be targeted like a permanent
/// can — which is why <see cref="TargetKind.CardInGraveyard"/> exists. Nothing produced one until
/// now, so every reanimation and every graveyard-hate card went unread despite the machinery
/// being in place.
/// </remarks>
public sealed record MoveTargetedCard(
    Zone Destination, int TargetIndex = 0, bool UnderYourControl = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.CardInGraveyard } target)
            return [];

        if (!context.State.TryGetObject(target.Subject, out var card)
            || card.Zone != Zone.Graveyard)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                target.Subject,
                ObjectId.New(),
                Zone.Graveyard,
                Destination,

                // CR 110.2a: a card put onto the battlefield enters under its owner's control
                // unless the effect says otherwise — and reanimation nearly always says
                // otherwise. Getting this wrong hands the creature back to the player it was
                // taken from, which is the opposite of what the card is for.
                UnderYourControl ? context.ControllerId : card.OwnerId,
                Destination == Zone.Exile ? MoveCause.Exile : MoveCause.Return),
        ];
    }
}

/// <summary>
/// Search your library for a card, put it somewhere, and shuffle (CR 701.23).
/// </summary>
/// <remarks>
/// Records that the search is owed rather than performing it, like a scry: which card is found is
/// the player's choice, and a choice halts the game.
/// </remarks>
/// <summary>Who does the searching, when it is not the controller.</summary>
public enum SearchWho
{
    /// <summary>The controller of the spell or ability.</summary>
    You,

    /// <summary>Whoever controls the permanent the ability is about - "its controller".</summary>
    SubjectController,
}

/// <summary>
/// Which zones one search instruction reaches (CR 701.23a).
/// </summary>
/// <remarks>
/// A search normally takes one zone, and every part of the machinery below was written assuming
/// the library: the candidates come from one list, the shuffle at the end is unconditional, and
/// the request carries one player. 103 corpus cards reach across two or three zones in a single
/// instruction — "search your library or graveyard for a card named ~", "search target player's
/// graveyard, hand, and library for all cards with that name and exile them" — and none of them
/// could be said at all.
/// <para>
/// Flags rather than a list, for the reason every filter here is a name: it has to survive being
/// written to a log and read back, and a set of flags is one word in the JSON either way. Order
/// carries no meaning — the cards that name more than one zone name them in every order, and the
/// union is the same set of candidates whichever way round they are printed.
/// </para>
/// <para>
/// The union is also exact rather than convenient, which is worth saying because "search your
/// library <em>or</em> graveyard" reads as a choice of one zone. Every corpus card printing "or"
/// searches for a single card, so a player who picks the zone the card is in and a player offered
/// both zones at once find precisely the same card; the cards that fetch several — Doomsday,
/// Ecological Appreciation, Chandra, Heart of Fire — all print "and". Where that stops being true
/// this has to become a choice of zone first.
/// </para>
/// </remarks>
[Flags]
public enum SearchIn
{
    /// <summary>The library, which is what a search means unless the card says otherwise.</summary>
    Library = 1,

    /// <summary>The graveyard — a public zone, so a search of it may not fail to find.</summary>
    Graveyard = 2,

    /// <summary>The hand.</summary>
    Hand = 4,
}

/// <summary>
/// Whose zones a search reaches, which is not the same question as who does the searching.
/// </summary>
/// <remarks>
/// <see cref="SearchWho"/> answers "who is holding the cards up and choosing", and until the
/// extraction family arrived it answered both questions at once, because a player only ever
/// searched their own library. Cranial Extraction separates them: its controller searches, and
/// what they search is somebody else's graveyard, hand and library. The two have to be carried
/// apart or the choice is put to the wrong player — and putting an opponent's hand in front of
/// its owner would be a card that does nothing.
/// </remarks>
public enum SearchWhoseZones
{
    /// <summary>The searcher's own zones.</summary>
    Searcher,

    /// <summary>The player this spell or ability targets — "target opponent's graveyard, hand, and library".</summary>
    TargetPlayer,

    /// <summary>Whoever controls the object it targets — "its controller's graveyard, hand, and library".</summary>
    TargetsController,

    /// <summary>Whoever owns the object it targets — "its owner's graveyard, hand, and library".</summary>
    TargetsOwner,
}

/// <remarks>
/// The three mana-value bounds are <see cref="Amount"/>s rather than numbers so that "with mana
/// value X or less" can be said at all: X is chosen as the spell is cast (CR 601.2b) and the
/// compiled definition is shared by every casting of the card, so the bound cannot be a number
/// until the effect resolves. What reaches the log still is one - <see cref="Resolve"/> settles
/// each bound against the resolution before the event is emitted, which is what keeps a stored
/// search replayable as the search it was.
/// </remarks>
public sealed record SearchLibrary(
    string FilterId,
    Zone Destination,
    bool Tapped = false,
    Amount? MaxManaValue = null,
    Amount? ExactManaValue = null,
    Amount? MinManaValue = null,
    int Count = 1,

    /// <summary>
    /// Whose library is searched. "Its controller may search their library" is the same search
    /// pointed at somebody else, and the fetch lands under their control rather than yours.
    /// </summary>
    SearchWho Who = SearchWho.You,

    /// <summary>
    /// Which of that player's zones this one instruction reaches (CR 701.23a).
    /// </summary>
    /// <remarks>
    /// Defaulted to the library, so every single-zone tutor in the corpus - and every log already
    /// written - says exactly what it said before.
    /// </remarks>
    SearchIn Zones = SearchIn.Library,

    /// <summary>Whose zones are searched, when they are not the searcher's own.</summary>
    SearchWhoseZones Whose = SearchWhoseZones.Searcher,

    /// <summary>
    /// Which target names the player whose zones are searched, when <see cref="Whose"/> is not
    /// <see cref="SearchWhoseZones.Searcher"/>.
    /// </summary>
    /// <remarks>
    /// Read only in that case, which is what <see cref="EffectTargets.ReadsATarget"/> is told, so
    /// an ordinary tutor is not reported as aiming at a target it never looks at.
    /// </remarks>
    int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // "A card named ~" - the tilde is the card's own name, and the compiler has no name to
        // put there because the parser reads a sentence and not a card. It is filled in here,
        // where the source is known, so what reaches the log names the card outright and a
        // replayed search looks for the same thing.
        var filter = FilterId;
        if (filter.Contains('~', StringComparison.Ordinal)
            && context.State.TryGetObject(context.PhysicalSourceId, out var self))
        {
            filter = filter.Replace("~", self.Card.Name, StringComparison.Ordinal);
        }

        // "All cards with the same name as that spell" - the name of what this effect targets,
        // followed forward because the sentence before it has already countered or exiled the
        // thing (CR 400.7). Without a target to read there is no name and therefore no search
        // (CR 608.2b): a filter left holding the sentinel would match nothing and the card would
        // report itself as having done its job.
        if (filter.Contains(SearchFilters.TargetsName, StringComparison.Ordinal))
        {
            if (context.PeerAt(TargetIndex) is not { } named)
                return [];

            filter = filter.Replace(
                SearchFilters.TargetsName, named.Card.Name, StringComparison.Ordinal);
        }

        var searcher = context.ControllerId;

        if (Who == SearchWho.SubjectController)
        {
            // The permanent the trigger was about, followed forward if it has since moved on
            // (CR 400.7) - a land that dies still names whoever controlled it.
            if (context.SubjectObject is not { } about)
                return [];

            var owner = context.State.TryGetObject(about, out var present)
                ? present
                : context.ObjectBehind?.Invoke(about);

            if (owner is null)
                return [];

            searcher = owner.ControllerId;
        }

        // Whose zones, which is a different question from who searches (CR 701.23a). An
        // extraction's controller does the searching and what they search belongs to somebody
        // else, so the two travel apart from here on.
        var zonesOf = searcher;

        if (Whose != SearchWhoseZones.Searcher)
        {
            var them = Whose switch
            {
                SearchWhoseZones.TargetPlayer =>
                    context.TargetAt(TargetIndex) is { Kind: TargetKind.Player } aimed
                        ? aimed.Player
                        : null,

                // The object is followed forward (CR 400.7): every card in this family counters
                // or exiles what it targets and then asks whose it was, so by now the target is
                // a card in a graveyard under a new id and asking the state alone finds nothing.
                SearchWhoseZones.TargetsController => context.PeerAt(TargetIndex)?.ControllerId,
                _ => context.PeerAt(TargetIndex)?.OwnerId,
            };

            // CR 608.2b: an effect that cannot work out whose zones it means does nothing at
            // all. Falling back on the searcher would point an extraction at its own caster's
            // library, which is the opposite card.
            if (them is not { } found)
                return [];

            zonesOf = found;
        }

        return
        [
            new LibrarySearchRequested(
                searcher, filter, Destination, Tapped, Count, MaxManaValue?.In(context),
                MinManaValue?.In(context), ExactManaValue?.In(context))
            {
                Zones = Zones,
                ZonesOf = zonesOf == searcher ? null : zonesOf,
            },
        ];
    }
}

/// <summary>
/// Seek a card: one taken at random from among the cards in a library that match, put where the
/// card says, without revealing or shuffling the library.
/// </summary>
/// <remarks>
/// Seeking is a digital-only keyword action. The Comprehensive Rules do not define it and there
/// is no paragraph to cite, so none is cited here — an invented number would be worse than none,
/// because it invites the next reader to trust it.
/// <para>
/// What it <em>can</em> be defined against is <see cref="SearchLibrary"/>, which it differs from
/// in exactly two ways, both of which matter:
/// </para>
/// <list type="bullet">
/// <item><description>The card is chosen <strong>at random by the game</strong>, not by the
/// player. Reading a seek as a search would hand its controller the pick of their library, which
/// is a strictly better card than the one printed — so it goes through the game's shared seeded
/// source, like a shuffle or a discard at random, and the log carries the moves that came out
/// rather than the roll that chose them.</description></item>
/// <item><description>The library is neither revealed nor shuffled. A search shuffles afterwards
/// (CR 701.23e); leaving the order alone is most of the reason the mechanic exists at all.
/// </description></item>
/// </list>
/// <para>
/// Everything else is the search vocabulary unchanged — the same <see cref="SearchFilters"/>
/// ids, the same mana-value bounds, the same destinations — because a second filter grammar
/// would drift from this one the first time either of them learned a word.
/// </para>
/// </remarks>
public sealed record Seek(
    string FilterId,
    Zone Destination = Zone.Hand,
    bool Tapped = false,
    int Count = 1,
    Amount? MaxManaValue = null,
    Amount? MinManaValue = null,
    Amount? ExactManaValue = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // "A card named ~" is the card's own name, filled in here where the source is known -
        // the same substitution a search makes and for the same reason: what reaches the log has
        // to name the card outright, so a replayed seek looks for the same thing.
        var filter = FilterId;
        if (filter.Contains('~', StringComparison.Ordinal)
            && context.State.TryGetObject(context.PhysicalSourceId, out var self))
        {
            filter = filter.Replace("~", self.Card.Name, StringComparison.Ordinal);
        }

        return
        [
            new SeekRequested(
                context.ControllerId, filter, Destination, Tapped, Count,
                MaxManaValue?.In(context), MinManaValue?.In(context),
                ExactManaValue?.In(context)),
        ];
    }
}

/// <summary>
/// Which cards a named search filter accepts (CR 701.23).
/// </summary>
/// <remarks>
/// A closed vocabulary of names rather than a predicate, so a search survives being written to a
/// log and read back. Anything not named here leaves the line unread — a search that found the
/// wrong sort of card would be a different card entirely.
/// </remarks>
public static class SearchFilters
{
    /// <summary>Any card with a basic land type (CR 305.6).</summary>
    public const string BasicLand = "basic-land";

    /// <summary>
    /// Any card that could be a permanent - "four or more permanent cards in your graveyard".
    /// </summary>
    /// <remarks>
    /// A card type is the wrong test: "permanent" is not one (CR 205.2a), it is the set of five
    /// that make one. Without this the word fell through to the subtype check and matched
    /// nothing, because no card is printed with the subtype "permanent".
    /// </remarks>
    public const string PermanentCard = "permanent-card";

    /// <summary>Any card at all — "search your library for a card".</summary>
    public const string AnyCard = "any";

    /// <summary>
    /// A card with one exact name — "search your library for a card named Llanowar Elves".
    /// </summary>
    /// <remarks>
    /// A prefix rather than a member of the closed vocabulary, because the vocabulary is names of
    /// <em>kinds</em> and this names one card. It stays a string that survives a log, which is
    /// the whole reason filters are strings.
    /// </remarks>
    public const string NamedPrefix = "name:";

    /// <summary>
    /// Stands in for the name of the card a search's target turned out to be — "all cards with
    /// the same name as that spell".
    /// </summary>
    /// <remarks>
    /// The same trick, and for the same reason, as the tilde a card uses for its own name: the
    /// parser reads a sentence and not a game, so it has no name to put here, and it is filled in
    /// when the effect resolves. What reaches the log names the card outright, which is what makes
    /// a replayed extraction look for the same thing rather than for whatever the replay's target
    /// happens to be.
    /// <para>
    /// Deliberately not a word: a card name is capitalised by definition, and a capital is how
    /// <see cref="Matches"/> tells a subtype from everything else. A sentinel that could be read
    /// as a name would be one more capitalised word in a type table, which this codebase has been
    /// bitten by seven times.
    /// </para>
    /// </remarks>
    public const string TargetsName = "*target*";

    /// <summary>Whether a card answers to a filter name.</summary>
    /// <remarks>
    /// The name is either a card type, a supertype-and-type pair, or a subtype, and they are told
    /// apart the same way everywhere else in the compiler: a capital letter means a subtype.
    /// </remarks>
    public static bool Matches(string filterId, Domain.Models.CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (string.Equals(filterId, AnyCard, StringComparison.Ordinal))
            return true;

        // "A creature or land card" is two filters and the card answers to either (CR 109.4).
        // Held as one string with a separator rather than as a list, because a filter id travels
        // in an event and has to be a value a log can carry - and because every place that reads
        // one then keeps working without knowing there are now two.
        if (filterId.Contains('|', StringComparison.Ordinal))
        {
            foreach (var one in filterId.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Matches(one, card))
                    return true;
            }

            return false;
        }

        if (filterId.StartsWith(NamedPrefix, StringComparison.Ordinal))
        {
            return string.Equals(
                card.Name,
                filterId[NamedPrefix.Length..],
                StringComparison.OrdinalIgnoreCase);
        }

        // "A noncreature, nonland card" is two filters the card must answer to *both* of, which
        // is the opposite of the bar above and needs its own separator. Written as an ampersand
        // for the same reasons the bar is a bar: it survives an event log, and every reader that
        // already handles a filter id keeps working without knowing there are now two.
        if (filterId.Contains('&', StringComparison.Ordinal))
        {
            foreach (var one in filterId.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Matches(one, card))
                    return false;
            }

            return true;
        }

        // "Noncreature" is the same question as "creature" with the answer turned round. Read
        // here rather than as its own entry per type, so a negation works anywhere a type does
        // and the two can never disagree about what a creature is.
        if (filterId.StartsWith("non", StringComparison.Ordinal))
            return !Matches(filterId[3..], card);

        if (string.Equals(filterId, PermanentCard, StringComparison.Ordinal))
        {
            return card.CardTypes.HasFlag(Domain.Enums.CardType.Artifact)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Creature)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Enchantment)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Land)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Planeswalker)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Battle);
        }

        if (string.Equals(filterId, BasicLand, StringComparison.Ordinal))
        {
            return card.Supertypes.Contains("Basic", StringComparer.OrdinalIgnoreCase)
                && card.CardTypes.HasFlag(Domain.Enums.CardType.Land);
        }

        if (CardTypeNamed(filterId) is { } type)
            return card.CardTypes.HasFlag(type);

        // Supertypes and colours, so "a basic Forest card" and "a green creature card" are two
        // filters joined rather than two phrases needing their own readers.
        if (SupertypeNamed(filterId) is { } supertype)
            return card.Supertypes.Contains(supertype, StringComparer.OrdinalIgnoreCase);

        // The card's colours (CR 202.2). Its colour identity counts the mana symbols in its
        // rules text as well (CR 903.4), which is a deck-building question and not this one.
        if (ColorNamed(filterId) is { } colour)
            return card.Colors.Contains(colour);

        // Colourless is the absence of all five rather than a sixth colour (CR 105.1), so it
        // cannot go in the table above and has to be asked as its own question.
        if (string.Equals(filterId, "colorless", StringComparison.Ordinal))
            return card.Colors.Count == 0;

        // "Multicolored" and "monocolored" count colours rather than naming one (CR 105.4), so
        // they are their own questions for the same reason colourless is. Both are printed by
        // the mana restrictions - "spend this mana only to cast a multicolored spell" - and by
        // the cost modifiers, and neither could be said with the table above.
        if (string.Equals(filterId, "multicolored", StringComparison.Ordinal))
            return card.Colors.Count > 1;

        if (string.Equals(filterId, "monocolored", StringComparison.Ordinal))
            return card.Colors.Count == 1;

        // "Historic" is legendary, artifact or Saga (CR 205.4h) - three unrelated things under
        // one word, so it cannot be a supertype lookup or a type lookup and has to be asked
        // whole. Two other readers in this compiler have answered it for as long as they have
        // existed - the permanent adjective vocabulary and the one that describes a spell being
        // cast - and this one, which decides what a search, a hand filter or a count of a
        // graveyard may name, had never been told. So "exile target historic card from your
        // graveyard" was refused one reader along from "whenever you cast a historic spell".
        if (string.Equals(filterId, "historic", StringComparison.Ordinal))
        {
            return card.Supertypes.Contains("Legendary", StringComparer.OrdinalIgnoreCase)
                || card.CardTypes.HasFlag(Domain.Enums.CardType.Artifact)
                || card.Subtypes.Contains("Saga", StringComparer.OrdinalIgnoreCase);
        }

        // A capitalised name is a subtype — "Forest", "Goblin", "Equipment".
        return card.Subtypes.Contains(filterId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A supertype a printed word names, or null if it is not one (CR 205.4).</summary>
    private static string? SupertypeNamed(string name) => name.ToLowerInvariant() switch
    {
        "basic" => "Basic",
        "legendary" => "Legendary",
        "snow" => "Snow",
        "world" => "World",
        _ => null,
    };

    /// <summary>A colour a printed word names, or null if it is not one (CR 105.1).</summary>
    private static Domain.Enums.ManaColor? ColorNamed(string name) => name.ToLowerInvariant() switch
    {
        "white" => Domain.Enums.ManaColor.White,
        "blue" => Domain.Enums.ManaColor.Blue,
        "black" => Domain.Enums.ManaColor.Black,
        "red" => Domain.Enums.ManaColor.Red,
        "green" => Domain.Enums.ManaColor.Green,
        _ => null,
    };

    private static Domain.Enums.CardType? CardTypeNamed(string name) => name switch
    {
        "creature" => Domain.Enums.CardType.Creature,
        "artifact" => Domain.Enums.CardType.Artifact,
        "enchantment" => Domain.Enums.CardType.Enchantment,
        "land" => Domain.Enums.CardType.Land,
        "planeswalker" => Domain.Enums.CardType.Planeswalker,
        "instant" => Domain.Enums.CardType.Instant,
        "sorcery" => Domain.Enums.CardType.Sorcery,
        _ => null,
    };
}

/// <summary>Whether a cost modifier adds to a cost or takes off it (CR 601.2f).</summary>
/// <remarks>
/// Two named values rather than a signed amount. The only cost modification the engine had could
/// physically only subtract — it accumulated a discount and called
/// <see cref="Mana.ManaCostSpec.WithoutGeneric"/> — so every "costs {1} more" on the board was
/// unread, and a sign convention smuggled into an int is exactly how the next reader gets it
/// backwards on a card that then costs less than printed.
/// </remarks>
public enum CostChange
{
    /// <summary>"...cost {1} less to cast."</summary>
    Reduction,

    /// <summary>"...cost {1} more to cast."</summary>
    Increase,
}

/// <summary>What a cost modifier modifies (CR 601.2f, 602.2b).</summary>
/// <remarks>
/// CR 602.2b makes an activated ability's activation cost the analogue of a spell's mana cost, so
/// the two are the same mechanism pointed at two different costs — which is why this is a field
/// rather than a second kind of modifier. It is also the half that did not exist: an ability's
/// cost was paid with no modifier hook at all.
/// </remarks>
public enum CostModifierKind
{
    /// <summary>"Spells you cast cost {1} less to cast."</summary>
    Spells,

    /// <summary>"Abilities you activate cost {1} less to activate."</summary>
    ActivatedAbilities,
}

/// <summary>
/// How many spells a player may cast in a turn, when a permanent says (CR 601.3).
/// </summary>
/// <remarks>
/// "A player can begin to cast a spell only if a rule or effect allows that player to cast it and
/// no rule or effect prohibits that player from casting it." This is that prohibition, and it is
/// the first one in the engine that comes from somewhere other than the card being cast:
/// <see cref="SpellDefinition.CastOnlyWhen"/> is a restriction a card prints about itself, and
/// nothing could say anything about anyone else's.
/// <para>
/// It is a value on the card rather than a continuous effect because there is nothing to compute:
/// CR 613 sequences effects that change objects' characteristics, and a limit on casting changes
/// no object at all. It modifies the rules, and the rule it modifies asks it directly.
/// </para>
/// <para>
/// No new state was needed for it. <see cref="State.PlayerState.SpellsCastThisTurn"/> and its two
/// siblings have counted this since "your first enchantment spell each turn" was built, which is
/// also why the qualified forms are expressible: the cards cast this turn are kept, not only
/// tallied, so "more than one <em>non-Phyrexian</em> spell" is a question that can be asked
/// rather than a counter nobody thought to add.
/// </para>
/// </remarks>
public sealed record CastLimit
{
    /// <summary>How many may be cast — "more than one" is a maximum of one.</summary>
    public required int Max { get; init; }

    /// <summary>
    /// Whose casting it limits, read around whoever controls the permanent printing it.
    /// </summary>
    /// <remarks>
    /// <see cref="PlayerScope.EachPlayer"/> is the common form and taxes its own controller too;
    /// <see cref="PlayerScope.You"/> is Moderation, which limits nobody else; and
    /// <see cref="PlayerScope.EnchantedPlayer"/> is Curse of Exhaustion, an Aura on a player
    /// (CR 303.4b). Defaulting a missing subject to the controller is the mistake this file
    /// records one layer over, so there is no default that means "guess".
    /// </remarks>
    public PlayerScope Who { get; init; } = PlayerScope.EachPlayer;

    /// <summary>
    /// A card type the limit does not count and does not restrict — "noncreature spell".
    /// </summary>
    public Domain.Enums.CardType ExceptTypes { get; init; }

    /// <summary>
    /// A subtype the limit does not count and does not restrict — "non-Phyrexian spell".
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="ExceptTypes"/> because the printing tells them apart and the
    /// engine has to as well: "noncreature" is a card type written closed up, "non-Phyrexian" is
    /// a subtype written with a hyphen. Reading one as the other would build a limit that matches
    /// nothing while compiling clean, which is the silent no-op this compiler keeps finding.
    /// </remarks>
    public string? ExceptSubtype { get; init; }

    /// <summary>Whether a spell of this card counts towards the limit, and is stopped by it.</summary>
    public bool Counts(Domain.Models.CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return (ExceptTypes == Domain.Enums.CardType.None
                || (card.CardTypes & ExceptTypes) == Domain.Enums.CardType.None)
            && (ExceptSubtype is null
                || !card.Subtypes.Contains(ExceptSubtype, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// A standing change a permanent makes to what somebody's spells or abilities cost (CR 601.2f).
/// </summary>
/// <remarks>
/// Supersedes <c>CostReducer</c>, which could say only one of the six things the corpus prints:
/// it walked the caster's own battlefield and could only ever subtract. The grid is
/// (whose spells or abilities) × (more or less) × (spells or abilities), and only
/// <em>your spells, less</em> was reachable.
/// <para>
/// Deliberately not a continuous effect, for the reason <c>CostReducer</c> already gave: what a
/// spell costs is worked out once as it is cast (CR 601.2f) and never recomputed, so this is read
/// at cast time from whatever is on the battlefield at that moment rather than folded into a
/// layer.
/// </para>
/// <para>
/// A reduction comes off the generic part only, and an increase is added to it. Neither can touch
/// a coloured pip (CR 601.2f), and a cost cannot be reduced below {0}.
/// </para>
/// </remarks>
public sealed record CostModifier
{
    /// <summary>
    /// Which spells or abilities' sources it applies to, in the shared filter vocabulary.
    /// </summary>
    /// <remarks>
    /// The same names <see cref="SearchFilters"/> gives tutors and digs, so "Dragon spells",
    /// "noncreature spells" and "artifact and enchantment spells" are one filter, a negation and
    /// two filters joined, rather than three readers. For
    /// <see cref="CostModifierKind.ActivatedAbilities"/> it is asked of the permanent whose
    /// ability is being activated — "activated abilities of creatures you control".
    /// </remarks>
    public string FilterId { get; init; } = SearchFilters.AnyCard;

    /// <summary>How much generic mana it moves.</summary>
    public required int Amount { get; init; }

    /// <summary>Which way (CR 601.2f).</summary>
    public CostChange Change { get; init; } = CostChange.Reduction;

    /// <summary>Whether it modifies spells being cast or abilities being activated.</summary>
    public CostModifierKind Kind { get; init; } = CostModifierKind.Spells;

    /// <summary>
    /// Whose spells or abilities, read around whoever controls the permanent printing this.
    /// </summary>
    /// <remarks>
    /// The distinction the old reducer could not make and the one this exists for.
    /// <see cref="PlayerScope.You"/> is "spells you cast", <see cref="PlayerScope.EachOpponent"/>
    /// is "spells your opponents cast", and <see cref="PlayerScope.EachPlayer"/> is the bare
    /// "noncreature spells cost {1} more to cast", which taxes its own controller too.
    /// </remarks>
    public PlayerScope Who { get; init; } = PlayerScope.You;

    /// <summary>
    /// Whose permanent the ability has to be on, or null for anyone's.
    /// </summary>
    /// <remarks>
    /// A second scope because the cards ask two different questions and folding them would answer
    /// one of them wrongly. "Abilities <em>you activate</em> cost {1} less to activate" is about
    /// who is paying, which is <see cref="Who"/>; "activated abilities <em>of creatures you
    /// control</em> cost {2} less to activate" is about whose permanent the ability is printed on,
    /// and says nothing at all about who activates it. Only <see cref="CostModifierKind"/>'s
    /// ability half reads this: a spell has no permanent behind it.
    /// </remarks>
    public PlayerScope? SourceController { get; init; }

    /// <summary>
    /// The zone a spell has to be cast from for this to apply, or null for any (CR 400.1).
    /// </summary>
    /// <remarks>
    /// "Spells you cast from your graveyard cost {1} less to cast." The zone has to be on both
    /// halves or on neither: a reducer carrying the zone that nothing consulted would apply the
    /// reduction from every zone, which is a strictly worse card than the unread one. It is read
    /// from where the card is standing when its cost is worked out, which is before it moves to
    /// the stack.
    /// </remarks>
    public State.Zone? FromZone { get; init; }

    /// <summary>
    /// Whether mana abilities are exempt — "unless they're mana abilities" (CR 605.1a).
    /// </summary>
    public bool ExceptManaAbilities { get; init; }

    /// <summary>
    /// Whether it applies only to the abilities of the permanent that prints it.
    /// </summary>
    /// <remarks>
    /// "This ability costs {1} less to activate" is a modifier a permanent makes to itself, and
    /// reading it as "abilities you activate" would discount every other permanent's abilities
    /// too. <see cref="Who"/> is not consulted when this is set: the source's own controller is
    /// whoever is activating it.
    /// </remarks>
    public bool SourceOnly { get; init; }

    /// <summary>
    /// Whether it applies only to a spell that targets the permanent printing it (CR 601.2c).
    /// </summary>
    /// <remarks>
    /// "Spells your opponents cast that target this creature cost {2} more to cast." The clause
    /// was refused for a while and the refusal was right at the time: this record had nowhere to
    /// put the condition, so the line could only have been read as the unconditional form - a
    /// tax on every spell an opponent casts, which is a far better card than the printed one.
    /// <para>
    /// It costs nothing to ask, because the answer is already known when it is asked: CR 601.2c
    /// chooses targets before CR 601.2f works the cost out, so the cast has the list in hand by
    /// the time the modifiers are gathered. An ability being activated is never passed one, so a
    /// modifier carrying this simply never applies there - which is what the printed word
    /// "spells" says.
    /// </para>
    /// </remarks>
    public bool TargetsSource { get; init; }

    /// <summary>
    /// Whether the subject is the card name this permanent chose rather than
    /// <see cref="FilterId"/> (CR 201.4).
    /// </summary>
    /// <remarks>
    /// A flag and not a filter id, and that is the point. A name is capitalised, and a
    /// capitalised word handed to <see cref="SearchFilters"/> is read as a <em>subtype</em>:
    /// "Disruptor Flute" would compile to a tax on cards with a creature type nothing has,
    /// which is a card that looks finished and does nothing. So the name never becomes a
    /// filter at all - the flag says "ask the host", and the host is asked by name.
    /// <para>
    /// Null on the host means the question has not been asked yet, and that taxes nothing.
    /// </para>
    /// </remarks>
    public bool ChosenName { get; init; }

    /// <summary>
    /// Printed symbols an increase adds, for a tax that is not generic mana (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// "Black spells you cast cost {B} more to cast" — the Leech cycle. Set instead of
    /// <see cref="Amount"/> rather than beside it, because the two are the same slot said two
    /// ways and a modifier carrying both would charge twice.
    /// <para>
    /// Increases only. A coloured <em>reduction</em> is a different rule with its own reminder
    /// text ("this effect reduces only the amount of colored mana you pay"), and the compiler
    /// refuses those lines rather than reading them as this.
    /// </para>
    /// </remarks>
    public string? Surcharge { get; init; }
}

/// <summary>
/// Where the engine finds the cost modifiers a card prints.
/// </summary>
/// <remarks>
/// A seam of its own rather than another member on <see cref="IAbilitySource"/>, so the engine
/// half can be built and tested before the compiler reads a word of it: an ability source that
/// does not implement this simply has no modifiers, which is what every source says today.
/// <para>
/// <see cref="IAbilitySource"/> should grow to extend this once the compiler emits them, at which
/// point <c>CostReducer</c> folds into <see cref="CostModifier"/> — it is exactly a
/// <see cref="CostChange.Reduction"/> of <see cref="CostModifierKind.Spells"/> scoped to
/// <see cref="PlayerScope.You"/> from any zone.
/// </para>
/// </remarks>
public interface ICostModifierSource
{
    /// <summary>What this permanent changes about somebody's costs (CR 601.2f).</summary>
    IReadOnlyList<CostModifier> CostModifiersOf(Domain.Models.CardDefinition card) => [];
}

/// <summary>Applying a set of cost modifiers to one cost (CR 601.2f).</summary>
public static class CostModification
{
    /// <summary>
    /// The cost after every modifier that applies has been taken into account.
    /// </summary>
    /// <remarks>
    /// CR 601.2f states the order and it is not the order they were found in: the total cost is
    /// the mana cost "plus all additional costs and cost increases, and minus all cost
    /// reductions". Increases first, then reductions — a {1} spell taxed {2} and discounted {2}
    /// costs {1}, where reducing first would floor at {0} and then charge {2}.
    /// <para>
    /// The mana component cannot be reduced below {0}, which
    /// <see cref="Mana.ManaCostSpec.WithoutGeneric"/> already gives us by taking off only what is
    /// there to take.
    /// </para>
    /// </remarks>
    public static Mana.ManaCostSpec Apply(
        Mana.ManaCostSpec cost, IEnumerable<CostModifier> modifiers)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(modifiers);

        var increase = 0;
        var reduction = 0;
        var surcharge = Mana.ManaCostSpec.Free;

        foreach (var modifier in modifiers)
        {
            // A coloured tax carries its symbols rather than an amount, and is added whole
            // (CR 601.2f). It goes on before the reductions like every other increase, and the
            // reductions cannot take it off again because they only take generic mana.
            if (modifier is { Change: CostChange.Increase, Surcharge: { Length: > 0 } printed })
            {
                surcharge = surcharge.Plus(Mana.ManaCostSpec.Parse(printed));
                continue;
            }

            if (modifier.Amount <= 0)
                continue;

            if (modifier.Change == CostChange.Increase)
                increase += modifier.Amount;
            else
                reduction += modifier.Amount;
        }

        return cost.PlusGeneric(increase).Plus(surcharge).WithoutGeneric(reduction);
    }
}

/// <summary>
/// Does something only if the spell was kicked (CR 702.33e).
/// </summary>
/// <remarks>
/// A wrapper rather than a flag on each effect, because the card prints it as one condition over
/// a whole clause and several effects can hang off it.
/// <para>
/// CR 702.33g: a part of an ability that only happens if the spell was kicked chooses its targets
/// only if it was kicked. That is not modelled — the targets are chosen either way — which shows
/// up as a spell asking for a target it will not use. Recorded rather than hidden.
/// </para>
/// <para>
/// <see cref="Else"/> carries the sentence the clause replaces, for the cards that say
/// "instead": "~ deals 2 damage to any target. If ~ was kicked, ~ deals 4 damage instead" does
/// exactly one of the two, decided by the flag as the spell resolves. Null on the additive
/// cards — the ones the whole-line reader takes — where the clause's effects simply happen on
/// top of whatever came before. The distinction is the card: an "instead" that added would deal
/// six.
/// </para>
/// </remarks>
public sealed record IfKicked(
    ImmutableList<IEffect> Effects, ImmutableList<IEffect>? Else = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var kicked =
            context.State.TryGetObject(context.SourceId, out var spell) && spell.WasKicked;

        var events = new List<GameEvent>();
        foreach (var effect in kicked ? Effects : Else ?? [])
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// "Each opponent attacking that player does the same" — the Curse family (CR 508.1b).
/// </summary>
/// <remarks>
/// The sentence before this one said what "the same" is, so the wrapper holds a copy of that
/// sentence's effects and runs them once per qualifying player with that player as "you" —
/// which is all "does the same" means, and why the head effects need no re-aiming: the four
/// cards that print it gain life, draw, or create a token, none of which reads a target. A
/// compile that would put a targeted effect in here is refused at the reader, because re-running
/// somebody else's choice for another player is not what any card says.
/// <para>
/// "That player" is the player the source enchants, read at resolution; "attacking" is read
/// off the live combat, which is still in the declare-attackers step when the trigger resolves.
/// A creature attacking that player's planeswalker is not attacking the player (CR 506.2 keeps
/// the two apart), and the attacker's controller is the computed one, so a creature stolen
/// mid-combat repeats the effect for its thief. The enchanted player is never an opponent
/// "attacking that player" — nobody attacks themself — and the controller already did the head
/// effect once, so both are excluded by the word "opponent".
/// </para>
/// </remarks>
public sealed record RepeatForOpponentsAttackingEnchanted(ImmutableList<IEffect> Inner) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The Curse names "that player" through its own attachment. Unattached — it left the
        // battlefield in response, or was never on a player — there is nobody to repeat for,
        // which is CR 608.2b's "do as much as it can" rather than an error.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Permanent?.AttachedToPlayer is not { } enchanted)
        {
            return [];
        }

        var attacking = new HashSet<Guid>();

        foreach (var (attackerId, attack) in context.State.Combat.Attackers)
        {
            if (attack.IsPlaneswalker || attack.DefendingPlayer != enchanted)
                continue;

            if (context.State.TryGetObject(attackerId, out var attacker))
            {
                attacking.Add(State.Characteristics
                    .Of(context.State, context.Abilities, attacker).ControllerId);
            }
        }

        var events = new List<GameEvent>();

        foreach (var opponent in context.State.ApnapOrder())
        {
            if (opponent == context.ControllerId
                || !attacking.Contains(opponent)
                || context.State.GetPlayer(opponent).HasLost)
            {
                continue;
            }

            var theirs = context with { ControllerId = opponent };

            foreach (var effect in Inner)
                events.AddRange(effect.Resolve(theirs));
        }

        return events;
    }
}

/// <summary>
/// Does something, and does the rest only if the first part actually happened.
/// </summary>
/// <remarks>
/// "Tap target untapped creature you control. **If you do**, add {C} equal to its power." The
/// engine understood "if you do" only after a *may* - after an offer, where the answer is a
/// choice - and this is the other half of the phrase: after a **mandatory** action, where "if you
/// do" asks whether the action came off at all. A target that has left, a creature already tapped,
/// a card no longer in the graveyard: the instruction is given and nothing happens.
/// <para>
/// "Happened" is read as "produced events", which is the engine's own record of something having
/// occurred and is what every effect here already answers with. It is an approximation in one
/// direction only - an action that does nothing produces nothing - and it is stated rather than
/// hidden.
/// </para>
/// </remarks>
public sealed record IfItHappened(
    ImmutableList<IEffect> Doing, ImmutableList<IEffect> Then) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var events = new List<GameEvent>();

        foreach (var effect in Doing)
            events.AddRange(effect.Resolve(context));

        if (events.Count == 0)
            return events;

        foreach (var effect in Then)
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Effects that happen only if the spell was bargained (CR 702.166c).
/// </summary>
/// <remarks>
/// Kicker's wrapper with a different flag, and deliberately not the same one. The two abilities
/// are linked to their own cost (CR 607.2), and a card that printed both would otherwise have
/// each half answering for the other.
/// <para>
/// <see cref="Else"/> carries the sentence the clause replaces, for the cards that say
/// "instead": "deals 3 damage to target creature. If this spell was bargained, destroy that
/// creature instead" does exactly one of the two, decided by the flag as the spell resolves.
/// Empty on the additive cards, where the clause's effects simply happen on top.
/// </para>
/// </remarks>
public sealed record IfBargained(
    ImmutableList<IEffect> Effects, ImmutableList<IEffect>? Else = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bargained =
            context.State.TryGetObject(context.SourceId, out var spell) && spell.WasBargained;

        var events = new List<GameEvent>();
        foreach (var effect in bargained ? Effects : Else ?? [])
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Effects that happen only if the spell was cast using teamwork (CR 702.194b).
/// </summary>
/// <remarks>
/// The bargain wrapper with teamwork's flag, kept apart for the same CR 607.2 reason: each
/// clause is linked to its own cost, and a wrapper reading another ability's flag would have
/// one half of a card answering for the other. <see cref="Else"/> is the "instead" branch,
/// exactly as it is there — and it is also how "unless this spell was cast using teamwork"
/// compiles: everything in the else arm, nothing in the main one.
/// </remarks>
public sealed record IfTeamwork(
    ImmutableList<IEffect> Effects, ImmutableList<IEffect>? Else = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var together =
            context.State.TryGetObject(context.SourceId, out var spell) && spell.WasTeamwork;

        var events = new List<GameEvent>();
        foreach (var effect in together ? Effects : Else ?? [])
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Effects that happen only if the spell was kicked with one particular kicker cost
/// (CR 702.33f).
/// </summary>
/// <remarks>
/// <see cref="IfKicked"/> asks a yes-or-no; this asks <em>which</em>. The cost is compared by
/// its printed text because that is how the clause names it — "if it was kicked with its
/// {2}{R} kicker" — and how the payment was recorded (CR 607.2 carries the linked fact, and
/// the spelling with it).
/// </remarks>
public sealed record IfKickedWith(
    string Cost, ImmutableList<IEffect> Effects, ImmutableList<IEffect>? Else = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var paid = context.State.TryGetObject(context.SourceId, out var spell)
            && spell.KickedWith.Contains(Cost, StringComparer.OrdinalIgnoreCase);

        var events = new List<GameEvent>();
        foreach (var effect in paid ? Effects : Else ?? [])
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// A permanent explores (CR 701.44a).
/// </summary>
/// <remarks>
/// Three instructions that decompose entirely into things the engine already had: reveal the top
/// card; if it is a land put it in hand; otherwise put a +1/+1 counter on the explorer and let its
/// controller decide whether the card stays on top or goes to the graveyard. That last decision
/// <em>is</em> surveil (CR 701.25a) — look at one, choose top or graveyard — so it reuses the same
/// deferred question rather than introducing another kind of choice.
/// <para>
/// The library is looked at here rather than the choice being asked blind, because whether it is
/// a land decides which branch happens and the rules only offer a choice in one of them.
/// </para>
/// <para>
/// Named for what explores rather than for the source, because it is not always the source.
/// CR 701.44a says <em>a permanent</em> explores and <em>its</em> controller does the revealing,
/// and the cards say so too: "whenever a creature you control enters, it explores" is about the
/// creature that entered, and Path of Discovery read it as itself — an enchantment quietly
/// collecting +1/+1 counters it can do nothing with, on a card that compiled and counted as read.
/// </para>
/// </remarks>
public sealed record Explore(
    EffectSubject Subject = EffectSubject.Source, int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } explorer)
            return [];

        // Whose library is revealed from is the explorer's controller, not the ability's
        // (CR 701.44a). The two differ the moment the sentence names somebody else's creature,
        // and a permanent that has already left is read from the state as it is — last known
        // information, which is the same rule the counter below relies on.
        var who = context.State.TryGetObject(explorer, out var permanent)
            && permanent.Zone == Zone.Battlefield
                ? Characteristics.Of(context.State, context.Abilities, permanent).ControllerId
                : context.ControllerId;

        var library = context.State.GetPlayer(who).Library;
        if (library.IsEmpty)
            return [];

        var top = context.State.GetObject(library[0]);

        if (top.Card.CardTypes.HasFlag(Domain.Enums.CardType.Land))
        {
            return
            [
                new ObjectMoved(
                    library[0], ObjectId.New(), Zone.Library, Zone.Hand,
                    who, MoveCause.Other),
            ];
        }

        // CR 701.44c: the counter goes on the exploring permanent, which is the source only when
        // the sentence says so — last known information decides who explored.
        return
        [
            new CountersChanged(explorer, CounterKinds.PlusOnePlusOne, 1),
            new LookAtTopRequested(who, 1, ToGraveyard: true),
        ];
    }
}

/// <summary>
/// Proliferate (CR 701.34a).
/// </summary>
/// <remarks>
/// "Choose any number of permanents and/or players with counters on them, then give each another
/// counter of each kind already there." The choosing is the whole mechanic and it is the player's,
/// so this records that the question is owed and the engine asks it — the same shape as scry.
/// </remarks>
public sealed record Proliferate : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return [new ProliferateRequested(context.ControllerId)];
    }
}

/// <summary>
/// Choose a permanent you control and move it (CR 609.4).
/// </summary>
/// <remarks>
/// "Return a land you control to its owner's hand", "sacrifice a creature" as an effect rather
/// than a cost. It is a <em>choice</em>, not a target: made on resolution, so hexproof does not
/// apply and nothing fizzles when there is nothing to pick.
/// <para>
/// <see cref="EffectIndex"/> is the locator that lets the engine find this record again when the
/// answer arrives, the same way <see cref="MayPay"/> finds its branches — the filter cannot ride
/// on the event, because a delegate is not something a log can rebuild.
/// </para>
/// </remarks>
public sealed record ChooseAndMove(
    TargetSpec What,
    Zone Destination,
    MoveCause Cause,
    int EffectIndex = 0,
    PlayerScope Scope = PlayerScope.You,
    int? TargetIndex = null,
    Zone From = Zone.Battlefield) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        // "Each opponent sacrifices a creature" is one question per opponent, and each of them
        // picks from their own board — which is why the request carries who is being asked rather
        // than the effect assuming the controller. The engine asks them one at a time in the
        // order they are owed, and a player with nothing to give is skipped rather than stalled.
        // A named target replaces the scope outright, the same way a targeted discard does.
        var asked = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        return
        [
            .. asked.Select(who => new ChoosePermanentRequested(
                who,
                context.PhysicalSourceId,
                abilityId,
                EffectIndex,
                What.Description)),
        ];
    }
}

/// <summary>
/// Takes control of a target until end of turn (CR 613.1b).
/// </summary>
/// <remarks>
/// Its own effect rather than a named continuous effect chosen at compile time, because who takes
/// the creature is not known until the spell resolves — the same card cast by either player
/// steals it for whoever cast it.
/// </remarks>
/// <summary>
/// "Gain control of [target] for as long as you control [this]" (CR 611.2b).
/// </summary>
/// <remarks>
/// The same control effect as its until-end-of-turn sibling with a different clock: no turn
/// number, and a condition carried in the definition's id so the state stays a fold of the log.
/// The condition names the source as it is *now* - a permanent that leaves and returns is a new
/// object (CR 400.7), the old id names nothing, and the effect ends, which is what the card says.
/// </remarks>
/// <summary>
/// "[Target] gains protection from the color of your choice until end of turn" (CR 202.2).
/// </summary>
/// <remarks>
/// The colour is not known when the effect resolves, so the effect cannot finish on its own: it
/// asks, and the answer builds the grant. That is the same shape as every other question the
/// game stops for, and the request carries the permanents it is about so nothing has to be
/// looked up again once the answer arrives.
/// </remarks>
/// <summary>
/// "Amass Orcs 2" — grow an Army, making one first if you have none (CR 701.44a).
/// </summary>
/// <remarks>
/// Two steps that have to happen in order and in one resolution: the token is created *then* the
/// counters go on it, so a player with no Army ends with a 2/2 rather than with a 0/0 that dies
/// to state-based actions before anything can grow it.
/// <para>
/// The token is created here and the counters are asked for separately, which works because the
/// request carries the candidates it was built with - the new Army among them - rather than
/// recomputing the set after the token has arrived.
/// </para>
/// </remarks>
public sealed record Amass(string Kind, Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;
        var many = Count.In(context);
        if (many <= 0)
            return [];

        var armies = context.State.Battlefield
            .Select(context.State.GetObject)
            .Where(o => Characteristics.Of(context.State, context.Abilities, o).ControllerId == you
                && o.Card.Subtypes.Contains("Army", StringComparer.OrdinalIgnoreCase))
            .Select(o => o.Id)
            .ToImmutableList();

        if (!armies.IsEmpty)
        {
            return [new CounterChoiceRequested(
                you, armies, CounterKinds.PlusOnePlusOne, many)];
        }

        // CR 701.44b: no Army means one is created first, and it is a 0/0 - it only survives
        // because the counters arrive in the same resolution, before state-based actions run.
        var token = new Domain.Models.CardDefinition
        {
            OracleId = "token-army-" + Kind.ToLowerInvariant(),
            Name = Kind + " Army",
            CardTypes = Domain.Enums.CardType.Creature | Domain.Enums.CardType.Token,
            Subtypes = [Kind, "Army"],
            Power = 0,
            Toughness = 0,
            Colors = [Domain.Enums.ManaColor.Black],
            ColorIdentity = [Domain.Enums.ManaColor.Black],
        };

        var made = ObjectId.New();

        return
        [
            new ObjectCreated(made, token, you, you, Zone.Battlefield),
            new CountersChanged(made, CounterKinds.PlusOnePlusOne, many),
        ];
    }
}

/// <summary>
/// "Bolster N" — counters on the smallest creature you control (CR 701.36a).
/// </summary>
/// <remarks>
/// The candidate set is the whole of the keyword: creatures you control tied for the least
/// toughness. With one that is not a choice and the counters simply land; with a tie the rules
/// say the player chooses, and the ask is what says so.
/// <para>
/// Toughness is the computed one, not the printed one - a 1/1 under an anthem is not the
/// smallest creature on a board with a printed 2/2 (CR 613).
/// </para>
/// </remarks>
/// <summary>"Blight N" - N -1/-1 counters on a creature you choose (CR 701.68a).</summary>
/// <remarks>
/// Bolster's mirror, and it borrows bolster's machinery entirely: the same request asking which
/// of a set of candidates the counters go on. The two differ in the set - bolster is forced onto
/// the least tough and this is free - and in which counter, and in nothing else.
/// </remarks>
public sealed record Blight(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;

        var mine = context.State.Battlefield
            .Select(context.State.GetObject)
            .Select(o => (Object: o, Computed: Characteristics.Of(context.State, context.Abilities, o)))
            .Where(pair => pair.Computed.IsCreature && pair.Computed.ControllerId == you)
            .Select(pair => pair.Object)
            .Select(o => o.Id)
            .ToImmutableList();

        var many = Count.In(context);

        return mine.IsEmpty || many <= 0
            ? []
            : [new CounterChoiceRequested(you, mine, CounterKinds.MinusOneMinusOne, many)];
    }
}

public sealed record Bolster(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;

        var mine = context.State.Battlefield
            .Select(context.State.GetObject)
            .Select(o => (Object: o, Computed: Characteristics.Of(context.State, context.Abilities, o)))
            .Where(pair => pair.Computed.IsCreature && pair.Computed.ControllerId == you)
            .ToList();

        if (mine.Count == 0)
            return [];

        var least = mine.Min(pair => pair.Computed.Toughness ?? 0);

        var smallest = mine
            .Where(pair => (pair.Computed.Toughness ?? 0) == least)
            .Select(pair => pair.Object.Id)
            .ToImmutableList();

        var many = Count.In(context);

        return many <= 0
            ? []
            : [new CounterChoiceRequested(
                you, smallest, CounterKinds.PlusOnePlusOne, many)];
    }
}

/// <summary>
/// "Look at the top four cards of your library, then put them back in any order" (CR 701.19a).
/// </summary>
/// <remarks>
/// Looking is what makes the arrangement meaningful, and both halves are one instruction: the
/// cards never leave the library, so nothing moves and nothing changes identity. The order is
/// stated once, when the player has answered.
/// </remarks>
public sealed record LookAtTopThenArrange(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var many = Count.In(context);
        if (many <= 0)
            return [];

        var top = context.State.GetPlayer(context.ControllerId).Library
            .Take(many)
            .ToImmutableList();

        // Fewer cards than the card names is not an error - a library can be short - and one card
        // has only one order, so neither is worth stopping the game for.
        return top.Count < 2
            ? []
            : [new LibraryOrderRequested(context.ControllerId, top)];
    }
}

/// <summary>
/// "Untap up to three lands" — the controller picks, as it resolves (CR 701.21a).
/// </summary>
/// <remarks>
/// No target is named, so the candidates are whatever is tapped when the effect resolves rather
/// than what was tapped when it was cast. The filter is the one every other group phrase uses, so
/// "lands" and "artifacts you control" mean here exactly what they mean everywhere else.
/// </remarks>
public sealed record UntapUpTo(
    Amount Most,
    Func<GameState, GameObject, Guid, bool> Candidate) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var you = context.ControllerId;

        var tapped = context.State.Battlefield
            .Select(context.State.GetObject)
            .Where(o => o.Permanent?.IsTapped == true && Candidate(context.State, o, you))
            .Select(o => o.Id)
            .ToImmutableList();

        // Nothing tapped is not a question: an effect that stopped the game to offer an empty
        // list would be a hang rather than a choice.
        var most = Most.In(context);

        return tapped.IsEmpty || most <= 0
            ? []
            : [new UntapChoiceRequested(you, tapped, most)];
    }
}

/// <summary>
/// "Target land becomes the basic land type of your choice until end of turn" (CR 305.7).
/// </summary>
/// <remarks>
/// The colour choice's shape with a different menu, and it borrows that effect's two guards for
/// the same reasons: a target that was not a permanent leaves nothing to ask about, and a land
/// that has left the battlefield is skipped rather than asked about (CR 608.2b) — stopping the
/// whole game to name a type for a permanent that is no longer there is a hang, not a choice.
/// <para>
/// <strong>The duration is not carried here.</strong> Every printed card in this family says
/// "until end of turn", the compiler refuses the sentence without one, and what the answer builds
/// is a floating effect stamped with the turn number. An indefinite arm would be a different
/// duration, not a flag on this: an effect that outlives what it should is as wrong as one that
/// ends early.
/// </para>
/// </remarks>
public sealed record ChooseBasicLandTypeForTarget(
    bool InAddition,
    int? TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Permanent } target
                ? target.Subject
                : (ObjectId?)null
            : context.PhysicalSourceId;

        if (subject is not { } chosen)
            return [];

        if (!context.State.TryGetObject(chosen, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new LandTypeChoiceRequested(
                context.ControllerId, context.PhysicalSourceId, [chosen], InAddition),
        ];
    }
}

public sealed record ChooseColorForTarget(
    ColorChoiceUse Use,
    int? TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // No index means the source itself - "~ becomes the color of your choice" rather than
        // "target permanent becomes ...". The two sentences differ only in what they aim at, so
        // they share the effect rather than having one each.
        var subject = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Permanent } target
                ? target.Subject
                : (ObjectId?)null
            : context.PhysicalSourceId;

        if (subject is not { } chosen)
            return [];

        // A subject that has left is skipped rather than asked about (CR 608.2b): choosing a
        // colour for a permanent that is no longer there would stop the game for nothing.
        if (!context.State.TryGetObject(chosen, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ColorChoiceRequested(
                context.ControllerId, context.PhysicalSourceId, [chosen], Use),
        ];
    }
}

/// <summary>
/// "Target creature blocks ~ this turn if able" - provoke and its relatives (CR 509.1a).
/// </summary>
/// <remarks>
/// The attacker is the source and is only known while the ability resolves, so the id is built
/// here rather than at compile time the way a fixed requirement's is. That is the whole reason
/// this is an effect of its own and not another <see cref="PumpUntilEndOfTurn"/>.
/// </remarks>
/// <summary>"Target creature can't block ~ this turn" (CR 509.1b).</summary>
/// <remarks>
/// The mirror of <see cref="MustBlockSource"/>, and an effect of its own for the same reason:
/// the attacker it names is the source, known only while the ability resolves.
/// </remarks>
/// <summary>Suspects a permanent - menace and no blocking, indefinitely (CR 701.60c).</summary>
/// <remarks>
/// "Until it leaves the battlefield" needs no expiry of its own: a permanent that leaves is a
/// new object (CR 400.7), so an effect keyed to the old id applies to nothing from that moment.
/// The designation is not an ability and not copiable (CR 701.60b), which is why it is granted
/// as a continuous effect on the object rather than written onto the card.
/// </remarks>
/// <summary>Goads a creature until the goader's next turn (CR 701.15a).</summary>
/// <remarks>
/// The goader is the ability's controller and the turn is now, and both have to travel with the
/// effect - it carries no state of its own and the requirement has to outlive the resolution
/// that made it.
/// </remarks>
public sealed record GoadTarget(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.GoadedById(
                    context.ControllerId, context.State.TurnNumber),
                [target.Subject],

                // The duration is the While predicate on the definition, not a turn number: it
                // ends on somebody's next turn rather than at the end of this one.
                UntilEndOfTurn: null),
        ];
    }
}

public sealed record SuspectTarget(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.GrantId(
                    KeywordAbility.Menace | KeywordAbility.CantBlock),
                [target.Subject],
                UntilEndOfTurn: null),
        ];
    }
}

public sealed record CantBlockSource(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.CantBlockAttackerId(source.Id),
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

public sealed record MustBlockSource(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        // A requirement to block something no longer on the battlefield is no requirement, and
        // the id would name nothing.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.MustBlockAttackerId(source.Id),
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

public sealed record GainControlWhileSourceHolds(
    Cards.GenerativeEffects.ControlHeldWhile Until,
    int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        // The source has to be a permanent for the condition to mean anything. An ability whose
        // source has already left takes nothing rather than taking it for ever.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.ControlWhileId(context.ControllerId, source.Id, Until),
                [target.Subject],
                UntilEndOfTurn: null),
        ];
    }
}

/// <summary>
/// Puts a named continuous effect on a target that lasts "for as long as …" (CR 611.2b).
/// </summary>
/// <remarks>
/// <see cref="PumpUntilEndOfTurn"/> with the other kind of duration, and the only difference is
/// which one: that one is ended by the turn number in the cleanup step, and this one by a
/// condition the definition carries. Folding the condition into the effect's own <c>Applies</c>
/// instead would be a different card — CR 611.2b says an effect whose duration ends is over and
/// does not start again, so a creature pumped "for as long as this artifact remains tapped" would
/// otherwise get its bonus back every time the artifact was tapped again.
/// <para>
/// The source has to be a permanent for the condition to mean anything, and an ability whose
/// source has already left does nothing rather than doing it for ever. That is the same guard
/// <see cref="GainControlWhileSourceHolds"/> makes, and for the same reason: the id names an
/// object, and an object that is gone is a different one (CR 400.7).
/// </para>
/// </remarks>
public sealed record HoldsWhileSourceHolds(
    string DefinitionId,
    Cards.GenerativeEffects.ControlHeldWhile Until,
    int TargetIndex = 0,
    EffectSubject Subject = EffectSubject.Target) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Subjects.Resolve(context, Subject, TargetIndex) is not { } affected)
            return [];

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || source.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.HeldWhileId(
                    DefinitionId, context.ControllerId, source.Id, Until),
                [affected],
                UntilEndOfTurn: null),
        ];
    }
}

public sealed record GainControlUntilEndOfTurn(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target)
            return [];

        return
        [
            new ContinuousEffectCreated(
                Guid.NewGuid(),
                Cards.GenerativeEffects.ControlId(context.ControllerId),
                [target.Subject],
                context.State.TurnNumber),
        ];
    }
}

/// <summary>
/// Flip a coin, and do one thing or the other (CR 705.2).
/// </summary>
/// <remarks>
/// Deferred like a question, though nobody is being asked: an effect returns events and cannot
/// reach the randomness, which lives on the game so that every random outcome in a match comes
/// from one seeded source and lands in the log. The engine flips at the next settle and runs the
/// branch the coin names.
/// </remarks>
/// <summary>"Clash with an opponent. If you win, ..." (CR 701.30b).</summary>
/// <remarks>
/// The coin flip's sibling, and it carries its branch the same way - through a locator back into
/// the card, because effects cannot travel in a log. What it does not share is the middle: a
/// clash reveals two cards and lets both players decide where theirs goes <em>before</em> the
/// winner matters, and those decisions change what a winner's "draw a card" draws.
/// </remarks>
public sealed record Clash(ImmutableList<IEffect> IfWon, int EffectIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        return
        [
            new ClashRequested(
                context.ControllerId, context.PhysicalSourceId, abilityId, EffectIndex)
            {
                SubjectObject = context.SubjectObject,
            },
        ];
    }
}

public sealed record FlipCoin(
    ImmutableList<IEffect> IfWon, ImmutableList<IEffect> IfLost, int EffectIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        return
        [
            new CoinFlipRequested(
                context.ControllerId, context.PhysicalSourceId, abilityId, EffectIndex)
            {
                SubjectObject = context.SubjectObject,
            },
        ];
    }
}

/// <summary>One striation of a results table (CR 706.3a), or a sentence after the roll.</summary>
/// <param name="From">The first result the row covers.</param>
/// <param name="To">The last result it covers, or null for the open-ended "N+" row.</param>
/// <remarks>
/// An <see cref="IEffect"/> that never resolves on its own: the engine reads the rows out of the
/// <see cref="RollDice"/> that holds them and runs the ones the result lands in. It wears the
/// interface anyway because that is what makes its contents visible — the effect tree walks
/// properties typed as effects, and rows hidden behind any other type would carry targets the
/// structural checks could not see were read.
/// <para>
/// A sentence that simply uses the result — "you gain life equal to the result" — is a row
/// covering every result, which is what CR 706.3a's "if any" collapses to when the table has no
/// striations. One shape, so the settle runs the whole answer in printed order.
/// </para>
/// </remarks>
public sealed record RollBranch(int From, int? To, ImmutableList<IEffect> Effects) : IEffect
{
    /// <summary>Whether a result falls inside this row (CR 706.3a).</summary>
    public bool Covers(int result) => result >= From && (To is null || result <= To);

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context) => [];
}

/// <summary>
/// Roll a die and do what the results table says (CR 706).
/// </summary>
/// <remarks>
/// Deferred exactly as the coin flip is, and for the same reason: an effect returns events and
/// cannot reach the randomness, which lives on the game so that every random outcome in a match
/// comes from one seeded source and lands in the log as its outcome. The engine rolls at the next
/// settle, records the number, and runs every row of the table the result falls in — with the
/// result as the branch's subject amount, so "equal to the result" inside a row reads the number
/// that actually came up.
/// </remarks>
public sealed record RollDice(
    int Sides, ImmutableList<RollBranch> Rows, int EffectIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var abilityId = context.AbilityId
            ?? (context.State.TryGetObject(context.SourceId, out var onStack)
                ? onStack.Ability?.AbilityId
                : null);

        return
        [
            new DiceRollRequested(
                context.ControllerId, context.PhysicalSourceId, abilityId, EffectIndex, Sides)
            {
                SubjectObject = context.SubjectObject,
                Targets = context.Targets,
            },
        ];
    }
}

/// <summary>
/// "If the roll was N or higher, ..." — a clause of a dice trigger's effect (CR 706.4).
/// </summary>
/// <remarks>
/// Not an <see cref="OnlyIf"/>, because the number it tests is nowhere on the board: the result
/// travels with the trigger as its subject amount (CR 603.2's "that many"), and a board condition
/// is handed a state and a permanent, neither of which remembers what was rolled. Reading the
/// context is the whole of the difference.
/// </remarks>
public sealed record OnlyIfRollAtLeast(int Least, ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.SubjectAmount is not { } rolled || rolled < Least)
            return [];

        var events = new List<GameEvent>();
        foreach (var effect in Effects)
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>Makes a permanent monstrous, with the counters that come with it (CR 701.32a).</summary>
/// <remarks>
/// One effect and not two, because the rule is one action: a creature that is already monstrous
/// gets neither the counters nor the designation, and splitting them would let a second
/// activation put counters on without the condition ever being asked.
/// </remarks>
/// <summary>
/// Runs an add-mana effect and keeps exactly what it produced through the emptying (CR 500.4).
/// </summary>
/// <remarks>
/// A wrapper rather than a variant of each add-mana effect, because the sentence before this one
/// takes a dozen different shapes - a fixed string of symbols, one per artifact you control, "that
/// much mana of any one color" - and every one of them ends with the same clause. Wrapping reads
/// all of them at once; fusing would need a persistent twin of each.
/// <para>
/// It reads its own inner effect's events to learn the amount, which is the only place that
/// number exists: the pool at this moment may also hold mana from elsewhere, and keeping the pool
/// would hand the player mana this ability never made.
/// </para>
/// </remarks>
public sealed record KeepManaAdded(IEffect Inner, Mana.ManaPersistence Until) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var produced = Inner.Resolve(context);

        var added = Mana.ManaPool.Empty;
        foreach (var e in produced.OfType<ManaAdded>())
        {
            if (e.PlayerId != context.ControllerId)
                continue;

            added = e.Color is { } color
                ? added.Add(color, e.Amount)
                : added.AddColorless(e.Amount);
        }

        if (added.IsEmpty)
            return produced;

        return [.. produced, new ManaMadePersistent(context.ControllerId, added, Until)];
    }
}

/// <summary>
/// "The owner of target nonland permanent puts it on their choice of the top or bottom of their
/// library."
/// </summary>
/// <remarks>
/// The choice belongs to the owner rather than to whoever cast this, which is the whole reason
/// it is asked at all: a card that let the caster decide would be a strictly better removal
/// spell than the one printed.
/// </remarks>
public sealed record OwnerChoosesLibraryEnd(int TargetIndex = 0) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TargetAt(TargetIndex) is not { Kind: TargetKind.Permanent } target
            || !context.State.TryGetObject(target.Subject, out var doomed)
            || doomed.Zone != Zone.Battlefield)
        {
            return [];
        }

        return [new LibraryEndChoiceRequested(doomed.OwnerId, doomed.Id)];
    }
}

/// <summary>"Cipher" - the spell may exile itself encoded on a creature (CR 702.99a).</summary>
public sealed record CipherSelf : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new CipherRequested(context.SourceId, context.ControllerId)];
    }
}

/// <summary>
/// "Exile ~ with three time counters on it" - the spell suspends itself as it finishes.
/// </summary>
/// <remarks>
/// Every printing of this also carries suspend, so the ticking and the free cast at the end are
/// already on the card: what was missing is only the exile that starts the clock. Asked for
/// rather than performed, because the card is still the spell on the stack right now and the
/// counters have to land on the object it becomes (CR 400.7).
/// </remarks>
public sealed record SuspendSourceOnResolve(int Counters) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new SuspendOnResolveRequested(context.SourceId, Counters)];
    }
}

/// <summary>
/// "You may put a land card from your hand onto the battlefield" - not a land drop (CR 305.1).
/// </summary>
/// <remarks>
/// The same choice machinery an opponent's discard uses, pointed at the controller's own hand and
/// allowed to decline. It is deliberately not a land play: a land put onto the battlefield this
/// way does not use the turn's one land drop, and reading it as a play would be a strictly worse
/// card on every turn the player had not yet played a land.
/// </remarks>
public sealed record PutFromHandOntoBattlefield(string FilterId, bool Tapped = false) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new HandChoiceRequested(
                context.ControllerId,
                context.ControllerId,
                context.PhysicalSourceId,
                context.AbilityId,
                EffectIndex: 0,
                string.Equals(FilterId, SearchFilters.AnyCard, StringComparison.Ordinal)
                    ? "a card"
                    : $"a {FilterId} card",
                FilterId,
                Zone.Battlefield,
                Optional: true,
                Tapped),
        ];
    }
}

/// <summary>
/// "When ~ enters, sacrifice it unless you [pay a cost that is not mana]."
/// </summary>
/// <remarks>
/// The same offer <see cref="MayPay"/> makes, with the "if you don't" branch fixed: this
/// permanent goes. It was its own effect because the offer could only charge mana, life and
/// energy; now that the offer can charge a selection too, the two ask the same question and
/// share the answering — <c>Game.PayableFor</c> lists what may be given up and
/// <c>Game.TakeChosenPayment</c> moves it, for both. What stays separate is only what the two
/// requests carry: this one names the permanent at stake, so the question can say what declining
/// costs, and that is a thing to say rather than a branch to run.
/// </remarks>
public sealed record SacrificeSourceUnlessPaid(
    ChosenCostKind Kind, int Count, string FilterId) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The permanent has to still be there to be sacrificed, and it is the permanent rather
        // than the trigger on the stack - the trigger's source id is the object that entered.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var self)
            || self.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new SacrificeUnlessPaidRequested(
                context.ControllerId, self.Id, Kind, Count, FilterId),
        ];
    }
}

/// <summary>"Take an extra turn after this one" (CR 500.7).</summary>
/// <remarks>
/// The subject is a target when the card names one and the controller otherwise, which is the
/// same choice every other player-aimed effect makes. It is queued rather than taken: the current
/// turn finishes first, and the rule is explicit that the extra one comes after it.
/// </remarks>
public sealed record TakeExtraTurn(int? TargetIndex = null) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TargetIndex is not { } index)
            return [new ExtraTurnCreated(context.ControllerId)];

        return context.TargetAt(index) is { Kind: TargetKind.Player } chosen
            && context.State.Players.ContainsKey(chosen.Player)
            ? [new ExtraTurnCreated(chosen.Player)]
            : [];
    }
}

/// <summary>Lets the controller play extra lands for the rest of this turn (CR 505.6b).</summary>
public sealed record GrantExtraLandDrop(int Count = 1) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return [new ExtraLandDropGranted(context.ControllerId, Count)];
    }
}

/// <summary>Makes the source permanent prepared, so its spell is available to copy.</summary>
/// <remarks>
/// The subject is always the source: every printing says "it", "this creature" or the card's own
/// name, and each of those is the permanent the ability is printed on. A permanent that is
/// already prepared is unchanged rather than doubly so - the designation is a fact about the
/// permanent, not a resource that stacks.
/// </remarks>
public sealed record PrepareSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var self)
            || self.Permanent is not { IsPrepared: false }
            || context.Abilities.PreparedSpellOf(self.Card) is null)
        {
            return [];
        }

        return [new BecamePrepared(self.Id)];
    }
}

public sealed record Monstrosity(Amount Count) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.State.TryGetObject(context.PhysicalSourceId, out var self)
            || self.Permanent is not { IsMonstrous: false })
        {
            return [];
        }

        return
        [
            new CountersChanged(self.Id, CounterKinds.PlusOnePlusOne, Count.In(context)),
            new BecameMonstrous(self.Id),
        ];
    }
}

/// <summary>
/// Does something only while a condition holds (CR 603.4).
/// </summary>
/// <remarks>
/// The intervening-if clause. CR 603.4 checks it twice — once when the ability would trigger, and
/// again as it resolves — so the trigger predicate carries the same condition and this is the
/// second half. A creature that was there when the upkeep began and has since died stops the
/// ability doing anything, which is the whole reason the rule checks twice.
/// <para>
/// The predicate is a delegate, which nothing in the state holds: effects live in the compiled
/// card definition and are rebuilt from the card, exactly like a trigger's own condition.
/// </para>
/// </remarks>
public sealed record OnlyIf(
    BoardCondition Condition,
    ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The seat CR 603.4 means by "that player" is the one the triggering event named, and
        // the resolution already carries it. Passed rather than dropped, so an intervening-if
        // about a particular player is re-checked about the same player it was checked about
        // when the ability triggered — which is the entire point of a rule that checks twice.
        if (!context.State.TryGetObject(context.PhysicalSourceId, out var source)
            || !Condition(context.State, context.Abilities, source, context.SubjectPlayer))
        {
            return [];
        }

        var events = new List<GameEvent>();
        foreach (var effect in Effects)
            events.AddRange(effect.Resolve(context));

        return events;
    }
}

/// <summary>
/// Binds X to a count for the sentence that defined it — "..., where X is the number of ...".
/// </summary>
/// <remarks>
/// X is ordinarily a number the caster chose (CR 601.2b), and every effect that uses one reads it
/// off the resolution context. A sentence that <em>defines</em> X instead is not asking for a
/// different kind of amount; it is answering the same question a different way. So this wraps the
/// sentence and fills the answer in, and every effect inside keeps reading X exactly as it did.
/// <para>
/// Wrapping rather than rewriting the amounts is the point. The alternative is walking the parsed
/// effects and replacing every variable amount inside them, which means knowing the shape of each
/// one — and there are dozens, some nested. This knows nothing about them.
/// </para>
/// <para>
/// The count is taken when the sentence resolves, not when the spell was cast: "where X is the
/// number of creatures you control" on a spell that killed a creature earlier in its own text
/// counts what is left, which is what the card says.
/// </para>
/// </remarks>
public sealed record WithCountedVariable(
    Func<ResolutionContext, int> Count, ImmutableList<IEffect> Effects) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bound = context with { VariableValue = Count(context) };

        var events = new List<GameEvent>();
        foreach (var effect in Effects)
            events.AddRange(effect.Resolve(bound));

        return events;
    }
}

/// <summary>Untaps the source itself (CR 701.26b).</summary>
public sealed record UntapSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        return context.State.TryGetObject(sourceId, out var permanent)
            && permanent.Permanent?.IsTapped == true
                ? StunCounters.Untapping(context.State, [sourceId])
                : [];
    }
}

/// <summary>Ascend - ten permanents buys the city's blessing (CR 702.131a).</summary>
/// <remarks>
/// The count is of <em>permanents</em>, not of creatures and not of cards: lands and the
/// enchantment asking the question are all in it, which is what makes ten reachable at all.
/// <para>
/// Doing nothing when the player already has it is the rule rather than an optimisation - the
/// ability reads "and you don't have the city's blessing" - and it is also what keeps a state
/// trigger from firing forever, since the condition it watches stays true once ten permanents
/// are out.
/// </para>
/// </remarks>
public sealed record GainCitysBlessing : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.State.GetPlayer(context.ControllerId).HasCitysBlessing)
            return [];

        var permanents = context.State.Battlefield.Count(
            id => context.State.TryGetObject(id, out var permanent)
                && permanent.ControllerId == context.ControllerId);

        return permanents >= 10 ? [new CitysBlessingGained(context.ControllerId)] : [];
    }
}

/// <summary>
/// The controller becomes the monarch (CR 725.3).
/// </summary>
/// <remarks>
/// Nothing to check and nothing to refuse: a player who is already the monarch becoming it again
/// is a no-op the state handles, and the previous monarch stops being one by the same event.
/// Unlike the city's blessing this is not one-way - the whole point of the designation is that it
/// moves, and it moves most often because somebody hit its holder in combat.
/// </remarks>
public sealed record BecomeTheMonarch : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.State.MonarchId == context.ControllerId
            ? []
            : [new MonarchChanged(context.ControllerId)];
    }
}

/// <summary>
/// The controller takes the initiative (CR 726.1) and ventures into Undercity for it.
/// </summary>
/// <remarks>
/// Deliberately <em>not</em> the monarch's no-op when the controller already holds it: CR 726.5
/// says a holder instructed to take the initiative takes it again — no second designation, but
/// the "whenever a player takes the initiative" inherent ability (CR 726.2) triggers and they
/// venture into Undercity all the same. So the taking is always emitted and the venture always
/// follows, with the same stated simplification the monarch's inherent abilities make: the rules
/// give these triggers no source, this engine keys pending triggers to a permanent, so the
/// venture happens directly and the window between trigger and venture is what is lost. The
/// <em>room</em> the marker then enters is a real triggered ability on the dungeon card and uses
/// the stack as normal.
/// </remarks>
public sealed record TakeTheInitiative : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            new InitiativeTaken(context.ControllerId),
            .. Dungeons.VentureEvents(context.State, context.ControllerId, Dungeons.Undercity),
        ];
    }
}

/// <summary>
/// The creature this ability belongs to becomes renowned (CR 702.112b).
/// </summary>
public sealed record BecomeRenowned : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        return context.State.TryGetObject(subject, out var hero)
            && hero.Permanent is { IsRenowned: false }
            ? [new BecameRenowned(subject)]
            : [];
    }
}

/// <summary>
/// The Case this ability belongs to becomes solved (CR 719.3a).
/// </summary>
/// <remarks>
/// The permanent behind the ability, not the ability on the stack - the same distinction every
/// other self-affecting effect here draws.
/// </remarks>
public sealed record SolveCase : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        return context.State.TryGetObject(subject, out var investigation)
            && investigation.Permanent is { IsSolved: false }
            ? [new CaseSolved(subject)]
            : [];
    }
}

/// <summary>
/// The Class this ability belongs to becomes the given level (CR 716.2a).
/// </summary>
/// <remarks>
/// A level bar sets the level rather than raising it by one. The two are the same thing while
/// the bars are activated in order - which the activation condition enforces - and writing it as
/// "gain a level" would quietly disagree with the card the moment anything else set a level.
/// <para>
/// The permanent behind the ability, not the ability on the stack: an ability is its own object
/// while it resolves, and the level belongs to the enchantment.
/// </para>
/// </remarks>
public sealed record GainClassLevel(int Level) : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        return context.State.TryGetObject(subject, out var klass) && klass.Permanent is not null
            ? [new ClassLevelChanged(subject, Level)]
            : [];
    }
}

/// <summary>
/// The Mount this ability belongs to becomes saddled (CR 702.171a).
/// </summary>
/// <remarks>
/// The permanent behind the ability rather than the ability on the stack, which is the same trap
/// <see cref="TransformSource"/> fell into: an ability is its own object while it resolves.
/// </remarks>
public sealed record SaddleSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subject = context.PhysicalSourceId;

        return context.State.TryGetObject(subject, out var mount) && mount.Permanent is not null
            ? [new PermanentSaddled(subject)]
            : [];
    }
}

/// <summary>
/// Turns the source to its other face (CR 701.28, "transform").
/// </summary>
/// <remarks>
/// Only a permanent with another face can transform, and a permanent already showing the face it
/// is told to show does nothing - both are silent no-ops rather than errors, because the rules
/// treat an impossible transform as simply not happening (CR 712.9).
/// </remarks>
public sealed record TransformSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The permanent behind the ability, not the ability on the stack. A triggered ability is
        // its own object while it is resolving, and asking that object for its faces finds none -
        // which is how the first version of this fired, resolved and turned nothing over.
        var subject = context.PhysicalSourceId;

        if (!context.State.TryGetObject(subject, out var permanent)
            || permanent.Permanent is not { } onBattlefield
            || permanent.Card.Faces.Count < 2)
        {
            return [];
        }

        var next = onBattlefield.FaceIndex == 0 ? 1 : 0;
        return [new PermanentTransformed(subject, next)];
    }
}

/// <summary>Sacrifices the source itself (CR 701.21).</summary>
/// <summary>
/// Exiles the permanent this ability belongs to (CR 701.13a).
/// </summary>
/// <remarks>
/// The twin of <see cref="SacrificeSource"/>, and it is a separate effect rather than a flag on
/// one because the two are different actions: a sacrifice can be watched for and an exile cannot,
/// and nothing that triggers on a permanent being sacrificed should fire for this.
/// </remarks>
public sealed record ExileSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var permanent)
            || permanent.Zone is Zone.Exile)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                sourceId, ObjectId.New(), permanent.Zone, Zone.Exile, permanent.OwnerId,
                MoveCause.Exile),
        ];
    }
}

public sealed record SacrificeSource : IEffect
{
    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sourceId = context.PhysicalSourceId;

        if (!context.State.TryGetObject(sourceId, out var permanent)
            || permanent.Zone != Zone.Battlefield)
        {
            return [];
        }

        return
        [
            new ObjectMoved(
                sourceId, ObjectId.New(), Zone.Battlefield, Zone.Graveyard, permanent.OwnerId,
                MoveCause.Sacrifice),
        ];
    }
}

/// <summary>Creates a token under the controller's control (CR 111.1).</summary>
public sealed record CreateToken(
    Domain.Models.CardDefinition Token,
    Amount Count = default,
    bool Tapped = false) : IEffect
{
    /// <summary>Who gets the tokens, when the sentence names somebody other than you.</summary>
    /// <remarks>
    /// A token is created under a player's control (CR 111.1), and which player is a question
    /// the sentence answers — "each opponent creates a Treasure token" gives them one each.
    /// </remarks>
    public PlayerScope Scope { get; init; } = PlayerScope.You;

    /// <summary>A named player, when the sentence targets one rather than naming a group.</summary>
    public int? TargetIndex { get; init; }

    /// <summary>What happens to each token later - "sacrifice it at the beginning of the next
    /// end step" (CR 603.7b).</summary>
    public DelayedTokenAction? Delayed { get; init; }

    public IReadOnlyList<GameEvent> Resolve(ResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // "Create a token" with no number means one, and a default Amount is zero — so an unset
        // count becomes one. A count that was given is used as it stands, including when it
        // counts to nothing: "a token for each artifact you control" with no artifacts makes no
        // tokens, and flooring that at one would be a card doing something it does not say.
        var count = Count.Equals(default(Amount)) ? 1 : Math.Max(0, Count.In(context));

        // A named target wins outright over a scope, the same way it does for drawing, life and
        // discarding: the sentence named one player and the scope named none.
        var owners = TargetIndex is { } index
            ? context.TargetAt(index) is { Kind: TargetKind.Player } aimed
                ? (IEnumerable<Guid>)[aimed.Player]
                : []
            : PlayerScopes.Resolve(Scope, context);

        var made = new List<GameEvent>();

        foreach (var owner in owners)
        {
            foreach (var _ in Enumerable.Range(0, count))
            {
                var id = ObjectId.New();

                made.Add(new ObjectCreated(id, Token, owner, owner, Zone.Battlefield));

                if (Tapped)
                    made.Add(new PermanentTapped(id));

                // CR 603.7d: the delayed ability is controlled by the player who controlled the
                // effect that created it, which is not always the player who got the token -
                // "each opponent creates a Treasure token" hands them out and keeps the say.
                if (Delayed is { } later)
                {
                    made.Add(new DelayedTriggerCreated(
                        Guid.NewGuid(),
                        context.ControllerId,
                        id,
                        later.Step,
                        later.EffectId,
                        context.State.TurnNumber));
                }
            }
        }

        return made;
    }
}
