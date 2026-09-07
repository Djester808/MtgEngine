using MtgEngine.Cards;
using MtgEngine.Rules.Commands;
using MtgEngine.Rules.Engine;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Combat;

/// <summary>
/// The restrictions on blocking, CR 509.1b: the evasion keywords of CR 702 and the continuous
/// effects that say a creature can't block or can't be blocked. Each arm names its rule, and
/// each reads the keyword from computed characteristics, so a granted keyword evades too.
/// </summary>
public sealed partial class CombatSystem
{
    [RuleRef("509.1a")]
    private void CheckBlocker(GameState state, CombatState combat, PlayerId player, BlockDeclaration block)
    {
        if (!OnBattlefield(state, block.Blocker))
            throw new RefusedException("A blocker must be a permanent on the battlefield.", "509.1a");

        var blocker = _characteristics.Of(state, block.Blocker);
        if (!blocker.Types.HasFlag(CardType.Creature))
            throw new RefusedException("Only a creature can block.", "509.1a");
        if (blocker.Controller != player)
            throw new RefusedException("A player may only block with creatures they control.", "509.1a");
        if (state.Objects[block.Blocker].Tapped)
            throw new RefusedException("A tapped creature can't block.", "509.1a");

        if (!combat.Attackers.TryGetValue(block.Attacker, out var target))
            throw new RefusedException("That creature is not attacking.", "509.1a");

        // CR 509.1a — the attacker must be attacking this player, or a permanent of theirs.
        if (DefendingPlayer(state, target) != player)
            throw new RefusedException("A creature may only block an attacker attacking its controller or their permanents.", "509.1a");

        CheckEvasion(state, block.Attacker, block.Blocker, blocker);
    }

    /// <summary>CR 509.1b — the per-blocker restrictions. Restrictions that count blockers are checked once the declaration is whole.</summary>
    [RuleRef("509.1b")]
    private void CheckEvasion(GameState state, ObjectId attackerId, ObjectId blockerId, Characteristics blocker)
    {
        var attacker = _characteristics.Of(state, attackerId);

        // CR 702.9b — flying can't be blocked except by creatures with flying and/or reach (CR 702.17b).
        if (attacker.Has(KeywordKind.Flying) && !blocker.Has(KeywordKind.Flying) && !blocker.Has(KeywordKind.Reach))
            throw new RefusedException("A creature with flying can't be blocked except by creatures with flying or reach.", "702.9b");

        // CR 702.28b — shadow can't be blocked by creatures without it, nor can a creature without it be blocked by one with it.
        if (attacker.Has(KeywordKind.Shadow) != blocker.Has(KeywordKind.Shadow))
            throw new RefusedException("Shadow and non-shadow creatures can't block each other.", "702.28b");

        // CR 702.31b — horsemanship can't be blocked by creatures without horsemanship.
        if (attacker.Has(KeywordKind.Horsemanship) && !blocker.Has(KeywordKind.Horsemanship))
            throw new RefusedException("A creature with horsemanship can't be blocked by creatures without horsemanship.", "702.31b");

        // CR 702.36b — fear can't be blocked except by artifact creatures and/or black creatures.
        if (attacker.Has(KeywordKind.Fear) && !blocker.Types.HasFlag(CardType.Artifact) && !blocker.Colors.HasFlag(Color.Black))
            throw new RefusedException("A creature with fear can't be blocked except by artifact creatures or black creatures.", "702.36b");

        // CR 702.13b — intimidate can't be blocked except by artifact creatures and/or creatures that share a color with it.
        if (attacker.Has(KeywordKind.Intimidate) && !blocker.Types.HasFlag(CardType.Artifact) && (attacker.Colors & blocker.Colors) == Color.Colorless)
            throw new RefusedException("A creature with intimidate can't be blocked except by artifact creatures or creatures sharing a color with it.", "702.13b");

        // CR 702.118b — skulk can't be blocked by creatures with greater power.
        if (attacker.Has(KeywordKind.Skulk) && (blocker.Power ?? 0) > (attacker.Power ?? 0))
            throw new RefusedException("A creature with skulk can't be blocked by creatures with greater power.", "702.118b");

        CheckLandwalk(state, attacker, blocker.Controller);
        CheckBlockRestrictions(state, attackerId, attacker, blockerId, blocker);
    }

    /// <summary>
    /// CR 702.14b — landwalk is an evasion ability: the creature can't be blocked at all as long
    /// as the defending player controls a land of the named type. The type is the keyword's own
    /// filter, and whether a land matches it is the query layer's answer, never a name here.
    /// </summary>
    [RuleRef("702.14b")]
    private void CheckLandwalk(GameState state, Characteristics attacker, PlayerId defending)
    {
        foreach (var walk in attacker.Abilities.OfType<KeywordAbility>().Where(k => k.Keyword == KeywordKind.Landwalk))
        {
            var of = walk.Of ?? new ObjectFilter(Types: CardType.Land);
            var context = Context(null, defending);
            foreach (var id in Battlefield(state))
            {
                if (_characteristics.Of(state, id).Controller != defending)
                    continue;
                if (_query.Matches(state, of, id, context))
                    throw new RefusedException("A creature with landwalk can't be blocked while the defending player controls a land of that type.", "702.14b");
            }
        }
    }

    /// <summary>CR 509.1b — "can't be blocked" on the attacker and "can't block" on the blocker.</summary>
    [RuleRef("509.1b")]
    private void CheckBlockRestrictions(GameState state, ObjectId attackerId, Characteristics attacker, ObjectId blockerId, Characteristics blocker)
    {
        foreach (var cant in Statics<CantBeBlocked>(attacker))
        {
            // "except by two or more creatures" counts blockers, so it waits for the whole declaration.
            if (cant.ExceptByTwoOrMore)
                continue;
            if (cant.By is null)
                throw new RefusedException("An effect says that creature can't be blocked.", "509.1b");
            if (_query.Matches(state, cant.By, blockerId, Context(attackerId, blocker.Controller)))
                throw new RefusedException("An effect says that creature can't be blocked by this creature.", "509.1b");
        }

        foreach (var cant in Statics<CantBlock>(blocker))
        {
            if (cant.Attackers is null)
                throw new RefusedException("An effect says that creature can't block.", "509.1b");
            if (_query.Matches(state, cant.Attackers, attackerId, Context(blockerId, blocker.Controller)))
                throw new RefusedException("An effect says that creature can't block that attacker.", "509.1b");
        }
    }

    /// <summary>CR 509.1b — the restrictions that count how many creatures block one attacker.</summary>
    [RuleRef("509.1b")]
    private void CheckBlockerCount(GameState state, ObjectId attackerId, ImmutableList<ObjectId> blockers)
    {
        var attacker = _characteristics.Of(state, attackerId);

        // CR 702.111b — menace can't be blocked except by two or more creatures.
        if (attacker.Has(KeywordKind.Menace) && blockers.Count < 2)
            throw new RefusedException("A creature with menace can't be blocked except by two or more creatures.", "702.111b");

        if (blockers.Count < 2 && Statics<CantBeBlocked>(attacker).Any(c => c.ExceptByTwoOrMore))
            throw new RefusedException("An effect says that creature can't be blocked except by two or more creatures.", "509.1b");

        foreach (var limit in Statics<CantBeBlockedByMoreThan>(attacker))
        {
            if (blockers.Count > limit.Count)
                throw new RefusedException("An effect says that creature can't be blocked by that many creatures.", "509.1b");
        }
    }
}
