using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;
using CardZone = MtgEngine.Cards.Zone;
using LegalActionsView = MtgEngine.Rules.Views.LegalActions;

namespace MtgEngine.Rules.Legal;

/// <summary>
/// What a player may do now, answered by the code that would do it: every castable card,
/// playable land and activatable ability is one <see cref="ICastingSystem.Check"/> allows, so
/// an offer and the command it stands for can never disagree (CR 305, 601, 602). Attackers and
/// blockers are the same bargain: <see cref="ICombatSystem.CouldAttack"/> and
/// <see cref="ICombatSystem.CouldBlock"/> are the one reading of CR 508.1a and 509.1a, so a
/// restriction the combat system enforces on a declaration is one this view never offers. What
/// stays here is what the combat system is not asked: the step, whose declaration it is, and
/// whether it has been made already (CR 508.1, 509.1).
/// </summary>
[RuleRef("117")]
public sealed class LegalActions(ICastingSystem casting, ICharacteristics characteristics, ICombatSystem? combat = null) : ILegalActions
{
    private readonly ICastingSystem _casting = casting;

    private readonly ICharacteristics _characteristics = characteristics;

    /// <summary>
    /// The combat system, or null when the game was composed without one. A view that cannot ask
    /// CR 508.1a offers no attackers and no blockers rather than guessing at the rule: a second
    /// reading here is exactly the drift this seam removed.
    /// </summary>
    private readonly ICombatSystem? _combat = combat;

    public string Name => "legal actions";

    public LegalActionsView For(GameState state, PlayerId player)
    {
        ArgumentNullException.ThrowIfNull(state);

        // CR 104.1: a game that has ended allows nothing. CR 101.4: while the game is waiting on
        // a choice, the only thing anyone may do is answer it — Game.Apply refuses the rest, so
        // nothing else is offered here either.
        if (state.Outcome is not null)
            return Nothing();
        if (state.Pending is not null)
            return Nothing() with { MustAnswerChoice = state.Pending.Player == player };

        // CR 117.1a and 116.2a: casting a spell, activating an ability and taking the special
        // action of playing a land all need priority.
        var priority = state.Priority.Holder == player;
        var hand = priority ? state.In(ZoneKey.Of(CardZone.Hand, player)) : [];
        return new LegalActionsView(
            [.. hand.Where(id => _casting.Check(state, new CastSpell(player, id, state.Objects[id].FaceIndex)) is null)],
            priority ? Activatable(state, player) : [],
            [.. hand.Where(id => _casting.Check(state, new PlayLand(player, id)) is null)],
            EligibleAttackers(state, player),
            EligibleBlockers(state, player),
            MayPass: priority,
            MustAnswerChoice: false);
    }

    private static LegalActionsView Nothing() => new([], [], [], [], [], MayPass: false, MustAnswerChoice: false);

    /// <summary>
    /// CR 602.1: an activated ability of a permanent may be activated by the player who
    /// controls it, CR 602.1a, unless its own text lets any player do so — which is why every
    /// permanent is asked about and not only this player's. Control is the layer system's
    /// answer, CR 613.1b, never the object's stored controller.
    /// </summary>
    [RuleRef("602.1")]
    private ImmutableList<Views.ActivatableAbility> Activatable(GameState state, PlayerId player)
    {
        var offers = ImmutableList.CreateBuilder<Views.ActivatableAbility>();
        foreach (var (obj, chars) in Permanents(state))
        {
            for (var index = 0; index < chars.Abilities.Count; index++)
            {
                if (chars.Abilities[index] is not ActivatedAbility ability)
                    continue;
                if (_casting.Check(state, new ActivateAbility(player, obj.Id, index)) is not null)
                    continue;
                offers.Add(new Views.ActivatableAbility(obj.Id, index, ability.Text ?? string.Empty));
            }
        }

        return offers.ToImmutable();
    }

    /// <summary>
    /// CR 508.1: the active player declares attackers in the declare attackers step, and does so
    /// once. Declaring is a turn-based action taken before any player receives priority in the
    /// step, so this never asks who holds it. Which of that player's creatures could be declared
    /// — untapped, controlled since the turn began (CR 302.6) or hasty (CR 702.10b), without
    /// defender (CR 702.3b), and unrestricted by any "can't attack" — is
    /// <see cref="ICombatSystem.CouldAttack"/>'s single reading of CR 508.1a, the same one the
    /// declaration itself is judged by.
    /// </summary>
    [RuleRef("508.1")]
    private ImmutableList<ObjectId> EligibleAttackers(GameState state, PlayerId player)
    {
        if (_combat is null)
            return [];
        if (state.Turn.Step != Step.DeclareAttackers || state.Turn.ActivePlayer != player || state.Combat is { AttackersDeclared: true })
            return [];

        return [.. Creatures(state, player).Where(c => _combat.CouldAttack(state, c.Object.Id)).Select(c => c.Object.Id)];
    }

    /// <summary>
    /// CR 509.1: in the declare blockers step, each defending player declares blockers once. A
    /// player defends when an attacker attacks them or a permanent they control, CR 506.2.
    /// Which of their creatures could block at all is
    /// <see cref="ICombatSystem.CouldBlock"/>'s reading of CR 509.1a; which attacker each one may
    /// block, and every evasion the attackers have, needs the attacker and so is judged when the
    /// declaration arrives.
    /// </summary>
    [RuleRef("509.1")]
    private ImmutableList<ObjectId> EligibleBlockers(GameState state, PlayerId player)
    {
        if (_combat is null)
            return [];
        if (state.Turn.Step != Step.DeclareBlockers || state.Combat is not { AttackersDeclared: true, BlockersDeclared: false } combat)
            return [];
        if (!combat.Attackers.Values.Any(target => Defends(state, target, player)))
            return [];

        return [.. Creatures(state, player).Where(c => _combat.CouldBlock(state, c.Object.Id)).Select(c => c.Object.Id)];
    }

    [RuleRef("506.2")]
    private bool Defends(GameState state, CombatTarget target, PlayerId player) =>
        target.Player == player
        || (target.Permanent is { } permanent
            && state.Objects.ContainsKey(permanent)
            && _characteristics.Of(state, permanent).Controller == player);

    private IEnumerable<(GameObject Object, Characteristics Chars)> Permanents(GameState state) =>
        state.Objects.Values
            .Where(o => o.Zone.Zone == CardZone.Battlefield)
            .OrderBy(o => o.Id.Value)
            .Select(o => (Object: o, Chars: _characteristics.Of(state, o.Id)));

    private IEnumerable<(GameObject Object, Characteristics Chars)> Creatures(GameState state, PlayerId player) =>
        Permanents(state).Where(p => p.Chars.Controller == player && p.Chars.Types.HasFlag(CardType.Creature));
}
