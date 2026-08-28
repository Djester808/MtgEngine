using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Engine;

/// <summary>
/// Who may attack, who may block, and how much damage goes where (CR 508–510).
/// </summary>
/// <remarks>
/// Pure: everything here reads state and reports a verdict or a list of events. The engine
/// applies them. Every characteristic it consults — power, toughness, keywords — is asked for
/// through <see cref="Characteristics"/> and so is the value after continuous effects, not the
/// value printed on the card. A creature given flying this turn can be blocked only by flyers and
/// reach; one that lost it can be blocked by anything.
/// </remarks>
public static class CombatRules
{
    /// <summary>Why a creature cannot be declared as an attacker, or null if it can (CR 508.1a).</summary>
    /// <param name="defendingPlayer">
    /// Who this creature is being declared against, when that is known. A restriction that asks
    /// about the defender's board cannot be checked without one, so a caller that has no defender
    /// in hand skips it rather than guessing (CR 506.3).
    /// </param>
    public static string? CannotAttack(
        GameState state,
        IAbilitySource abilities,
        GameObject creature,
        Guid attackingPlayer,
        Guid? defendingPlayer = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(abilities);

        var computed = Characteristics.Of(state, abilities, creature);

        if (creature.Zone != Zone.Battlefield || !computed.IsCreature)
            return "only a creature on the battlefield can attack";

        // CR 613.1b: control is layer 2, so the controller stored on the object is only where
        // the permanent started. Threaten took a creature, untapped it and gave it haste, and
        // this then refused the attack because the stored value was still its owner's.
        if (computed.ControllerId != attackingPlayer)
            return "you do not control it";

        if (creature.Permanent?.IsTapped == true)
            return "it is tapped (CR 508.1a)";

        // CR 508.1a: haste, or controlled continuously since the turn began (CR 302.6).
        if (creature.Permanent?.HasSummoningSickness == true
            && !computed.Has(KeywordAbility.Haste))
        {
            return "it has summoning sickness and no haste (CR 302.6)";
        }

        // CR 702.3b.
        if (computed.Has(KeywordAbility.Defender))
            return "it has defender (CR 702.3b)";

        // CR 702.141a: an encore token is made to attack one named opponent. A restriction, not
        // a requirement - that it attacks at all is the MustAttack keyword, and this says where.
        if (defendingPlayer is { } aimedAt
            && computed.MustAttackPlayer is { } owedTo
            && aimedAt != owedTo
            && !state.GetPlayer(owedTo).HasLost)
        {
            return $"it must attack {state.GetPlayer(owedTo).Name} (CR 702.141a)";
        }

        // CR 701.15b: a goaded creature attacks a player other than the one who goaded it, if
        // able. In a two-player game there is nobody else and the restriction never binds, which
        // is right - a goaded creature there simply has to attack.
        //
        // "If able" is read as "somebody else is there to be attacked" rather than by asking
        // whether this creature could legally attack each of them: that question is this method,
        // and asking it of itself would recurse. With several goaders the requirements combine
        // in a way CR 701.15c leaves to the player, and this does not attempt it.
        if (defendingPlayer is { } goadedAt
            && computed.GoadedBy.Contains(goadedAt)
            && state.TurnOrder.Any(other =>
                other != attackingPlayer
                && other != goadedAt
                && !state.GetPlayer(other).HasLost))
        {
            return "it is goaded and must attack somebody else (CR 701.15b)";
        }

        // CR 506.3: an attack restriction that reads the defender's board. Checked with the same
        // helper landwalk uses, and for the same reason: land types are changed by real cards, so
        // it has to ask what the lands are now rather than what they were printed as.
        if (defendingPlayer is { } defender
            && abilities.AttacksOnlyIfDefenderControls(creature.Card) is { } needed
            && !DefenderControls(state, abilities, defender, needed))
        {
            return $"the defending player controls no {needed} (CR 506.3)";
        }

        return null;
    }

    /// <summary>
    /// Why a creature cannot be declared as a blocker for the given attacker, or null (CR 509.1a).
    /// </summary>
    public static string? CannotBlock(
        GameState state,
        IAbilitySource abilities,
        GameObject blocker,
        GameObject attacker,
        Guid defendingPlayer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(blocker);
        ArgumentNullException.ThrowIfNull(attacker);

        var blocking = Characteristics.Of(state, abilities, blocker);
        var attacking = Characteristics.Of(state, abilities, attacker);

        if (blocker.Zone != Zone.Battlefield || !blocking.IsCreature)
            return "only a creature on the battlefield can block";

        // The same read as in CannotAttack, and the same bug: a creature taken with a
        // "you control enchanted creature" Aura could not be blocked with (CR 613.1b).
        if (blocking.ControllerId != defendingPlayer)
            return "you do not control it";

        if (blocker.Permanent?.IsTapped == true)
            return "it is tapped (CR 509.1a)";

        // "Target creature can't block ~ this turn" - a restriction naming one attacker rather
        // than all of them, which the blanket can't-block flag cannot say: that creature is free
        // to block anything else.
        if (blocking.CantBlockAttacker == attacker.Id)
            return $"it cannot block {attacker.Card.Name} this turn (CR 509.1b)";

        // CR 702.9b: flying can be blocked only by creatures with flying or reach. An evasion
        // ability is a restriction on the block, not on the attack (CR 509.1b).
        if (attacking.Has(KeywordAbility.Flying)
            && !blocking.Has(KeywordAbility.Flying)
            && !blocking.Has(KeywordAbility.Reach))
        {
            return "it cannot block a creature with flying (CR 702.9b)";
        }

        // CR 701.54c: the Ring's first ability. Power is the computed value on both sides, so a
        // blocker pumped after blocking is not retroactively illegal and one pumped before it
        // is - which is what "can't be blocked by" means everywhere else.
        if (attacking.CantBeBlockedByGreaterPower
            && (blocking.Power ?? 0) > (attacking.Power ?? 0))
        {
            return "it has greater power than the Ring-bearer (CR 701.54c)";
        }

        // CR 702.5b: horsemanship is flying's older cousin — only another horseman may block it.
        if (attacking.Has(KeywordAbility.Horsemanship)
            && !blocking.Has(KeywordAbility.Horsemanship))
        {
            return "it cannot block a creature with horsemanship (CR 702.5b)";
        }

        // CR 702.16e: a creature with protection cannot be blocked by anything it is protected
        // from — the "B" of DEBT, and the reason protection reads as evasion.
        if (attacking.IsProtectedFrom(blocking))
            return "that creature has protection from it (CR 702.16e)";

        // CR 702.36b: fear. Artifact creatures and black creatures may block it; nothing else.
        if (attacking.Has(KeywordAbility.Fear)
            && !blocking.CardTypes.HasFlag(Domain.Enums.CardType.Artifact)
            && !blocking.Colors.Contains(ManaColor.Black))
        {
            return "it cannot block a creature with fear (CR 702.36b)";
        }

        // CR 702.13b: intimidate is fear by colour — it takes an artifact, or a creature sharing
        // one of its own colours.
        if (attacking.Has(KeywordAbility.Intimidate)
            && !blocking.CardTypes.HasFlag(Domain.Enums.CardType.Artifact)
            && !attacking.Colors.Any(blocking.Colors.Contains))
        {
            return "it cannot block a creature with intimidate (CR 702.13b)";
        }

        // CR 702.28b: shadow cuts both ways — a creature with shadow can only be blocked by one
        // with shadow, and can only block one with shadow.
        if (attacking.Has(KeywordAbility.Shadow) != blocking.Has(KeywordAbility.Shadow))
            return "shadow keeps them apart (CR 702.28b)";

        // CR 702.17b: skulk asks about power as it stands now, which is why it is computed and
        // not read off the card — a pumped blocker can block what it could not a moment ago.
        if (attacking.Has(KeywordAbility.Skulk) && (blocking.Power ?? 0) > (attacking.Power ?? 0))
            return "it cannot block a creature with skulk (CR 702.17b)";

        if (blocking.Has(KeywordAbility.CantBlock))
            return "it cannot block";

        if (attacking.Has(KeywordAbility.CantBeBlocked))
            return "that creature cannot be blocked";

        // The mirror of reach: a creature that can block only fliers cannot stop anything else.
        if (blocking.Has(KeywordAbility.BlocksOnlyFlying) && !attacking.Has(KeywordAbility.Flying))
            return "it can block only creatures with flying";

        // CR 702.14: landwalk is unblockable while the defending player controls that land type.
        // It asks about the land the *defender* controls, not the attacker, which is why it is
        // checked here with the defending player in hand rather than at declaration.
        if (Landwalks(attacking) is { } walk
            && DefenderControls(state, abilities, defendingPlayer, walk.LandType))
        {
            return $"that creature has {walk.Name} and you control a {walk.LandType} (CR 702.14)";
        }

        return null;
    }

    /// <summary>The landwalk this creature has, if any (CR 702.14).</summary>
    private static (string Name, string LandType)? Landwalks(ComputedCharacteristics attacking)
    {
        if (attacking.Has(KeywordAbility.Swampwalk))
            return ("swampwalk", "Swamp");
        if (attacking.Has(KeywordAbility.Forestwalk))
            return ("forestwalk", "Forest");
        if (attacking.Has(KeywordAbility.Islandwalk))
            return ("islandwalk", "Island");
        if (attacking.Has(KeywordAbility.Mountainwalk))
            return ("mountainwalk", "Mountain");
        if (attacking.Has(KeywordAbility.Plainswalk))
            return ("plainswalk", "Plains");

        return null;
    }

    /// <summary>
    /// Whether the defending player controls a land of that subtype (CR 702.14b).
    /// </summary>
    /// <remarks>
    /// Computed, not printed. Land types are changed by real and common cards — an effect that
    /// makes every land a Swamp turns swampwalk on against everyone, and one that strips land
    /// types turns it off. Reading the printed subtypes would make landwalk answer to what the
    /// lands were rather than what they are.
    /// <para>
    /// Control is computed for the same reason it is everywhere else: a land you have taken is
    /// yours, and it is your creatures it lets through (CR 613.1b).
    /// </para>
    /// </remarks>
    private static bool DefenderControls(
        GameState state, IAbilitySource abilities, Guid defendingPlayer, string landType)
    {
        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);

            // The type test is the expensive half, so the cheap ownership test goes first — and
            // ownership itself has to be computed, which is why it is not simply obj.ControllerId.
            var computed = Characteristics.Of(state, abilities, obj);
            if (computed.ControllerId == defendingPlayer && computed.HasSubtype(landType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Why a whole set of attacks is illegal, or null (CR 508.1d).
    /// </summary>
    /// <remarks>
    /// Requirements are about the declaration, not about one creature: "attacks each combat if
    /// able" is broken by leaving that creature at home, which is only visible once you know the
    /// whole set. A creature that <em>cannot</em> attack — tapped, sick, a defender — is not
    /// able, and so is not required.
    /// </remarks>
    public static string? IllegalAttackSet(
        GameState state,
        IAbilitySource abilities,
        Guid attackingPlayer,
        IReadOnlyCollection<ObjectId> attackers)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(attackers);

        // CR 506.3c: "can't attack alone" is a restriction on the declaration, not on the
        // creature, so it cannot be answered by CannotAttack - which is handed one creature and
        // has no way to know how many others are coming with it.
        if (attackers.Count == 1)
        {
            var only = state.GetObject(attackers.First());
            if (Characteristics.Of(state, abilities, only).Has(KeywordAbility.CantAttackAlone))
                return $"{only.Card.Name} cannot attack alone";
        }

        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            if (attackers.Contains(id))
                continue;

            // Computed before the controller is read, not after: CR 508.1d is about creatures the
            // attacking player controls *now*, and a creature taken from its owner is one of them.
            var computed = Characteristics.Of(state, abilities, obj);
            if (computed.ControllerId != attackingPlayer)
                continue;

            if (!computed.Has(KeywordAbility.MustAttack))
                continue;

            // Only required when it could have attacked at all.
            if (CannotAttack(state, abilities, obj, attackingPlayer) is null)
                return $"{obj.Card.Name} attacks each combat if able (CR 508.1d)";
        }

        return null;
    }

    /// <summary>
    /// Why a whole set of blocks is illegal, or null. Checked across the declaration, because
    /// some restrictions are about how many creatures block one attacker (CR 509.1c).
    /// </summary>
    public static string? IllegalBlockSet(
        GameState state,
        IAbilitySource abilities,
        IReadOnlyDictionary<ObjectId, ImmutableListOfBlockers> blocks)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(blocks);

        // CR 509.1a: a creature blocks one attacker unless something lets it block more. The
        // limit is counted across the whole declaration rather than per attacker, because that
        // is the only place the same creature appearing in two lists is visible at all.
        var blocking = new Dictionary<ObjectId, int>();
        foreach (var (_, blockers) in blocks)
        {
            foreach (var blockerId in blockers.Ids)
            {
                blocking[blockerId] = blocking.TryGetValue(blockerId, out var soFar) ? soFar + 1 : 1;
            }
        }

        // CR 509.1b, the mirror of the attack restriction. "Alone" counts blocking creatures
        // across the whole declaration: another creature blocking a *different* attacker is
        // company enough, which is why this is asked here rather than per attacker.
        if (blocking.Count == 1)
        {
            var only = state.GetObject(blocking.Keys.First());
            if (Characteristics.Of(state, abilities, only).Has(KeywordAbility.CantBlockAlone))
                return $"{only.Card.Name} cannot block alone";
        }

        foreach (var (blockerId, count) in blocking)
        {
            var allowed = 1 + Characteristics.Of(state, abilities, state.GetObject(blockerId)).ExtraBlocks;
            if (count > allowed)
            {
                return $"{state.GetObject(blockerId).Card.Name} cannot block {count} creatures "
                    + $"at once (CR 509.1a)";
            }
        }

        if (UnmetBlockRequirement(state, abilities, blocks, blocking) is { } unmet)
            return unmet;

        foreach (var (attackerId, blockers) in blocks)
        {
            var attacker = Characteristics.Of(state, abilities, state.GetObject(attackerId));

            // CR 702.111b: menace means it can't be blocked except by two or more creatures.
            if (attacker.Has(KeywordAbility.Menace) && blockers.Ids.Count == 1)
                return "a creature with menace cannot be blocked by exactly one creature (CR 702.111b)";

            // The same rule with a number the keyword cannot say - "except by three or more".
            // Checked beside menace rather than instead of it, because a card can print either.
            if (attacker.MinBlockers > 1 && blockers.Ids.Count < attacker.MinBlockers)
            {
                return $"{state.GetObject(attackerId).Card.Name} cannot be blocked by fewer than "
                    + $"{attacker.MinBlockers} creatures (CR 509.1b)";
            }

            // CR 509.1b: an evasion restriction is about one blocker at a time, so each is asked
            // separately — unlike menace, which is about how many there are.
            if (attacker.BlockRestrictions.IsEmpty)
                continue;

            foreach (var blockerId in blockers.Ids)
            {
                var blocker = state.GetObject(blockerId);
                var attackerObj = state.GetObject(attackerId);

                if (attacker.BlockRestrictions.Any(
                    allows => !allows(state, abilities, attackerObj, blocker)))
                {
                    return $"{state.GetObject(blockerId).Card.Name} cannot block "
                        + $"{state.GetObject(attackerId).Card.Name} (CR 509.1b)";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Which block requirement the declaration fails to satisfy, or null (CR 509.1c).
    /// </summary>
    /// <remarks>
    /// A lure - "all creatures able to block ~ do so" - is a requirement, and the rule is not
    /// "every creature blocks it" but "as many requirements are satisfied as possible without
    /// breaking a restriction". The difference shows the moment there are two lures: a creature
    /// that can block both still blocks only one, and the declaration is legal either way.
    /// <para>
    /// So each creature is asked how many lures it could block and how many it does, and the
    /// answer has to be as many as it can manage - which is one, or more if something has let it
    /// block additional creatures. Requirements from anywhere other than a lure are not modelled;
    /// this is the only shape of requirement the compiler can produce.
    /// </para>
    /// <para>
    /// "Able" is judged one creature at a time, so a lure with menace requires nothing at all -
    /// no single creature is able to block it, and whether two together are is a question about
    /// the set that this shape of check cannot ask. Deliberately the lenient way round: the
    /// alternative leaves the defender with no legal declaration, forced to block by the
    /// requirement and refused by menace for blocking alone.
    /// </para>
    /// </remarks>
    private static string? UnmetBlockRequirement(
        GameState state,
        IAbilitySource abilities,
        IReadOnlyDictionary<ObjectId, ImmutableListOfBlockers> blocks,
        Dictionary<ObjectId, int> blocking)
    {
        var lures = new List<ObjectId>();
        var wanted = new List<ObjectId>();

        foreach (var (attackerId, _) in state.Combat.Attackers)
        {
            var computed = Characteristics.Of(state, abilities, state.GetObject(attackerId));

            if (computed.MustBeBlockedByAll)
                lures.Add(attackerId);

            if (computed.MustBeBlocked)
                wanted.Add(attackerId);
        }

        // "~ must be blocked if able" asks for one blocker, not all of them (CR 509.1c). Checked
        // first and separately, because a creature that carries both is answered by neither
        // check alone - and because this one is satisfied the moment anything blocks it.
        foreach (var attackerId in wanted)
        {
            if (blocks.TryGetValue(attackerId, out var already) && already.Ids.Count > 0)
                continue;

            var defender = state.Combat.Attackers[attackerId].DefendingPlayer;

            var couldHave = state.Battlefield.Any(candidateId =>
            {
                var candidate = state.GetObject(candidateId);
                var computed = Characteristics.Of(state, abilities, candidate);

                return computed.CardTypes.HasFlag(CardType.Creature)
                    && computed.ControllerId == defender
                    && !blocking.ContainsKey(candidateId)
                    && CanBlockForRequirement(state, abilities, candidate, attackerId, defender);
            });

            if (couldHave)
            {
                return $"{state.GetObject(attackerId).Card.Name} must be blocked if able "
                    + "(CR 509.1c).";
            }
        }

        // The requirements read from the blocker's side. Checked before the lures because they
        // are the more specific demand: a creature told to block one named attacker has no
        // freedom left to satisfy anything else with.
        foreach (var candidateId in state.Battlefield)
        {
            var candidate = state.GetObject(candidateId);
            var computed = Characteristics.Of(state, abilities, candidate);

            if (!computed.CardTypes.HasFlag(CardType.Creature))
                continue;

            if (computed.MustBlockAttacker is { } owed)
            {
                // The attacker may not be attacking at all - the requirement outlives the combat
                // it was made in - and then there is nothing to satisfy.
                if (!state.Combat.Attackers.TryGetValue(owed, out var attacked)
                    || attacked.DefendingPlayer != computed.ControllerId)
                {
                    continue;
                }

                if (blocks.TryGetValue(owed, out var already) && already.Ids.Contains(candidateId))
                    continue;

                if (!CanBlockForRequirement(state, abilities, candidate, owed, computed.ControllerId))
                    continue;

                return $"{candidate.Card.Name} must block "
                    + $"{state.GetObject(owed).Card.Name} (CR 509.1a).";
            }

            if (!computed.MustBlock || blocking.ContainsKey(candidateId))
                continue;

            // "Blocks this turn if able" names no attacker, so any of them will do - and the
            // requirement only binds while there is one this creature could legally block.
            var couldBlockSomething = state.Combat.Attackers.Any(
                pair => pair.Value.DefendingPlayer == computed.ControllerId
                    && CanBlockForRequirement(
                        state, abilities, candidate, pair.Key, computed.ControllerId));

            if (couldBlockSomething)
                return $"{candidate.Card.Name} must block this turn (CR 509.1a).";
        }

        if (lures.Count == 0)
            return null;

        foreach (var attackerId in lures)
        {
            var defender = state.Combat.Attackers[attackerId].DefendingPlayer;

            foreach (var candidateId in state.Battlefield)
            {
                var candidate = state.GetObject(candidateId);
                var computed = Characteristics.Of(state, abilities, candidate);

                if (!computed.CardTypes.HasFlag(CardType.Creature) || computed.ControllerId != defender)
                    continue;

                // How many of the lures this creature could block, and how many it does. Counted
                // across all of them rather than for this one alone, because a creature that can
                // only block once satisfies whichever requirement its controller picks.
                var able = 0;
                var taken = 0;
                foreach (var lureId in lures)
                {
                    if (!CanBlockForRequirement(state, abilities, candidate, lureId, defender))
                        continue;

                    able++;
                    if (blocks.TryGetValue(lureId, out var declared) && declared.Ids.Contains(candidateId))
                        taken++;
                }

                if (able == 0)
                    continue;

                var owed = Math.Min(able, 1 + computed.ExtraBlocks);
                if (taken >= owed)
                    continue;

                // A creature that is blocking something else instead is the commonest way to get
                // here, and the message says so rather than only naming the rule.
                var busy = blocking.TryGetValue(candidateId, out var count) && count > 0;
                return $"{candidate.Card.Name} must block "
                    + $"{state.GetObject(attackerId).Card.Name}"
                    + (busy ? " rather than another attacker" : string.Empty)
                    + " (CR 509.1c)";
            }
        }

        return null;
    }

    /// <summary>Whether a creature is able to block a lure, for the requirement (CR 509.1c).</summary>
    private static bool CanBlockForRequirement(
        GameState state,
        IAbilitySource abilities,
        GameObject candidate,
        ObjectId attackerId,
        Guid defender)
    {
        var attackerObj = state.GetObject(attackerId);
        if (CannotBlock(state, abilities, candidate, attackerObj, defender) is not null)
            return false;

        var attacker = Characteristics.Of(state, abilities, attackerObj);

        // Menace is about how many block, so one creature alone is not able to block it. Asked
        // here rather than left to the set check so the requirement never demands an illegal
        // declaration - see the remarks on the caller.
        if (attacker.Has(KeywordAbility.Menace))
            return false;

        return attacker.BlockRestrictions.All(allows => allows(state, abilities, attackerObj, candidate));
    }

    /// <summary>
    /// The damage every attacker and blocker deals, as one batch (CR 510.1, 510.2).
    /// </summary>
    /// <remarks>
    /// Assigned and dealt as one simultaneous event, which is why two creatures that kill each
    /// other both die: neither is destroyed before the other assigns.
    /// <para>
    /// <paramref name="firstStrikeOnly"/> selects the first of the two damage steps first strike
    /// creates (CR 510.4). In that step only first and double strikers assign; in the one after,
    /// everything that has not already assigned does, plus double strikers again.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GameEvent> AssignCombatDamage(
        GameState state,
        IAbilitySource abilities,
        bool firstStrikeOnly,
        IReadOnlyDictionary<ObjectId, Dictionary<ObjectId, int>>? division = null,
        IReadOnlySet<ObjectId>? assigningAsThoughUnblocked = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var combat = state.Combat;
        var events = new List<GameEvent>();

        foreach (var (attackerId, target) in combat.Attackers)
        {
            var defendingPlayer = target.DefendingPlayer;
            if (!state.TryGetObject(attackerId, out var attacker))
                continue;

            var computed = Characteristics.Of(state, abilities, attacker);
            if (!AssignsThisStep(computed, firstStrikeOnly))
                continue;

            var power = computed.Power ?? 0;
            if (power <= 0)
                continue;

            // CR 509.1h: still blocked even if every blocker has gone, so it deals nothing.
            if (combat.Blocked.Contains(attackerId))
            {
                AssignToBlockers(
                    state, abilities, attackerId, computed, power, target,
                    division?.GetValueOrDefault(attackerId),
                    assigningAsThoughUnblocked?.Contains(attackerId) == true,
                    events);
            }
            else
            {
                events.Add(Unblocked(state, target, attackerId, power, computed));
            }
        }

        foreach (var (attackerId, blockers) in combat.Blockers)
        {
            foreach (var blockerId in blockers)
            {
                if (!state.TryGetObject(blockerId, out var blocker))
                    continue;

                var computed = Characteristics.Of(state, abilities, blocker);
                if (!AssignsThisStep(computed, firstStrikeOnly))
                    continue;

                var power = computed.Power ?? 0;
                if (power <= 0 || !state.TryGetObject(attackerId, out _))
                    continue;

                // CR 510.1d: a blocker assigns its damage to the creature it is blocking.
                events.Add(new DamageMarked(
                    attackerId,
                    power,
                    computed.Has(KeywordAbility.Deathtouch),
                    blockerId,
                    IsCombat: true));
            }
        }

        // Summarised after the fact rather than tracked alongside it, so there is one list and
        // no way for the summary to disagree with the events it summarises.
        var dealers = events
            .Select(e => e switch
            {
                PlayerDamaged hit => hit.SourceId,
                DamageMarked struck => struck.SourceId,
                _ => (ObjectId?)null,
            })
            .OfType<ObjectId>();

        if (dealers.Any())
        {
            events.Add(new CombatDamageDealt(
                [.. dealers],
                [.. events.OfType<PlayerDamaged>().Select(hit => hit.SourceId)]));
        }

        return events;
    }

    private static void AssignToBlockers(
        GameState state,
        IAbilitySource abilities,
        ObjectId attackerId,
        ComputedCharacteristics computed,
        int power,
        AttackTarget target,
        IReadOnlyDictionary<ObjectId, int>? chosenDivision,
        bool asThoughUnblocked,
        List<GameEvent> events)
    {
        var remaining = power;
        var deathtouch = computed.Has(KeywordAbility.Deathtouch);

        // CR 510.1a: the attacking player took the permission, so the blockers are assigned
        // nothing at all and the whole amount goes to what the creature was attacking. It is
        // still a blocked creature - the blockers deal their damage back as usual.
        if (asThoughUnblocked)
        {
            events.Add(Unblocked(state, target, attackerId, remaining, computed));
            return;
        }

        // CR 510.1c: divided as the attacker's controller chooses. With more than one blocker
        // the division is asked for; with one there is nothing to divide and all of it goes
        // there.
        if (chosenDivision is not null)
        {
            foreach (var (blockerId, amount) in chosenDivision)
            {
                if (amount <= 0 || !state.TryGetObject(blockerId, out _))
                    continue;

                events.Add(new DamageMarked(blockerId, amount, deathtouch, attackerId, IsCombat: true));
                remaining -= amount;
            }
        }
        else
        {
            foreach (var blockerId in state.Combat.BlockersOf(attackerId))
            {
                if (remaining <= 0)
                    break;

                if (!state.TryGetObject(blockerId, out var blocker))
                    continue;

                var lethal = LethalDamage(state, abilities, blocker, deathtouch);
                var assigned = Math.Min(remaining, lethal);
                if (assigned <= 0)
                    continue;

                events.Add(new DamageMarked(blockerId, assigned, deathtouch, attackerId, IsCombat: true));
                remaining -= assigned;
            }
        }

        // CR 702.19b: trample assigns whatever is left to what the creature was attacking, once
        // every blocker has lethal damage. Without trample the excess is simply not assigned.
        if (remaining > 0 && computed.Has(KeywordAbility.Trample))
            events.Add(Unblocked(state, target, attackerId, remaining, computed));
    }

    /// <summary>
    /// Damage from a creature nothing is standing in the way of (CR 510.1b).
    /// </summary>
    /// <remarks>
    /// A planeswalker takes it as loyalty counters removed rather than as life lost (CR 306.7),
    /// which is why the target is carried rather than just the defending player.
    /// </remarks>
    private static GameEvent Unblocked(
        GameState state,
        AttackTarget target,
        ObjectId attackerId,
        int amount,
        ComputedCharacteristics computed)
    {
        if (target.IsPlaneswalker && state.TryGetObject(target.Planeswalker, out _))
        {
            return new DamageMarked(
                target.Planeswalker,
                amount,
                computed.Has(KeywordAbility.Deathtouch),
                attackerId,
                IsCombat: true);
        }

        // CR 508.1b: a creature attacking a planeswalker that has left the battlefield assigns
        // no combat damage at all — it does not fall through to the player.
        return target.IsPlaneswalker
            ? new NothingHappened()
            : new PlayerDamaged(target.DefendingPlayer, attackerId, amount, IsCombat: true);
    }

    /// <summary>
    /// How much damage is lethal to a creature right now (CR 510.1c): its toughness less damage
    /// already marked, or 1 if the source has deathtouch (CR 702.2b).
    /// </summary>
    private static int LethalDamage(
        GameState state, IAbilitySource abilities, GameObject creature, bool deathtouch)
    {
        if (deathtouch)
            return 1;

        var toughness = Characteristics.ToughnessOf(state, abilities, creature) ?? 0;
        return Math.Max(0, toughness - (creature.Permanent?.DamageMarked ?? 0));
    }

    /// <summary>Which creatures assign damage in this step (CR 510.4).</summary>
    internal static bool AssignsThisStep(ComputedCharacteristics computed, bool firstStrikeOnly)
    {
        var first = computed.Has(KeywordAbility.FirstStrike);
        var doubleStrike = computed.Has(KeywordAbility.DoubleStrike);

        // In the first step, only first and double strikers. In the second, everything else —
        // plus double strikers, which assign in both.
        return firstStrikeOnly ? first || doubleStrike : !first || doubleStrike;
    }

    /// <summary>Whether any creature in combat has first or double strike (CR 510.4).</summary>
    public static bool NeedsFirstStrikeStep(GameState state, IAbilitySource abilities)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Combat.Attackers.Keys
            .Concat(state.Combat.Blockers.Values.SelectMany(b => b))
            .Where(id => state.TryGetObject(id, out _))
            .Select(id => Characteristics.Of(state, abilities, state.GetObject(id)))
            .Any(c => c.Has(KeywordAbility.FirstStrike) || c.Has(KeywordAbility.DoubleStrike));
    }
}

/// <summary>A declared block: one attacker and the creatures blocking it, in damage order.</summary>
public sealed record ImmutableListOfBlockers(IReadOnlyList<ObjectId> Ids);
