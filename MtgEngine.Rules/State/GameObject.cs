using System.Collections.Immutable;
using MtgEngine.Domain.Models;

namespace MtgEngine.Rules.State;

/// <summary>
/// Identity of one object in one zone at one time.
/// </summary>
/// <remarks>
/// Deliberately not the card's identity. CR 400.7: "An object that moves from one zone to
/// another becomes a new object with no memory of, or relation to, its previous existence."
/// A creature that dies and is returned is a different object from the one that died, and an
/// engine that reuses an id there will happily let a dead creature's aura reattach to it.
/// <para>
/// A struct wrapper rather than a bare <see cref="Guid"/> so a permanent id and a player id
/// cannot be passed to each other's parameters.
/// </para>
/// </remarks>
public readonly record struct ObjectId(Guid Value)
{
    public static ObjectId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N")[..8];
}

/// <summary>
/// The part of an object that only exists while it is on the battlefield (CR 403.3: every
/// object on the battlefield is a permanent).
/// </summary>
/// <remarks>
/// Everything here is <em>status</em> — what has been done to the permanent. None of it is a
/// characteristic. Power, toughness, types, colours and abilities are never stored: they are
/// computed from the printed card plus the continuous effects that apply, every time they are
/// asked for (CR 613). The engine this replaces stored them and wrote static abilities into
/// state as mutations, which is why a buff outlived the lord that granted it.
/// </remarks>
public sealed record PermanentState
{
    /// <summary>CR 701.26a. Untapped is the default; nothing enters tapped without an effect.</summary>
    public bool IsTapped { get; init; }

    /// <summary>Whether this permanent has been made monstrous (CR 701.32b).</summary>
    /// <remarks>
    /// A designation rather than a counter: monstrosity puts counters on as well, but "becomes
    /// monstrous" is a separate fact that a second activation reads and that some three dozen
    /// cards trigger on. Counting the counters would say yes to a creature that got them any
    /// other way.
    /// </remarks>
    public bool IsMonstrous { get; init; }

    /// <summary>
    /// Whether this Mount is saddled (CR 702.171a).
    /// </summary>
    /// <remarks>
    /// Until end of turn, so it is cleared with damage at the cleanup step rather than carried -
    /// the same moment every other "until end of turn" ends (CR 514.2). A flag rather than a
    /// continuous effect because nothing about the permanent changes: being saddled is a fact
    /// its own triggers ask about and nothing else can see.
    /// </remarks>
    public bool IsSaddled { get; init; }

    /// <summary>Whether this skips its controller's next untap step (CR 502.3).</summary>
    /// <remarks>
    /// Stored rather than computed, unlike the continuous "does not untap" that an Aura or a
    /// static ability gives: this one was put here by an effect that has finished resolving, and
    /// there is nothing left in the game to ask. It clears itself the moment it is honoured.
    /// </remarks>
    public bool SkipsNextUntap { get; init; }

    /// <summary>
    /// Whether this permanent is face down (CR 707.2).
    /// </summary>
    /// <remarks>
    /// A face-down permanent is a 2/2 colourless creature with no name, no types beyond creature
    /// and no abilities — and that is not an effect applied to the card, it is what the object
    /// <em>is</em>. So the card underneath stays exactly as it was and this flag redirects what
    /// the characteristics are computed from; nothing needs to remember a "real" card to restore,
    /// because it was never changed.
    /// </remarks>
    public bool IsFaceDown { get; init; }

    /// <summary>Whether this face-down permanent was manifested (CR 701.40a).</summary>
    /// <remarks>
    /// Kept apart from <see cref="IsFaceDown"/> because the two are turned face up by different
    /// procedures: a morphed permanent may only pay its morph cost (CR 702.37e), and a
    /// manifested one pays the card's own mana cost if it is a creature card (CR 701.40b).
    /// Without the distinction a morphed creature could be turned up the cheaper way, which is a
    /// different card.
    /// </remarks>
    public bool IsManifested { get; init; }

    /// <summary>Whether this permanent is prepared, and so has its spell available.</summary>
    /// <remarks>
    /// A designation like monstrous rather than a counter, and stored rather than computed
    /// because nothing continuous grants it: it is turned on by the card's own words and turned
    /// off by casting the spell it was holding, and both are events.
    /// </remarks>
    public bool IsPrepared { get; init; }

    /// <summary>
    /// Which face this permanent is showing, for a card that has more than one (CR 712.8d).
    /// </summary>
    /// <remarks>
    /// Zero is the front face, which is what every ordinary permanent shows and what a
    /// double-faced one enters as unless something says otherwise (CR 712.8c).
    /// <para>
    /// Kept beside the object's card rather than instead of it. The card the object is
    /// <see cref="GameObject.Card"/> is swapped for the face's characteristics as it transforms,
    /// so everything downstream - the layers, the ability source, the view - reads the face it is
    /// on without knowing faces exist. This index is what lets the fold say which face that was,
    /// and it is what a replay reads rather than trying to infer it from a name.
    /// </para>
    /// </remarks>
    public int FaceIndex { get; init; }

    /// <summary>
    /// Set while the permanent has not been controlled continuously since its controller's most
    /// recent turn began (CR 302.6). Named for the rule, not for the folklore: it gates attacking
    /// and {T} costs, and it applies to every permanent type, not only creatures.
    /// </summary>
    public bool HasSummoningSickness { get; init; } = true;

    /// <summary>
    /// The turn this permanent entered the battlefield.
    /// </summary>
    /// <remarks>
    /// "As long as ~ entered this turn", "Activate only if this land entered this turn", "destroy
    /// all creatures that entered this turn" — 87 corpus cards, and the whole cycle of lands whose
    /// second mana ability is switched on only on the turn they arrive.
    /// <para>
    /// <b>Not <see cref="HasSummoningSickness"/>, which is a different fact and answers the
    /// question wrongly.</b> Sickness lasts until its controller's next turn begins (CR 302.6), so
    /// a creature that arrived on your turn 5 still has it all through the opponent's turn 6 — and
    /// a card reading "as long as it entered this turn" is emphatically <em>off</em> during that
    /// turn. The two agree only on the turn the permanent arrived and diverge on the next one,
    /// which is exactly the turn combat happens on, so the substitution would have been wrong
    /// where it mattered most and right everywhere it was easy to test.
    /// </para>
    /// <para>
    /// A turn number rather than a flag, because there is nothing to clear: the turn moves on and
    /// the comparison stops being true by itself. Zero means "before the first turn", which is
    /// where the permanents dealt out by a game's setup sit and is not a turn any card can ask
    /// about.
    /// </para>
    /// <para>
    /// It lives on the object rather than in a list somewhere, for the reason foretell, plot and
    /// discard all do: entering the battlefield is a zone change, so the thing that arrives is a
    /// new object with no relation to whatever it was before (CR 400.7), and the fact belongs to
    /// it. A permanent that leaves and comes back has entered again, on whichever turn that was.
    /// </para>
    /// </remarks>
    public int EnteredOnTurn { get; init; }

    /// <summary>
    /// Damage marked on the permanent this turn (CR 120.3). Cleared during cleanup (CR 514.2),
    /// not when it is dealt, and compared against toughness by state-based actions.
    /// </summary>
    public int DamageMarked { get; init; }

    /// <summary>What has dealt damage to this permanent since damage was last cleared.</summary>
    /// <remarks>
    /// "Whenever a creature dealt damage by this creature this turn dies" asks a question the
    /// damage total cannot answer: how much is on it says nothing about who put it there, and by
    /// the time the creature dies the damage may have been dealt by several things. Cleared
    /// alongside the damage at cleanup (CR 514.2), which is the same "this turn" the cards mean.
    /// </remarks>
    public ImmutableHashSet<ObjectId> DamagedBy { get; init; } = [];

    /// <summary>
    /// The permanent's class level (CR 716.2b), where 1 means "no level yet" (CR 716.2d).
    /// </summary>
    /// <remarks>
    /// <strong>Not a counter, deliberately.</strong> A level is a designation, and CR 716.4 says
    /// so in as many words: level counters and class levels do not interact. Keeping it in the
    /// counter dictionary would have made a Class answer to proliferate, to "remove a counter",
    /// and to every card that counts counters on a permanent - none of which touch a Class.
    /// <para>
    /// It lives on any permanent rather than only a Class because CR 716.2b says a Class keeps
    /// its level even after it stops being one.
    /// </para>
    /// </remarks>
    public int Level { get; init; } = 1;

    /// <summary>Whether this Case has been solved (CR 719.3b).</summary>
    /// <remarks>
    /// A designation, like the class level beside it and for the same reason: CR 719.3b says it
    /// "is neither an ability nor part of the permanent's copiable values", so it is a fact about
    /// this permanent and not about its card. It stays until the permanent leaves the
    /// battlefield, which it does by simply not surviving the move to another zone - a new object
    /// arrives there (CR 400.7) and this is not copied onto it.
    /// </remarks>
    public bool IsSolved { get; init; }

    /// <summary>Whether this creature is renowned (CR 702.112b).</summary>
    /// <remarks>
    /// A designation, like the solved flag above it: "once a permanent becomes renowned, it stays
    /// renowned" until it leaves the battlefield, and it is not a counter - nothing that counts
    /// counters or removes them touches it.
    /// </remarks>
    public bool IsRenowned { get; init; }

    /// <summary>
    /// Which halves of a split permanent are unlocked, by face index (CR 709.5c).
    /// </summary>
    /// <remarks>
    /// "Left half unlocked" and "right half unlocked" are designations, and a half without its
    /// designation is locked: CR 709.5 says the permanent then "doesn't have the name, mana cost,
    /// or rules text" of that half. Held as indices rather than two flags because the compiler
    /// numbers faces and nothing else in the engine has a concept of left or right.
    /// </remarks>
    public ImmutableHashSet<int> UnlockedHalves { get; init; } = [];

    /// <summary>
    /// Counters on the permanent, by kind (CR 122). "+1/+1" and "-1/-1" are the common two and
    /// annihilate each other as a state-based action (CR 704.5q), which is slice 3's business.
    /// </summary>
    public ImmutableDictionary<string, int> Counters { get; init; } =
        ImmutableDictionary<string, int>.Empty;

    /// <summary>
    /// Whether a source with deathtouch has dealt this permanent damage since the last
    /// state-based action check (CR 704.5h).
    /// </summary>
    /// <remarks>
    /// Deathtouch does not destroy on its own and does not set damage equal to toughness. It is
    /// a separate state-based action, which is why one damage from a deathtouch source kills a
    /// 6/6 without ever marking six damage on it — and why marking the damage is not enough for
    /// the engine to remember what killed it.
    /// </remarks>
    public bool DealtDeathtouchDamage { get; init; }

    /// <summary>
    /// What this permanent is attached to, if it is an Aura, Equipment or Fortification
    /// (CR 301.5, 303.4).
    /// </summary>
    /// <remarks>
    /// Held on the attaching permanent rather than as a list on the host, because that is the
    /// direction the rules ask about: an Aura that finds itself attached to nothing is put into
    /// its owner's graveyard (CR 704.5m), and a host needs no opinion about what is stuck to it.
    /// The reverse view — everything attached to a creature — is derived when a client needs it.
    /// </remarks>
    public ObjectId? AttachedTo { get; init; }

    /// <summary>
    /// The player this is attached to, for an Aura that enchants one (CR 303.4a).
    /// </summary>
    /// <remarks>
    /// A separate field because a player is not an object and has no <see cref="ObjectId"/> —
    /// there was nowhere to put the answer, so every Aura that enchants a player attached to
    /// nothing and did nothing.
    /// </remarks>
    public Guid? AttachedToPlayer { get; init; }

    /// <summary>
    /// Regeneration shields waiting to be used (CR 701.19).
    /// </summary>
    /// <remarks>
    /// Regenerating is not healing and not prevention: it is a replacement standing by, and the
    /// next destruction this turn is swapped for tapping, clearing damage and removing it from
    /// combat. Several can be stacked, so it is a count rather than a flag, and they all fall
    /// away in the cleanup step.
    /// </remarks>
    public int RegenerationShields { get; init; }

    /// <summary>
    /// Damage still to be prevented on this permanent this turn (CR 615.1).
    /// </summary>
    /// <remarks>
    /// A prevention shield is not a replacement of the whole event: it soaks up a number of
    /// damage and the rest still lands, which is why it is an amount rather than a count of
    /// uses. It falls away in the cleanup step with everything else that lasts "this turn".
    /// </remarks>
    public int DamageToPrevent { get; init; }

    /// <summary>
    /// How much of the next damage to this permanent is dealt somewhere else instead
    /// (CR 614.1b).
    /// </summary>
    /// <remarks>
    /// A redirection is not a prevention: the damage is still dealt, and something else takes it.
    /// Kept as an amount beside the destination for the same reason a shield is an amount - "the
    /// next 2 damage" redirects two and lets the third through.
    /// </remarks>
    public int DamageToRedirect { get; init; }

    /// <summary>Where redirected damage goes, while there is any left to redirect.</summary>
    public ObjectId? RedirectDamageTo { get; init; }

    /// <summary>The creature this one is paired with, while a soulbond pairing stands (CR 702.95b).</summary>
    /// <remarks>
    /// A designation like monstrous, not a characteristic: pairing is a fact a soulbond ability
    /// wrote onto both creatures, and the cards ask about it from either end — so it is held on
    /// both halves, each pointing at the other, and read only where the two agree
    /// (<see cref="GameState.PairedPartnerOf"/>). CR 702.95e's break-up conditions are swept
    /// where state-based actions are checked; a partner that has already left the battlefield
    /// reads as unpaired in the window before the sweep records it. Never restored: once the
    /// sweep clears it, only a new soulbond trigger can pair again — a creature that briefly
    /// stopped being a creature does not resume its old pairing (CR 702.95e).
    /// </remarks>
    public ObjectId? PairedWithId { get; init; }

    /// <summary>
    /// The player designated to protect this battle (CR 310.9).
    /// </summary>
    /// <remarks>
    /// Null on everything that is not a battle, and on a battle that has just entered and not
    /// yet had its controller choose (CR 310.9a) — the settle sweep asks, or answers for them
    /// when only one player may be chosen. The protector decides who the battle may be attacked
    /// through (CR 310.9b) and who may block for it (CR 310.9c); "defending player" relative to
    /// an attacked battle means this player, not its controller (CR 310.9d).
    /// </remarks>
    public Guid? ProtectorId { get; init; }

    // Records compare collections by reference; see Structural.
    public bool Equals(PermanentState? other) =>
        other is not null &&
        IsTapped == other.IsTapped &&
        IsMonstrous == other.IsMonstrous &&
        IsSaddled == other.IsSaddled &&
        SkipsNextUntap == other.SkipsNextUntap &&
        IsFaceDown == other.IsFaceDown &&
        IsManifested == other.IsManifested &&
        IsPrepared == other.IsPrepared &&
        FaceIndex == other.FaceIndex &&
        HasSummoningSickness == other.HasSummoningSickness &&
        EnteredOnTurn == other.EnteredOnTurn &&
        DamageMarked == other.DamageMarked &&
        DamagedBy.SetEquals(other.DamagedBy) &&
        DealtDeathtouchDamage == other.DealtDeathtouchDamage &&
        AttachedTo == other.AttachedTo &&
        AttachedToPlayer == other.AttachedToPlayer &&
        RegenerationShields == other.RegenerationShields &&
        DamageToPrevent == other.DamageToPrevent &&
        DamageToRedirect == other.DamageToRedirect &&
        RedirectDamageTo == other.RedirectDamageTo &&
        PairedWithId == other.PairedWithId &&
        Level == other.Level &&
        IsSolved == other.IsSolved &&
        IsRenowned == other.IsRenowned &&
        ProtectorId == other.ProtectorId &&
        UnlockedHalves.SetEquals(other.UnlockedHalves) &&
        Structural.Same(Counters, other.Counters);

    public override int GetHashCode() =>
        HashCode.Combine(
            IsTapped, HasSummoningSickness, DamageMarked, Counters.Count, AttachedTo, Level);
}

/// <summary>
/// An ability waiting on the stack, which is an object but not a card (CR 113.7a, 603.3).
/// </summary>
public sealed record AbilityOnStack
{
    /// <summary>The object whose ability this is. It may already have left the battlefield.</summary>
    public required ObjectId SourceId { get; init; }

    /// <summary>Which of the source's abilities, by the id its definition carries.</summary>
    public required string AbilityId { get; init; }

    /// <summary>The ability's text — everything it has (CR 405.4).</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The player the triggering event was about — what "that player" means (CR 603.2).
    /// </summary>
    /// <remarks>
    /// A trigger is the only kind of ability whose text can point at something the ability never
    /// chose: "whenever this creature deals combat damage to a player, that player discards a
    /// card" names a player decided by the event, not by the controller. It has to be recorded
    /// when the ability triggers and carried to where it resolves, because by then the event is
    /// long past and nothing else in the game says which player it was.
    /// <para>
    /// It is deliberately not a target. A target is chosen, is checked for legality twice, and
    /// can be made illegal by hexproof; this is none of those things, and putting it in the
    /// target list to save a field would have made the board show it as one.
    /// </para>
    /// </remarks>
    public Guid? SubjectPlayer { get; init; }

    /// <summary>
    /// The attack this ability's source is to join when it resolves (CR 506.3c).
    /// </summary>
    /// <remarks>
    /// Ninjutsu returns an unblocked attacker as a cost and the ninja arrives attacking whoever
    /// that creature was attacking - a fact known when the cost is paid and needed when the
    /// ability resolves, with the creature that knew it back in its owner's hand by then.
    /// <para>
    /// Deliberately not <see cref="SubjectPlayer"/>, which exists for triggers and means
    /// something the ability never chose. This is chosen, by paying the cost.
    /// </para>
    /// </remarks>
    public AttackTarget? JoiningAgainst { get; init; }

    /// <summary>
    /// The object the triggering event was about — what "it" means in a trigger (CR 603.2).
    /// </summary>
    /// <remarks>
    /// The twin of the subject player, and recorded for the same reason: a ward trigger has to
    /// counter the spell that targeted it, and nothing in the game says which spell that was once
    /// the event has passed.
    /// <para>
    /// Only templates that write their own ability text may use it. The printed pronoun is not
    /// safe to read from the shared grammar — of the corpus lines saying "that creature", most
    /// are not triggers at all and mean whatever the sentence before targeted.
    /// </para>
    /// </remarks>
    public ObjectId? SubjectObject { get; init; }

    /// <summary>How much the triggering event was about — "that many" (CR 603.2).</summary>
    public int? SubjectAmount { get; init; }
}

/// <summary>
/// One object in the game: a card in a zone, a permanent on the battlefield, or an ability on
/// the stack.
/// </summary>
public sealed record GameObject
{
    /// <summary>Identity in the current zone. Replaced on every zone change (CR 400.7).</summary>
    public required ObjectId Id { get; init; }

    /// <summary>The id this object had before its last zone change, if it had one.</summary>
    /// <remarks>
    /// CR 400.7 makes a card that changes zones a new object, and that is exactly right - but an
    /// ability that triggered on it leaving still has to be able to name it. "When this dies,
    /// return <em>another</em> target artifact card from your graveyard" is the case: the card
    /// in the graveyard is a new object, so comparing ids offered the player the very card whose
    /// ability had just told them they could not have it.
    /// <para>
    /// One hop, which is all any such ability needs - the trigger is about the move that just
    /// happened. It is not a general history and nothing should read it as one.
    /// </para>
    /// </remarks>
    public ObjectId? PreviousId { get; init; }

    /// <summary>
    /// The printed card. Its characteristics are the starting point for every calculation and
    /// are never edited — effects layer over them (CR 613) rather than rewriting them here.
    /// </summary>
    public required CardDefinition Card { get; init; }

    /// <summary>
    /// The player who started the game with this card (CR 108.3). Never changes, whatever
    /// happens to control of it, and decides which graveyard, hand, or library it returns to
    /// (CR 400.3).
    /// </summary>
    public required Guid OwnerId { get; init; }

    /// <summary>
    /// Who currently controls it (CR 108.4). Equal to the owner until an effect says otherwise.
    /// Objects in a hidden or owner-specific zone are controlled by their owner.
    /// </summary>
    public required Guid ControllerId { get; init; }

    /// <summary>Where it is now.</summary>
    public required Zone Zone { get; init; }

    /// <summary>
    /// When this object came into being, as a monotonic counter rather than a clock.
    /// </summary>
    /// <remarks>
    /// CR 613.7: continuous effects are applied in timestamp order within a layer, so this has
    /// to be a total order over everything in the game. A wall clock is not one — two objects
    /// entering in the same tick would tie, and the tie-break would be arbitrary. The counter
    /// lives on <see cref="GameState"/> and only ever goes up.
    /// </remarks>
    public required long Timestamp { get; init; }

    /// <summary>Non-null exactly while <see cref="Zone"/> is <see cref="Zone.Battlefield"/>.</summary>
    public PermanentState? Permanent { get; init; }

    /// <summary>
    /// Non-null when this object is an ability on the stack rather than a card (CR 113.7a).
    /// </summary>
    /// <remarks>
    /// An ability on the stack "has the text of the ability that created it and no other
    /// characteristics" (CR 405.4). It keeps its source's card here only so a client can show
    /// what produced it; nothing in the rules reads those characteristics. When it finishes
    /// resolving it ceases to exist rather than going to a graveyard — it was never a card.
    /// </remarks>
    public AbilityOnStack? Ability { get; init; }

    /// <summary>
    /// Targets chosen as this spell or ability was put on the stack (CR 601.2c, 602.2b).
    /// </summary>
    /// <remarks>
    /// Chosen once, on announcement, and never re-chosen — but re-checked on resolution
    /// (CR 608.2b), because a spell all of whose targets have become illegal does not resolve.
    /// </remarks>
    public ImmutableList<Abilities.Target> Targets { get; init; } = [];

    /// <summary>
    /// Whether this is a copy of a spell rather than a card on the stack (CR 707.10).
    /// </summary>
    /// <remarks>
    /// A copy is not a card, so it has no graveyard to go to: it resolves and stops existing, the
    /// same way an ability does. Without this the copy would land in the graveyard and the player
    /// would have two of a card they own one of.
    /// </remarks>
    public bool IsCopy { get; init; }

    /// <summary>
    /// Whether this spell's buyback cost was paid as it was cast (CR 702.27a).
    /// </summary>
    /// <remarks>
    /// Recorded on the spell rather than remembered by the engine, because it has to survive a
    /// replay: the log says the cost was paid and the state has to say so too, or a rebuilt game
    /// puts the card in the graveyard where the original put it back in hand.
    /// </remarks>
    public bool WasBoughtBack { get; init; }

    /// <summary>The turn this card was plotted, if it was (CR 702.169a).</summary>
    /// <remarks>
    /// Foretell's twin, and separate from it because the two charge differently on the way out:
    /// a foretold card is cast for its foretell cost and a plotted one for nothing at all. One
    /// field could not say which had happened.
    /// </remarks>
    public int? PlottedOnTurn { get; init; }

    /// <summary>Whether this spell was cast for its escape cost (CR 702.139a).</summary>
    /// <remarks>
    /// Read by the permanent the spell becomes, through the link the move left behind: "escapes
    /// with a +1/+1 counter on it" is about how the creature got here, and by the time it is
    /// here the spell that knew is a different object (CR 400.7).
    /// </remarks>
    public bool WasEscaped { get; init; }

    /// <summary>Whether this spell was cast for its warp cost (CR 702.185a).</summary>
    /// <remarks>
    /// Read as the spell resolves, to decide whether the permanent it becomes is exiled at the
    /// next end step. A warp that was not paid is an ordinary cast and leaves nothing behind.
    /// </remarks>
    public bool WasWarped { get; init; }

    /// <summary>
    /// Whether this was cast as a prototyped spell, so it keeps the smaller size (CR 718.2).
    /// </summary>
    /// <remarks>
    /// Carried onto the permanent rather than read off the stack, because the alternative
    /// characteristics apply "while it is a spell <em>or while it is a permanent</em>" - the
    /// choice was made once, on the stack, and the permanent has to remember it.
    /// </remarks>
    public bool WasPrototyped { get; init; }

    /// <summary>The turn this card was exiled by its own warp ability (CR 702.185b).</summary>
    /// <remarks>
    /// The card sitting in exile is not permission by itself, exactly as for foretell and plot:
    /// this one got there by being warped, and its owner may cast it once the turn has ended.
    /// </remarks>
    public int? WarpedOnTurn { get; init; }

    /// <summary>
    /// Whether this card is exiled on an adventure, and so playable by its owner (CR 715.3d).
    /// </summary>
    /// <remarks>
    /// The card being in exile is not permission on its own - a plotted card and a foretold card
    /// are in the same zone and are each playable only by whoever paid to put them there. This
    /// says which of them this one is.
    /// </remarks>
    public bool OnAdventure { get; init; }

    /// <summary>
    /// The turn this card was discarded on, for the cards that ask (CR 701.8a).
    /// </summary>
    /// <remarks>
    /// Stamped on the object in the graveyard rather than remembered in a list, for the same
    /// reason foretell and plot are: the card in the graveyard is a new object (CR 400.7), and
    /// the fact belongs to it. A card discarded, returned to hand and discarded again is stamped
    /// with the second discard, which is the answer "did you discard it this turn" wants.
    /// </remarks>
    public int? DiscardedOnTurn { get; init; }

    /// <summary>
    /// The colour or creature type chosen as this permanent entered (CR 614.12).
    /// </summary>
    /// <remarks>
    /// One field rather than one per kind, because a permanent chooses at most one thing and the
    /// card says which kind it is: "choose a color" and "choose a creature type" never appear on
    /// the same permanent. Held as the printed word — "white", "Goblin" — so the value a log
    /// carries is the value the card names.
    /// <para>
    /// Null until chosen, and it stays with the object: a permanent that leaves and comes back is
    /// a new object (CR 400.7) and chooses again, which is what the cards intend.
    /// </para>
    /// </remarks>
    public string? Chosen { get; init; }

    /// <summary>Time counters on a suspended card in exile (CR 702.62a).</summary>
    /// <remarks>
    /// Not in <see cref="PermanentState.Counters"/>, which only exists on the battlefield: a
    /// suspended card is in exile and has no permanent to hang counters on. It is the one kind of
    /// counter that lives on a card rather than on a permanent, so it gets its own number rather
    /// than a dictionary nothing else would use.
    /// </remarks>
    public int TimeCounters { get; init; }

    /// <summary>Who suspended this card, and so who may cast it (CR 702.62b).</summary>
    public Guid? SuspendedBy { get; init; }

    /// <summary>Whether this card has been shown to everyone (CR 701.16a).</summary>
    /// <remarks>
    /// A reveal is momentary, and this flag is not: once a card has been shown, every player
    /// legitimately knows it, so keeping it visible tells nobody anything they were not already
    /// told. The flag never hides something that should be seen, which is the direction that
    /// would matter.
    /// <para>
    /// It travels with the card only while the card stays where it is: a zone change makes a new
    /// object (CR 400.7) and the new one has not been revealed.
    /// </para>
    /// </remarks>
    public bool IsRevealed { get; init; }

    /// <summary>Whether this spell was cast with its offspring cost paid (CR 702.171a).</summary>
    public bool WasOffspring { get; init; }

    /// <summary>Whether this was cast as an Aura for its bestow cost (CR 702.103a).</summary>
    /// <remarks>
    /// Survives the move to the battlefield, because it is what tells the permanent it is an
    /// Aura rather than a creature - and the permanent goes on being one until it falls off.
    /// </remarks>
    public bool WasBestowed { get; init; }

    /// <summary>
    /// Whether this was cast for its mutate cost, making it a mutating creature spell
    /// (CR 702.140a).
    /// </summary>
    /// <remarks>
    /// It is the flag two rules read, and both of them read it <em>instead of</em> the ordinary
    /// path rather than alongside it. CR 608.3b: a mutating creature spell whose target has
    /// become illegal does not fizzle — it stops being a mutating creature spell and resolves as
    /// an ordinary creature spell. CR 702.140c: one whose target is still legal does not enter
    /// the battlefield at all, and merges instead.
    /// </remarks>
    public bool WasMutated { get; init; }

    /// <summary>Which end of the stack a mutating creature spell was put on (CR 702.140c).</summary>
    /// <remarks>
    /// The rule gives the choice to the spell's controller as it resolves. It is taken with the
    /// cast instead, beside the decision to pay the mutate cost at all — the same place modes,
    /// kicker and every other alternative cost are chosen, and for the reason given in
    /// <c>Game.CastSpell</c>: a cast is one atomic action, and a question asked mid-resolution
    /// would be a continuation the log cannot rebuild. The cost is that the choice is public
    /// earlier than printed, which commits the caster sooner rather than later; it can never
    /// make the card better than printed.
    /// </remarks>
    public bool MutatesOnTop { get; init; }

    /// <summary>Whether this spell was cast for its overload cost (CR 702.96a).</summary>
    public bool WasOverloaded { get; init; }

    /// <summary>
    /// The attack this <em>spell</em> is to join when it becomes a permanent (CR 702.190b).
    /// </summary>
    /// <remarks>
    /// Sneak's half of what <see cref="AbilityOnStack.JoiningAgainst"/> does for ninjutsu, and it
    /// has to live out here rather than there because a spell on the stack has no ability: the
    /// object is the card itself. The fact is known when the cost is paid - the creature being
    /// returned is still attacking then - and needed when the spell resolves, by which time that
    /// creature is in its owner's hand.
    /// <para>
    /// Null on everything else, which is every spell ever cast but these.
    /// </para>
    /// </remarks>
    public AttackTarget? JoiningAgainst { get; init; }

    /// <summary>
    /// Whether this spell's teamwork cost was paid (CR 702.194b).
    /// </summary>
    /// <remarks>
    /// Recorded and, for now, read by nothing: what paying it buys lives on the card's other
    /// lines - "if this spell was cast using teamwork, ..." - and those sentences are not read
    /// yet. The fact belongs in the state rather than nowhere, because the payment happened and
    /// a log that does not say so cannot be replayed into a game that knows it.
    /// </remarks>
    public bool WasTeamwork { get; init; }

    /// <summary>
    /// Whether this spell was cast for its awaken cost (CR 702.113a).
    /// </summary>
    /// <remarks>
    /// Read while the spell is still on the stack — the awaken half is a spell ability, so it
    /// resolves before the card goes anywhere — which is why this needs none of the carrying
    /// across a zone change that "was kicked" does.
    /// </remarks>
    public bool WasAwakened { get; init; }

    /// <summary>Whether this spell was cast for its evoke cost (CR 702.74a).</summary>
    /// <remarks>
    /// Evoke buys the enters trigger and nothing else: the creature is sacrificed the moment it
    /// arrives, and the trigger it came for resolves anyway because a trigger is its own object
    /// once it has fired.
    /// </remarks>
    public bool WasEvoked { get; init; }

    /// <summary>Whether this spell was cast for its dash cost (CR 702.109a).</summary>
    /// <remarks>
    /// On the spell for the same reason buyback is: the permanent it becomes has to gain haste
    /// and owe its return to hand, and a rebuilt game that did not know the dash cost was the one
    /// paid would leave a creature on the battlefield that the original returned.
    /// </remarks>
    public bool WasDashed { get; init; }

    /// <summary>Whether this spell's - or this permanent's - blitz cost was paid (CR 702.152a).</summary>
    /// <remarks>
    /// Set twice, and deliberately: once on the spell, and again on the permanent it becomes.
    /// Dash needs neither, because everything dash owes is handed out at the moment the permanent
    /// arrives. Blitz is written as a static ability that functions <em>while the permanent is on
    /// the battlefield</em> - "as long as this permanent's blitz cost was paid, it has haste and
    /// 'when this permanent is put into a graveyard from the battlefield, draw a card'" - so the
    /// permanent has to be able to answer the question long after the spell has stopped existing
    /// (CR 400.7). Copying the flag across the zone change would be the same thing said quietly;
    /// emitting the event against the new object says it in the log, where a replay can see it.
    /// </remarks>
    public bool WasBlitzed { get; init; }

    /// <summary>How many times this spell's kicker cost was paid (CR 702.33c).</summary>
    /// <remarks>
    /// A count beside the flag rather than instead of it. Ordinary kicker is paid once or not at
    /// all and every card reading it asks a yes-or-no question, so <see cref="WasKicked"/> stays
    /// what those cards read; multikicker is the same cost paid any number of times, and the
    /// cards that care ask "for each time it was kicked". Collapsing the two would make every
    /// existing kicker reader count to one and back.
    /// <para>
    /// Carried onto the permanent the same way the flag is (CR 607.2), because that is where it
    /// is asked: the spell that was kicked and the permanent that arrives are different objects.
    /// </para>
    /// </remarks>
    public int TimesKicked { get; init; }

    /// <summary>
    /// The turn this card was foretold, if it was (CR 702.143a).
    /// </summary>
    /// <remarks>
    /// The turn number and not a flag, because the rule is "on a later turn": a card foretold and
    /// then cast the same turn would be paying the cheaper cost for nothing, which is the whole
    /// tension the mechanic is built on. Stored on the exiled card rather than remembered by the
    /// engine so that it survives a replay.
    /// </remarks>
    public int? ForetoldOnTurn { get; init; }

    /// <summary>
    /// Whether this card may be cast from where it is without paying its mana cost (CR 601.2b).
    /// </summary>
    /// <remarks>
    /// Cascade, madness and rebound all end with "you may cast it without paying its mana cost",
    /// and the engine has no way to <em>perform</em> a cast from inside a resolution — casting is
    /// a sequence of questions, and a question cannot be asked in the middle of answering one.
    /// So the effect grants permission instead and the player casts it themselves through the
    /// ordinary path.
    /// <para>
    /// The permission is revoked the next time its owner passes priority, which is as close as
    /// this gets to the printed timing: the rules give the player one window, right there in the
    /// resolution. What they gain here is the chance to act on something else first. That is a
    /// real difference and a small one, and it is written down rather than pretended away.
    /// </para>
    /// </remarks>
    public bool MayCastFree { get; init; }

    /// <summary>
    /// The creature this exiled card is encoded on, for cipher (CR 702.99b).
    /// </summary>
    /// <remarks>
    /// Held on the card rather than on the creature because the card is the thing that persists:
    /// a creature that leaves takes nothing with it, and the card simply stops being connected to
    /// anything. Which is also what the rule describes - the encoding is a property of the exiled
    /// card, and nothing puts it back.
    /// </remarks>
    public ObjectId? EncodedOn { get; init; }

    /// <summary>The last turn on which this exiled card may still be played (CR 601.3e).</summary>
    /// <remarks>
    /// "Exile the top card of your library. You may play that card this turn" - permission to
    /// play from exile, paying as normal, which is a different thing from
    /// <see cref="MayCastFree"/>. Stale values need no cleaning up: the check is against the
    /// turn number, so a permission that has run out simply stops granting.
    /// </remarks>
    public int? MayPlayUntilTurn { get; init; }

    /// <summary>
    /// The turn a longer play window was granted on, for "until the end of your next turn".
    /// </summary>
    /// <remarks>
    /// Kept as the turn it started rather than as a deadline, because the deadline is not
    /// knowable when it is granted: whose turn comes next depends on the turn order, and an
    /// extra turn inserted afterwards would move it. The permission is revoked at the cleanup of
    /// the owner's next turn instead, which is exact whatever happens in between.
    /// </remarks>
    public int? MayPlayThroughOwnersNextTurn { get; init; }

    /// <summary>Whether a declined free cast sends this card to its owner's hand (CR 701.57a).</summary>
    /// <remarks>
    /// Discover's difference from cascade, and the only one that shows: both exile until they
    /// find a cheap enough nonland and both offer it free, but a cascade the player declines
    /// stays in exile and a discover they decline is drawn.
    /// </remarks>
    public bool ToHandIfCastDeclined { get; init; }

    /// <summary>
    /// What the standing offer charges, as printed. Empty means nothing at all.
    /// </summary>
    /// <remarks>
    /// Madness offers a cast for a stated cost rather than for free, so the offer has to carry
    /// one. It is the printed string and not a parsed <c>ManaCostSpec</c> deliberately: that type
    /// holds a list, records compare lists by reference, and a game rebuilt from its log would
    /// construct an equal-but-different list — which would make the rebuilt state compare unequal
    /// to the original and break the invariant every test asserts.
    /// </remarks>
    public string OfferedCost { get; init; } = string.Empty;

    /// <summary>
    /// Whether taking the standing offer casts this card transformed (CR 712.11a).
    /// </summary>
    /// <remarks>
    /// A defeated Siege's offer is "you may cast it transformed without paying its mana cost"
    /// (CR 310.12b): the card goes on the stack with its back face up and only that face's
    /// characteristics. On the offer rather than derived from the card, because the same battle
    /// in exile under a cascade offer is cast as its front face — only the offer knows which
    /// cast it bought. Cleared with <see cref="MayCastFree"/> when the offer lapses.
    /// </remarks>
    public bool CastsTransformed { get; init; }

    /// <summary>
    /// The permanent whose ability exiled this card, when it is coming back (CR 400.7).
    /// </summary>
    /// <remarks>
    /// The link the "exile until this leaves the battlefield" family turns on. It lives on the
    /// exiled card rather than as a list on the permanent because that is the direction the
    /// question is asked in every case that matters: when the permanent goes, the game asks the
    /// exile zone what belonged to it, and a permanent that left has nowhere left to keep a list.
    /// <para>
    /// It names the permanent's battlefield id, which stops existing when the permanent leaves
    /// (CR 400.7) — and that is exactly right: the id is only ever compared against the id of a
    /// permanent that is in the act of leaving, and a new object with a new id is a new object
    /// that never exiled anything.
    /// </para>
    /// </remarks>
    public ObjectId? ExiledBy { get; init; }

    /// <summary>
    /// Which modes were chosen for a modal spell, in the order the card lists them (CR 700.2).
    /// </summary>
    /// <remarks>
    /// On the object rather than worked out at resolution, because the choice is made as the
    /// spell is cast (CR 601.2b) and cannot be re-derived afterwards — and because a spell whose
    /// modes were chosen has to replay as one whose modes were chosen.
    /// </remarks>
    public ImmutableList<int> ChosenModes { get; init; } = [];

    /// <summary>
    /// The cards spliced onto this spell as it was cast, in order (CR 702.47a).
    /// </summary>
    /// <remarks>
    /// Held as the cards themselves rather than as their effects, because an effect list is not
    /// something a log can carry: the object is rebuilt by replaying the cast, and the effects
    /// are looked up from the card again at resolution the same way the spell's own are.
    /// </remarks>
    public ImmutableList<Domain.Models.CardDefinition> Spliced { get; init; } = [];

    /// <summary>
    /// The cards under the topmost one, when this permanent is represented by more than one
    /// card (CR 730.2) — a mutated permanent.
    /// </summary>
    /// <remarks>
    /// <see cref="Card"/> stays the <em>topmost</em> component, which is what makes this
    /// affordable: CR 730.2a says a merged permanent has only its topmost component's
    /// characteristics, so every existing reader of <c>obj.Card</c> — the layers, the view, the
    /// legality checks, <see cref="Characteristics.CardOf"/> and therefore every copy effect —
    /// is already answering the question the rule asks, and none of them had to learn that a
    /// permanent can be a stack of cards.
    /// <para>
    /// Only the <em>abilities</em> are the exception (CR 702.140e: "a mutated permanent has all
    /// abilities of each card and token that represents it"), and abilities are not
    /// characteristics — they are looked up from an <see cref="Abilities.IAbilitySource"/> by
    /// card. So the components' abilities are put where a permanent's non-printed abilities
    /// already live, <c>ComputedCharacteristics.GrantedActivated</c> and its triggered twin,
    /// which is the same place a copied card's go.
    /// </para>
    /// <para>
    /// Ordered top-first, so index 0 is the card immediately under <see cref="Card"/>. Empty for
    /// every ordinary permanent, which is nearly all of them.
    /// </para>
    /// </remarks>
    public ImmutableList<Domain.Models.CardDefinition> MergedComponents { get; init; } = [];

    /// <summary>
    /// How many times this permanent has mutated (CR 702.140), which four corpus cards read
    /// as X.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored. Every merge adds exactly one card to the stack and nothing
    /// ever takes one away while the permanent is on the battlefield, so the count of components
    /// under the top <em>is</em> the number of merges — and a derived number cannot fall out of
    /// step with the stack it describes, nor be left out of <see cref="Equals(GameObject?)"/>
    /// and have its updates silently dropped.
    /// </remarks>
    public int TimesMutated => MergedComponents.Count;

    /// <summary>
    /// How many times this spell's squad cost was paid as it was cast (CR 702.157a).
    /// </summary>
    /// <remarks>
    /// On the spell rather than on the permanent it becomes, because the permanent does not
    /// exist when the choice is made and the copies are created the moment it arrives.
    /// </remarks>
    public int SquadPaid { get; init; }

    /// <summary>
    /// Whether this spell's kicker cost was paid as it was cast (CR 702.33d).
    /// </summary>
    /// <remarks>
    /// On the object because it is decided at cast time and asked about at resolution, and the
    /// two are separated by every response either player cares to make.
    /// </remarks>
    public bool WasKicked { get; init; }

    /// <summary>
    /// Whether its controller declared the intention to pay its bargain cost (CR 702.166b).
    /// </summary>
    /// <remarks>
    /// Kicker's question with a different cost. A yes-or-no, because bargain is paid at most once
    /// and every card that reads it asks only whether it was paid at all.
    /// </remarks>
    public bool WasBargained { get; init; }

    /// <summary>
    /// Whether this spell's cleave cost was paid as it was cast (CR 702.148a).
    /// </summary>
    /// <remarks>
    /// The fact that chooses which of the card's two readings resolves: paying the cleave cost
    /// removes the words in square brackets, and the engine holds that as a second compiled
    /// spell rather than as edited text. Recorded on the object so the choice survives every
    /// response between casting and resolving — and so a resumed game still resolves the reading
    /// that was paid for, which the code-only <c>_castAs</c> table cannot promise.
    /// </remarks>
    public bool WasCleaved { get; init; }

    /// <summary>
    /// The opponent this spell's gift was promised to, or null when it was not (CR 702.174k).
    /// </summary>
    /// <remarks>
    /// One field for two facts, because the rules never separate them: promising the gift
    /// <em>is</em> choosing an opponent (CR 702.174a), so a promise with nobody chosen cannot
    /// exist. The player rather than a flag, because the delivery — "the chosen player creates a
    /// Food token" — happens at resolution, chosen at cast, and the two are separated by every
    /// response either player cares to make. Deliberately not a target: nothing about the choice
    /// uses targeting rules, and hexproof does not refuse a present.
    /// </remarks>
    public Guid? GiftedTo { get; init; }

    /// <summary>Which colours of mana paid for this (CR 202.2).</summary>
    /// <remarks>
    /// Survives resolution the way the kicker flag does, because sunburst is asked as the
    /// permanent enters and the spell that was paid for is gone by then (CR 400.7, 607.2).
    /// </remarks>
    public Mana.ManaPool ManaSpent { get; init; } = Mana.ManaPool.Empty;

    /// <summary>Who cast this, if it was cast at all (CR 601.2).</summary>
    /// <remarks>
    /// "If you cast it" is a real distinction: a permanent can reach the battlefield without
    /// being cast - reanimated, put there by a search, or made as a token - and about fifty
    /// cards pay only for the version that was cast. Who did it rather than whether it happened,
    /// because the sentence says "you", and a permanent an opponent cast and you have since
    /// taken is not one you cast.
    /// </remarks>
    public Guid? CastBy { get; init; }

    /// <summary>
    /// The zone this was cast from, for the cards that ask (CR 400.7).
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="CastBy"/> and carried the same way: the move to the stack is the
    /// only moment that knows, because the object it names stops existing as it leaves.
    /// </remarks>
    public Zone? CastFromZone { get; init; }

    /// <summary>The value chosen for X as this was cast (CR 601.2b).</summary>
    public int VariableValue { get; init; }

    /// <summary>
    /// How much damage each target is to be dealt, by target index (CR 601.2d).
    /// </summary>
    /// <remarks>
    /// Chosen as the spell is cast and not as it resolves, which is why it rides here beside the
    /// targets rather than being asked for on resolution. It is public information from the
    /// moment it is announced, and a player deciding whether to respond is entitled to know
    /// which of their creatures is about to take three and which is about to take one.
    /// <para>
    /// Empty for everything else, which is nearly every spell.
    /// </para>
    /// </remarks>
    public ImmutableList<int> DamageDivision { get; init; } = [];

    /// <summary>Convenience for the common check; see <see cref="Permanent"/>.</summary>
    public bool IsPermanent => Permanent is not null;

    /// <remarks>
    /// The card is compared by oracle id rather than by reference. Within one game the same
    /// <see cref="CardDefinition"/> instance travels with the object, but a state rebuilt from a
    /// stored log has its own instances, and two states of the same game must still be equal.
    /// </remarks>
    public bool Equals(GameObject? other) =>
        other is not null &&
        Id == other.Id &&
        PreviousId == other.PreviousId &&
        OwnerId == other.OwnerId &&
        ControllerId == other.ControllerId &&
        Zone == other.Zone &&
        Timestamp == other.Timestamp &&
        string.Equals(Card.OracleId, other.Card.OracleId, StringComparison.Ordinal) &&
        Equals(Permanent, other.Permanent) &&
        Equals(Ability, other.Ability) &&
        VariableValue == other.VariableValue &&
        DamageDivision.SequenceEqual(other.DamageDivision) &&
        Structural.Same(Targets, other.Targets) &&
        // Anything the state carries has to be compared here, and not only for correctness of
        // equality: the objects live in an ImmutableDictionary, and SetItem skips the write when
        // the new value compares equal to the old. A field left out of this list is a field whose
        // updates are silently dropped — which is how the chosen modes of a modal spell went
        // missing between being chosen and being resolved.
        SquadPaid == other.SquadPaid &&
        Structural.Same(ChosenModes, other.ChosenModes) &&
        Structural.Same(
            Spliced.ConvertAll(c => c.Name), other.Spliced.ConvertAll(c => c.Name)) &&

        // The stack a mutated permanent is, compared the way the card above is: by oracle id,
        // because a state rebuilt from a stored log has its own CardDefinition instances. Leaving
        // it out would drop the second and every later merge — SetItem skips a write when the new
        // value compares equal — so the permanent would keep the abilities of the first card
        // under it and quietly stop counting mutations.
        Structural.Same(
            MergedComponents.ConvertAll(c => c.OracleId),
            other.MergedComponents.ConvertAll(c => c.OracleId)) &&
        WasMutated == other.WasMutated &&
        MutatesOnTop == other.MutatesOnTop &&
        IsCopy == other.IsCopy &&
        WasBoughtBack == other.WasBoughtBack &&
        WasDashed == other.WasDashed &&
        WasBlitzed == other.WasBlitzed &&
        TimesKicked == other.TimesKicked &&
        WasEvoked == other.WasEvoked &&
        WasOverloaded == other.WasOverloaded &&
        WasAwakened == other.WasAwakened &&
        WasTeamwork == other.WasTeamwork &&
        Equals(JoiningAgainst, other.JoiningAgainst) &&
        WasBestowed == other.WasBestowed &&
        WasOffspring == other.WasOffspring &&
        IsRevealed == other.IsRevealed &&
        TimeCounters == other.TimeCounters &&
        PlottedOnTurn == other.PlottedOnTurn &&
        WasWarped == other.WasWarped &&
        WasPrototyped == other.WasPrototyped &&
        WasEscaped == other.WasEscaped &&
        WarpedOnTurn == other.WarpedOnTurn &&
        OnAdventure == other.OnAdventure &&
        DiscardedOnTurn == other.DiscardedOnTurn &&
        string.Equals(Chosen, other.Chosen, StringComparison.Ordinal) &&
        SuspendedBy == other.SuspendedBy &&
        ForetoldOnTurn == other.ForetoldOnTurn &&
        MayCastFree == other.MayCastFree &&
        EncodedOn == other.EncodedOn &&
        MayPlayUntilTurn == other.MayPlayUntilTurn &&
        MayPlayThroughOwnersNextTurn == other.MayPlayThroughOwnersNextTurn &&
        ToHandIfCastDeclined == other.ToHandIfCastDeclined &&
        string.Equals(OfferedCost, other.OfferedCost, StringComparison.Ordinal) &&
        CastsTransformed == other.CastsTransformed &&
        ExiledBy == other.ExiledBy &&
        WasKicked == other.WasKicked &&
        WasBargained == other.WasBargained &&
        WasCleaved == other.WasCleaved &&
        GiftedTo == other.GiftedTo &&
        CastBy == other.CastBy &&
        CastFromZone == other.CastFromZone &&
        ManaSpent == other.ManaSpent;

    public override int GetHashCode() =>
        HashCode.Combine(Id, OwnerId, ControllerId, Zone, Timestamp, Card.OracleId, Permanent);
}
