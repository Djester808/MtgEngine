using System.Collections.Immutable;
using MtgEngine.Domain.Enums;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Views;

/// <summary>
/// Turns the full game state into what one player may see (CR 400.2).
/// </summary>
/// <remarks>
/// The one rule this file exists to hold: <b>a <see cref="GameState"/> never reaches a client.</b>
/// Everything the transport sends goes through here first, so hidden zones are dropped once,
/// in a place with tests on it, rather than at each of the places that send something.
/// </remarks>
public static class PlayerViewProjector
{
    /// <summary>Builds the view for one seated player.</summary>
    public static GameView Project(GameState state, Guid viewer, IAbilitySource? abilities = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.Players.ContainsKey(viewer))
            throw new InvalidOperationException($"Player {viewer} is not in this game.");

        return new GameView
        {
            GameId = state.GameId,
            Viewer = viewer,
            TurnNumber = state.TurnNumber,
            ActivePlayerId = state.ActivePlayerId,
            CurrentStep = state.CurrentStep.ToString(),
            PriorityPlayerId = state.Priority.Holder,
            // Combat is public: who is attacking and who is blocking is visible to everyone at
            // the table (CR 506.1 happens in the open).
            AttackersDeclared = state.Combat.AttackersDeclared,
            BlockersDeclared = state.Combat.BlockersDeclared,
            Attackers = state.Combat.Attackers.ToImmutableDictionary(
                kv => kv.Key.Value,
                kv => new AttackTargetView(
                    kv.Value.DefendingPlayer,
                    kv.Value.IsPlaneswalker ? kv.Value.Planeswalker.Value : null)),
            Blockers = state.Combat.Blockers.ToImmutableDictionary(
                kv => kv.Key.Value, kv => kv.Value.Select(b => b.Value).ToImmutableList()),
            Players = [.. state.TurnOrder.Select(id => ProjectPlayer(state, id, viewer, abilities))],
            Battlefield = ProjectZone(state, state.Battlefield, abilities),
            Stack = ProjectZone(state, state.Stack, abilities),
            Exile = ProjectZone(state, state.Exile, abilities),
            Command = ProjectZone(state, state.Command, abilities),
            Choice = ProjectChoice(state, viewer),
        };
    }

    /// <summary>
    /// The name of a commander, found wherever it currently is.
    /// </summary>
    /// <remarks>
    /// Being a commander belongs to the card and survives every zone change (CR 903.3), so the
    /// search covers every object rather than one zone. Falls back to the oracle id, which is
    /// worse to read but never wrong.
    /// </remarks>
    private static string NameOfCommander(GameState state, string oracleId)
    {
        foreach (var (_, obj) in state.Objects)
        {
            if (string.Equals(obj.Card.OracleId, oracleId, StringComparison.Ordinal))
                return obj.Card.Name;
        }

        return oracleId;
    }

    private static ChoiceView? ProjectChoice(GameState state, Guid viewer)
    {
        if (state.Choice is not { } choice)
            return null;

        return new ChoiceView
        {
            Id = choice.Id,
            PlayerId = choice.PlayerId,
            Kind = choice.Kind.ToString(),
            Prompt = choice.Prompt,
            MinPicks = choice.MinPicks,
            MaxPicks = choice.MaxPicks,
            IsOrdering = choice.IsOrdering,
            IsDivision = choice.IsDivision,
            TotalToDivide = choice.TotalToDivide,
            // The options can be hidden information — bottoming after a mulligan lists the
            // asked player's hand — so only they are sent them.
            Options = choice.PlayerId == viewer
                ? [.. choice.Options.Select(o => new ChoiceOptionView(o.Id, o.Label))]
                : null,
        };
    }

    private static PlayerView ProjectPlayer(
        GameState state, Guid playerId, Guid viewer, IAbilitySource? abilities = null)
    {
        var player = state.GetPlayer(playerId);
        var isViewer = playerId == viewer;

        return new PlayerView
        {
            PlayerId = player.PlayerId,
            Name = player.Name,
            Life = player.Life,
            PoisonCounters = player.PoisonCounters,
            // Counts only. Nobody may look at a library, not even its owner (CR 401.2) - unless
            // something they control says otherwise, and then only for them.
            LibraryCount = player.Library.Count,
            TopOfLibrary = player.Library.Count > 0
                && SeesTopOfLibrary(state, abilities, playerId, isViewer)
                    ? ProjectObject(state, state.GetObject(player.Library[0]), abilities)
                    : null,
            HandCount = player.Hand.Count,
            Hand = isViewer ? ProjectZone(state, player.Hand, abilities) : null,

            // Revealed cards are their own list rather than a partly-filled hand, because null
            // Hand means "you may not see this" and an empty one would read as "there are none".
            // Sending the whole hand and letting the client hide the rest would be the cheating
            // vector the projection exists to close, so the filtering happens here.
            RevealedHand = ProjectZone(
                state,
                [.. player.Hand.Where(id => state.GetObject(id).IsRevealed)],
                abilities),
            Graveyard = ProjectZone(state, player.Graveyard, abilities),
            HasLost = player.HasLost,
            CommanderDamage = player.CommanderDamage.ToImmutableDictionary(
                kv => NameOfCommander(state, kv.Key), kv => kv.Value, StringComparer.Ordinal),
            CommanderName = player.CommanderOracleId is { } id ? NameOfCommander(state, id) : null,
            LandsPlayedThisTurn = player.LandsPlayedThisTurn,
            ManaPool = ProjectManaPool(player.ManaPool),
            RestrictedMana = [.. player.ManaPool.Restricted.Select(ProjectRestricted)],
        };
    }

    /// <summary>
    /// The pool as symbols, dropping the kinds a player has none of (CR 106.4).
    /// </summary>
    /// <remarks>
    /// Colourless is its own kind rather than an absence of colour (CR 106.1b), so it gets its
    /// own "C" entry rather than being folded into a total.
    /// </remarks>
    private static ImmutableDictionary<string, int> ProjectManaPool(ManaPool pool)
    {
        var symbols = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var (color, amount) in pool.Colored)
        {
            if (amount > 0)
            {
                symbols[SymbolOf(color)] = amount;
            }
        }

        if (pool.Colorless > 0)
        {
            symbols["C"] = pool.Colorless;
        }

        return symbols.ToImmutable();
    }

    private static RestrictedManaView ProjectRestricted(RestrictedMana mana) => new()
    {
        Symbol = mana.Color is { } color ? SymbolOf(color) : "C",
        SpendableOn = Describe(mana.Restriction),
    };

    /// <summary>The restriction in words a board can show without knowing the model.</summary>
    private static string Describe(ManaRestriction restriction)
    {
        var types = restriction.Types == default
            ? string.Empty
            : restriction.Types.ToString().Replace(", ", " or ", StringComparison.Ordinal).ToLowerInvariant() + " ";

        var what = new List<string>();
        if (restriction.Purposes.HasFlag(ManaPurpose.CastSpell))
            what.Add($"casting {types}spells");

        if (restriction.Purposes.HasFlag(ManaPurpose.ActivateAbility))
            what.Add($"activating abilities of {(types.Length == 0 ? "anything" : types.Trim())}");

        return what.Count == 0 ? "nothing" : string.Join(" or ", what);
    }

    private static string SymbolOf(ManaColor color) => color switch
    {
        ManaColor.White => "W",
        ManaColor.Blue => "U",
        ManaColor.Black => "B",
        ManaColor.Red => "R",
        ManaColor.Green => "G",
        _ => "C",
    };

    private static ImmutableList<ObjectView> ProjectZone(
        GameState state, ImmutableList<ObjectId> zone, IAbilitySource? abilities = null) =>
        [.. zone.Select(id => ProjectObject(state, state.GetObject(id), abilities))];

    /// <summary>
    /// Whether anything this player controls lets them see the top of their library (CR 401.2).
    /// </summary>
    /// <remarks>
    /// Asked of the battlefield rather than remembered on the player, because the permission
    /// lasts exactly as long as the permanent granting it - a card that leaves takes the view
    /// with it, and nothing has to notice that it left.
    /// </remarks>
    private static bool SeesTopOfLibrary(
        GameState state, IAbilitySource? abilities, Guid playerId, bool isViewer)
    {
        if (abilities is null)
            return false;

        foreach (var id in state.Battlefield)
        {
            var permanent = state.GetObject(id);
            if (permanent.ControllerId != playerId)
                continue;

            // Revealed is for everyone; "you may look" is for the player it belongs to. Asking
            // them in this order matters: a card that reveals to the table would otherwise be
            // shown to its controller only, which is the opposite of what it says.
            if (abilities.RevealsTopOfLibrary(permanent.Card))
                return true;

            if (isViewer && abilities.ShowsTopOfLibrary(permanent.Card))
                return true;
        }

        return false;
    }

    private static ObjectView ProjectObject(
        GameState state, GameObject obj, IAbilitySource? abilities = null)
    {
        var card = obj.Card;

        return new ObjectView
        {
            Id = obj.Id.Value,
            Name = card.Name,
            OracleId = card.OracleId,
            ControllerId = obj.ControllerId,
            ManaCost = string.IsNullOrEmpty(card.ManaCostRaw) ? null : card.ManaCostRaw,
            TypeLine = TypeLine(card.Supertypes, card.CardTypes, card.Subtypes),
            OracleText = string.IsNullOrWhiteSpace(card.OracleText) ? null : card.OracleText,
            ArtUri = card.ImageUriArtCrop ?? card.ImageUriSmall,
            ImageUri = card.ImageUriNormal ?? card.ImageUriLarge,
            IsPlaneswalker = card.CardTypes.HasFlag(CardType.Planeswalker),
            IsCreature = card.CardTypes.HasFlag(CardType.Creature),
            IsLand = card.CardTypes.HasFlag(CardType.Land),
            Abilities = ProjectAbilities(state, obj, abilities),
            Colors = [.. card.Colors.Select(c => c.ToString())],
            PrintedPower = card.Power,
            PrintedToughness = card.Toughness,
            IsTapped = obj.Permanent?.IsTapped,
            HasSummoningSickness = obj.Permanent?.HasSummoningSickness,
            DamageMarked = obj.Permanent?.DamageMarked,
            Counters = obj.Permanent?.Counters,

            // CR 730.2: the permanent is every one of these cards, and the fields above are all
            // the topmost. Nothing is hidden by listing them - a mutated permanent is public
            // information, and a player who cannot see what is underneath cannot work out what
            // the creature in front of them does.
            MergedUnder = [.. obj.MergedComponents.Select(c => new MergedCardView
            {
                Name = c.Name,
                OracleId = c.OracleId,
                OracleText = string.IsNullOrWhiteSpace(c.OracleText) ? null : c.OracleText,
                ImageUri = c.ImageUriNormal ?? c.ImageUriLarge,
            })],
        };
    }

    /// <summary>What a client may offer on this object (CR 602.1).</summary>
    /// <remarks>
    /// Asks the same question the engine does — printed abilities plus anything layer 6 granted
    /// this particular permanent — rather than asking the card. A board built from the card would
    /// not offer the mana ability an Aura just gave a land, and the player would have no way to
    /// reach it.
    /// </remarks>
    private static ImmutableList<AbilityView> ProjectAbilities(
        GameState state, GameObject obj, IAbilitySource? abilities) =>
        abilities is null
            ? []
            : [.. Engine.Game.ActivatedAbilitiesOf(state, abilities, obj).Select(a => new AbilityView
            {
                Id = a.Id,
                Text = a.Text,
                RequiresTap = a.RequiresTap,
                TargetCount = a.Targets.Count,
                // The definition already answers this, and to the rule: CR 605.1a requires
                // that a mana ability take no target, which "it produces mana" alone misses.
                IsManaAbility = a.IsManaAbility,
                Timing = a.Timing == ActivationTiming.AnyTime ? null : a.Timing.ToString(),
                CostChoices = [.. a.ChosenCosts.Select(c => new CostChoiceView(
                    c.Kind.ToString(), c.Count, c.What?.Description))],
            })];

    /// <summary>
    /// Rebuilds the printed type line, e.g. "Legendary Creature — Human Wizard" (CR 205.1).
    /// </summary>
    /// <remarks>
    /// Assembled from the parts rather than stored, because the parts are what the rules act on;
    /// a stored string would be a second copy to keep in step once type-changing effects land.
    /// </remarks>
    private static string TypeLine(
        IReadOnlyList<string> supertypes, CardType types, IReadOnlyList<string> subtypes)
    {
        var left = string.Join(' ', supertypes.Concat(Names(types)));
        return subtypes.Count == 0 ? left : $"{left} — {string.Join(' ', subtypes)}";
    }

    private static IEnumerable<string> Names(CardType types) =>
        Enum.GetValues<CardType>()
            .Where(t => t != CardType.None && types.HasFlag(t))
            .Select(t => t.ToString());
}
