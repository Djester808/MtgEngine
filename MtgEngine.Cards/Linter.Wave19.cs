namespace MtgEngine.Cards;

/// <summary>
/// The wave-19 records that read something the game did rather than something the card says — the
/// zone an object came from, the damage an event dealt, the ability an effect is part of, the game's
/// own opening — and the printed alternative cost. Each check is where two halves of one of those
/// records can contradict each other or say nothing, and each names the rule it reads, as
/// <c>Linter.cs</c> does.
/// </summary>
public static partial class Linter
{
    /// <summary>Damage-shaped triggers: the events <see cref="TriggeringDamage"/> can read an amount from.</summary>
    private static bool IsDamageTrigger(object node) =>
        node is DealsDamage or IsDealtDamage or PlayerIsDealtDamage;

    private static void CheckWave19(Ability ability, IReadOnlyList<object> nodes, string path, List<LintFinding> findings)
    {
        // CR 603.6: a zone-change trigger names the zone it watches. From and NotFrom are the two
        // halves of one printed phrase — "from your graveyard", "from anywhere other than the
        // battlefield" — and a card prints one; with neither of them and no To the trigger fires on
        // every zone change in the game, which is a sentence lost rather than a trigger.
        foreach (var zone in nodes.OfType<ZoneChange>())
        {
            if (zone.From is not null && zone.NotFrom is not null)
                findings.Add(new(LintSeverity.Error, "L025", "ZoneChange sets both From and NotFrom; a card names the zone it watches or the one it excludes, never both.", path));
            if (zone.From is null && zone.NotFrom is null && zone.To is null)
                findings.Add(new(LintSeverity.Error, "L025", "ZoneChange names no zone at all; give it To, From or NotFrom, or the trigger fires on every zone change in the game.", path));
        }

        // CR 603.10: the amount is the triggering event's own information, so there has to be a
        // triggering damage event. The ability's own trigger or a delayed trigger it creates is
        // where that comes from; anywhere else there is nothing to read.
        if (nodes.OfType<TriggeringDamage>().Any())
        {
            var damage = ability is TriggeredAbility triggered && Walk(triggered.When).Any(IsDamageTrigger);
            damage = damage || nodes.OfType<DelayedTrigger>().Any(d => Walk(d.When).Any(IsDamageTrigger));

            // CR 613.1f: an Equipment's "equipped creature has 'whenever this creature deals combat
            // damage to a player, draw a card'" is a StaticAbility granting a TriggeredAbility, and
            // the damage the amount reads is that granted ability's own trigger. The scan stopped at
            // the static ability and refused every card shaped that way, so it descends through the
            // grant: any granted triggered ability whose trigger is damage-shaped is the event.
            damage = damage || nodes.OfType<GrantAbility>().Any(g => g.Ability is TriggeredAbility granted && Walk(granted.When).Any(IsDamageTrigger));
            if (!damage)
                findings.Add(new(LintSeverity.Error, "L028", "TriggeringDamage reads the damage of the event that triggered this ability and nothing here triggers on damage (CR 603.10).", path));
        }

        // CR 103.6: the one ability that functions before the game begins does so from the hand,
        // and a static ability scoped to the battlefield would never be read.
        if (nodes.OfType<MayBeginGameOnBattlefield>().Any() && ability is not StaticAbility { Where: Zone.Hand })
            findings.Add(new(LintSeverity.Error, "L027", "MayBeginGameOnBattlefield must be a StaticAbility whose Where is Hand; the ability functions from the opening hand before the game begins (CR 103.6).", path));

        // CR 707.9b with 109.5: "this ability" is the activated or triggered ability the effect is
        // part of. A static ability has none to hand the copy, and a spell ability leaves the stack
        // with the card, so the flag there keeps nothing.
        if (nodes.OfType<BecomesCopyOf>().Any(c => c.ExceptThisAbility) && ability is StaticAbility or SpellAbility)
            findings.Add(new(LintSeverity.Error, "L029", "BecomesCopyOf sets ExceptThisAbility on a static or spell ability; 'this ability' names the activated or triggered ability the effect is part of (CR 707.9b).", path));
    }

    /// <summary>
    /// A face's printed alternative costs, CR 118.9. A face-level check, not an ability one: the
    /// option is offered as the spell is cast, before any ability of it exists.
    /// </summary>
    private static void CheckAlternativeCosts(Face face, string path, List<LintFinding> findings)
    {
        if (face.AlternativeCosts is not { } alternatives)
            return;

        if (alternatives.Count == 0)
        {
            findings.Add(new(LintSeverity.Error, "L026", "AlternativeCosts is an empty list; leave it null when the face prints no alternative cost.", $"{path}.alternativeCosts"));
            return;
        }

        for (var i = 0; i < alternatives.Count; i++)
        {
            // CR 118.9: the alternative replaces the mana cost, so one that asks for nothing makes
            // the spell free — which is a printed line ("without paying its mana cost") no card
            // says this way, and far more often a cost the reading dropped.
            // ... unless the face prints exactly that line: "you may cast this spell without paying
            // its mana cost" is an alternative cost of nothing, and CR 118.9 names it in so many
            // words. The flag is that spelling and the empty Costs is its companion (L050).
            if (alternatives[i].WithoutPayingManaCost)
                continue;

            if (alternatives[i].Cost is Costs { All.Count: 0 } or ChooseCost { Options.Count: 0 })
                findings.Add(new(LintSeverity.Error, "L026", "An AlternativeCost asks for nothing, which would make the spell free; give it the cost the card prints (CR 118.9).", $"{path}.alternativeCosts[{i}]"));
        }
    }
}
