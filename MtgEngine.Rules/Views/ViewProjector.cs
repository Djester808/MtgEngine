using MtgEngine.Cards;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;

namespace MtgEngine.Rules.Views;

/// <summary>
/// One player's view of the game, CR 400.2. The battlefield, the stack, every graveyard, exile
/// and the command zone are public zones, so a viewer is given them in full; a library and a
/// hand are hidden zones, so a viewer is given the size of every library, the size of every
/// other hand, and their own hand. A face-down object carries no name and no oracle id,
/// CR 708.2 and 708.4: either would say which card it is.
/// <para>
/// Nothing here hides a field of the state — it builds a new object out of what may be seen,
/// so a field added to <see cref="GameObject"/> is absent from the view until someone decides
/// it is public. <see cref="Enforced"/> comes from the definition store the game was created
/// with, as a lookup by oracle id, so the projector never holds the store itself.
/// </para>
/// </summary>
[RuleRef("400.2")]
public sealed class ViewProjector(ICharacteristics characteristics, Func<string, bool> enforced) : IViewProjector
{
    private readonly ICharacteristics _characteristics = characteristics;

    /// <summary>Oracle id in, enforced out: whether the table plays this card's rules or badges it honour-system.</summary>
    private readonly Func<string, bool> _enforced = enforced;

    public string Name => "view projection";

    public GameView Project(GameState state, PlayerId viewer)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new GameView(
            viewer,
            state.Turn.Number,
            state.Turn.Step,
            state.Turn.ActivePlayer,
            state.Priority.Holder,
            [.. state.Players.Select(p => Seat(state, p))],
            ViewsOf(state, InZone(state, CardZone.Battlefield)),
            ViewsOf(state, InZone(state, CardZone.Stack)),
            ViewsOf(state, InZone(state, CardZone.Hand, viewer)),
            ViewsOf(state, InZone(state, CardZone.Exile)),
            Redact(state.Pending, viewer),
            state.Outcome);
    }

    /// <summary>
    /// What is known about one player: their life and counters, the sizes of their hidden zones
    /// (CR 400.2 — a hidden zone's size is public), and their public zones in full. The mana
    /// pool is public too, CR 106.4.
    /// </summary>
    [RuleRef("400.2")]
    private PlayerView Seat(GameState state, PlayerState player)
    {
        var graveyard = InZone(state, CardZone.Graveyard, player.Id);
        return new PlayerView(
            player.Id,
            player.Name,
            player.Life,
            InZone(state, CardZone.Hand, player.Id).Count,
            InZone(state, CardZone.Library, player.Id).Count,
            graveyard.Count,
            player.Counters,
            player.Mana,
            ViewsOf(state, graveyard),
            ViewsOf(state, InZone(state, CardZone.Command, player.Id)));
    }

    /// <summary>
    /// The objects in a zone, in the order the state keeps them: a library is top-first and the
    /// stack bottom-first. A zone the rules give each player their own (CR 400.1) is read by
    /// owner rather than by key, so a shared key holding several players' cards — the command
    /// zone as the zone system keys it — reads the same way as a per-player one.
    /// </summary>
    [RuleRef("400.1")]
    private static ImmutableList<ObjectId> InZone(GameState state, CardZone zone, PlayerId? owner = null)
    {
        var ids = ImmutableList.CreateBuilder<ObjectId>();
        foreach (var key in state.Zones.Keys.Where(k => k.Zone == zone).OrderBy(k => k.Player?.Value ?? -1))
        {
            foreach (var id in state.Zones[key])
            {
                if (owner is null || (state.Objects.TryGetValue(id, out var obj) && obj.Owner == owner))
                    ids.Add(id);
            }
        }

        return ids.ToImmutable();
    }

    private ImmutableList<ObjectView> ViewsOf(GameState state, ImmutableList<ObjectId> ids) =>
        [.. ids.Where(state.Objects.ContainsKey).Select(id => View(state, state.Objects[id]))];

    /// <summary>
    /// One object as it may be seen. Every characteristic is the computed one, CR 613.5, so a
    /// creature that was made a 1/1 is shown as a 1/1 and never as its printed self.
    /// </summary>
    [RuleRef("613.5")]
    private ObjectView View(GameState state, GameObject obj)
    {
        if (obj.FaceDown)
            return FaceDown(state, obj);

        var chars = _characteristics.Of(state, obj.Id);
        return new ObjectView(
            obj.Id,
            chars.Name,
            obj.Definition.OracleId,
            chars.Controller,
            obj.Owner,
            obj.Tapped,
            FaceDown: false,
            chars.Power,
            chars.Toughness,
            chars.Loyalty,
            obj.Damage,
            obj.Counters,
            obj.AttachedTo,
            [.. chars.Abilities.OfType<KeywordAbility>().Select(k => k.Keyword.ToString())],
            TypeNames(chars.Types),
            chars.Subtypes,
            _enforced(obj.Definition.OracleId),

            // CR 205.3d: a changeling on the table is every creature type, and none of those
            // names is in its subtype list, so the flag travels with the list or the view lies.
            chars.EveryCreatureType);
    }

    /// <summary>
    /// CR 708.2: a face-down permanent is a 2/2 creature with no name, no mana cost, no
    /// subtypes and no abilities; CR 708.4 keeps what it really is from everyone not entitled
    /// to look, so the oracle id is withheld as well — it names the card as surely as the name
    /// does. A face-down card that is not a permanent, exiled face down under CR 701.19a, is
    /// not a 2/2 either. The enforcement flag stays true to the store: it says whether the
    /// table plays that object's rules, and thousands of cards share each answer. "Every creature
    /// type" is withheld with everything else: a face-down permanent has no subtypes at all, and
    /// saying it had them all would name the changeling underneath.
    /// </summary>
    [RuleRef("708.2")]
    private ObjectView FaceDown(GameState state, GameObject obj)
    {
        var permanent = obj.Zone.Zone == CardZone.Battlefield;
        return new ObjectView(
            obj.Id,
            string.Empty,
            string.Empty,
            _characteristics.ControllerOf(state, obj.Id),
            obj.Owner,
            obj.Tapped,
            FaceDown: true,
            permanent ? 2 : null,
            permanent ? 2 : null,
            null,
            obj.Damage,
            obj.Counters,
            obj.AttachedTo,
            [],
            permanent ? [nameof(CardType.Creature)] : [],
            [],
            _enforced(obj.Definition.OracleId));
    }

    /// <summary>The card types an object has now, CR 205.2a, named rather than numbered so the client reads them.</summary>
    [RuleRef("205.2a")]
    private static ImmutableList<string> TypeNames(CardType types) =>
        [.. Enum.GetValues<CardType>().Where(t => t != 0 && types.HasFlag(t)).Select(t => t.ToString())];

    /// <summary>
    /// CR 101.4: a choice belongs to one player, and its options can name cards only that
    /// player may see. Another viewer is told who is choosing and what kind of choice it is —
    /// enough to know the game is waiting and on whom — and nothing that was offered.
    /// </summary>
    [RuleRef("101.4")]
    private static PendingChoice? Redact(PendingChoice? pending, PlayerId viewer) =>
        pending is null || pending.Player == viewer
            ? pending
            : new PendingChoice(
                pending.Id,
                pending.Player,
                pending.Kind,
                string.Empty,
                [],
                pending.Min,
                pending.Max,
                new ChoiceContext(null, null, null, ImmutableDictionary<string, string>.Empty));
}
