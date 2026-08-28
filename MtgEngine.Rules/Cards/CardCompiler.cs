using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using MtgEngine.Domain.Enums;
using MtgEngine.Domain.Models;
using MtgEngine.Rules.Abilities;
using MtgEngine.Rules.Events;
using MtgEngine.Rules.Mana;
using MtgEngine.Rules.State;

namespace MtgEngine.Rules.Cards;

/// <summary>
/// Turns a card's printed rules text into abilities the engine can run.
/// </summary>
/// <remarks>
/// Cards are not written one at a time. There are about 33,000 playable Magic cards and roughly
/// 59,000 lines of rules text between them, but only ~32,000 distinct lines once card names and
/// numbers are normalised out — and the head of that distribution is steep: the thirty commonest
/// line shapes account for a fifth of all text. Magic is written to templates, so the work is a
/// compiler over those templates rather than a file of hand-written definitions.
/// <para>
/// Each line of oracle text is offered to a chain of matchers in rough frequency order; the first
/// that recognises it contributes an ability. A line nothing recognises is recorded in
/// <see cref="CompiledCard.Unhandled"/> rather than ignored, because a card that is silently
/// half-implemented is worse than one a deck check can refuse — and because those lines, counted
/// across the corpus, <em>are</em> the work queue.
/// </para>
/// <para>
/// The matchers bottom out in <see cref="EffectPhrase"/>, which reads one sentence of effect
/// text. That is the reuse the approach rests on: "destroy target creature" is the same phrase
/// whether it is a sorcery, the effect of an activated ability, or what happens when something
/// dies, so it is parsed once and wrapped differently by each caller.
/// </para>
/// </remarks>
public static partial class CardCompiler
{
    /// <summary>Compiles a card, reporting anything it could not read.</summary>
    public static CompiledCard Compile(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        // A card with faces keeps its rules on them, and the two halves are two different sets of
        // abilities that happen to share a piece of cardboard. The card itself is its **front**
        // face while it is anywhere but the battlefield showing its back (CR 712.8a, 712.8d), so
        // that is what compiles here - and the other faces are compiled too, only to be counted:
        // a card is understood when every face of it is, and a card whose back face says
        // something the engine cannot read is not one it can be trusted to play.
        //
        // The back face's own abilities are not merged in. They are reached by compiling that
        // face's definition, which is what a transforming permanent's card becomes - so the pool
        // finds them under the face's own oracle id, at the moment the permanent is showing it.
        // A Class is not one list of abilities but several, each switched on by a level bar
        // (CR 716.2a). The lines under a bar belong to that bar's static ability and must not
        // function before the Class reaches it, so they cannot go through the ordinary line
        // loop alongside everything else - the loop reads a line and hands the ability to a
        // builder, with nothing left to say "only from level 3".
        //
        // They are compiled as their own little cards instead and the abilities gated on the
        // way back, which reuses every matcher in this file rather than teaching each of them
        // what a level is.
        if (IsClassWithLevels(card))
            return CompileClass(card);

        // A Case is the same shape as a Class with a simpler switch: one designation rather than
        // a ladder of levels, set by a condition rather than bought. The section under
        // "Solved -" must not function before it is (CR 719.3c), which is the same requirement
        // and gets the same answer.
        if (IsCase(card))
            return CompileCase(card);

        if (card.Faces.Count > 1 && !card.OracleId.Contains('#', StringComparison.Ordinal))
        {
            var front = Compile(FrontOnly(card));

            var elsewhere = ImmutableList.CreateBuilder<string>();
            elsewhere.AddRange(front.Unhandled);

            SpellDefinition? adventure = null;
            string? adventureCost = null;

            // CR 722.2a calls the inset frame a "prepare spell", but nothing in the parsed card
            // says which frame is inset: the front face's own words are the only structural sign
            // of the layout, and the compiler knows nothing of layouts. That is the same test the
            // Room and aftermath branches make, for the same reason.
            var prepares = card.Faces[0].OracleText
                .Split('\n')
                .Any(l => PreparedLine().IsMatch(l.Trim()) || BecomesPreparedLine().IsMatch(l.Trim()));

            SpellDefinition? preparedSpell = null;
            string? preparedCost = null;
            var preparedInstant = false;

            for (var face = 1; face < card.Faces.Count; face++)
            {
                var compiled = Compile(CardFaces.Definition(card, face));
                elsewhere.AddRange(compiled.Unhandled);

                // CR 715.2: the inset frame "defines alternative characteristics that the object
                // may have while it's a spell". They are kept on the front face's compiled card
                // because that is the card - CR 715.4 says an adventurer card has only its normal
                // characteristics in every zone except the stack - and the alternative ones are
                // reached only by choosing them as it is cast.
                if (adventure is null
                    && card.Faces[face].Subtypes.Contains("Adventure", StringComparer.Ordinal))
                {
                    adventure = compiled.Spell;
                    adventureCost = card.Faces[face].ManaCostRaw;
                }
            }

            // A face with a printed cost is a face somebody can pay for. That is what separates
            // the halves of a split card from the back of a transforming one, which has no cost
            // and is reached only by turning the permanent over - and it needs no knowledge of
            // layouts, which the compiler does not have.
            //
            // An Adventure is excluded because it is already handled above and is not a half: it
            // does not have its own card, and casting it exiles the card rather than resolving
            // one of two spells.
            if (prepares && card.Faces.Count > 1)
            {
                preparedSpell = Compile(CardFaces.Definition(card, 1)).Spell;
                preparedCost = card.Faces[1].ManaCostRaw;
                preparedInstant = card.Faces[1].CardTypes.HasFlag(CardType.Instant);
            }

            var halves = ImmutableList.CreateBuilder<CardHalf>();

            // CR 722.3: "Preparation cards can't be cast using the alternative characteristics
            // found within their inset frames." The inset frame has a printed cost, so it looks
            // exactly like the half of a split card and is not one - letting it through would put
            // a free extra spell in every hand.
            if (adventure is null && !prepares)
            {
                for (var face = 0; face < card.Faces.Count; face++)
                {
                    var printed = card.Faces[face];
                    if (printed.ManaCostRaw.Length == 0)
                        continue;

                    var behind = Compile(CardFaces.Definition(card, face));

                    halves.Add(new CardHalf(
                        face,
                        printed.Name,
                        printed.ManaCostRaw,
                        printed.CardTypes,
                        behind.Spell)
                    {
                        // Per line, because the keyword is one line of several and the pattern
                        // is anchored: matched against the whole face it found nothing, and the
                        // half compiled as an ordinary one that could be cast from hand.
                        HasAftermath = printed.OracleText
                            .Split('\n')
                            .Any(l => AftermathLine().IsMatch(l.Trim())),
                    });
                }
            }

            // CR 709.5: a Room's two halves are both on the battlefield at once, and each one's
            // text is switched on by its own door. That is the Class section gate again with a
            // different key, so the abilities of every half are merged onto the permanent and
            // wrapped in a test for that door being open - a locked half "doesn't have the name,
            // mana cost, or rules text" of that half, and an ability that functioned anyway would
            // give a player both halves of the card for the price of one.
            var doors = halves.Count > 1
                && card.Faces.All(f => f.Subtypes.Contains("Room", StringComparer.Ordinal));

            if (doors)
            {
                var roomTriggers = ImmutableList.CreateBuilder<TriggeredAbilityDefinition>();
                var roomStatics = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();
                var roomActivated = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();

                foreach (var door in halves)
                {
                    var behind = Compile(CardFaces.Definition(card, door.Index));
                    var key = door.Index;

                    roomTriggers.AddRange(behind.Triggers.Select(t => t with
                    {
                        Id = "door" + key.ToString(CultureInfo.InvariantCulture) + t.Id,

                        // The unlock trigger is the exception, and it has to be: it is printed
                        // behind the door it fires on, and the state a trigger reads is the one
                        // before the event, where that door is still shut.
                        Triggers = t.OpensDoor
                            ? (e, _, source) =>
                                e is HalfUnlocked opening
                                && opening.Id == source.Id
                                && opening.Half == key
                            : (e, state, source) =>
                                Unlocked(state, source.Id, key) && t.Triggers(e, state, source),
                    }));

                    roomStatics.AddRange(behind.Statics.Select(c => c with
                    {
                        Id = "door" + key.ToString(CultureInfo.InvariantCulture) + c.Id,
                        Applies = (state, source, built) =>
                            source is not null
                            && Unlocked(state, source.Id, key)
                            && c.Applies(state, source, built),
                    }));

                    roomActivated.AddRange(behind.Activated.Select(a => a with
                    {
                        Id = "door" + key.ToString(CultureInfo.InvariantCulture) + a.Id,
                        ActivateOnlyIf = (state, abilities, self) =>
                            Unlocked(state, self.Id, key)
                            && a.ActivateOnlyIf?.Invoke(state, abilities, self) != false,
                    }));
                }

                return front with
                {
                    Unhandled = elsewhere.ToImmutable(),
                    Halves = halves.ToImmutable(),
                    Triggers = roomTriggers.ToImmutable(),
                    Statics = roomStatics.ToImmutable(),
                    Activated = roomActivated.ToImmutable(),
                };
            }

            return front with
            {
                Unhandled = elsewhere.ToImmutable(),
                Adventure = adventure,
                AdventureCostRaw = adventureCost,
                PreparedSpell = preparedSpell,
                PreparedCostRaw = preparedCost,
                PreparedIsInstant = preparedInstant,
                Halves = halves.Count > 1 ? halves.ToImmutable() : [],
                HasFuse = card.Faces.Any(f => f.OracleText
                    .Split('\n')
                    .Any(l => FuseLine().IsMatch(l.Trim()))),
            };
        }

        var spellEffects = ImmutableList.CreateBuilder<IEffect>();
        var spellTargets = ImmutableList.CreateBuilder<TargetSpec>();
        var activated = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();
        var triggers = ImmutableList.CreateBuilder<TriggeredAbilityDefinition>();
        var replacements = ImmutableList.CreateBuilder<ReplacementEffectDefinition>();
        var statics = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();
        var unhandled = ImmutableList.CreateBuilder<string>();

        var grantedKeywords = KeywordAbility.None;
        TapToPay? tapToPay = null;
        AlternativeCastZone? castFrom = null;
        Func<GameState, Guid, bool>? castOnly = null;

        // CR 305.6: a land with a basic land type has the matching mana ability whether or not
        // it is printed. It is printed only as reminder text, in brackets — which this compiler
        // strips as noise — so without this the commonest cards in Magic compile to nothing at
        // all and no deck can produce mana.
        foreach (var (subtype, colour) in BasicLandTypes)
        {
            if (!card.Subtypes.Contains(subtype, StringComparer.OrdinalIgnoreCase))
                continue;

            activated.Add(new ActivatedAbilityDefinition
            {
                Id = "mana" + Suffix(activated.Count),
                Text = "{T}: Add " + colour.Symbol + ".",
                RequiresTap = true,
                Produces = [colour.Production],
            });
        }

        var isSpell = card.CardTypes.HasFlag(CardType.Instant)
            || card.CardTypes.HasFlag(CardType.Sorcery);

        var modes = ImmutableList.CreateBuilder<SpellMode>();
        var modesToChoose = 0;
        var modesMax = 0;

        // Which trigger, if any, the bullets that follow belong to. "When ~ enters, choose one —"
        // is a trigger whose effect is a menu, and the menu arrives on the lines after it - so
        // something has to remember, between lines, that the modes being read are the ability's
        // and not the spell's.
        var modalTriggerAt = -1;
        ManaCostSpec? entwine = null;
        ManaCostSpec? splice = null;
        ManaCostSpec? squad = null;
        var reducers = ImmutableList.CreateBuilder<CostReducer>();
        var showsTop = false;
        var noHandLimit = false;
        var chooses = ChoiceOnEntry.None;
        var devour = 0;
        ManaCostSpec? flashSurcharge = null;
        ManaCostSpec? prototypeCost = null;
        ManaCostSpec? miracleCost = null;
        int? prototypePower = null;
        int? prototypeToughness = null;
        var extraLandDrops = 0;
        var mayDeclineUntap = false;
        var skipsDraw = false;
        var revealsTop = false;
        var assist = false;
        ManaCostSpec? escalate = null;
        ManaCostSpec? kicker = null;
        ChosenCost? bargain = null;
        ManaCostSpec? buyback = null;
        ManaCostSpec? dash = null;
        ManaCostSpec? evoke = null;
        ConditionalCost? conditionalCost = null;
        ManaCostSpec? blitz = null;
        ManaCostSpec? multikicker = null;
        string? partnerRule = null;
        ManaCostSpec? plot = null;
        ManaCostSpec? replicate = null;
        ManaCostSpec? offspring = null;
        ManaCostSpec? suspend = null;
        ManaCostSpec? overload = null;
        ChosenCost? conspire = null;
        var splitSecond = false;
        ManaCostSpec? bestow = null;
        TargetSpec? bestowTarget = null;
        string? backupLine = null;
        var backup = 0;
        var overloadEffects = ImmutableList<IEffect>.Empty;
        var suspendCount = 0;
        string? attacksOnlyIf = null;
        ManaCostSpec? foretell = null;
        string? madness = null;
        var additionalCosts = ImmutableList.CreateBuilder<ChosenCost>();
        Func<GameState, Guid, IReadOnlyList<Target>, int>? costReduction = null;
        ManaCostSpec? morphCost = null;
        var hasDelve = false;
        var morphAddsCounter = false;
        ManaCostSpec? faceDownWard = null;

        // Oblivion Ring's shape, printed as two lines rather than one. They are paired before
        // the loop for the reason the one-line form is built as a pair: an exile that compiles
        // without its return is removal that never gives the card back, which is a strictly
        // better card than the one printed. Reading them independently would risk exactly that,
        // so either both are read or neither is.
        var paired = PairExileAndReturn(card, triggers);

        foreach (var line in Lines(card))
        {
            if (paired.Contains(line))
                continue;

            // CR 702.47a: only "onto Arcane" is read. Splice onto a quality the engine cannot
            // test would compile to a permission it could not check, and every printing but one
            // says Arcane.
            if (AssistLine().IsMatch(line))
            {
                assist = true;
                continue;
            }

            if (SquadLine().Match(line) is { Success: true } squadded)
            {
                squad = ManaCostSpec.Parse(squadded.Groups["cost"].Value);
                continue;
            }

            if (SpliceLine().Match(line) is { Success: true } spliced)
            {
                splice = ManaCostSpec.Parse(spliced.Groups["cost"].Value);
                continue;
            }

            if (EntwineLine().Match(line) is { Success: true } entwined)
            {
                entwine = ManaCostSpec.Parse(entwined.Groups["cost"].Value);
                continue;
            }

            if (EscalateLine().Match(line) is { Success: true } escalated)
            {
                escalate = ManaCostSpec.Parse(escalated.Groups["cost"].Value);
                continue;
            }

            if (TryModalTriggerBullet(line, triggers, modalTriggerAt))
                continue;

            if (TryModal(line, modes, ref modesToChoose, ref modesMax))
                continue;

            if (IsKeywordLine(line, card))
                continue;

            if (SentenceKeywords.TryGetValue(line, out var granted))
            {
                grantedKeywords |= granted;
                continue;
            }

            if (DelveLine().IsMatch(line))
            {
                hasDelve = true;
                continue;
            }

            if (TryMorph(line, card, triggers, ref morphCost, ref morphAddsCounter, ref faceDownWard))
                continue;

            if (TryUndaunted(line, ref costReduction))
                continue;

            if (TryAffinity(line, ref costReduction))
                continue;

            if (TryCountedCostReduction(line, ref costReduction))
                continue;

            if (TryConditionalCostReduction(line, card, ref costReduction))
                continue;

            if (TryTargetCostReduction(line, ref costReduction))
                continue;

            if (TryAdditionalCost(line, additionalCosts, unhandled))
                continue;

            if (TrySoulshift(line, triggers, card, unhandled))
                continue;

            if (TryToxic(line, triggers, card))
                continue;

            if (TryWard(line, triggers, card))
                continue;

            if (TryKeywordList(line, card, triggers))
                continue;

            if (TryProwess(line, triggers))
                continue;

            if (TryExalted(line, triggers))
                continue;

            if (TryRenown(line, triggers))
                continue;

            if (TryUndyingOrPersist(line, triggers))
                continue;

            if (TryEncore(line, activated))
                continue;

            if (TryExploit(line, triggers))
                continue;

            if (TryExert(line, triggers))
                continue;

            if (TryDecayed(line, triggers, ref grantedKeywords))
                continue;

            if (TryReconfigure(line, activated, statics))
                continue;

            if (TryJobSelect(line, triggers))
                continue;

            if (TryProvoke(line, triggers))
                continue;

            if (TryFlanking(line, triggers, ref grantedKeywords))
                continue;

            if (TryDevoid(line, statics))
                continue;

            if (TryExileUntilLeaves(line, card, triggers))
                continue;

            if (TryFabricate(line, card, triggers))
                continue;

            if (MadnessLine().Match(line) is { Success: true } mad)
            {
                madness = mad.Groups["cost"].Value;
                continue;
            }

            if (TryRebound(line, card, triggers))
                continue;

            if (TryCascade(line, card, triggers))
                continue;

            if (TryStorm(line, card, triggers))
                continue;

            if (TryJumpStart(line, card, ref castFrom))
                continue;

            if (TryRetrace(line, card, ref castFrom))
                continue;

            if (TryDredge(line, replacements))
                continue;

            if (TryUmbraArmor(line, replacements))
                continue;

            if (TryStealHost(line, statics))
                continue;

            if (TryMentor(line, card, triggers))
                continue;

            if (TryBattleCry(line, card, triggers))
                continue;

            if (TryIngest(line, card, triggers))
                continue;

            if (TryRiot(line, card, triggers))
                continue;

            if (TryUnleash(line, card, triggers, statics))
                continue;

            if (TryExtort(line, card, triggers))
                continue;

            if (TryAfterlife(line, card, triggers))
                continue;

            if (ShowTopOfLibraryLine().IsMatch(line))
            {
                showsTop = true;
                continue;
            }

            if (NoMaximumHandSizeLine().IsMatch(line))
            {
                noHandLimit = true;
                continue;
            }

            if (SkipDrawStepLine().IsMatch(line))
            {
                skipsDraw = true;
                continue;
            }

            if (MayDeclineUntapLine().IsMatch(line))
            {
                mayDeclineUntap = true;
                continue;
            }

            if (ExtraLandDropLine().Match(line) is { Success: true } extraLands)
            {
                extraLandDrops += extraLands.Groups["n"].Success
                    ? NumberWord(extraLands.Groups["n"].Value)
                    : 1;

                continue;
            }

            if (ChooseAsEntersLine().Match(line) is { Success: true } choosing)
            {
                chooses = choosing.Groups["what"].Value.Equals(
                    "color", StringComparison.OrdinalIgnoreCase)
                    ? ChoiceOnEntry.Color
                    : ChoiceOnEntry.CreatureType;

                continue;
            }

            // "Play with the top card of your library revealed" - the same card, shown to
            // everybody rather than to one player. Kept apart from the private permission
            // because the difference is the whole of what the line says.
            if (RevealTopOfLibraryLine().IsMatch(line))
            {
                revealsTop = true;
                continue;
            }

            if (TryCostReducer(line, reducers))
                continue;

            if (TryVanishing(line, card, triggers, replacements))
                continue;

            if (TryRecover(line, card, triggers))
                continue;

            if (DevourLine().Match(line) is { Success: true } devouring)
            {
                devour = int.Parse(
                    devouring.Groups["n"].Value, CultureInfo.InvariantCulture);

                continue;
            }

            if (CipherLine().IsMatch(line))
            {
                spellEffects.Add(new CipherSelf());
                continue;
            }

            // "This effect doesn't remove ~." The rider on the protection Auras, saying that the
            // protection they grant does not then send them to the graveyard (CR 704.5n).
            //
            // Read as doing nothing, and correct rather than convenient: the only Aura
            // state-based action this engine performs is for an Aura attached to nothing at all
            // (CR 704.5m). Nothing here removes an Aura for illegality, so the sentence describes
            // a removal that cannot happen. If that action is ever implemented, this has to
            // become a real exemption on the Aura instead of a line that is simply understood.
            if (EffectDoesNotRemoveLine().IsMatch(line))
                continue;

            if (TryAssignAsThoughUnblocked(line, statics))
                continue;

            if (TryFirebending(line, card, triggers))
                continue;

            if (TryAfflict(line, card, triggers))
                continue;

            if (TryDethrone(line, card, triggers))
                continue;

            if (TryRampage(line, card, triggers))
                continue;

            if (TryEvolve(line, card, triggers))
                continue;

            if (TryMyriad(line, card, triggers))
                continue;

            if (TryStartYourEngines(line, card, triggers))
                continue;

            if (TryForMirrodin(line, card, triggers))
                continue;

            if (TryLivingWeapon(line, card, triggers))
                continue;

            if (TryFading(line, card, triggers, replacements))
                continue;

            if (TryEcho(line, card, triggers, replacements))
                continue;

            if (TryBloodthirst(line, replacements))
                continue;

            if (TryEntersWithCountersPerGroup(line, card, replacements))
                continue;

            if (TryEntersWithCounters(line, card, replacements))
                continue;

            if (TrySagaChapter(line, triggers, unhandled))
                continue;

            if (TryClassLevelTrigger(line, triggers, unhandled))
                continue;

            if (TryUnlockTrigger(line, triggers, unhandled))
                continue;

            // CR 702.124a: a deck-construction rule that functions before the game begins, so
            // there is nothing for the engine to do but write down which one it was. "Partner
            // with [name]" is excluded on purpose - see CompiledCard.PartnerRule.
            if (PartnerLine().IsMatch(line))
            {
                partnerRule = line.TrimEnd('.').Trim();
                continue;
            }

            if (TryBushido(line, triggers))
                continue;

            if (TryTraining(line, card, triggers))
                continue;

            if (TryAnnihilator(line, card, triggers))
                continue;

            if (TryOutlast(line, card, activated, unhandled))
                continue;

            if (TryMobilize(line, card, triggers))
                continue;

            if (TryMelee(line, card, triggers))
                continue;

            if (TryIncrement(line, card, triggers))
                continue;

            if (TryAscend(line, card, spellEffects, triggers))
                continue;

            if (TryReinforce(line, card, activated, unhandled))
                continue;

            if (TryCycling(line, activated))
                continue;

            if (TryTypecycling(line, activated, unhandled))
                continue;

            if (TryEnchant(line, spellTargets))
                continue;

            if (TrySmallCreaturesCantBlock(line, card, statics))
                continue;

            if (TryExtraBlocks(line, card, statics))
                continue;

            if (TryMustBeBlocked(line, card, statics))
                continue;

            if (TryMinimumBlockers(line, card, statics))
                continue;

            if (TryCantBeBlockedExceptBy(line, card, statics))
                continue;

            if (TryCantBeBlockedBy(line, card, statics))
                continue;

            if (TryDoesNotUntap(line, card, statics))
                continue;

            if (TryAttachedBuff(line, statics))
                continue;

            if (TryGrantedAbility(line, card, statics))
                continue;

            if (TryDefinedPowerToughness(line, card, statics))
                continue;

            if (TryCountingStatic(line, card, statics))
                continue;

            if (TryConditionalStatic(line, card, statics))
                continue;

            if (TryMassStatic(line, card, statics))
                continue;

            if (TryEquip(line, activated))
                continue;

            if (TryCrew(line, activated))
                continue;

            if (TryLivingMetal(line, statics))
                continue;

            if (TryStation(line, activated))
                continue;

            // CR 702.127a: three static abilities about where this half may be cast from and
            // where it goes afterwards. All three are enforced where casting happens; the line
            // is read here so the card is not reported as unread for saying so.
            if (AftermathLine().IsMatch(line))
                continue;

            // CR 702.102a: a static ability that applies while the card is in hand, and the
            // whole of what it does is offer a second way to cast the card. Enforced where
            // casting happens, and read here so the line does not count against the card.
            if (FuseLine().IsMatch(line))
                continue;

            if (TryStationThreshold(line, card, statics))
                continue;

            if (TrySaddle(line, activated))
                continue;

            if (TryTapToPay(line, ref tapToPay))
                continue;

            if (AttacksOnlyIfLine().Match(line) is { Success: true } restricted)
            {
                attacksOnlyIf = restricted.Groups["land"].Value;
                continue;
            }

            if (TryCumulativeUpkeep(line, card, triggers))
                continue;

            if (TryEternalize(line, card, activated))
                continue;

            if (TryTransmute(line, card, activated))
                continue;

            if (TryNinjutsu(line, activated))
                continue;

            if (TryScavenge(line, card, activated))
                continue;

            if (TryUnearth(line, activated))
                continue;

            if (TryMayhem(line, ref castFrom))
                continue;

            if (TryEscapesWithCounters(line, replacements))
                continue;

            if (TryEscape(line, ref castFrom))
                continue;

            if (TryDisturb(line, ref castFrom))
                continue;

            if (TryFlashback(line, ref castFrom))
                continue;

            if (TryKicker(line, ref kicker))
                continue;

            if (SplitSecondLine().IsMatch(line))
            {
                splitSecond = true;
                continue;
            }

            if (BestowLine().Match(line) is { Success: true } lent)
            {
                bestowTarget = EffectPhrase.Specs.Parse("target creature");

                if (bestowTarget is null)
                {
                    unhandled.Add(line);
                    continue;
                }

                bestow = ManaCostSpec.Parse(lent.Groups["cost"].Value);
                statics.Add(AuraWhileAttached());
                continue;
            }

            if (BackupLine().Match(line) is { Success: true } backing)
            {
                // Held until the whole card is read: whether backup can be honoured depends on
                // what else the card has, and that is not known yet.
                backupLine = line;
                backup = int.Parse(backing.Groups["n"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (CasualtyLine().Match(line) is { Success: true } casualty)
            {
                conspire = CasualtyCostFor(
                    int.Parse(casualty.Groups["n"].Value, CultureInfo.InvariantCulture));

                if (conspire is null)
                {
                    unhandled.Add(line);
                }

                continue;
            }

            if (ConspireLine().IsMatch(line))
            {
                conspire = ConspireCostFor(card);
                if (conspire is null)
                {
                    unhandled.Add(line);
                }

                continue;
            }

            if (OverloadLine().Match(line) is { Success: true } loud)
            {
                // Read now rather than at the end, because a rewrite that cannot be read must
                // leave the line unhandled: accepting the cost with no effects behind it would
                // resolve the card's ordinary, targeted text for the overload price - the wrong
                // spell rather than an unread one.
                overloadEffects = OverloadedEffects(card);

                if (overloadEffects.IsEmpty)
                {
                    unhandled.Add(line);
                    continue;
                }

                overload = ManaCostSpec.Parse(loud.Groups["cost"].Value);
                continue;
            }

            if (OffspringLine().Match(line) is { Success: true } small)
            {
                offspring = ManaCostSpec.Parse(small.Groups["cost"].Value);
                continue;
            }

            if (ReplicateLine().Match(line) is { Success: true } copied)
            {
                replicate = ManaCostSpec.Parse(copied.Groups["cost"].Value);
                continue;
            }

            if (PlotLine().Match(line) is { Success: true } laid)
            {
                plot = ManaCostSpec.Parse(laid.Groups["cost"].Value);
                continue;
            }

            if (MiracleLine().Match(line) is { Success: true } drawn)
            {
                miracleCost = ManaCostSpec.Parse(drawn.Groups["cost"].Value);
                continue;
            }

            if (PrototypeLine().Match(line) is { Success: true } smaller)
            {
                prototypeCost = ManaCostSpec.Parse(smaller.Groups["cost"].Value);
                prototypePower = int.Parse(
                    smaller.Groups["p"].Value, CultureInfo.InvariantCulture);
                prototypeToughness = int.Parse(
                    smaller.Groups["t"].Value, CultureInfo.InvariantCulture);

                continue;
            }

            if (FlashSurchargeLine().Match(line) is { Success: true } hasted)
            {
                flashSurcharge = ManaCostSpec.Parse(hasted.Groups["cost"].Value);
                continue;
            }

            if (SuspendLine().Match(line) is { Success: true } waited)
            {
                suspend = ManaCostSpec.Parse(waited.Groups["cost"].Value);
                suspendCount = int.Parse(
                    waited.Groups["n"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (EvokeLine().Match(line) is { Success: true } evoked)
            {
                evoke = ManaCostSpec.Parse(evoked.Groups["cost"].Value);
                continue;
            }

            if (DashLine().Match(line) is { Success: true } dashed)
            {
                dash = ManaCostSpec.Parse(dashed.Groups["cost"].Value);
                continue;
            }

            // CR 702.152a: three abilities printed as one word. Two of them are handled by the
            // cost and by the engine at resolution; the third is a real triggered ability that
            // belongs to the card, so it is added here rather than granted - and it asks whether
            // this particular permanent's blitz cost was paid, because a card with blitz cast for
            // its printed cost has the word on it and none of the consequences.
            if (MultikickerLine().Match(line) is { Success: true } repeated)
            {
                multikicker = ManaCostSpec.Parse(repeated.Groups["cost"].Value);
                continue;
            }

            if (BlitzLine().Match(line) is { Success: true } rushed)
            {
                blitz = ManaCostSpec.Parse(rushed.Groups["cost"].Value);

                triggers.Add(new TriggeredAbilityDefinition
                {
                    Id = "blitz-draw",
                    Text = "When this permanent is put into a graveyard from the battlefield, "
                        + "draw a card.",
                    FunctionsFrom = Zone.Battlefield,
                    Triggers = static (e, _, source) =>
                        source.WasBlitzed
                        && e is Events.ObjectMoved
                        {
                            From: Zone.Battlefield,
                            To: Zone.Graveyard,
                        } gone
                        && gone.OldId == source.Id,
                    Effects = [new DrawCards(EffectPhrase.Number("1"))],
                });

                continue;
            }

            // CR 702.100a and CR 702.137a: surge and spectacle are the same offer asked about
            // two different facts, so they are read as one shape. A card printed with both does
            // not exist; the second to be read would win, and reading them separately would only
            // make that arbitrary choice harder to see.
            // "Freerunning [cost]" - the same alternative-cost shape, asking a question the
            // engine now records as combat damage lands: the rule is about what the creature was
            // at the time it connected, which cannot be reconstructed afterwards.
            if (FreerunningLine().Match(line) is { Success: true } freerunning)
            {
                conditionalCost = new ConditionalCost(
                    "freerunning",
                    "CR 702.173a",
                    ManaCostSpec.Parse(freerunning.Groups["cost"].Value),
                    static (state, playerId) =>
                        state.GetPlayer(playerId).AssassinOrCommanderConnectedThisTurn);

                continue;
            }

            if (SurgeLine().Match(line) is { Success: true } surged)
            {
                conditionalCost = new ConditionalCost(
                    "surge",
                    "CR 702.100a",
                    ManaCostSpec.Parse(surged.Groups["cost"].Value),
                    // "You or a teammate": with no teams in the engine, that is you. The count
                    // does not yet include the spell being cast - it reaches the log only once
                    // its cost is paid - so "another spell this turn" is simply one or more.
                    static (state, playerId) => state.GetPlayer(playerId).SpellsCastThisTurn > 0);

                continue;
            }

            // CR 702.185a: warp is an alternative cost with no condition at all - it is always
            // on offer from hand. What it buys is on the other side of resolution, so the
            // keyword only has to say what the cost is; the engine remembers it was paid.
            if (WarpLine().Match(line) is { Success: true } bent)
            {
                conditionalCost = new ConditionalCost(
                    "warp",
                    "CR 702.185a",
                    ManaCostSpec.Parse(bent.Groups["cost"].Value),
                    static (_, _) => true);

                continue;
            }

            if (SpectacleLine().Match(line) is { Success: true } shown)
            {
                conditionalCost = new ConditionalCost(
                    "spectacle",
                    "CR 702.137a",
                    ManaCostSpec.Parse(shown.Groups["cost"].Value),
                    // Life *lost*, which is not the same question as damage dealt: an opponent
                    // who paid life to a cost has lost it and nothing dealt them anything.
                    static (state, playerId) => state.TurnOrder.Any(
                        other => other != playerId && state.GetPlayer(other).LostLifeThisTurn));

                continue;
            }

            if (BuybackLine().Match(line) is { Success: true } bought)
            {
                buyback = ManaCostSpec.Parse(bought.Groups["cost"].Value);
                continue;
            }

            if (ForetellLine().Match(line) is { Success: true } told)
            {
                foretell = ManaCostSpec.Parse(told.Groups["cost"].Value);
                continue;
            }

            // "If this spell was kicked, ..." — a clause that happens only when it was
            // (CR 702.33e). Only meaningful on a card that has a kicker to pay.
            if (kicker is not null && TryIfKicked(line, spellEffects, spellTargets))
                continue;

            // CR 702.166a. The cost is the same on every card that has it, so the keyword is the
            // whole of the line - and the clause that reads it back is only meaningful on a card
            // that can be bargained at all, exactly as "if this was kicked" is.
            if (BargainLine().IsMatch(line))
            {
                bargain = new ChosenCost(
                    ChosenCostKind.SacrificePermanents,
                    Count: 1,
                    What: EffectPhrase.Specs.Parse(
                        "target artifact, enchantment, or token you control"));

                continue;
            }


            if (CastOnlyLine().Match(line) is { Success: true } onlyWhen)
            {
                // Unread words leave the card alone rather than dropping the restriction: a
                // spell that may be cast at any time is a different and better card than the one
                // printed, which is the direction that must never happen by accident.
                if (CastRestriction(onlyWhen.Groups["when"].Value.Trim()) is not { } gate)
                {
                    unhandled.Add(line);
                    continue;
                }

                castOnly = castOnly is { } already
                    ? (state, who) => already(state, who) && gate(state, who)
                    : gate;

                continue;
            }

            if (TryKickedCounters(line, replacements))
                continue;

            if (TryMultikickedCounters(line, replacements))
                continue;

            if (TrySunburst(line, card, replacements))
                continue;

            if (TryPhantomDamage(line, replacements))
                continue;

            if (TryShockland(line, replacements))
                continue;

            if (TryGraveyardReplacement(line, replacements))
                continue;

            if (TryEntersPrepared(line, replacements))
                continue;

            if (TryEntersTapped(line, replacements))
                continue;

            if (TryLoyaltyAbility(line, activated))
                continue;

            if (TryManaAbility(line, activated))
                continue;

            if (TryActivatedAbility(line, card, activated, unhandled))
                continue;

            if (TryTrigger(line, card, triggers, unhandled, ref modalTriggerAt))
                continue;

            // A bare effect sentence on an instant or sorcery is what the spell does.
            if (isSpell && EffectPhrase.TryParse(line, out var parsed))
            {
                // Shifted by what is already there. The phrase parser numbers a line's targets
                // from zero because it does not know what it is being folded into, so a card
                // printing the same sentence twice - "target creature gets +3/+3 until end of
                // turn" on three lines - added three targets and pointed all three pumps at the
                // first. The player chose three creatures and one of them got everything.
                var offset = spellTargets.Count;

                foreach (var effect in parsed.Effects)
                    spellEffects.Add(EffectTargets.Shift(effect, offset));

                spellTargets.AddRange(parsed.Targets);
                continue;
            }

            unhandled.Add(line);
        }

        // Read last, because whether backup can be honoured depends on what else the card turned
        // out to have (CR 702.164a).
        if (backupLine is not null)
            TryBackup(backupLine, card, backup, triggers, unhandled);

        var built = new SpellDefinition
        {
            Targets = spellTargets.ToImmutable(),
            Effects = spellEffects.ToImmutable(),
            TapToPayCost = tapToPay,
            CastFrom = castFrom,
            CastOnlyWhen = castOnly,
            Modes = modes.ToImmutable(),
            ModesToChoose = modesToChoose,

            // "Or more" is stored as -1 while the bullets are still being counted, since
            // how many there are is only known once the last one has been read.
            ModesMax = modesMax < 0 ? modes.Count : modesMax,
            EntwineCost = entwine,
            SpliceCost = splice,
            SquadCost = squad,
            HasAssist = assist,
            EscalateCost = escalate,
            KickerCost = kicker,
            BargainCost = bargain,
            BuybackCost = buyback,
            DashCost = dash,
            BlitzCost = blitz,
            MultikickerCost = multikicker,
            EvokeCost = evoke,
            ConditionalAlternativeCost = conditionalCost,
            HasSplitSecond = splitSecond,
            BestowCost = bestow,
            BestowTarget = bestowTarget,
            CopyingCost = conspire,
            OverloadCost = overload,
            OverloadEffects = overloadEffects,
            PlotCost = plot,
            ReplicateCost = replicate,
            OffspringCost = offspring,
            SuspendCost = suspend,
            FlashSurcharge = flashSurcharge,
            MiracleCost = miracleCost,
            PrototypeCost = prototypeCost,
            PrototypePower = prototypePower,
            PrototypeToughness = prototypeToughness,
            SuspendCount = suspendCount,
            ForetellCost = foretell,
            MadnessCost = madness,
            ChosenCosts = additionalCosts.ToImmutable(),
            CostReduction = costReduction,
            MorphCost = morphCost,
            HasDelve = hasDelve,
            MorphAddsCounter = morphAddsCounter,
            FaceDownWard = faceDownWard,
        };

        // CR 702.166c: the clause is linked to the bargain ability printed on the same card, so
        // a card that reads one back without having one is not a card this engine can play. The
        // sentence grammar cannot see the rest of the card, so the check is here, where it can.
        if (bargain is null
            && EffectTree.Flatten(spellEffects).Any(e => e is IfBargained))
        {
            unhandled.Add("if this spell was bargained");
        }

        // A trigger that says "choose one —" and has no bullets under it offers a menu with
        // nothing on it. The header alone reads as a complete card, so the line goes back to
        // unread and the ability is dropped rather than left offering a choice it cannot make.
        if (modalTriggerAt >= 0 && triggers[modalTriggerAt].Modes.IsEmpty)
        {
            unhandled.Add(triggers[modalTriggerAt].Text);
            triggers.RemoveAt(modalTriggerAt);
        }

        // CR 714.3a: "each Saga without read ahead has the intrinsic ability 'this Saga enters
        // with a lore counter on it'". Nothing prints that line, which is why it is added here
        // rather than read: it is a property of the card type, and a Saga without it would sit
        // on the battlefield at zero counters and never begin.
        //
        // Conditioned on the card actually having a chapter, so that a Saga whose chapters this
        // compiler could not read does not quietly gain a counter it has nothing to spend on.
        // Read ahead (CR 702.155) replaces this ability with a choice, and is not read yet - a
        // Saga carrying it keeps that line unread, so the card is incomplete and unplayable
        // rather than silently starting at chapter one.
        if (card.Subtypes.Contains("Saga", StringComparer.OrdinalIgnoreCase)
            && triggers.Any(t => t.Chapter is not null))
        {
            replacements.Add(new ReplacementEffectDefinition
            {
                Id = "saga-enters-with-lore",
                FunctionsFrom = null,
                Applies = (e, _, source) => Arriving(e, source) is not null,
                Replace = (e, _, source) =>
                    [e, new CountersChanged(Arriving(e, source)!.Value, CounterKinds.Lore, 1)],
            });
        }

        return new CompiledCard
        {
            Name = card.Name,
            PartnerRule = partnerRule,
            // An Aura is the case that proves targets and effects are separate questions: it
            // targets what it will enchant and has no effects at all, so keying the spell on
            // "are there effects" left it with no targets and the engine refused the cast.
            // Convoke on a permanent spell is still a spell definition even with no effects:
            // the definition is where the cost reduction lives, and a creature with convoke and
            // no other text would otherwise compile to nothing that knows how to be cast cheaply.
            // Built first and judged afterwards, by asking the record whether it carries
            // anything. This used to be a hand-written list of every field, and a field left off
            // that list was not a compile error - it was a cost read, assigned, and dropped, on a
            // card that reported itself fully compiled.
            Spell = SpellDefinition.CarriesNothing(built) ? null : built,
            Activated = activated.ToImmutable(),
            Triggers = triggers.ToImmutable(),
            Replacements = replacements.ToImmutable(),
            CostReducers = reducers.ToImmutable(),
            ShowsTopOfLibrary = showsTop,
            RemovesHandLimit = noHandLimit,
            ChoosesOnEntry = chooses,
            DevourCount = devour,
            ExtraLandDrops = extraLandDrops,
            MayDeclineUntap = mayDeclineUntap,
            SkipsDrawStep = skipsDraw,
            RevealsTopOfLibrary = revealsTop,
            Statics = statics.ToImmutable(),
            GrantedKeywords = grantedKeywords,
            AttacksOnlyIfDefenderControls = attacksOnlyIf,
            Unhandled = unhandled.ToImmutable(),
        };
    }

    /// <summary>
    /// The card's rules text, one line at a time, with the noise taken out.
    /// </summary>
    /// <remarks>
    /// Reminder text in brackets is not rules text — it restates a keyword for players who do not
    /// know it — so it is removed rather than parsed. The card's own name becomes "~", which is
    /// how one template is recognised across the many cards that differ only by name.
    /// </remarks>
    /// <summary>
    /// The card as its front face alone, with no face list to recurse into.
    /// </summary>
    /// <remarks>
    /// A two-faced card's top-level characteristics are already its front face's - that is how the
    /// bulk data records it and what CR 712.8a says a card is anywhere but the battlefield showing
    /// its back. So only two things change: the text becomes the front face's rather than the two
    /// halves joined, and the face list is emptied so <see cref="Compile"/> reads this as an
    /// ordinary card instead of coming straight back here.
    /// </remarks>
    private static CardDefinition FrontOnly(CardDefinition card) => new()
    {
        OracleId = card.OracleId,
        Name = card.Faces[0].Name.Length > 0 ? card.Faces[0].Name : card.Name,
        OracleText = card.Faces[0].OracleText,
        ManaCostRaw = card.ManaCostRaw,
        Cmc = card.Cmc,
        CardTypes = card.CardTypes,
        Subtypes = card.Subtypes,
        Supertypes = card.Supertypes,
        Keywords = card.Keywords,
        Colors = card.Colors,
        ColorIdentity = card.ColorIdentity,
        Power = card.Power,
        Toughness = card.Toughness,
    };

    /// <summary>
    /// Compiles one face of a card that has more than one (CR 712.8d).
    /// </summary>
    /// <remarks>
    /// A face has its own name, cost, types, numbers and rules text, so it compiles exactly the
    /// way a card does - the only work here is handing the phrase parser a definition that says
    /// what the face says instead of what the card says. The oracle id is the card's with the
    /// face's name after it, because two faces of one card are two sets of abilities and a pool
    /// keyed by oracle id has to be able to hold both.
    /// <para>
    /// This compiles a face; it does not make the card playable. The engine has one set of
    /// characteristics per object and no way to cast one face rather than another, which is why
    /// the `//` between the halves is still deliberately left unread by <see cref="Lines"/>.
    /// What this buys today is the measurement: how much of a two-faced card the compiler would
    /// already understand if there were somewhere to put it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// "When you unlock this door, [effect]" (CR 709.5f).
    /// </summary>
    /// <remarks>
    /// Built here rather than left to the trigger grammar for the same two reasons the Class one
    /// is: the event it watches has no word in that vocabulary, and the ability has to be marked
    /// so the half it is printed behind does not gate it out of its own event.
    /// </remarks>
    private static bool TryUnlockTrigger(
        string line,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var m = UnlockTriggerLine().Match(line);
        if (!m.Success)
            return false;

        if (!EffectPhrase.TryParse(m.Groups["effect"].Value.Trim(), out var parsed))
        {
            unhandled.Add(line);
            return true;
        }

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "unlocked",
            Text = line,
            OpensDoor = true,
            Targets = parsed.Targets,
            Effects = parsed.Effects,

            // Replaced by the Room path, which is the only thing that knows which door this is.
            // Left answering nothing rather than answering every unlock, so a half compiled on
            // its own cannot fire on the other door opening.
            Triggers = (_, _, _) => false,
        });

        return true;
    }

    /// <summary>
    /// "When this Class becomes level N, [effect]" (CR 716.2a).
    /// </summary>
    /// <remarks>
    /// Built here rather than left to the trigger grammar because the event it watches is not one
    /// the grammar's vocabulary has a word for, and because the ability has to be marked: the
    /// section it is printed in gates everything else on having reached that level, and this is
    /// the one ability that fires on the way there.
    /// </remarks>
    private static bool TryClassLevelTrigger(
        string line,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var m = ClassLevelTriggerLine().Match(line);
        if (!m.Success)
            return false;

        if (!EffectPhrase.TryParse(m.Groups["effect"].Value.Trim(), out var parsed))
        {
            unhandled.Add(line);
            return true;
        }

        var at = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "becomes" + at.ToString(CultureInfo.InvariantCulture),
            Text = line,
            AnnouncesLevel = true,
            Targets = parsed.Targets,
            Effects = parsed.Effects,
            Triggers = (e, _, source) =>
                e is ClassLevelChanged levelled
                && levelled.Id == source.Id
                && levelled.Level == at,
        });

        return true;
    }

    /// <summary>Whether a permanent has this door open (CR 709.5c).</summary>
    private static bool Unlocked(GameState state, ObjectId id, int half) =>
        state.TryGetObject(id, out var room)
        && room.Permanent is { } permanent
        && permanent.UnlockedHalves.Contains(half);

    /// <summary>Whether this card is a Case with a solve condition (CR 719.3).</summary>
    private static bool IsCase(CardDefinition card) =>
        card.Subtypes.Contains("Case", StringComparer.Ordinal)
        && !card.OracleId.Contains('$', StringComparison.Ordinal)
        && Lines(card).Any(line => ToSolveLine().IsMatch(line));

    /// <summary>
    /// Compiles a Case: its ordinary text, the condition that solves it, and what it does once
    /// solved (CR 719.3a, 719.3c).
    /// </summary>
    /// <remarks>
    /// "To solve - [condition]" is not a line of its own doing: CR 719.3a spells it out as "at
    /// the beginning of your end step, if [condition] and this Case is not solved, this Case
    /// becomes solved". The "and this Case is not solved" half is what stops it firing again
    /// every turn for the rest of the game.
    /// <para>
    /// Sections are compiled as cards of their own and gated on the way back, exactly as
    /// <see cref="CompileClass"/> does, so every matcher in this file is reused unchanged.
    /// </para>
    /// </remarks>
    private static CompiledCard CompileCase(CardDefinition card)
    {
        var always = new List<string>();
        var whenSolved = new List<string>();
        var solving = new List<string>();
        var seenSolved = false;

        foreach (var line in Lines(card))
        {
            var solve = ToSolveLine().Match(line);
            if (solve.Success)
            {
                // The full stop goes with it. Every condition in the shared vocabulary is
                // anchored at both ends, and "You control seven or more lands." with the stop
                // still on it matches none of them - which read as the vocabulary being missing
                // when it was there all along.
                solving.Add(solve.Groups["cond"].Value.Trim().TrimEnd('.'));
                continue;
            }

            var solved = SolvedLine().Match(line);
            if (solved.Success)
            {
                seenSolved = true;
                whenSolved.Add(solved.Groups["ability"].Value.Trim());
                continue;
            }

            // CR 719.3c gives a Case one solved ability, so anything after it belongs to it.
            (seenSolved ? whenSolved : always).Add(line);
        }

        var activated = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();
        var triggers = ImmutableList.CreateBuilder<TriggeredAbilityDefinition>();
        var statics = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();
        var replacements = ImmutableList.CreateBuilder<ReplacementEffectDefinition>();
        var unhandled = ImmutableList.CreateBuilder<string>();

        CompiledCard Section(IEnumerable<string> lines, int tag) => Compile(new CardDefinition
        {
            OracleId = card.OracleId + "$" + tag.ToString(CultureInfo.InvariantCulture),
            Name = card.Name,
            OracleText = string.Join('\n', lines),
            ManaCostRaw = card.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = card.CardTypes,
            Subtypes = card.Subtypes,
            Supertypes = card.Supertypes,
            Keywords = card.Keywords,
            Colors = card.Colors,
            ColorIdentity = card.ColorIdentity,
        });

        var plain = Section(always, 0);
        unhandled.AddRange(plain.Unhandled);
        activated.AddRange(plain.Activated);
        triggers.AddRange(plain.Triggers);
        statics.AddRange(plain.Statics);
        replacements.AddRange(plain.Replacements);

        foreach (var condition in solving)
        {
            if (BoardConditions.Parse(condition) is not { } holds)
            {
                unhandled.Add("To solve \u2014 " + condition);
                continue;
            }

            // Without the "at": the shared trigger vocabulary is handed the clause a card puts
            // after "When" or "At", not the whole sentence.
            var atEndStep = TriggerConditions.Parse("the beginning of your end step");
            if (atEndStep is null)
            {
                unhandled.Add("To solve \u2014 " + condition);
                continue;
            }

            triggers.Add(new TriggeredAbilityDefinition
            {
                Id = "tosolve",
                Text = "To solve \u2014 " + condition,
                Triggers = (e, state, source) =>
                    atEndStep(e, state, source)
                    && holds(state, source.Abilities, source)

                    // CR 719.3a: "and this Case is not solved". Without it the Case solves itself
                    // again at the end of every one of your turns for the rest of the game, and
                    // anything watching for the event sees it each time.
                    && state.TryGetObject(source.Id, out var found)
                    && found.Permanent is { IsSolved: false },
                Effects = [new SolveCase()],
            });
        }

        if (whenSolved.Count > 0)
        {
            var section = Section(whenSolved, 1);
            unhandled.AddRange(section.Unhandled);

            activated.AddRange(section.Activated.Select(a => a with
            {
                Id = "solved" + a.Id,
                ActivateOnlyIf = (state, abilities, self) =>
                    self.Permanent is { IsSolved: true }
                    && a.ActivateOnlyIf?.Invoke(state, abilities, self) != false,
            }));

            triggers.AddRange(section.Triggers.Select(t => t with
            {
                Id = "solved" + t.Id,
                Triggers = (e, state, source) =>
                    state.TryGetObject(source.Id, out var self)
                    && self.Permanent is { IsSolved: true }
                    && t.Triggers(e, state, source),
            }));

            statics.AddRange(section.Statics.Select(c => c with
            {
                Id = "solved" + c.Id,
                Applies = (state, source, built) =>
                    source?.Permanent is { IsSolved: true } && c.Applies(state, source, built),
            }));

            replacements.AddRange(section.Replacements.Select(r => r with
            {
                Id = "solved" + r.Id,
                Applies = (e, state, source) =>
                    source?.Permanent is { IsSolved: true } && r.Applies(e, state, source),
            }));
        }

        return new CompiledCard
        {
            Name = card.Name,
            Activated = activated.ToImmutable(),
            Triggers = triggers.ToImmutable(),
            Statics = statics.ToImmutable(),
            Replacements = replacements.ToImmutable(),
            Unhandled = unhandled.ToImmutable(),
        };
    }

    /// <summary>Whether this card is a Class that prints at least one level bar (CR 716.2).</summary>
    private static bool IsClassWithLevels(CardDefinition card) =>
        card.Subtypes.Contains("Class", StringComparer.Ordinal)
        && !card.OracleId.Contains('$', StringComparison.Ordinal)
        && Lines(card).Any(line => ClassLevelBar().IsMatch(line));

    /// <summary>
    /// Compiles a Class: the always-on text, then one section per level bar (CR 716.2a, 716.3).
    /// </summary>
    /// <remarks>
    /// Each section is compiled as a card of its own, so every matcher in this file is reused
    /// unchanged, and the abilities that come back are wrapped in a level test before they are
    /// merged. The synthetic oracle id carries a "$" so that this does not recurse: a section
    /// still has the Class subtype and would otherwise be split again forever.
    /// </remarks>
    private static CompiledCard CompileClass(CardDefinition card)
    {
        var sections = new List<(int Floor, ManaCostSpec? Cost, List<string> Lines)>
        {
            (1, null, []),
        };

        foreach (var line in Lines(card))
        {
            var bar = ClassLevelBar().Match(line);
            if (bar.Success)
            {
                sections.Add((
                    int.Parse(bar.Groups["n"].Value, CultureInfo.InvariantCulture),
                    ManaCostSpec.Parse(bar.Groups["cost"].Value),
                    []));

                continue;
            }

            sections[^1].Lines.Add(line);
        }

        var activated = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();
        var triggers = ImmutableList.CreateBuilder<TriggeredAbilityDefinition>();
        var statics = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();
        var replacements = ImmutableList.CreateBuilder<ReplacementEffectDefinition>();
        var unhandled = ImmutableList.CreateBuilder<string>();

        foreach (var (floor, cost, lines) in sections)
        {
            if (cost is not null)
            {
                var to = floor;

                // CR 716.2a: "activate only if this Class is level N-1 and only as a sorcery".
                // The first half is what keeps the bars in order; without it a Class could be
                // taken straight to its last level for one activation, skipping every bar it
                // never paid for.
                activated.Add(new ActivatedAbilityDefinition
                {
                    Id = "level" + to.ToString(CultureInfo.InvariantCulture),
                    Text = "Level " + to.ToString(CultureInfo.InvariantCulture),
                    ManaCost = cost,
                    Timing = ActivationTiming.SorceryOnly,
                    ActivateOnlyIf = (_, _, self) => self.Permanent?.Level == to - 1,
                    Effects = [new GainClassLevel(to)],
                });
            }

            var section = Compile(new CardDefinition
            {
                OracleId = card.OracleId + "$" + floor.ToString(CultureInfo.InvariantCulture),
                Name = card.Name,
                OracleText = string.Join('\n', lines),
                ManaCostRaw = card.ManaCostRaw,
                Cmc = card.Cmc,
                CardTypes = card.CardTypes,
                Subtypes = card.Subtypes,
                Supertypes = card.Supertypes,
                Keywords = card.Keywords,
                Colors = card.Colors,
                ColorIdentity = card.ColorIdentity,
            });

            unhandled.AddRange(section.Unhandled);

            // The top section is the Class's text at all times (CR 716.3), merged as it is.
            if (floor <= 1)
            {
                activated.AddRange(section.Activated);
                triggers.AddRange(section.Triggers);
                statics.AddRange(section.Statics);
                replacements.AddRange(section.Replacements);
                continue;
            }

            var need = floor;

            activated.AddRange(section.Activated.Select(a => a with
            {
                Id = "l" + need.ToString(CultureInfo.InvariantCulture) + a.Id,
                ActivateOnlyIf = (state, abilities, self) =>
                    AtLeastLevel(self, need)
                    && a.ActivateOnlyIf?.Invoke(state, abilities, self) != false,
            }));

            triggers.AddRange(section.Triggers.Select(t => t with
            {
                Id = "l" + need.ToString(CultureInfo.InvariantCulture) + t.Id,

                // The level-change trigger is the exception, and it has to be. "When this Class
                // becomes level 3" is printed inside section 3, and a trigger predicate reads the
                // state as it was before the event - where the Class is still level 2. Gated like
                // the rest it would refuse the one event it exists for, and no Class would ever
                // announce a level.
                Triggers = (e, state, source) =>
                    (t.AnnouncesLevel || AtLeastLevel(SelfOf(state, source.Id), need))
                    && t.Triggers(e, state, source),
            }));

            statics.AddRange(section.Statics.Select(c => c with
            {
                Id = "l" + need.ToString(CultureInfo.InvariantCulture) + c.Id,
                Applies = (state, source, built) =>
                    AtLeastLevel(source, need) && c.Applies(state, source, built),
            }));

            replacements.AddRange(section.Replacements.Select(r => r with
            {
                Id = "l" + need.ToString(CultureInfo.InvariantCulture) + r.Id,
                Applies = (e, state, source) =>
                    AtLeastLevel(source, need) && r.Applies(e, state, source),
            }));
        }

        return new CompiledCard
        {
            Name = card.Name,
            Activated = activated.ToImmutable(),
            Triggers = triggers.ToImmutable(),
            Statics = statics.ToImmutable(),
            Replacements = replacements.ToImmutable(),
            Unhandled = unhandled.ToImmutable(),
        };
    }

    /// <summary>Whether a permanent has reached a level (CR 716.2a, 716.2d).</summary>
    private static bool AtLeastLevel(GameObject? permanent, int level) =>
        (permanent?.Permanent?.Level ?? 1) >= level;

    private static GameObject? SelfOf(GameState state, ObjectId id) =>
        state.TryGetObject(id, out var found) ? found : null;

    public static CompiledCard CompileFace(CardDefinition card, CardFace face)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(face);

        return Compile(new CardDefinition
        {
            OracleId = card.OracleId + "#" + face.Name,
            Name = face.Name,
            OracleText = face.OracleText,
            ManaCostRaw = face.ManaCostRaw,
            Cmc = card.Cmc,
            CardTypes = face.CardTypes,
            Subtypes = face.Subtypes,
            Supertypes = face.Supertypes,
            Keywords = face.Keywords,
            Colors = face.Colors,
            ColorIdentity = card.ColorIdentity,
            Power = face.Power,
            Toughness = face.Toughness,
        });
    }

    public static IEnumerable<string> Lines(CardDefinition card)
    {
        ArgumentNullException.ThrowIfNull(card);

        // A card with faces has no text of its own worth reading - the two halves are on the
        // faces, and the `//` the pool writes between them is a rule on the cardboard rather than
        // a sentence. Counted as the sum of what the faces say, which is what the compiler now
        // reads and what makes the two numbers agree.
        if (card.Faces.Count > 1 && !card.OracleId.Contains('#', StringComparison.Ordinal))
        {
            foreach (var face in Enumerable.Range(0, card.Faces.Count))
            {
                foreach (var line in Lines(face == 0 ? FrontOnly(card) : CardFaces.Definition(card, face)))
                    yield return line;
            }

            yield break;
        }

        var text = card.OracleText ?? string.Empty;
        if (text.Length == 0)
            yield break;

        var shortName = card.Name.Split(',')[0].Split(" //", StringSplitOptions.None)[0].Trim();

        foreach (var line in text.Split('\n'))
        {
            var cleaned = Reminder().Replace(line, string.Empty);

            if (card.Name.Length > 2)
                cleaned = cleaned.Replace(card.Name, "~", StringComparison.Ordinal);
            if (shortName.Length > 2)
                cleaned = cleaned.Replace(shortName, "~", StringComparison.Ordinal);

            // Cards printed since 2022 refer to themselves as "this creature" rather than by
            // name, and older ones were errata'd to match. Both spellings mean the source, so
            // both become "~" and one template covers the card whichever wording it carries.
            cleaned = SelfReference().Replace(cleaned, "~");

            // "Support N" is a sentence with its words taken out (CR 701.41a). They are put
            // back before anything else reads the line, because what is left is an instruction
            // this grammar already understands - and which of the two forms it becomes depends
            // on the card, since only a permanent says "other".
            cleaned = SupportWord().Replace(
                cleaned,
                m => "put a +1/+1 counter on each of up to " + NumberInWords(m.Groups["n"].Value) + " "
                    + (card.CardTypes.HasFlag(CardType.Instant)
                        || card.CardTypes.HasFlag(CardType.Sorcery)
                        ? string.Empty
                        : "other ")
                    + "target creatures");

            // "Up to two target creatures **each** get +1/+1" is the same sentence as "up to two
            // target creatures get +1/+1", with the distribution spelled out. The engine makes
            // one effect per target either way, so the word comes out - and it comes out only
            // between a target phrase and its verb, so the "each" that is a quantifier ("each
            // creature you control gets +1/+1") is untouched. 43 cards use this word order.
            cleaned = TargetsEach().Replace(cleaned, "$1 $2");

            // "On each of up to two target creatures" and "on up to two target creatures" are
            // the same instruction; the first spells out that the effect happens to each of them
            // and the second leaves it implied. The engine already reads the implied form - it
            // makes one effect per target - so the words are removed rather than a second reader
            // written for them. 83 cards say it the long way, and every "support N" in the game
            // is the long way with the words taken out again.
            //
            // Anchored on a number and the word "target", so the "each of" that means something
            // else - "each of your opponents", "each of the exiled cards" - is left alone.
            cleaned = EachOfTargets().Replace(cleaned, "$1");

            // CR 700.4 defines "dies" as exactly "is put into a graveyard from the battlefield",
            // so the two are one event written two ways and the long form is normalised to the
            // short one. Every trigger template here is written against "dies", so a card saying
            // it the long way was not read at all while the same card saying "dies" was - the
            // same asymmetry the enters-the-battlefield rewrite above exists to remove. 130 lines
            // use the long spelling.
            //
            // Anchored on "from the battlefield" and nowhere else: "put into a graveyard from
            // anywhere" is a different event that also catches a card being milled or discarded,
            // and rewriting that one to "dies" would narrow it to permanents.
            cleaned = DiesTheLongWay().Replace(
                cleaned, m => m.Groups["are"].Success ? "die" : "dies");

            // "Enters the battlefield" and "enters" are the same event written two ways.
            // Wizards' 2024 templating shortened the first to the second and the comprehensive
            // rules still use both interchangeably, but the corpus carries two hundred lines in
            // the older form and every template here is written in the newer one - so a card
            // saying "enters the battlefield tapped" was not read at all while the same card
            // saying "enters tapped" was. Normalised here for the same reason "this creature"
            // becomes "~": one template should not have to be written twice.
            cleaned = EntersTheBattlefield().Replace(cleaned, "enters");

            // An ability word — "Landfall —", "Constellation —" — is flavour with no rules
            // meaning at all (CR 207.2c). Stripping it lets the sentence behind be read.
            cleaned = AbilityWord().Replace(cleaned, string.Empty);

            // Oracle text uses typographic quotes and dashes. Normalising them means one
            // template matches whichever glyph the printing used, rather than the compiler
            // silently missing every line containing an apostrophe.
            cleaned = cleaned
                .Replace('\u2019', '\'')
                .Replace('\u2018', '\'')
                .Replace('\u201C', '"')
                .Replace('\u201D', '"');

            cleaned = Whitespace().Replace(cleaned, " ").Trim();

            // The rule between a card's two faces, which the card pool writes as a line of its
            // own. It is deliberately left unread, and that is the whole of the engine's answer
            // to two-faced cards: a CardDefinition holds one set of characteristics and one body
            // of text, so a transform, adventure or split card arrives with both halves' rules
            // merged into a single spell. Skipping the separator would let such a card compile as
            // complete and play as one face doing the other's work; leaving it unread says
            // plainly that the card is not implemented, which is what the legality gate is for.
            // 103 cards read every other line and are held back by this one alone.

            if (cleaned.Length > 0)
                yield return cleaned;
        }
    }

    // ---- Matchers ---------------------------------------------------------------------------

    /// <summary>
    /// A line that is nothing but keywords the engine already models (CR 702).
    /// </summary>
    /// <remarks>
    /// The commonest line in Magic by a wide margin — "Flying" is on 2,481 cards — and it needs
    /// no compilation: the keyword is already a flag on the card, set from the card database.
    /// Recognising the line only stops it being reported as unread.
    /// </remarks>
    private static bool IsKeywordLine(string line, CardDefinition card)
    {
        var body = line.TrimEnd('.', ' ');
        if (body.Length == 0)
            return false;

        var sawOne = false;
        foreach (var part in body.Split(','))
        {
            var word = part.Trim();
            if (word.Length == 0)
                continue;

            // A keyword the engine models, and one this card actually has — "Flying" on a card
            // without flying is granting it to something else, which is a different sentence.
            if (!KeywordNames.TryGetValue(word, out var flag) || !card.Keywords.HasFlag(flag))
                return false;

            sawOne = true;
        }

        return sawOne;
    }

    /// <summary>
    /// A comma-separated keyword line where one of the keywords carries a cost.
    /// </summary>
    /// <remarks>
    /// A keyword line is read whole or not at all - deliberately, because half a list is a card
    /// that does half of what it prints - and <see cref="IsKeywordLine"/> can only recognise the
    /// keywords that are flags on the card. One costed keyword in the list therefore took the
    /// whole line down with it: **"Flying, ward {2}" was unread while "Flying" and "Ward {2}"
    /// were both read on their own.** 55 corpus lines carry a ward inside a list.
    /// <para>
    /// Every part is checked before any of it is accepted, which is what keeps the whole-or-
    /// nothing rule: the loop below validates the list first and only then builds, so a part the
    /// engine cannot read leaves the line unread rather than leaving a half-built ability behind.
    /// </para>
    /// </remarks>
    private static bool TryKeywordList(
        string line,
        CardDefinition card,
        ImmutableList<TriggeredAbilityDefinition>.Builder triggers)
    {
        var body = line.TrimEnd('.', ' ');
        if (!body.Contains(',', StringComparison.Ordinal))
            return false;

        var parts = body.Split(',')
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

        if (parts.Count < 2)
            return false;

        var costed = new List<string>();
        var sawPlain = false;

        foreach (var part in parts)
        {
            // A keyword the engine models and this card actually has, exactly as the plain line
            // reader asks it.
            if (KeywordNames.TryGetValue(part, out var flag) && card.Keywords.HasFlag(flag))
            {
                sawPlain = true;
                continue;
            }

            if (WardLine().IsMatch(part) || ToxicLine().IsMatch(part))
            {
                costed.Add(part);
                continue;
            }

            return false;
        }

        // At least one plain keyword, or this is not a list of keywords - it is a single costed
        // keyword with a comma in it, and its own reader has already had the line.
        if (!sawPlain || costed.Count == 0)
            return false;

        foreach (var part in costed)
        {
            if (!TryWard(part, triggers, card) && !TryToxic(part, triggers, card))
                return false;
        }

        return true;
    }

    /// <summary>
    /// "Ward [cost]" — a tax on targeting it (CR 702.21a).
    /// </summary>
    /// <remarks>
    /// The trigger has to know two things the event decided and the board no longer says: which
    /// spell targeted this, so it can be countered, and who cast it, so they can be asked to pay.
    /// Both are the trigger's subject, recorded when it fired.
    /// <para>
    /// Ward does not target — if it did, hexproof and shroud would turn it off, and the two are
    /// printed together constantly. That is why "counter it" is its own effect rather than the
    /// ordinary counter-target-spell.
    /// </para>
    /// </remarks>
    private static bool TryWard(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into, CardDefinition card)
    {
        var m = WardLine().Match(line);
        if (!m.Success)
            return false;

        // "Ward-Pay 3 life" is the same ability with a different currency, and the offer already
        // knows how to charge life. "Ward-Discard a card" and "Ward-Sacrifice a creature" are
        // the third currency: a card or a permanent chosen by the player being taxed. The offer
        // could not ask for one until the engine learned chosen costs, which is why this line
        // was read as three shapes rather than one.
        var wardLife = m.Groups["life"].Success
            ? int.Parse(m.Groups["life"].Value, CultureInfo.InvariantCulture)
            : 0;

        // The noun goes through the shared filter vocabulary, so "a creature" and "a permanent
        // with mana value 3 or greater" are one question asked of the same table every search and
        // every other chosen cost asks. A noun it does not know leaves the line unread rather
        // than taxing the opponent a card of any kind - which would be a harder ward than the one
        // printed, and on the wrong player's cards.
        ChosenCostKind? wardKind = null;
        var wardFilter = SearchFilters.AnyCard;

        if (m.Groups["verb"].Success)
        {
            var noun = m.Groups["what"].Value.Trim();

            // "Discard a card" names no type at all, which the noun table reads as a word it does
            // not know rather than as "any". Answered here, where the difference is visible.
            if (noun.Length > 0)
            {
                if (EffectPhrase.SearchFilterFor(noun) is not { } named)
                    return false;

                wardFilter = named;
            }

            wardKind = m.Groups["verb"].Value.StartsWith(
                "discard", StringComparison.OrdinalIgnoreCase)
                ? ChosenCostKind.DiscardCards
                : ChosenCostKind.SacrificePermanents;
        }

        var cost = wardLife > 0 || wardKind is not null
            ? ManaCostSpec.Parse(string.Empty)
            : ManaCostSpec.Parse(m.Groups["cost"].Value);

        // CR 702.21a: only an opponent's spell taxes. Your own targeting is free, which is what
        // makes ward a defensive ability rather than a drawback.
        bool Triggers(GameEvent e, GameState state, TriggerSource source) =>
            e is TargetsChosen aimed
            && aimed.Targets.Any(t => t.Subject == source.Id)
            && state.TryGetObject(aimed.StackId, out var aiming)
            && aiming.ControllerId != source.ControllerId;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "ward",
            Text = $"Whenever {card.Name} becomes the target of a spell or ability an opponent "
                + "controls, counter it unless that player "
                + (wardKind is not null
                    ? $"{m.Groups["verb"].Value.ToLowerInvariant()}s a {wardFilter}."
                    : wardLife > 0
                        ? $"pays {wardLife} life."
                        : $"pays {m.Groups["cost"].Value}."),
            Triggers = Triggers,
            Effects =
            [
                new MayPay(
                    cost,
                    IfYouDo: [],
                    IfYouDont: [new CounterSubjectSpell()],
                    EffectIndex: 0,
                    AskSubjectPlayer: true,
                    LifeCost: wardLife,
                    ChosenKind: wardKind,
                    ChosenFilterId: wardFilter),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Toxic N" — combat damage also gives poison counters (CR 702.164a).
    /// </summary>
    /// <remarks>
    /// Built here rather than written out as a sentence for the parser, because the sentence it
    /// stands for says "that player" — and that phrase means the trigger's subject only inside a
    /// trigger. Half the corpus uses it to mean a target instead ("Target opponent reveals their
    /// hand ... That player discards that card"), so the shared vocabulary does not read it, and
    /// the only places that may use <see cref="PlayerScope.TriggerSubject"/> are the ones like
    /// this that write the ability themselves and know what they are building.
    /// <para>
    /// Toxic is printed as a static rider on damage, not as a trigger. Modelled as a trigger it
    /// gives the counters after the damage rather than with it, which is observable only if
    /// something responds in between — and against that, a trigger is a mechanism the engine has
    /// and a damage rider is one it does not.
    /// </para>
    /// </remarks>
    private static bool TryToxic(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into, CardDefinition card)
    {
        var m = ToxicLine().Match(line);
        if (!m.Success)
            return false;

        var predicate = TriggerConditions.Parse("~ deals combat damage to a player");
        if (predicate is null)
            return false;

        var count = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "toxic",
            Text = $"Whenever {card.Name} deals combat damage to a player, "
                + $"that player gets {count} poison counters.",
            Triggers = predicate,
            Effects = [new GivePoisonCounters(count, PlayerScope.TriggerSubject)],
        });

        return true;
    }

    /// <summary>
    /// "Soulshift N" — a death trigger printed as a word and a number (CR 702.46a).
    /// </summary>
    /// <remarks>
    /// Written out as the sentence it stands for and handed to the ordinary parser, rather than
    /// assembled from effects here. That is the difference between teaching the compiler a
    /// mechanic and teaching it a card: every piece soulshift needs — the death trigger, the
    /// graveyard target, the mana-value cap, the optional "you may" — is grammar that other
    /// cards already use, so the keyword costs one sentence and no new machinery. It also means
    /// the keyword cannot drift away from the wording it is shorthand for.
    /// </remarks>
    private static bool TrySoulshift(
        string line,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        CardDefinition card,
        ImmutableList<string>.Builder unhandled)
    {
        var m = SoulshiftLine().Match(line);
        if (!m.Success)
            return false;

        var n = m.Groups["n"].Value;
        var effect =
            $"you may return target Spirit card with mana value {n} or less "
            + "from your graveyard to your hand";

        var predicate = TriggerConditions.Parse("~ dies");
        if (predicate is null || !EffectPhrase.TryParse(effect, out var parsed))
        {
            unhandled.Add(line);
            return true;
        }

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "soulshift" + Suffix(into.Count),
            Text = $"When {card.Name} dies, {effect}.",
            Triggers = predicate,
            Targets = parsed.Targets,
            Effects = parsed.Effects,
        });

        return true;
    }

    /// <summary>
    /// "Prowess" — a triggered ability printed as a single word (CR 702.108a).
    /// </summary>
    /// <remarks>
    /// Keyword abilities are not all static. Prowess is shorthand for a whole trigger, so it is
    /// expanded into one here rather than added to the keyword flags, where it would have no
    /// machinery behind it.
    /// </remarks>
    private static bool TryProwess(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!ProwessLine().IsMatch(line))
            return false;

        var predicate = TriggerConditions.Parse("you cast a noncreature spell");
        if (predicate is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "prowess",
            Text = "Whenever you cast a noncreature spell, this creature gets +1/+1 until end of turn.",
            Triggers = predicate,
            Effects = [new PumpSourceUntilEndOfTurn(GenerativeEffects.PumpId(1, 1))],
        });

        return true;
    }

    /// <summary>"Exalted" — a trigger printed as one word (CR 702.90a).</summary>
    private static bool TryExalted(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!ExaltedLine().IsMatch(line))
            return false;

        var predicate = TriggerConditions.Parse("a creature you control attacks alone");
        if (predicate is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "exalted" + Suffix(into.Count),
            Text = "Whenever a creature you control attacks alone, that creature gets "
                + "+1/+1 until end of turn.",
            Triggers = predicate,

            // CR 702.83a: "that creature gets +1/+1", and "that creature" is the one that
            // attacked alone - not the creature with exalted. Pumping the source instead is
            // right exactly when the exalted creature is the one attacking, which is the common
            // case and the reason this went unnoticed for so long: every game where it mattered
            // looked like a game where it did not.
            //
            // Prowess above keeps the source, and that is not an inconsistency: its rule says
            // "this creature".
            Effects =
            [
                new PumpUntilEndOfTurn(
                    GenerativeEffects.PumpId(1, 1),
                    Subject: EffectSubject.TriggeringObject),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Undying" and "Persist" — a dies trigger printed as one word (CR 702.92a, 702.78a).
    /// </summary>
    /// <remarks>
    /// The two are mirror images: undying returns it with a +1/+1 counter unless it already had
    /// one, persist with a -1/-1 counter. The "unless it had one" clause is what stops the loop,
    /// and it is not modelled yet — recorded here rather than hidden, because without it a card
    /// with either keyword returns forever.
    /// </remarks>
    private static bool TryUndyingOrPersist(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var undying = UndyingLine().IsMatch(line);
        var persist = PersistLine().IsMatch(line);
        if (!undying && !persist)
            return false;

        var predicate = TriggerConditions.Parse("~ dies");
        if (predicate is null)
            return false;

        var kind = undying ? CounterKinds.PlusOnePlusOne : CounterKinds.MinusOneMinusOne;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = undying ? "undying" : "persist",
            Text = undying
                ? "When this creature dies, if it had no +1/+1 counters on it, return it to the "
                    + "battlefield under its owner's control with a +1/+1 counter on it."
                : "When this creature dies, if it had no -1/-1 counters on it, return it to the "
                    + "battlefield under its owner's control with a -1/-1 counter on it.",
            // CR 702.79a and 702.92a: "if it <em>had</em> no [counter] on it". The condition was
            // missing entirely, so a persist creature came back every time it died - forever -
            // and an undying one the same. The first death looks identical either way, which is
            // the only death most games see.
            //
            // Checked in the predicate because that is the only place that can see it. "Had" is
            // the creature as it last existed on the battlefield (CR 608.2h, last known
            // information), and a trigger predicate is handed the state as it was before the
            // event - which is precisely that moment. By the time the ability resolves the
            // permanent is a card in a graveyard and its counters are gone with it, so an
            // intervening-if checked there would refuse every persist in the game.
            Triggers = (e, state, source) =>
                predicate(e, state, source)
                && state.TryGetObject(source.Id, out var dying)
                && dying.Permanent?.Counters.GetValueOrDefault(kind) is null or 0,

            Effects = [new ReturnSourceFromGraveyard(kind, 1)],
        });

        return true;
    }

    /// <summary>
    /// "Flanking" — a blocking trigger printed as one word (CR 702.25a).
    /// </summary>
    /// <remarks>
    /// It weakens whichever creature blocked, which is not a target and not the source — so the
    /// effect has to reach the blockers of this attacker through the combat state rather than
    /// through a target index.
    /// </remarks>
    private static bool TryFlanking(
        string line,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ref KeywordAbility granted)
    {
        if (!FlankingLine().IsMatch(line))
            return false;

        // The word grants the keyword as well as the trigger, and the trigger reads the keyword:
        // a flanking creature blocked by another flanking creature is spared (CR 702.25a).
        granted |= KeywordAbility.Flanking;

        var predicate = TriggerConditions.Parse("~ becomes blocked");
        if (predicate is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "flanking",
            Text = "Whenever this creature becomes blocked by a creature without flanking, "
                + "the blocking creature gets -1/-1 until end of turn.",
            Triggers = predicate,
            Effects = [new PumpBlockersOfSource(GenerativeEffects.PumpId(-1, -1), KeywordAbility.Flanking)],
        });

        return true;
    }

    /// <summary>
    /// "Encore [cost]" - an ability that works from the graveyard (CR 702.141a).
    /// </summary>
    /// <remarks>
    /// Three facts the engine already had, in one place: the ability functions from a graveyard,
    /// its cost exiles the card itself, and it is sorcery-speed. What is new is what it makes -
    /// a token per opponent, each told which one to go at.
    /// </remarks>
    private static bool TryEncore(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = EncoreLine().Match(line);
        if (!m.Success)
            return false;

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "encore",
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            FunctionsFrom = Zone.Graveyard,
            SelfCost = SelfCost.ExileSelfFromGraveyard,
            Timing = ActivationTiming.SorceryOnly,
            Effects = [new EncoreCopies()],
        });

        return true;
    }

    /// <summary>
    /// "Exploit" - an enter trigger offering a sacrifice (CR 702.110a).
    /// </summary>
    /// <remarks>
    /// The offer and the choice are asked as one question, because the card asks one: "you may
    /// sacrifice a creature" is answered by naming one or declining. The half that matters to
    /// the rest of the card is the announcement afterwards - every exploit card's second line
    /// says "when this creature exploits a creature", and only a dedicated event can answer it.
    /// </remarks>
    private static bool TryExploit(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!ExploitKeywordLine().IsMatch(line))
            return false;

        var predicate = TriggerConditions.Parse("~ enters");
        if (predicate is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "exploit",
            Text = "When this creature enters, you may sacrifice a creature.",
            Triggers = predicate,
            Effects = [new OfferExploit()],
        });

        return true;
    }

    /// <summary>
    /// "You may exert ~ as it attacks. When you do, ..." (CR 701.43a).
    /// </summary>
    /// <remarks>
    /// Exerting is a free yes-or-no with a consequence, which is exactly what an optional
    /// payment of nothing already is - so the offer, the log-answerable question and the "when
    /// you do" half all come for free, and the only new thing is the untap the creature gives up.
    /// <para>
    /// The choice is asked as the attack trigger resolves rather than as attackers are declared,
    /// which is where CR 701.43a puts it. Nothing between the two moments can see the difference
    /// in this engine - there is no "whenever a creature is exerted" trigger to fire early, and
    /// the untap it skips is steps away either way - but it is an approximation and is recorded
    /// as one.
    /// </para>
    /// </remarks>
    private static bool TryExert(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = ExertLine().Match(line);
        if (!m.Success)
            return false;

        var predicate = TriggerConditions.Parse("~ attacks");
        if (predicate is null)
            return false;

        var payload = ImmutableList.CreateBuilder<IEffect>();
        payload.Add(new SkipNextUntapSource());

        var targets = ImmutableList<TargetSpec>.Empty;

        // The "when you do" half goes through the ordinary effect grammar, so whatever it says
        // is as readable here as it would be anywhere else - and a half this compiler cannot
        // read leaves the whole line unread rather than compiling an exert that does nothing.
        if (m.Groups["then"].Success)
        {
            if (!EffectPhrase.TryParse(m.Groups["then"].Value.Trim(), out var parsed, true))
                return false;

            payload.AddRange(parsed.Effects);
            targets = parsed.Targets;
        }

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "exert",
            Text = line,
            Triggers = predicate,
            Targets = targets,
            Effects =
            [
                new MayPay(
                    ManaCostSpec.Parse(string.Empty),
                    payload.ToImmutable(),
                    [],
                    YesLabel: "Exert it",
                    NoLabel: "Don't exert it"),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Decayed" - a static and a trigger, printed as one word (CR 702.147a).
    /// </summary>
    /// <remarks>
    /// The sacrifice is a delayed triggered ability rather than something the attack does, which
    /// is why the creature survives combat damage and dies after it: a decayed attacker still
    /// deals its damage, and every card that has the keyword depends on that.
    /// </remarks>
    private static bool TryDecayed(
        string line,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ref KeywordAbility granted)
    {
        if (!DecayedLine().IsMatch(line))
            return false;

        var predicate = TriggerConditions.Parse("~ attacks");
        if (predicate is null)
            return false;

        granted |= KeywordAbility.CantBlock;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "decayed",
            Text = "When this creature attacks, sacrifice it at end of combat.",
            Triggers = predicate,
            Effects = [new DelaySourceAction("sacrifice", State.TurnStep.EndOfCombat)],
        });

        return true;
    }

    /// <summary>
    /// "Reconfigure [cost]" - two abilities and a type change (CR 702.151a, 702.151b).
    /// </summary>
    /// <remarks>
    /// Both halves are sorcery-speed, and the second is only legal while the permanent is
    /// attached - written as a condition rather than left to the player, because an ability that
    /// unattaches nothing would still be activatable and would still cost the mana.
    /// <para>
    /// CR 702.151b - it stops being a creature while attached - is the static half, and it is
    /// what makes reconfigure different from equip: the card is a creature in its own right
    /// until it goes on somebody.
    /// </para>
    /// </remarks>
    private static bool TryReconfigure(
        string line,
        ImmutableList<ActivatedAbilityDefinition>.Builder into,
        ImmutableList<ContinuousEffectDefinition>.Builder statics)
    {
        var m = ReconfigureLine().Match(line);
        if (!m.Success)
            return false;

        var cost = ManaCostSpec.Parse(m.Groups["cost"].Value);

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "reconfigure",
            Text = line,
            ManaCost = cost,
            Timing = ActivationTiming.SorceryOnly,
            Targets = [EffectPhrase.Specs.TargetCreatureYouControl],
            Effects = [new AttachSourceTo()],
        });

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "unreconfigure",
            Text = line,
            ManaCost = cost,
            Timing = ActivationTiming.SorceryOnly,
            ActivateOnlyIf = (_, _, source) => source.Permanent?.AttachedTo is not null,
            Effects = [new UnattachSource()],
        });

        statics.Add(new ContinuousEffectDefinition
        {
            Id = "reconfigure-not-a-creature",
            Layer = EffectLayer.Type,
            Applies = (_, source, _) => source?.Permanent?.AttachedTo is not null,
            Apply = (_, _, builder) => builder.CardTypes &= ~CardType.Creature,
        });

        return true;
    }

    /// <summary>
    /// "Job select" - an Equipment that brings its own wearer (CR 702.182a).
    /// </summary>
    /// <remarks>
    /// The token is not described on the card; the keyword carries the whole definition, the way
    /// a Treasure's does. Nineteen Equipment cards are held up by this one word.
    /// </remarks>
    private static bool TryJobSelect(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!JobSelectLine().IsMatch(line))
            return false;

        var predicate = TriggerConditions.Parse("~ enters");
        if (predicate is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "job-select",
            Text = "When this Equipment enters, create a 1/1 colorless Hero creature token, "
                + "then attach this Equipment to it.",
            Triggers = predicate,
            Effects =
            [
                new CreateTokenAndAttachSource(new Domain.Models.CardDefinition
                {
                    OracleId = "token-hero",
                    Name = "Hero",
                    CardTypes = CardType.Creature | CardType.Token,
                    Subtypes = ["Hero"],
                    Power = 1,
                    Toughness = 1,
                }),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Provoke" - an attack trigger that drags a blocker out (CR 702.39a).
    /// </summary>
    /// <remarks>
    /// The requirement it creates names both halves - this creature, that attacker - which is
    /// what <see cref="MustBlockSource"/> exists for; neither "must be blocked" nor a lure can
    /// say it.
    /// <para>
    /// The printed "you may" is read as an optional target rather than as a decision taken on
    /// resolution. The player's choice is the same one either way and it lands a step earlier;
    /// the only case the two come apart is a card that cares about having been targeted, and
    /// nothing in the corpus pairs such a card with provoke. Recorded because it is an
    /// approximation and not an exact reading.
    /// </para>
    /// </remarks>
    private static bool TryProvoke(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!ProvokeLine().IsMatch(line))
            return false;

        var predicate = TriggerConditions.Parse("~ attacks");
        if (predicate is null)
            return false;

        if (EffectPhrase.Specs.Parse("target creature defending player controls") is not { } dragged)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "provoke",
            Text = "Whenever this creature attacks, you may have target creature defending "
                + "player controls untap and block it this combat if able.",
            Triggers = predicate,
            Targets = [dragged with { Optional = true }],
            Effects = [new UntapTarget(), new MustBlockSource()],
        });

        return true;
    }

    /// <summary>
    /// "Devoid" — a characteristic-defining ability making the card colourless (CR 702.114a).
    /// </summary>
    /// <remarks>
    /// Layer 5, and it functions everywhere rather than only on the battlefield (CR 604.3). The
    /// engine computes characteristics for permanents, so the part that shows is the permanent —
    /// but it is written as a static rather than as a keyword flag because colour is not a
    /// keyword, and a flag would have nowhere to apply.
    /// </remarks>
    private static bool TryDevoid(
        string line, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        if (!DevoidLine().IsMatch(line))
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = "devoid",
            Layer = EffectLayer.Color,
            Applies = (_, source, target) => source is not null && target.Subject.Id == source.Id,
            Apply = (_, _, builder) => builder.Colors.Clear(),
        });

        return true;
    }

    /// <summary>
    /// "Modular N", "Bloodthirst N", "Graft N" — enters with counters (CR 702.43a, 702.54a).
    /// </summary>
    /// <remarks>
    /// Three keywords printed as different words for one replacement effect: the permanent
    /// arrives with counters already on it, rather than arriving and then gaining them
    /// (CR 614.1c). The difference is visible — nothing can respond in between, and no ability
    /// sees it arrive without them.
    /// <para>
    /// Only the half that is a replacement is compiled. Modular's dies trigger moves the counters
    /// on to another artifact creature and bloodthirst's condition asks whether an opponent was
    /// dealt damage this turn; neither is modelled, so the line is left unread rather than
    /// half-implemented — a bloodthirst creature that always arrives with counters is a strictly
    /// better card than the one printed.
    /// </para>
    /// </remarks>
    /// <summary>
    /// "When ~ enters, exile target [thing] until ~ leaves the battlefield" (CR 400.7).
    /// </summary>
    /// <remarks>
    /// One printed line, two triggered abilities — and they have to be built together because
    /// neither half means anything alone. An exile with no way back is a strictly better card;
    /// a return with nothing exiled does nothing. Reading them as separate lines would risk
    /// compiling one and not the other, which is the worse of those two failures.
    /// <para>
    /// The card that comes back is a new object (CR 400.7): no counters, no Auras, nothing it
    /// had before, and under its owner's control rather than whoever exiled it. None of that is
    /// arranged here — it is simply what changing zones means, and the reason this family is
    /// used as removal that undoes itself rather than as theft.
    /// </para>
    /// </remarks>
    private static bool TryExileUntilLeaves(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = ExileUntilLeavesLine().Match(line);
        if (!m.Success)
            return false;

        if (EffectPhrase.Specs.Parse(m.Groups["t"].Value.Trim()) is not
            { Kind: Abilities.TargetKind.Permanent } exiled)
        {
            return false;
        }

        var enters = TriggerConditions.Parse("~ enters");
        var leaves = TriggerConditions.Parse("~ leaves the battlefield");

        if (enters is null || leaves is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "exile-until-leaves",
            Text = line,
            Triggers = enters,
            Targets = [exiled],
            Effects = [new ExileUntilSourceLeaves()],
        });

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "return-when-leaves",
            Text = $"When {card.Name} leaves the battlefield, return the exiled card.",
            Triggers = leaves,
            Effects = [new ReturnExiledBySource()],
        });

        return true;
    }

    /// <summary>
    /// The Oblivion Ring pair: an enters-exile line and a leaves-return line, read together.
    /// </summary>
    /// <returns>The lines consumed, which the caller skips; empty when the pair is not there.</returns>
    /// <remarks>
    /// The return line is the one that decides. Several cards return the exiled card somewhere
    /// other than the battlefield — to its owner's hand, to a graveyard — and those are different
    /// cards with a different effect; only the battlefield form is read here, and the rest are
    /// left alone rather than given this one's behaviour.
    /// </remarks>
    private static ImmutableHashSet<string> PairExileAndReturn(
        CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        string? exileLine = null;
        string? returnLine = null;
        Abilities.TargetSpec? exiled = null;

        foreach (var line in Lines(card))
        {
            if (ReturnExiledLine().IsMatch(line))
            {
                returnLine = line;
                continue;
            }

            if (EntersExileTargetLine().Match(line) is not { Success: true } m)
                continue;

            if (EffectPhrase.Specs.Parse(m.Groups["t"].Value.Trim()) is
                { Kind: Abilities.TargetKind.Permanent } spec)
            {
                exileLine = line;
                exiled = spec;
            }
        }

        if (exileLine is null || returnLine is null || exiled is null)
            return [];

        var enters = TriggerConditions.Parse("~ enters");
        var leaves = TriggerConditions.Parse("~ leaves the battlefield");

        if (enters is null || leaves is null)
            return [];

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "exile-until-leaves",
            Text = exileLine,
            Triggers = enters,
            Targets = [exiled],
            Effects = [new ExileUntilSourceLeaves()],
        });

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "return-when-leaves",
            Text = returnLine,
            Triggers = leaves,
            Effects = [new ReturnExiledBySource()],
        });

        return [exileLine, returnLine];
    }

    /// <summary>
    /// "Fabricate N" — counters on it, or that many Servos (CR 702.122a).
    /// </summary>
    /// <remarks>
    /// A binary choice made on resolution, which is what a free optional payment already is: two
    /// branches, one question, answered after the ability resolves. It is built on that rather
    /// than getting a mechanism of its own, and the two answers are given words so the player is
    /// asked which they want instead of being asked to "Pay ?".
    /// <para>
    /// Neither branch is the default. The engine treats declining as the second branch rather
    /// than as nothing happening, which is the whole reason this fits: fabricate has no "no".
    /// </para>
    /// </remarks>
    private static bool TryFabricate(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = FabricateLine().Match(line);
        if (!m.Success)
            return false;

        var count = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        var entered = TriggerConditions.Parse("~ enters");
        if (entered is null)
            return false;

        var servo = new CardDefinition
        {
            OracleId = "token-servo",
            Name = "Servo",
            CardTypes = CardType.Artifact | CardType.Creature | CardType.Token,
            Subtypes = ["Servo"],
            Power = 1,
            Toughness = 1,
        };

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "fabricate",
            Text = $"When {card.Name} enters, put {count} +1/+1 counters on it or create "
                + $"{count} 1/1 colorless Servo artifact creature tokens.",
            Triggers = entered,
            Effects =
            [
                new MayPay(
                    ManaCostSpec.Free,
                    IfYouDo: [new PutCountersOnSource(CounterKinds.PlusOnePlusOne, count)],
                    IfYouDont: [new CreateToken(servo, count)],
                    EffectIndex: 0,
                    YesLabel: $"Put {count} +1/+1 counter(s) on it",
                    NoLabel: $"Create {count} Servo token(s)"),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Rebound" — cast it again next upkeep, for nothing (CR 702.88a).
    /// </summary>
    /// <remarks>
    /// Two halves that only work together: the spell exiles itself instead of going to the
    /// graveyard, and a delayed ability offers it back at the next upkeep. The exile is what makes
    /// the offer possible, and the offer is the only reason to exile it.
    /// <para>
    /// The printed rider "if you cast this spell from your hand" is not read. The engine has no
    /// other way to cast most of these cards, so the condition is true wherever it is checked —
    /// but a card that could be cast from a graveyard would rebound when it should not, and that
    /// is the shape of card to be careful with if this is widened.
    /// </para>
    /// </remarks>
    private static bool TryRebound(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!ReboundLine().IsMatch(line))
            return false;

        var cast = TriggerConditions.Parse("you cast ~");
        if (cast is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "rebound",
            Text = $"If {card.Name} was cast from your hand, exile it as it resolves. At the "
                + "beginning of your next upkeep, you may cast it from exile without paying its "
                + "mana cost.",
            FunctionsFrom = Zone.Stack,

            // CR 702.88a: "if this spell was cast from your hand". The condition is what stops
            // the mechanic eating itself - rebound's own free cast comes *from exile*, so without
            // it the spell rebounds again, and again, and is cast for nothing at the beginning of
            // every upkeep for the rest of the game. The first rebound looks perfectly correct.
            Triggers = (e, state, source) =>
                cast(e, state, source) && e is SpellCastEvent { From: Zone.Hand },
            Effects = [new DelaySourceAction("rebound", State.TurnStep.Upkeep)],
        });

        return true;
    }

    /// <summary>
    /// "Cascade" — dig for something cheaper and offer it free (CR 702.85a).
    /// </summary>
    private static bool TryCascade(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!CascadeLine().IsMatch(line))
            return false;

        var cast = TriggerConditions.Parse("you cast ~");
        if (cast is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "cascade",
            Text = $"When you cast {card.Name}, exile cards from the top of your library until "
                + "you exile a nonland card that costs less. You may cast it without paying its "
                + "mana cost. Put the exiled cards on the bottom in a random order.",
            FunctionsFrom = Zone.Stack,
            Triggers = cast,
            Effects = [new Cascade()],
        });

        return true;
    }

    /// <summary>
    /// "Storm" — a copy for each spell cast before it this turn (CR 702.40a).
    /// </summary>
    /// <remarks>
    /// The count is read as the trigger resolves rather than when the spell was cast, which is
    /// the same thing here — nothing can be cast in between, because the storm trigger goes on
    /// the stack above the spell and above anything already there. Counting at resolution keeps
    /// it a question about the state rather than something the trigger has to have remembered.
    /// <para>
    /// "Before it" is why the count subtracts one: the spell that has storm was itself cast, and
    /// the player's count already includes it.
    /// </para>
    /// </remarks>
    private static bool TryStorm(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!StormLine().IsMatch(line))
            return false;

        var cast = TriggerConditions.Parse("you cast ~");
        if (cast is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "storm",
            Text = $"When you cast {card.Name}, copy it for each spell cast before it this turn.",
            FunctionsFrom = Zone.Stack,
            Triggers = cast,
            Effects =
            [
                new CopySpell
                {
                    CountFrom = context => context.State
                        .GetPlayer(context.ControllerId).SpellsCastThisTurn - 1,
                },
            ],
        });

        return true;
    }

    /// <summary>
    /// "Living weapon" — the Equipment brings its own wielder (CR 702.90b).
    /// </summary>
    /// <remarks>
    /// The Germ is a 0/0, so it dies to state-based actions the moment the Equipment comes off —
    /// which is the whole joke of the mechanic, and it needs nothing special to work: the token
    /// arrives at 0/0 and survives only because the Equipment attached to it is making it bigger.
    /// </remarks>
    /// <summary>
    /// "Start your engines!" — get moving if you are not already (CR 702.179a).
    /// </summary>
    /// <remarks>
    /// The same keyword fires from two different places depending on what the card is: a
    /// permanent starts its engines as it enters, and a spell that never becomes a permanent
    /// starts them as it is cast, because entering is something it will never do.
    /// </remarks>
    /// <summary>
    /// "Jump-start" - cast it from the graveyard by discarding a card (CR 702.132a).
    /// </summary>
    /// <remarks>
    /// Retrace and flashback both, and nothing new: the card's own mana cost like retrace, a
    /// discard charged only when it is cast this way like retrace, and the exile on resolving
    /// like flashback. What it discards is any card at all, which is why the cost carries no
    /// spec - the ones that name what may be discarded are the exception, not this.
    /// </remarks>
    private static bool TryJumpStart(
        string line, CardDefinition card, ref AlternativeCastZone? into)
    {
        if (!JumpStartLine().IsMatch(line))
            return false;

        into = new AlternativeCastZone(
            Zone.Graveyard,
            ManaCostSpec.Parse(card.ManaCostRaw ?? string.Empty),
            ExileOnResolve: true,
            [new ChosenCost(ChosenCostKind.DiscardCards, 1)]);

        return true;
    }

    /// <summary>
    /// "Retrace" — cast it again from the graveyard, for a land (CR 702.82a).
    /// </summary>
    /// <remarks>
    /// Flashback's zone permission with two differences, and both matter. The cost is the card's
    /// own, not a stated one; and the land discard is charged only when it is cast this way, so it
    /// rides on the zone rather than on the spell — a retrace card cast from hand costs exactly
    /// what is printed on it.
    /// <para>
    /// It does not exile itself on resolving either, which is the whole point: the card goes back
    /// to the graveyard and can be retraced again for as long as the lands hold out.
    /// </para>
    /// </remarks>
    private static bool TryRetrace(
        string line, CardDefinition card, ref AlternativeCastZone? into)
    {
        if (!RetraceLine().IsMatch(line))
            return false;

        // Described as a card rather than as a permanent: the spec's filter is what does the
        // work and it reads card types wherever the object is, but the description is what a
        // player is told when they offer the wrong thing, and they are offering it from hand.
        if (EffectPhrase.Specs.Parse("target land") is not { } land)
            return false;

        land = land with { Description = "a land card" };

        into = new AlternativeCastZone(
            Zone.Graveyard,
            ManaCostSpec.Parse(card.ManaCostRaw ?? string.Empty),
            ExileOnResolve: false,
            [new ChosenCost(ChosenCostKind.DiscardCards, 1, land)]);

        return true;
    }

    /// <summary>
    /// "Dredge N" - mill instead of drawing, and take the card back (CR 702.52a).
    /// </summary>
    /// <remarks>
    /// The first optional replacement the compiler produces, and the reason replacements learned
    /// to ask: every one before it said "instead" and simply happened. This one says "you may",
    /// and the question comes up in the middle of applying a draw rather than at the end of a
    /// resolution - so the engine holds the event, asks, and re-emits it, which is the machinery
    /// that already existed for choosing between two replacements.
    /// <para>
    /// The library check is in <c>Applies</c> rather than in the replacement, because a player
    /// with too few cards is not offered the choice at all (CR 702.52b) - offering it and then
    /// milling nothing would let a player decline a draw for free.
    /// </para>
    /// </remarks>
    private static bool TryDredge(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = DredgeLine().Match(line);
        if (!m.Success)
            return false;

        var many = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new ReplacementEffectDefinition
        {
            Id = $"dredge:{many}",
            FunctionsFrom = Zone.Graveyard,
            IsOptional = true,
            Applies = (e, state, source) =>
                e is Events.ObjectMoved { To: Zone.Hand, Cause: Events.MoveCause.Draw } drawn
                && drawn.ControllerId == source.OwnerId
                && state.GetPlayer(source.OwnerId).Library.Count >= many,
            Replace = (_, state, source) =>
            {
                var owner = source.OwnerId;
                var events = ImmutableList.CreateBuilder<Events.GameEvent>();

                foreach (var card in state.GetPlayer(owner).Library.Take(many))
                {
                    events.Add(new Events.ObjectMoved(
                        card,
                        State.ObjectId.New(),
                        Zone.Library,
                        Zone.Graveyard,
                        owner,
                        Events.MoveCause.Mill));
                }

                events.Add(new Events.ObjectMoved(
                    source.Id,
                    State.ObjectId.New(),
                    Zone.Graveyard,
                    Zone.Hand,
                    owner,
                    Events.MoveCause.Return));

                return events.ToImmutable();
            },
        });

        return true;
    }

    /// <summary>
    /// "Umbra armor" - the Aura dies instead of what it is on (CR 702.170a).
    /// </summary>
    /// <remarks>
    /// A replacement on somebody else's destruction, which is what makes it different from
    /// regeneration: the shield is on the Aura and the event it watches is about the host. The
    /// original move is not returned, so the host is simply not destroyed.
    /// <para>
    /// The damage comes off the host alone. Reaching for the cleanup step's global event here
    /// would save every damaged creature in play, which is the bug this keyword turned up in
    /// regeneration.
    /// </para>
    /// </remarks>
    private static bool TryUmbraArmor(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        if (!UmbraArmorLine().IsMatch(line))
            return false;

        into.Add(new ReplacementEffectDefinition
        {
            Id = "umbra-armor",
            FunctionsFrom = Zone.Battlefield,
            Applies = (e, _, source) =>
                e is Events.ObjectMoved { To: Zone.Graveyard, Cause: Events.MoveCause.Destroy } gone
                && source.Permanent?.AttachedTo == gone.OldId,
            Replace = (_, _, source) =>
            [
                new Events.DamageRemoved(source.Permanent!.AttachedTo!.Value),
                new Events.ObjectMoved(
                    source.Id,
                    State.ObjectId.New(),
                    Zone.Battlefield,
                    Zone.Graveyard,
                    source.OwnerId,
                    Events.MoveCause.Destroy),
            ],
        });

        return true;
    }

    /// <summary>
    /// "You control enchanted creature" - the Aura takes what it is on (CR 613.1b).
    /// </summary>
    /// <remarks>
    /// Layer 2, ahead of everything about what the creature <em>is</em>, because who controls a
    /// permanent decides which side of every later question it falls on - whether a lord pumps
    /// it, whether a sweeper aimed at "creatures you control" finds it, and who it may attack.
    /// <para>
    /// The controller is the Aura's own, read when the effect is applied rather than when the
    /// card was compiled: an Aura that changes hands takes its host with it.
    /// </para>
    /// </remarks>
    private static bool TryStealHost(
        string line, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        if (!StealHostLine().IsMatch(line))
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = "steal-enchanted",
            Layer = EffectLayer.Control,
            Applies = (_, source, target) =>
                source?.Permanent?.AttachedTo is { } host && target.Subject.Id == host,
            Apply = (state, _, target) =>
            {
                foreach (var id in state.Battlefield)
                {
                    var aura = state.GetObject(id);

                    if (aura.Permanent?.AttachedTo == target.Subject.Id)
                    {
                        target.ControllerId = aura.ControllerId;
                        return;
                    }
                }
            },
        });

        return true;
    }

    /// <summary>
    /// "Mentor" - the bigger attacker grows the smaller one (CR 702.134a).
    /// </summary>
    /// <remarks>
    /// "Lesser power" is lesser than <em>this</em> creature's, which is the other thing a target
    /// filter needs its source for. It also settles a question the card never has to state: a
    /// creature cannot mentor itself, because nothing has power less than its own.
    /// <para>
    /// Both powers are read computed, so a mentor pumped this turn can reach a target it could
    /// not have a moment ago.
    /// </para>
    /// </remarks>
    private static bool TryMentor(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!MentorLine().IsMatch(line))
            return false;

        var attacks = TriggerConditions.Parse("~ attacks");
        if (attacks is null)
            return false;

        if (EffectPhrase.Specs.Parse("target attacking creature") is not { } attacker)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "mentor",
            Text = $"Whenever {card.Name} attacks, put a +1/+1 counter on target attacking "
                + "creature with lesser power.",
            Triggers = attacks,
            Targets =
            [
                attacker with
                {
                    Description = "target attacking creature with lesser power",
                    SourceFilter = (state, abilities, obj, source, _) =>
                        source is not null
                        && State.Characteristics.Of(state, abilities, obj).Power
                            < State.Characteristics.Of(state, abilities, source).Power,
                },
            ],
            Effects = [new PutCounters(State.CounterKinds.PlusOnePlusOne, 1)],
        });

        return true;
    }

    /// <summary>
    /// "Battle cry" - every other attacker swings harder (CR 702.90a).
    /// </summary>
    /// <remarks>
    /// The first phrase in the engine that needs to know what is doing the choosing: "each
    /// <em>other</em> attacking creature" is the whole ability, and a group filter that could
    /// not see its own source would either pump the crier too or need a hand-built exception.
    /// </remarks>
    private static bool TryBattleCry(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!BattleCryLine().IsMatch(line))
            return false;

        var attacks = TriggerConditions.Parse("~ attacks");
        if (attacks is null)
            return false;

        if (EffectPhrase.Specs.ParseGroup("each attacking creature") is not
            { Kind: Abilities.TargetKind.Permanent } attackers)
        {
            return false;
        }

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "battle-cry",
            Text = $"Whenever {card.Name} attacks, each other attacking creature gets +1/+0 "
                + "until end of turn.",
            Triggers = attacks,
            Effects =
            [
                new PumpGroup(
                    GenerativeEffects.PumpId(1, 0),
                    attackers with
                    {
                        Description = "each other attacking creature",
                        SourceFilter = (_, _, obj, source, _) => source is null || obj.Id != source.Id,
                    }),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Ingest" - the player it hits loses the top of their library (CR 702.114a).
    /// </summary>
    /// <remarks>
    /// A combat damage trigger and an exile, both of which the vocabulary already reads. "That
    /// player" is the one the damage was dealt to, which the trigger records as its subject -
    /// exiling from the controller's own library instead would be the opposite card.
    /// </remarks>
    private static bool TryIngest(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!IngestLine().IsMatch(line))
            return false;

        var hit = TriggerConditions.Parse("~ deals combat damage to a player");
        if (hit is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "ingest",
            Text = $"Whenever {card.Name} deals combat damage to a player, that player exiles "
                + "the top card of their library.",
            Triggers = hit,
            Effects = [new ExileFromTopOfLibrary(1, PlayerScope.TriggerSubject)],
        });

        return true;
    }

    /// <summary>
    /// "Riot" - a counter or haste, whichever you want (CR 702.135a).
    /// </summary>
    /// <remarks>
    /// Printed as a replacement with a choice in it, and read here as an enters trigger for the
    /// same reason unleash is: the engine can only ask a question at the end of a resolution.
    /// <para>
    /// Riot loses less to that than unleash does. A creature that takes haste has it before it
    /// could have attacked either way, and one that takes the counter is bigger before anything
    /// reads its size except an ability counting counters as it entered.
    /// </para>
    /// </remarks>
    private static bool TryRiot(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!RiotLine().IsMatch(line))
            return false;

        var entered = TriggerConditions.Parse("~ enters");
        if (entered is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "riot",
            Text = $"{card.Name} enters with a +1/+1 counter on it or with haste (your choice).",
            Triggers = entered,
            Effects =
            [
                new MayPay(
                    ManaCostSpec.Free,
                    IfYouDo: [new PutCountersOnSource(State.CounterKinds.PlusOnePlusOne, 1)],
                    IfYouDont:
                    [
                        new PumpSourceUntilEndOfTurn(
                            GenerativeEffects.GrantId(KeywordAbility.Haste)),
                    ],
                    YesLabel: "A +1/+1 counter",
                    NoLabel: "Haste"),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Unleash" - bigger if you give up blocking with it (CR 702.108a).
    /// </summary>
    /// <remarks>
    /// The printed first half is a replacement with a choice in it: "you may have this creature
    /// enter with a +1/+1 counter on it". The engine cannot ask a question in the middle of
    /// applying a replacement - a choice is only answerable at the end of a resolution - so this
    /// is an enters trigger that offers the counter instead.
    /// <para>
    /// The difference is that the counter arrives a moment after the creature rather than with
    /// it, which is visible only to something that reads counters as a permanent enters. Nothing
    /// with unleash is a 0/0 that would need the counter to survive, so what it costs is the
    /// wording rather than the game.
    /// </para>
    /// <para>
    /// The second half is an ordinary static: it cannot block while the counter is on it, which
    /// is layer 6 like every other granted ability.
    /// </para>
    /// </remarks>
    private static bool TryUnleash(
        string line,
        CardDefinition card,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ImmutableList<ContinuousEffectDefinition>.Builder statics)
    {
        if (!UnleashLine().IsMatch(line))
            return false;

        var entered = TriggerConditions.Parse("~ enters");
        if (entered is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "unleash",
            Text = $"You may have {card.Name} enter with a +1/+1 counter on it. It can't block "
                + "as long as it has a +1/+1 counter on it.",
            Triggers = entered,
            Effects =
            [
                new MayPay(
                    ManaCostSpec.Free,
                    IfYouDo: [new PutCountersOnSource(State.CounterKinds.PlusOnePlusOne, 1)],
                    IfYouDont: [],
                    YesLabel: "Enter with a +1/+1 counter",
                    NoLabel: "Enter without one"),
            ],
        });

        statics.Add(new ContinuousEffectDefinition
        {
            Id = "unleash-cant-block",
            Layer = EffectLayer.Ability,
            Applies = (_, source, target) =>
                source is not null
                && target.Subject.Id == source.Id
                && source.Permanent?.Counters.GetValueOrDefault(
                    State.CounterKinds.PlusOnePlusOne, 0) > 0,
            Apply = (_, _, target) => target.Keywords |= KeywordAbility.CantBlock,
        });

        return true;
    }

    /// <summary>
    /// "Extort" - every spell you cast can drain the table (CR 702.101a).
    /// </summary>
    /// <remarks>
    /// "You gain that much life" is not a fixed one: it is however much was lost in total, which
    /// is one per opponent still in the game. In a two-player game the two are the same number,
    /// which is exactly why it has to be counted rather than written down - a card that reads
    /// correctly at two players and pays a third of what it should at four is worse than one
    /// that goes unread.
    /// </remarks>
    private static bool TryExtort(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!ExtortLine().IsMatch(line))
            return false;

        var cast = TriggerConditions.Parse("you cast a spell");
        if (cast is null)
            return false;

        static int LivingOpponents(Abilities.ResolutionContext context) =>
            context.State.TurnOrder.Count(
                id => id != context.ControllerId && !context.State.GetPlayer(id).HasLost);

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "extort",
            Text = $"Whenever you cast a spell, you may pay {{W/B}}. If you do, each opponent "
                + $"loses 1 life and you gain that much life.",
            Triggers = cast,
            Effects =
            [
                new MayPay(
                    ManaCostSpec.Parse("{W/B}"),
                    IfYouDo:
                    [
                        new ChangeLifeOfEach(-1, PlayerScope.EachOpponent),
                        // One per opponent: the fixed part is the multiplier and the counter is
                        // the count, so a zero here would gain nothing however many were drained.
                        new ChangeLife(new Amount(1) { Counter = LivingOpponents }),
                    ],
                    IfYouDont: []),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Afterlife N" - it leaves Spirits behind when it dies (CR 702.135a).
    /// </summary>
    /// <remarks>
    /// A death trigger and a token, both of which the vocabulary already reads - the keyword is
    /// only a shorter way of printing them. The token is white *and* black, which is why its
    /// colours are a list rather than one entry.
    /// </remarks>
    private static bool TryAfterlife(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = AfterlifeLine().Match(line);
        if (!m.Success)
            return false;

        var died = TriggerConditions.Parse("~ dies");
        if (died is null)
            return false;

        var count = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        var spirit = new CardDefinition
        {
            OracleId = "token-spirit-1-1-wb-flying",
            Name = "Spirit",
            CardTypes = CardType.Creature | CardType.Token,
            Subtypes = ["Spirit"],
            Power = 1,
            Toughness = 1,
            Keywords = KeywordAbility.Flying,
            ColorIdentity = [ManaColor.White, ManaColor.Black],
            Colors = [ManaColor.White, ManaColor.Black],
        };

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "afterlife",
            Text = $"When {card.Name} dies, create {count} 1/1 white and black Spirit creature "
                + "token(s) with flying.",
            Triggers = died,
            Effects = [new CreateToken(spirit, count)],
        });

        return true;
    }

    /// <summary>
    /// "Creature spells you cast cost {1} less to cast" (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// Not a continuous effect: what a spell costs is worked out once as it is cast and never
    /// recomputed, so this is read at cast time from whatever the caster controls then. The
    /// filter is the vocabulary searching already uses, which is what lets "instant and sorcery"
    /// be two filters rather than a phrase needing its own reader.
    /// </remarks>
    private static bool TryCostReducer(string line, ImmutableList<CostReducer>.Builder into)
    {
        var m = CostReducerLine().Match(line);
        if (!m.Success)
            return false;

        var what = m.Groups["what"].Value.Trim();
        var filter = what.Length == 0
            ? SearchFilters.AnyCard
            : EffectPhrase.SearchFilterFor(what.Replace(" and ", " or ", StringComparison.OrdinalIgnoreCase));

        if (filter is null)
            return false;

        into.Add(new CostReducer(
            filter, int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)));

        return true;
    }

    /// <summary>
    /// "Vanishing N" - it arrives on a clock (CR 702.63a).
    /// </summary>
    /// <remarks>
    /// Three printed abilities and two compiled ones: the counters it enters with are the
    /// replacement every enters-with-counters card uses, and the upkeep tick and the sacrifice
    /// are folded into one effect because the second only ever fires as the first finishes.
    /// <para>
    /// Both halves are added together or neither is. A vanishing permanent that entered with no
    /// counters would never leave, and one that ticked without entering with any would be
    /// sacrificed on its controller's next upkeep - each is a different card from the one
    /// printed, and worse, a plausible one.
    /// </para>
    /// </remarks>
    private static bool TryVanishing(
        string line,
        CardDefinition card,
        ImmutableList<TriggeredAbilityDefinition>.Builder triggers,
        ImmutableList<ReplacementEffectDefinition>.Builder replacements)
    {
        var m = VanishingLine().Match(line);
        if (!m.Success)
            return false;

        var many = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (many <= 0)
            return false;

        var atUpkeep = TriggerConditions.Parse("the beginning of your upkeep");
        if (atUpkeep is null)
            return false;

        replacements.Add(new ReplacementEffectDefinition
        {
            Id = $"vanishing-counters:{many}",
            FunctionsFrom = null,
            Applies = (e, _, source) =>
                e is Events.ObjectMoved { To: Zone.Battlefield } moved && moved.OldId == source.Id,
            Replace = (e, _, _) =>
            {
                var move = (Events.ObjectMoved)e;
                return [move, new Events.CountersChanged(move.NewId, CounterKinds.Time, many)];
            },
        });

        triggers.Add(new TriggeredAbilityDefinition
        {
            Id = "vanishing",
            Text = $"At the beginning of your upkeep, remove a time counter from {card.Name}. "
                + "When the last is removed, sacrifice it.",
            Triggers = atUpkeep,
            Effects = [new TickVanishing()],
        });

        return true;
    }

    /// <summary>
    /// "Recover [cost]" - buy it back out of the graveyard when a creature dies (CR 702.59a).
    /// </summary>
    /// <remarks>
    /// A triggered ability that functions from the graveyard, which the zone-change grammar and
    /// the optional payment both already handle - what is new is only that the "if you don't"
    /// branch does something. Declining exiles the card, so a recover card is used once either
    /// way, and forgetting the second branch would have made it a free second chance every time
    /// a creature died.
    /// <para>
    /// The rule says "put into <em>your</em> graveyard", which is about ownership; the trigger
    /// reads "another creature you control dies", which is about control. The two differ only
    /// for a creature you have stolen, which dies to its owner's graveyard and should not fire
    /// this - a known deviation rather than an oversight.
    /// </para>
    /// </remarks>
    private static bool TryRecover(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = RecoverLine().Match(line);
        if (!m.Success)
            return false;

        var died = TriggerConditions.Parse("another creature you control dies");
        if (died is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "recover",
            Text = $"When another creature you control dies, you may pay "
                + $"{m.Groups["cost"].Value}. If you do, return {card.Name} from your graveyard "
                + "to your hand. Otherwise, exile it.",
            FunctionsFrom = Zone.Graveyard,
            Triggers = died,
            Effects =
            [
                new MayPay(
                    ManaCostSpec.Parse(m.Groups["cost"].Value),
                    [new ReturnSourceToHand()],
                    [new ExileSource()],
                    YesLabel: "Pay and take it back",
                    NoLabel: "Let it be exiled"),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Afflict N" - blocking it costs the defender life (CR 702.130a).
    /// </summary>
    /// <remarks>
    /// Nothing new: the trigger rampage uses and the player scope an attack trigger already
    /// names. Worth having anyway, because "defending player" is only meaningful while the
    /// source is attacking, and being blocked is the one moment that is guaranteed.
    /// </remarks>
    /// <summary>
    /// "You may have ~ assign its combat damage as though it weren't blocked" (CR 510.1a).
    /// </summary>
    /// <remarks>
    /// A permission recorded as a characteristic rather than an effect, because it is asked for
    /// during the combat damage step and the ability itself does nothing until then. The choice
    /// belongs to the attacking player and is offered once per combat damage step.
    /// </remarks>
    private static bool TryAssignAsThoughUnblocked(
        string line, ImmutableList<ContinuousEffectDefinition>.Builder statics)
    {
        if (!AssignAsThoughUnblockedLine().IsMatch(line))
            return false;

        statics.Add(new ContinuousEffectDefinition
        {
            Id = "assign-as-though-unblocked",

            // Layer 6: it is an ability the creature has, and what it grants is read during
            // combat rather than applied to any characteristic here (CR 613.1f).
            Layer = EffectLayer.Ability,
            Applies = (_, source, _) => source is not null,
            Apply = (_, _, builder) => builder.MayAssignAsThoughUnblocked = true,
        });

        return true;
    }

    /// <summary>
    /// "Firebending N" - attacking pays out red mana that outlasts the step (CR 702.189a).
    /// </summary>
    /// <remarks>
    /// Written out as the rule spells it rather than as a keyword the engine knows, because every
    /// part already exists: the attack trigger, adding mana, and the permission for that mana to
    /// survive the emptying. The last of those is the reason this could not be read before.
    /// </remarks>
    private static bool TryFirebending(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = FirebendingLine().Match(line);
        if (!m.Success)
            return false;

        var attacks = TriggerConditions.Parse("~ attacks");
        if (attacks is null)
            return false;

        var much = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "firebending",
            Text = $"Whenever {card.Name} attacks, add {much} red mana that you do not lose as "
                + "steps end until end of combat.",
            Triggers = attacks,
            Effects =
            [
                new KeepManaAdded(
                    new AddMana([new ManaProduction(ManaColor.Red, much)]),
                    Mana.ManaPersistence.EndOfCombat),
            ],
        });

        return true;
    }

    private static bool TryAfflict(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = AfflictLine().Match(line);
        if (!m.Success)
            return false;

        var blocked = TriggerConditions.Parse("~ becomes blocked");
        if (blocked is null)
            return false;

        var much = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "afflict",
            Text = $"Whenever {card.Name} becomes blocked, defending player loses {much} life.",
            Triggers = blocked,
            Effects = [new ChangeLifeOfEach(new Amount(-much), PlayerScope.DefendingPlayer)],
        });

        return true;
    }

    /// <summary>
    /// "Dethrone" - it grows for attacking whoever is ahead (CR 702.105a).
    /// </summary>
    /// <remarks>
    /// The life comparison is an intervening-if rather than part of the trigger (CR 603.4), so
    /// the ability triggers on any attack and checks the life totals when it resolves - which is
    /// the same deviation rampage and evolve both carry, and for the same reason: the trigger
    /// grammar matches events, and "the player with the most life" is a fact about the board.
    /// </remarks>
    private static bool TryDethrone(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!DethroneLine().IsMatch(line))
            return false;

        var attacks = TriggerConditions.Parse("~ attacks");
        if (attacks is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "dethrone",
            Text = $"Whenever {card.Name} attacks the player with the most life or tied for most "
                + "life, put a +1/+1 counter on it.",
            Triggers = attacks,
            Effects = [new DethroneCounter()],
        });

        return true;
    }

    /// <summary>
    /// "Rampage N" - bigger the more creatures gang up on it (CR 702.23a).
    /// </summary>
    /// <remarks>
    /// The printed trigger is "becomes blocked by more than one creature"; this triggers on being
    /// blocked at all and the effect does nothing when only one creature blocked. The difference
    /// is an ability that goes on the stack and does nothing, which an opponent could respond to
    /// where the real card gives them no such window.
    /// </remarks>
    private static bool TryRampage(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = RampageLine().Match(line);
        if (!m.Success)
            return false;

        var blocked = TriggerConditions.Parse("~ becomes blocked");
        if (blocked is null)
            return false;

        var each = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "rampage",
            Text = $"Whenever {card.Name} becomes blocked by more than one creature, it gets "
                + $"+{each}/+{each} until end of turn for each creature blocking it beyond the "
                + "first.",
            Triggers = blocked,
            Effects = [new RampageBonus(each)],
        });

        return true;
    }

    /// <summary>
    /// "Evolve" - grows whenever something bigger arrives (CR 702.100a).
    /// </summary>
    /// <remarks>
    /// The comparison is the whole keyword, and it is an intervening-if rather than part of the
    /// trigger condition (CR 603.4): the ability triggers on any creature entering and then checks
    /// the sizes, so a creature that shrinks in between does not get its counter.
    /// <para>
    /// The source's own size is read computed, so a printed 5/5 shrunk to 1/1 evolves off a 2/2
    /// as it should. What the arriving creature is measured as is a subtler question - see
    /// <see cref="IsBigger"/>.
    /// </para>
    /// </remarks>
    private static bool TryEvolve(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!EvolveLine().IsMatch(line))
            return false;

        var entered = TriggerConditions.Parse("another creature you control enters");
        if (entered is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "evolve",
            Text = $"Whenever a creature you control enters, if that creature has greater power "
                + $"or toughness than {card.Name}, put a +1/+1 counter on {card.Name}.",
            Triggers = (e, state, source) =>
                entered(e, state, source) && IsBigger(e, state, source),
            Effects = [new PutCountersOnSource(State.CounterKinds.PlusOnePlusOne, 1)],
        });

        return true;
    }

    /// <summary>
    /// Whether the creature this event is about outsizes the source (CR 702.100a).
    /// </summary>
    /// <remarks>
    /// A trigger predicate is handed the state as it was <em>before</em> the event, which is what
    /// makes an intervening-if answerable at all - but it means the arriving permanent is not in
    /// it yet. So the newcomer's size is read off the event: the card it was made from, or the
    /// object it moved from. The source is looked up normally, because it was already there.
    /// <para>
    /// The consequence is that the newcomer is measured as printed while the source is measured
    /// as computed. A creature that enters already modified - one that enters with counters, or
    /// arrives under a lord - is measured smaller than it will be. That is the direction that
    /// under-triggers rather than over-triggers, which is the safe way round for a keyword that
    /// hands out counters.
    /// </para>
    /// </remarks>
    private static bool IsBigger(Events.GameEvent e, GameState state, GameObject source)
    {
        // A permanent arrives either by moving there or by being made there, and a token that
        // outsizes this evolves it exactly as a cast creature does (CR 111.1). Reading only the
        // move missed every token and every card put onto the battlefield directly.
        var arriving = e switch
        {
            Events.ObjectMoved { To: State.Zone.Battlefield } moved
                => state.TryGetObject(moved.OldId, out var before) ? before.Card : null,
            Events.ObjectCreated { Zone: State.Zone.Battlefield } made => made.Card,
            _ => null,
        };

        if (arriving is not { CardTypes: var types } newcomer
            || !types.HasFlag(Domain.Enums.CardType.Creature))
        {
            return false;
        }

        var mine = Characteristics.Of(state, EmptyAbilities.Instance, source);

        return newcomer.Power > mine.Power || newcomer.Toughness > mine.Toughness;
    }

    /// <summary>
    /// "Myriad" - one attack becomes an attack on everyone (CR 702.115a).
    /// </summary>
    private static bool TryMyriad(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!MyriadLine().IsMatch(line))
            return false;

        var attacks = TriggerConditions.Parse("~ attacks");
        if (attacks is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "myriad",
            Text = $"Whenever {card.Name} attacks, for each opponent other than the defending "
                + "player, you may create a token that's a copy of it that's tapped and attacking "
                + "that player. Exile the tokens at end of combat.",
            Triggers = attacks,
            Effects = [new MyriadCopies()],
        });

        return true;
    }

    private static bool TryStartYourEngines(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!StartYourEnginesLine().IsMatch(line))
            return false;

        var isSpell = card.CardTypes.HasFlag(CardType.Instant)
            || card.CardTypes.HasFlag(CardType.Sorcery);

        var when = TriggerConditions.Parse(isSpell ? "you cast ~" : "~ enters");
        if (when is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "start-your-engines",
            Text = $"When you {(isSpell ? "cast" : "play")} {card.Name}, if you have no speed, "
                + "your speed becomes 1.",
            FunctionsFrom = isSpell ? Zone.Stack : Zone.Battlefield,
            Triggers = when,
            Effects = [new StartYourEngines()],
        });

        return true;
    }

    /// <summary>
    /// "For Mirrodin!" - the Equipment brings its own wielder (CR 702.161a).
    /// </summary>
    /// <remarks>
    /// Living weapon with a better token: a 2/2 red Rebel rather than a 0/0 Germ, which is the
    /// whole difference between an Equipment that needs help and one that does not.
    /// </remarks>
    private static bool TryForMirrodin(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!ForMirrodinLine().IsMatch(line))
            return false;

        var entered = TriggerConditions.Parse("~ enters");
        if (entered is null)
            return false;

        var rebel = new CardDefinition
        {
            OracleId = "token-red-rebel",
            Name = "Rebel",
            CardTypes = CardType.Creature | CardType.Token,
            Subtypes = ["Rebel"],
            Power = 2,
            Toughness = 2,
            ColorIdentity = [ManaColor.Red],
            Colors = [ManaColor.Red],
        };

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "for-mirrodin",
            Text = $"When {card.Name} enters, create a 2/2 red Rebel creature token, then attach "
                + $"{card.Name} to it.",
            Triggers = entered,
            Effects = [new CreateTokenAndAttach(rebel)],
        });

        return true;
    }

    private static bool TryLivingWeapon(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!LivingWeaponLine().IsMatch(line))
            return false;

        var entered = TriggerConditions.Parse("~ enters");
        if (entered is null)
            return false;

        var germ = new CardDefinition
        {
            OracleId = "token-phyrexian-germ",
            Name = "Germ",
            CardTypes = CardType.Creature | CardType.Token,
            Subtypes = ["Phyrexian", "Germ"],
            Power = 0,
            Toughness = 0,
            ColorIdentity = [ManaColor.Black],
            Colors = [ManaColor.Black],
        };

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "living-weapon",
            Text = $"When {card.Name} enters, create a 0/0 black Phyrexian Germ creature token, "
                + $"then attach {card.Name} to it.",
            Triggers = entered,
            Effects = [new CreateTokenAndAttach(germ)],
        });

        return true;
    }

    /// <summary>
    /// "Fading N" — it arrives with N counters and goes when they run out (CR 702.32a).
    /// </summary>
    /// <remarks>
    /// Two triggers with opposite conditions rather than one with an "if you can't". The rules
    /// wording is a single ability that removes a counter or, failing that, sacrifices — but the
    /// two halves never both apply, so a pair of intervening-ifs says the same thing with
    /// machinery the engine already has (CR 603.4).
    /// <para>
    /// The consequence of that split is worth knowing: they are two abilities on the stack rather
    /// than one, and an opponent could in principle respond between them. Nothing in the corpus
    /// cares, and the alternative was an effect that can branch on a count.
    /// </para>
    /// </remarks>
    private static bool TryFading(
        string line,
        CardDefinition card,
        ImmutableList<TriggeredAbilityDefinition>.Builder triggers,
        ImmutableList<ReplacementEffectDefinition>.Builder replacements)
    {
        var m = FadingLine().Match(line);
        if (!m.Success)
            return false;

        var count = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        const string Fade = "fade";

        var atUpkeep = TriggerConditions.Parse("the beginning of your upkeep");
        if (atUpkeep is null)
            return false;

        replacements.Add(new ReplacementEffectDefinition
        {
            Id = "fading",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null,
            Replace = (e, _, source) =>
            {
                var arrived = Arriving(e, source)!.Value;
                return [e, new CountersChanged(arrived, Fade, count)];
            },
        });

        static int FadeCounters(GameObject source) =>
            source.Permanent?.Counters.GetValueOrDefault("fade", 0) ?? 0;

        triggers.Add(new TriggeredAbilityDefinition
        {
            Id = "fading-remove",
            Text = $"At the beginning of your upkeep, remove a fade counter from {card.Name}.",
            Triggers = (e, state, source) => atUpkeep(e, state, source) && FadeCounters(source) > 0,
            Effects = [new PutCountersOnSource(Fade, -1)],
        });

        triggers.Add(new TriggeredAbilityDefinition
        {
            Id = "fading-sacrifice",
            Text = $"At the beginning of your upkeep, if you can't remove a fade counter from "
                + $"{card.Name}, sacrifice it.",
            Triggers = (e, state, source) => atUpkeep(e, state, source) && FadeCounters(source) == 0,
            Effects = [new SacrificeSource()],
        });

        return true;
    }

    /// <summary>The counter that remembers a permanent still owes its echo (CR 702.29a).</summary>
    /// <remarks>
    /// Echo asks whether the permanent came under your control since your last upkeep, and the
    /// engine keeps no such fact. Rather than add one, the permanent arrives carrying a counter
    /// and the first upkeep that sees it takes it off — which is the same question asked from the
    /// other end, and costs no new state at all: counters are keyed by name and the engine has
    /// never cared which names exist.
    /// <para>
    /// It is invisible in every way that matters — nothing counts counters of an unknown kind,
    /// and no card refers to one by this name. The one place the model differs from the rules is
    /// a permanent that changes controller: real echo is owed again by its new controller, and
    /// this counter has already been spent. That is a narrower wrong answer than not reading the
    /// keyword at all, and it is written here so the next reader knows it was a choice.
    /// </para>
    /// </remarks>
    private const string EchoCounter = "echo";

    /// <summary>
    /// "~ escapes with N +1/+1 counters on it" (CR 702.139b).
    /// </summary>
    /// <remarks>
    /// Not the same as entering with them: the counters arrive only when the creature got here
    /// by escaping, and the spell that knew that is a different object by the time the permanent
    /// exists (CR 400.7). The link the move left behind is what carries the answer across.
    /// </remarks>
    private static bool TryEscapesWithCounters(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = EscapesWithCountersLine().Match(line);
        if (!m.Success)
            return false;

        var many = NumberWordOrDigits(m.Groups["n"].Value);
        if (many <= 0)
            return false;

        into.Add(new ReplacementEffectDefinition
        {
            Id = "escapes-with-counters",
            FunctionsFrom = null,
            // The source here is the object that is moving - the spell on the stack - which is
            // exactly the one the cast marked. Reaching for the permanent it is about to become
            // finds nothing: the replacement runs before the move, so that object does not exist
            // yet.
            Applies = (e, _, source) => Arriving(e, source) is not null && source.WasEscaped,
            Replace = (e, _, source) =>
            {
                var arrived = Arriving(e, source)!.Value;
                return [e, new CountersChanged(arrived, State.CounterKinds.PlusOnePlusOne, many)];
            },
        });

        return true;
    }

    /// <summary>
    /// "Echo [cost]" — pay again on your next upkeep or lose it (CR 702.29a).
    /// </summary>
    private static bool TryEcho(
        string line,
        CardDefinition card,
        ImmutableList<TriggeredAbilityDefinition>.Builder triggers,
        ImmutableList<ReplacementEffectDefinition>.Builder replacements)
    {
        var m = EchoLine().Match(line);
        if (!m.Success)
            return false;

        var cost = ManaCostSpec.Parse(m.Groups["cost"].Value);

        replacements.Add(new ReplacementEffectDefinition
        {
            Id = "echo-owed",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null,
            Replace = (e, _, source) =>
            {
                var arrived = Arriving(e, source)!.Value;
                return [e, new CountersChanged(arrived, EchoCounter, 1)];
            },
        });

        var atUpkeep = TriggerConditions.Parse("the beginning of your upkeep");
        if (atUpkeep is null)
            return false;

        triggers.Add(new TriggeredAbilityDefinition
        {
            Id = "echo",
            Text = $"At the beginning of your upkeep, if {card.Name} came under your control "
                + $"since the beginning of your last upkeep, sacrifice it unless you pay "
                + $"{m.Groups["cost"].Value}.",

            // CR 603.4: the intervening if. It is asked of the counter, which is only there for a
            // permanent that has not yet seen one of its controller's upkeeps.
            Triggers = (e, state, source) =>
                atUpkeep(e, state, source)
                && source.Permanent?.Counters.GetValueOrDefault(EchoCounter, 0) > 0,

            Effects =
            [
                // Taken off first, so the payment cannot be asked twice for one arrival even if
                // something responds to the trigger.
                new PutCountersOnSource(EchoCounter, -1),
                new MayPay(cost, IfYouDo: [], IfYouDont: [new SacrificeSource()], EffectIndex: 1),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Bloodthirst N" — enters bigger if an opponent has bled this turn (CR 702.54a).
    /// </summary>
    /// <remarks>
    /// A replacement effect rather than a trigger, which is the whole of the mechanic: the
    /// creature <em>enters</em> with the counters, so it was never a smaller creature that grew.
    /// Anything watching for a creature entering sees the larger one, and it is never briefly on
    /// the battlefield at its printed size for a state-based action to kill.
    /// </remarks>
    private static bool TryBloodthirst(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = BloodthirstLine().Match(line);
        if (!m.Success)
            return false;

        if (BoardConditions.Parse("an opponent was dealt damage this turn") is not { } bloodied)
            return false;

        var count = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new ReplacementEffectDefinition
        {
            Id = "bloodthirst",
            FunctionsFrom = null,
            Applies = (e, state, source) =>
                Arriving(e, source) is not null
                && bloodied(state, EmptyAbilities.Instance, source),
            Replace = (e, _, source) =>
            {
                var arrived = Arriving(e, source)!.Value;
                return
                [
                    e,
                    new CountersChanged(arrived, CounterKinds.PlusOnePlusOne, count),
                ];
            },
        });

        return true;
    }

    /// <summary>
    /// "I &#8212; [effect]", "II, III &#8212; [effect]" &#8212; a Saga's chapter ability (CR 714.2b).
    /// </summary>
    /// <remarks>
    /// A chapter symbol is a keyword ability standing for a triggered one: "{rN}&#8212;[effect]"
    /// means "when one or more lore counters are put onto this Saga, if the number of lore
    /// counters on it was less than N and became at least N, [effect]".
    /// <para>
    /// <strong>The crossing is the trigger, not the count.</strong> Reading it as "there are now
    /// N counters" would fire chapter II again every time anything else put a lore counter on
    /// the Saga, and would fire nothing at all if two counters arrived at once - which is
    /// exactly what a second Saga-advancing effect does. Both halves of 714.2b can be asked
    /// because the event carries its own delta.
    /// </para>
    /// <para>
    /// A trigger predicate is handed the state as it was <em>before</em> the event, so the count
    /// it reads is the "was less than N" half and the delta supplies the "became at least N"
    /// half. Written the other way round - reading the count as the total after - every chapter
    /// fired exactly one advance late, and chapter I never fired at all, because the Saga
    /// entering with its first counter found a count of zero and concluded it had not got there.
    /// </para>
    /// <para>
    /// One printed line can be two abilities (CR 714.2c): "II, III &#8212; [effect]" is chapter II
    /// and chapter III, each with the same effect, and it has to be two so that the Saga fires
    /// it twice.
    /// </para>
    /// <para>
    /// Sagas were invisible to the work queue, and worth finding for that reason as much as for
    /// the cards. The queue ranks templates by how many cards each would <em>finish</em>, and a
    /// Saga has three or four unread chapters at once - so no single chapter line was ever any
    /// card's sole blocker, and 240 cards sat below a queue whose head was worth fourteen.
    /// </para>
    /// </remarks>
    private static bool TrySagaChapter(
        string line,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var m = SagaChapterLine().Match(line);
        if (!m.Success)
            return false;

        var chapters = new List<int>();
        foreach (var numeral in m.Groups["chapters"].Value.Split(','))
        {
            var n = RomanNumeral(numeral.Trim());
            if (n is null)
            {
                unhandled.Add(line);
                return true;
            }

            chapters.Add(n.Value);
        }

        // "I - Gungnir - Destroy target creature an opponent controls." The middle word is a
        // flavour word: the same typographic convention as an ability word, and with the same
        // amount of rules meaning, which is none (CR 207.2c). Stripped here rather than in the
        // shared cleaner because only a chapter line carries one in this position - the cleaner
        // only ever looks at the start of a line, and by then the chapter symbol is there.
        var effectText = ChapterFlavourWord()
            .Replace(m.Groups["effect"].Value.Trim(), string.Empty)
            .Trim();

        if (!EffectPhrase.TryParse(effectText, out var parsed))
        {
            unhandled.Add(line);
            return true;
        }

        foreach (var chapter in chapters)
        {
            var at = chapter;

            into.Add(new TriggeredAbilityDefinition
            {
                Id = "chapter" + at.ToString(CultureInfo.InvariantCulture),
                Text = line,
                Chapter = at,
                Targets = parsed.Targets,
                Effects = parsed.Effects,
                Triggers = (e, state, source) =>
                    e is CountersChanged counters
                    && counters.Id == source.Id
                    && counters.Kind == CounterKinds.Lore
                    && counters.Delta > 0
                    && state.TryGetObject(source.Id, out var saga)
                    && saga.Permanent is { } permanent
                    && permanent.Counters.GetValueOrDefault(CounterKinds.Lore) is var before
                    && before < at
                    && before + counters.Delta >= at,
            });
        }

        return true;
    }

    /// <summary>Whether a word names a card type or supertype rather than a subtype.</summary>
    private static bool IsTypeWord(string word) => word.ToLowerInvariant() switch
    {
        "artifact" or "creature" or "enchantment" or "land" or "planeswalker" or "instant"
            or "sorcery" or "battle" or "basic" or "legendary" or "snow" or "world" => true,
        _ => false,
    };

    /// <summary>The Roman numeral in a chapter symbol, or null if it is not one (CR 714.2a).</summary>
    /// <remarks>
    /// Deliberately not a general Roman numeral reader. Chapter symbols run from I to about V on
    /// printed cards, and a permissive parser here would take the "I" out of an ordinary
    /// sentence and turn a line of rules text into a chapter that never fires.
    /// </remarks>
    private static int? RomanNumeral(string numeral) => numeral switch
    {
        "I" => 1,
        "II" => 2,
        "III" => 3,
        "IV" => 4,
        "V" => 5,
        "VI" => 6,
        _ => null,
    };

    /// <summary>How many things a printed group names, or null if the group cannot be read.</summary>
    /// <remarks>
    /// Both halves of the vocabulary in one place: a group on the battlefield goes through the
    /// target grammar, and a group in a zone goes through the card-type reader, because a card in
    /// a graveyard is not a permanent and has no computed characteristics to ask (CR 109.3).
    /// </remarks>
    private static Func<GameState, Guid, int>? CountOf(string group)
    {
        if (ZoneCountLine().Match(group) is { Success: true } zoned
            && CardKindNamed(zoned.Groups["what"].Value.Trim()) is { } kind)
        {
            var inGraveyard = zoned.Groups["zone"].Value.Equals(
                "graveyard", StringComparison.OrdinalIgnoreCase);
            var everyone = zoned.Groups["whose"].Value.Equals(
                "each", StringComparison.OrdinalIgnoreCase);

            return (state, you) =>
            {
                var many = 0;
                foreach (var playerId in state.TurnOrder)
                {
                    if (!everyone && playerId != you)
                        continue;

                    var player = state.GetPlayer(playerId);
                    var cards = inGraveyard ? player.Graveyard : player.Hand;

                    many += cards.Count(id =>
                        kind == Domain.Enums.CardType.None
                        || (state.GetObject(id).Card.CardTypes & kind) != 0);
                }

                return many;
            };
        }

        if (EffectPhrase.Specs.ParseGroup(group) is
            { Kind: Abilities.TargetKind.Permanent } spec)
        {
            return (state, you) => state.Battlefield.Count(
                id => spec.ObjectFilter?.Invoke(
                    state, EmptyAbilities.Instance, state.GetObject(id), you) != false);
        }

        return null;
    }

    /// <summary>
    /// "~ enters with a +1/+1 counter on it for each [group]" (CR 614.1c).
    /// </summary>
    /// <remarks>
    /// The fixed-number and conditional forms were read already; this is the third, where the
    /// number is a count taken as the permanent enters. A group the reader cannot count leaves
    /// the line unread rather than entering with none - a creature that is quietly smaller than
    /// the card says is as wrong as one that is quietly bigger, and neither announces itself.
    /// </remarks>
    private static bool TryEntersWithCountersPerGroup(
        string line, CardDefinition card, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = EntersWithCountersPerGroupLine().Match(line);
        if (!m.Success)
            return false;

        var each = NumberWordOrDigits(m.Groups["n"].Value);
        if (each <= 0)
            return false;

        if (CountOf(m.Groups["group"].Value.Trim()) is not { } counted)
            return false;

        var kind = m.Groups["kind"].Value is "+1/+1" or "-1/-1"
            ? m.Groups["kind"].Value
            : m.Groups["kind"].Value.ToLowerInvariant();

        into.Add(new ReplacementEffectDefinition
        {
            Id = $"enters-counters-per:{card.Name}:{m.Groups["group"].Value.Trim()}",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null,
            Replace = (e, state, source) =>
            {
                var arrived = Arriving(e, source)!.Value;
                var many = each * counted(state, source.ControllerId);

                return many <= 0
                    ? [e]
                    : [e, new Events.CountersChanged(arrived, kind, many)];
            },
        });

        return true;
    }

    private static bool TryEntersWithCounters(
        string line, CardDefinition card, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = EntersWithCountersLine().Match(line);
        if (!m.Success)
            return false;

        // "Enters with X +1/+1 counters" — X was chosen as the spell was cast (CR 601.2b) and
        // rides on the object that is moving, so the number is read when the replacement runs
        // rather than fixed here. A fixed zero would make every one of these a vanilla creature.
        var variable = string.Equals(m.Groups["n"].Value, "X", StringComparison.OrdinalIgnoreCase);
        var count = variable ? 0 : NumberWordOrDigits(m.Groups["n"].Value);

        // Modular and graft name no counter because theirs is always +1/+1; a storage land says
        // which kind it arrives with.
        var kind = m.Groups["kind"].Success && m.Groups["kind"].Value is not ("+1/+1" or "-1/-1")
            ? m.Groups["kind"].Value.ToLowerInvariant()
            : CounterKinds.PlusOnePlusOne;

        var tapped = m.Value.Contains(" tapped ", StringComparison.OrdinalIgnoreCase);

        // "...on it if a creature died this turn" is the same replacement with a question in
        // front of it. A condition the board reader cannot parse leaves the whole line unread
        // rather than dropping the "if": a creature that always entered with the counters would
        // be strictly better than the one printed, and nothing would say so.
        Func<GameState, IAbilitySource, GameObject, bool>? when = null;
        if (m.Groups["when"].Success)
        {
            when = BoardConditions.Parse(m.Groups["when"].Value.Trim());
            if (when is null)
                return false;
        }

        into.Add(new ReplacementEffectDefinition
        {
            Id = "enters-with-counters",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null,
            Replace = (e, state, source) =>
            {
                var arrived = Arriving(e, source)!.Value;

                // Asked as it enters, which is when the replacement runs (CR 614.1c).
                if (when is not null && !when(state, EmptyAbilities.Instance, source))
                    return tapped ? [e, new PermanentTapped(arrived)] : [e];

                // X rides on the object that is casting, which only a move has: a token was
                // never cast and has no X to read, so it arrives with the fixed number or none.
                var many = variable
                    ? e is ObjectMoved move && state.TryGetObject(move.OldId, out var cast)
                        ? cast.VariableValue
                        : 0
                    : count;

                if (many <= 0)
                    return tapped ? [e, new PermanentTapped(arrived)] : [e];

                return tapped
                    ?
                    [
                        e,
                        new PermanentTapped(arrived),
                        new CountersChanged(arrived, kind, many),
                    ]
                    : [e, new CountersChanged(arrived, kind, many)];
            },
        });

        // Recorded rather than silently accepted: the card also says something this does not do.
        _ = card;
        return true;
    }

    /// <summary>"Renown N" — a combat-damage trigger printed as a word (CR 702.111a).</summary>
    /// <remarks>
    /// The printed ability only becomes renowned once; the engine puts the counters on every
    /// connection, which is the one thing about it not yet modelled — recorded here rather than
    /// hidden, because it makes a renowned creature grow more than it should.
    /// </remarks>
    private static bool TryRenown(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = RenownLine().Match(line);
        if (!m.Success)
            return false;

        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var predicate = TriggerConditions.Parse("~ deals combat damage to a player");
        if (predicate is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "renown",
            Text = $"Whenever this creature deals combat damage to a player, if it isn't "
                + $"renowned, put {n} +1/+1 counter(s) on it and it becomes renowned.",

            // CR 702.112a: "if it isn't renowned". The condition was missing from the text as
            // well as from the behaviour, so the card grew every time it connected and nothing
            // on the compiled ability said otherwise. Renowned is a designation (CR 702.112b),
            // not a counter, and a creature keeps it for as long as it stays on the battlefield.
            //
            // Checked in the predicate, where the permanent is still the one that dealt the
            // damage, and set as an effect so the two happen in the order the rule gives them.
            Triggers = (e, state, source) =>
                predicate(e, state, source)
                && state.TryGetObject(source.Id, out var hero)
                && hero.Permanent is { IsRenowned: false },

            Effects =
            [
                new PutCountersOnSource(CounterKinds.PlusOnePlusOne, n),
                new BecomeRenowned(),
            ],
        });

        return true;
    }

    /// <summary>"Ascend" — one word meaning two different abilities (CR 702.131a, 702.131b).</summary>
    /// <remarks>
    /// The same word compiles to different things depending on what it is written on, and the rule
    /// says so outright: on an instant or sorcery it is a <em>spell ability</em>, resolving once
    /// with the rest of the spell; on a permanent it is a <em>static ability</em> that applies
    /// "any time" the condition holds. Reading it as one thing would leave half the cards wrong,
    /// and the half that was wrong would be silent - an enchantment that only checked as it
    /// entered would never notice the tenth permanent arriving afterwards.
    /// <para>
    /// The permanent form is a state trigger (CR 603.8): it answers to no event, and is checked
    /// whenever state-based actions are. That is exactly what "any time" asks for, and it is why
    /// the effect declines when the blessing is already held - the condition it watches stays true
    /// forever once ten permanents are out, so something has to stop it firing again.
    /// </para>
    /// </remarks>
    private static bool TryAscend(
        string line,
        CardDefinition card,
        ImmutableList<IEffect>.Builder spellEffects,
        ImmutableList<TriggeredAbilityDefinition>.Builder triggers)
    {
        if (!AscendLine().IsMatch(line))
            return false;

        var isSpell = card.CardTypes.HasFlag(Domain.Enums.CardType.Instant)
            || card.CardTypes.HasFlag(Domain.Enums.CardType.Sorcery);

        if (isSpell)
        {
            spellEffects.Add(new GainCitysBlessing());
            return true;
        }

        triggers.Add(new TriggeredAbilityDefinition
        {
            Id = "ascend",
            Text = "Any time you control ten or more permanents and you don't have the city's "
                + "blessing, you get the city's blessing for the rest of the game.",
            StateCondition = static (state, _, source) => state.Battlefield.Count(
                id => state.TryGetObject(id, out var permanent)
                    && permanent.ControllerId == source.ControllerId) >= 10,
            Triggers = static (_, _, _) => false,
            Effects = [new GainCitysBlessing()],
        });

        return true;
    }

    /// <summary>"Increment" — a trigger printed as one word (CR 702.191a).</summary>
    /// <remarks>
    /// The rule carries a clause the printed reminder text leaves out: "<em>if this permanent is
    /// a creature</em> and the amount of mana spent...". A permanent with increment that has
    /// stopped being a creature does not trigger, and reading only the card would have missed it.
    /// <para>
    /// "Greater than this creature's power <em>or</em> toughness" is two comparisons and not one:
    /// a 4/1 increments off two mana because two is greater than its toughness, even though it is
    /// nowhere near its power. Both are the computed values, so a creature pumped in response is
    /// measured at the size it is when the trigger looks.
    /// </para>
    /// <para>
    /// The mana spent is read off the spell rather than counted from its cost, because they are
    /// different numbers - a cost reduction, an alternative cost or a convoked permanent all make
    /// the mana actually spent smaller than the mana printed.
    /// </para>
    /// </remarks>
    private static bool TryIncrement(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!IncrementLine().IsMatch(line))
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "increment",
            Text = $"Whenever you cast a spell, if {card.Name} is a creature and the amount of "
                + "mana spent to cast that spell is greater than its power or its toughness, "
                + $"put a +1/+1 counter on {card.Name}.",
            Triggers = static (e, state, source) =>
            {
                if (e is not Events.SpellCastEvent cast
                    || cast.PlayerId != source.ControllerId)
                {
                    return false;
                }

                var mine = State.Characteristics.Of(state, EmptyAbilities.Instance, source);
                if (!mine.IsCreature)
                    return false;

                if (!state.TryGetObject(cast.StackId, out var spell))
                    return false;

                var spent = spell.ManaSpent.Total;

                return spent > (mine.Power ?? 0) || spent > (mine.Toughness ?? 0);
            },
            Effects = [new PutCountersOnSource(State.CounterKinds.PlusOnePlusOne, 1)],
        });

        return true;
    }

    /// <summary>"Melee" — a trigger printed as one word (CR 702.121a).</summary>
    /// <remarks>
    /// The printed reminder text says "for each opponent you attacked this combat"; the rule says
    /// "attacked <em>with a creature</em>". They agree, because only creatures attack, and the
    /// rule is what the ability text repeats so that the two cannot drift.
    /// </remarks>
    private static bool TryMelee(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!MeleeLine().IsMatch(line))
            return false;

        var attacks = TriggerConditions.Parse("~ attacks");
        if (attacks is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "melee",
            Text = $"Whenever {card.Name} attacks, it gets +1/+1 until end of turn for each "
                + "opponent you attacked with a creature this combat.",
            Triggers = attacks,
            Effects = [new PumpSourceUntilEndOfTurn(GenerativeEffects.MeleePumpId(1, 1))],
        });

        return true;
    }

    /// <summary>"Mobilize N" — a trigger printed as one word and a number (CR 702.180a).</summary>
    /// <remarks>
    /// The Warriors attack whoever this creature is attacking, which is read off the combat when
    /// the trigger resolves rather than decided here: the same printed card attacks a different
    /// player every game, and a planeswalker in some of them.
    /// </remarks>
    private static bool TryMobilize(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = MobilizeLine().Match(line);
        if (!m.Success)
            return false;

        var attacks = TriggerConditions.Parse("~ attacks");
        if (attacks is null)
            return false;

        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "mobilize",
            Text = $"Whenever {card.Name} attacks, create {n} tapped and attacking 1/1 red "
                + "Warrior creature tokens. Sacrifice them at the beginning of the next end step.",
            Triggers = attacks,
            Effects = [new MobilizeTokens(n)],
        });

        return true;
    }

    /// <summary>"Outlast {cost}" — an activated ability printed as one word (CR 702.104a).</summary>
    /// <remarks>
    /// Expanded into the sentence the reminder text spells out and handed to the ordinary
    /// activated-ability reader, the way overload hands its rewritten spell to the phrase parser.
    /// Nothing about outlast is new - a cost, a tap, a counter and sorcery timing are all read
    /// already - so writing the ability by hand would be a second copy of that reader, free to
    /// drift from it. What the keyword contributes is the expansion, and that is all it does.
    /// <para>
    /// The expansion fails loudly rather than quietly: if the reader cannot take the sentence,
    /// this returns false and the printed line stays unread, instead of adding an ability that
    /// costs the wrong thing.
    /// </para>
    /// </remarks>
    /// <summary>"Reinforce N—[cost]" — discard it to grow something (CR 702.76a).</summary>
    /// <remarks>
    /// Outlast's shape with a different body: nothing in it is new, so it is expanded into the
    /// sentence the reminder text spells out and handed to the ordinary activated-ability reader.
    /// The pieces - a mana cost, discarding this card as a cost, an ability that works from the
    /// hand, counters on a target - are the same ones cycling and the counter effects already
    /// use, and writing them out here would be a second copy free to drift.
    /// <para>
    /// Like outlast, it fails loudly: a sentence the reader will not take leaves the printed line
    /// unread rather than adding an ability that costs or does the wrong thing.
    /// </para>
    /// </remarks>
    private static bool TryReinforce(
        string line,
        CardDefinition card,
        ImmutableList<ActivatedAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var m = ReinforceLine().Match(line);
        if (!m.Success)
            return false;

        var many = NumberWordOrDigits(m.Groups["n"].Value);
        if (many <= 0)
            return false;

        var counters = many == 1 ? "a +1/+1 counter" : $"{many} +1/+1 counters";

        var rejected = ImmutableList.CreateBuilder<string>();
        var written = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();

        var expanded =
            $"{m.Groups["cost"].Value}, Discard ~: Put {counters} on target creature.";

        if (!TryActivatedAbility(expanded, card, written, rejected) || rejected.Count > 0)
            return false;

        into.AddRange(written);
        _ = unhandled;

        return true;
    }

    private static bool TryOutlast(
        string line,
        CardDefinition card,
        ImmutableList<ActivatedAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var m = OutlastLine().Match(line);
        if (!m.Success)
            return false;

        var rejected = ImmutableList.CreateBuilder<string>();
        var written = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();

        var expanded = $"{m.Groups["cost"].Value}, {{T}}: Put a +1/+1 counter on ~. "
            + "Activate only as a sorcery.";

        if (!TryActivatedAbility(expanded, card, written, rejected) || rejected.Count > 0)
            return false;

        into.AddRange(written);
        _ = unhandled;

        return true;
    }

    /// <summary>"Training" — a trigger printed as one word (CR 702.149a).</summary>
    /// <remarks>
    /// "Attacks with another creature with greater power" is one declaration, read once. The
    /// batch is a single event carrying every attacker (CR 508.1), so both halves of the question
    /// - that this creature is in it, and that somebody else in it is bigger - are answered from
    /// the same event rather than from the board, which by the time a trigger resolves may have
    /// changed. Power is the computed one and not the printed one: a creature pumped in the
    /// declare-attackers step counts at the size it is.
    /// </remarks>
    private static bool TryTraining(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        if (!TrainingLine().IsMatch(line))
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "training",
            Text = $"Whenever {card.Name} attacks with another creature with greater power, "
                + $"put a +1/+1 counter on {card.Name}.",
            Triggers = static (e, state, source) =>
            {
                if (e is not Events.AttackersDeclared batch
                    || !batch.Attackers.ContainsKey(source.Id))
                {
                    return false;
                }

                // CR 702.149a says "power", not printed power, and the trigger carries the
                // ability source that makes the difference. Computed with an empty one, an
                // anthem is invisible - and an anthem is the ordinary reason one 2/2 is bigger
                // than another. The comparison was right until the first lord.
                var mine = source.Now(state).Power;

                return batch.Attackers.Keys.Any(
                    other => other != source.Id
                        && state.TryGetObject(other, out var fellow)
                        && State.Characteristics.Of(state, source.Abilities, fellow).Power
                            > mine);
            },
            Effects = [new PutCountersOnSource(State.CounterKinds.PlusOnePlusOne, 1)],
        });

        return true;
    }

    /// <summary>"Annihilator N" — a trigger printed as one word and a number (CR 702.86a).</summary>
    /// <remarks>
    /// N separate sacrifices rather than one worth N, because the defending player chooses each
    /// permanent in turn and what they lose first changes what is left to lose second. Each
    /// carries its own effect index, which is how the engine reads a completed choice back to the
    /// effect that asked it - N effects sharing an index would be ambiguous and answer neither.
    /// <para>
    /// "Of their choice" is the whole difficulty and costs nothing here: the scope names the
    /// defending player, so the request is addressed to them and they pick from their own board.
    /// An attacker choosing would be a different and much stronger card.
    /// </para>
    /// </remarks>
    private static bool TryAnnihilator(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = AnnihilatorLine().Match(line);
        if (!m.Success)
            return false;

        var attacks = TriggerConditions.Parse("~ attacks");
        if (attacks is null)
            return false;

        if (EffectPhrase.Specs.Parse("target permanent") is not
            { Kind: Abilities.TargetKind.Permanent } anything)
        {
            return false;
        }

        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "annihilator",
            Text = $"Whenever {card.Name} attacks, defending player sacrifices {n} "
                + "permanents of their choice.",
            Triggers = attacks,
            Effects =
            [
                .. Enumerable.Range(0, n).Select(index => new ChooseAndMove(
                    anything with { Description = "a permanent to sacrifice" },
                    Zone.Graveyard,
                    MoveCause.Sacrifice,
                    index,
                    PlayerScope.DefendingPlayer)),
            ],
        });

        return true;
    }

    /// <summary>"Bushido N" — a trigger printed as one word and a number (CR 702.46a).</summary>
    private static bool TryBushido(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = BushidoLine().Match(line);
        if (!m.Success)
            return false;

        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var predicate = TriggerConditions.Parse("~ blocks or becomes blocked");
        if (predicate is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "bushido",
            Text = $"Whenever this creature blocks or becomes blocked, it gets +{n}/+{n} until end of turn.",
            Triggers = predicate,
            Effects = [new PumpSourceUntilEndOfTurn(GenerativeEffects.PumpId(n, n))],
        });

        return true;
    }

    /// <summary>"Cycling {cost}" — discard this card to draw one (CR 702.29a).</summary>
    /// <remarks>
    /// An activated ability that works from the hand rather than the battlefield, which the
    /// engine already allows for: an ability declares where it functions from, and cycling is
    /// the commonest card in Magic that is played without ever being cast.
    /// </remarks>
    private static bool TryCycling(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = CyclingLine().Match(line);
        if (!m.Success)
            return false;

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "cycling",
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            SelfCost = SelfCost.DiscardSelf,
            FunctionsFrom = Zone.Hand,
            Effects = [new DrawCards(1)],
        });

        return true;
    }

    /// <summary>
    /// "[Type]cycling [cost]" — discard this card to fetch one of that type (CR 702.29b-e).
    /// </summary>
    /// <remarks>
    /// Plain cycling with a search instead of a draw, and one template for the whole family:
    /// landcycling, basic landcycling, and every typecycling variant differ only in what the
    /// search is allowed to find. Enumerating them would have been six near-identical matchers
    /// that each go stale when a set prints a seventh.
    /// <para>
    /// The type name is handed to the same vocabulary every other search uses, so an unreadable
    /// one leaves the line unread rather than fetching something the card does not allow — a
    /// tutor that finds more than it should is a strictly better card, and nothing downstream
    /// would notice.
    /// </para>
    /// </remarks>
    private static bool TryTypecycling(
        string line,
        ImmutableList<ActivatedAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var m = TypecyclingLine().Match(line);
        if (!m.Success)
            return false;

        // "Search your library for a basic land card" and "for a Forest card" are sentences the
        // effect parser already reads, so the keyword is written out as one rather than having
        // its own filter lookup that could disagree with the parser's.
        // The capital is what tells a subtype from a card type, and the keyword's own spelling
        // capitalises whichever word comes first regardless of which it is. "Artifact
        // landcycling" therefore asked the search vocabulary for a card of the *subtype*
        // "Artifact", which no card in the game has - so the ability compiled, activated, and
        // searched a library that could never contain a match.
        //
        // Only the type words are lowered; a real subtype keeps its capital, so "Forestcycling"
        // still fetches a Forest and "Wizardcycling" a Wizard.
        var what = string.Join(
            ' ',
            m.Groups["what"].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => IsTypeWord(word) ? word.ToLowerInvariant() : word));

        var effect = $"search your library for a {what} card, put it into your hand";

        if (!EffectPhrase.TryParse(effect, out var parsed) || parsed.Effects.IsEmpty)
        {
            unhandled.Add(line);
            return true;
        }

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "typecycling",
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            SelfCost = SelfCost.DiscardSelf,
            FunctionsFrom = Zone.Hand,
            Effects = parsed.Effects,
        });

        return true;
    }

    /// <summary>
    /// "Enchant creature" — what an Aura spell targets, and what it will be attached to.
    /// </summary>
    /// <remarks>
    /// CR 303.4a: the enchant ability is a targeting restriction on the Aura spell itself, not an
    /// effect. So the line contributes a target and nothing else; the attaching happens when the
    /// spell resolves, because that is when the permanent exists to be attached.
    /// </remarks>
    private static bool TryEnchant(string line, ImmutableList<TargetSpec>.Builder targets)
    {
        var m = EnchantLine().Match(line);
        if (!m.Success)
            return false;

        // Through the target grammar rather than a switch that collapsed everything except
        // "creature" into "any permanent" - which meant an Aura reading "enchant land" would
        // go on a creature, and "enchant artifact or creature" was not read at all.
        var phrase = "target " + m.Groups["what"].Value.Trim() + m.Groups["own"].Value;

        // A player is a legal thing to enchant (CR 303.4a) and the target grammar calls that a
        // player rather than a permanent, so both kinds are accepted here - refusing one of them
        // left every "enchant player" Aura unread.
        if (EffectPhrase.Specs.Parse(phrase) is not
            { Kind: Abilities.TargetKind.Permanent or Abilities.TargetKind.Player } subject)
        {
            return false;
        }

        targets.Add(subject);
        return true;
    }

    /// <summary>
    /// "Enchanted creature gets +N/+N" — a continuous effect on whatever this is attached to.
    /// </summary>
    /// <remarks>
    /// CR 613.4c, layer 7c. It applies to the host rather than to the Aura, which is why it is
    /// keyed on the attachment rather than on the source: the effect belongs to the Aura and
    /// changes something else.
    /// </remarks>
    private static bool TryAttachedBuff(
        string line, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = AttachedBuffLine().Match(line);
        if (!m.Success)
            return false;

        var keywords = m.Groups["kw"].Success ? EffectPhrase.Keywords(m.Groups["kw"].Value) : null;

        // A keyword the engine cannot grant leaves the whole line unread: an Equipment that gave
        // the bonus but not the ability would look implemented and play as a weaker card.
        if (m.Groups["kw"].Success && keywords is null)
            return false;

        // "Can't attack or block" is a pair of restrictions the engine already models as
        // keywords: defender is exactly "can't attack" (CR 702.3b), and can't-block has its own
        // flag. Reading them here rather than in a separate matcher keeps one attachment rule.
        if (m.Groups["cant"].Success)
        {
            var stopped = m.Groups["cant"].Value.ToLowerInvariant() switch
            {
                "attack" => KeywordAbility.Defender,
                "block" => KeywordAbility.CantBlock,
                "be blocked" => KeywordAbility.CantBeBlocked,
                _ => KeywordAbility.Defender | KeywordAbility.CantBlock,
            };

            keywords = keywords is { } already ? already | stopped : stopped;
        }

        // "And attacks each combat if able" is a requirement rather than a restriction, and the
        // engine already models it as a flag (CR 508.1a). It sits beside the "can't" arm because
        // an Aura's tail may carry either and the sentence is the same shape.
        if (m.Groups["must"].Success)
        {
            keywords = keywords is { } already2
                ? already2 | KeywordAbility.MustAttack
                : KeywordAbility.MustAttack;
        }

        // The effect belongs to the Aura or Equipment and changes something else, so it is keyed
        // on what the source is attached to rather than on the source (CR 701.3c).
        static bool OnTheHost(GameState _, GameObject? source, CharacteristicsBuilder target) =>
            source?.Permanent?.AttachedTo is { } host && target.Subject.Id == host;

        // "And its activated abilities can't be activated" - the second half of a pacifism, and
        // the half that was never read: the keywords above could say "can't attack or block" and
        // had no way at all to say this, so forty-odd lines carrying both went unread for want
        // of the shorter clause.
        if (m.Groups["silenced"].Success)
        {
            into.Add(new ContinuousEffectDefinition
            {
                Id = "attached:abilities-off",
                Layer = EffectLayer.Ability,
                Applies = OnTheHost,
                Apply = (_, _, builder) => builder.AbilitiesCantBeActivated = true,
            });
        }

        if (m.Groups["p"].Success)
        {
            var power = int.Parse(
                m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var toughness = int.Parse(
                m.Groups["tough"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            into.Add(new ContinuousEffectDefinition
            {
                Id = "attached:" + GenerativeEffects.PumpId(power, toughness),
                Layer = EffectLayer.PowerToughnessModify,
                Applies = OnTheHost,
                Apply = (_, _, builder) => builder.Modify(power, toughness),
            });
        }

        if (keywords is { } granted)
        {
            into.Add(new ContinuousEffectDefinition
            {
                Id = "attached:" + GenerativeEffects.GrantId(granted),
                // Adding an ability is layer 6; changing power and toughness is 7c. One sentence,
                // two layers, so two effects (CR 613.1f, 613.4c).
                Layer = EffectLayer.Ability,
                Applies = OnTheHost,
                Apply = (_, _, builder) => builder.Keywords |= granted,
            });
        }

        return true;
    }

    /// <summary>
    /// "~ gets +N/+N as long as ...", "During your turn, ~ has first strike" (CR 604.3).
    /// </summary>
    /// <remarks>
    /// A static ability whose effect only applies while something is true. It is not a trigger and
    /// not a one-shot: the bonus appears and disappears as the condition changes, with nothing to
    /// remember it by — which is exactly what computing characteristics from scratch every time
    /// buys, and what the previous engine could not express at all.
    /// <para>
    /// The condition goes through <see cref="BoardConditions"/>, the same grammar the conditional
    /// lands use, so a condition learned for one is available to the other.
    /// </para>
    /// </remarks>
    /// <summary>
    /// "[This] can't be blocked by [creatures ...]" — an evasion restriction (CR 509.1b).
    /// </summary>
    /// <remarks>
    /// The filter half is read by the same target grammar everything else uses wherever it can
    /// be — "artifact creatures", "Walls", "blue creatures" are all phrases it already knows, so
    /// they arrive supported. Only the power comparisons need their own reading, because a target
    /// phrase never has to compare one permanent against another.
    /// <para>
    /// An unreadable filter leaves the line unread rather than restricting nothing. A creature
    /// that can be blocked by anything when the card says otherwise is strictly worse for its
    /// controller, and nothing downstream would notice it had gone wrong.
    /// </para>
    /// </remarks>
    /// <summary>
    /// "~ can't be blocked except by three or more creatures" — menace, with a bigger number.
    /// </summary>
    /// <remarks>
    /// Menace is this rule with a minimum of two and is left as its own keyword, because that is
    /// the word most cards print. What it cannot say is any other number, and the cards that name
    /// one are otherwise identical - so the count generalises it rather than replacing it, and
    /// the two are checked in the same place.
    /// </remarks>
    private static bool TryMinimumBlockers(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = MinimumBlockersLine().Match(line);
        if (!m.Success)
            return false;

        var fewest = NumberWordOrDigits(m.Groups["n"].Value);
        if (fewest < 2)
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"min-blockers:{card.Name}:{fewest}",
            Layer = EffectLayer.Ability,
            Applies = (_, source, target) => source is not null && target.Subject.Id == source.Id,
            Apply = (_, _, builder) => builder.MinBlockers = fewest,
        });

        return true;
    }

    /// <summary>
    /// "[This] can't be blocked except by [creatures ...]" — the same rule, said the other way.
    /// </summary>
    /// <remarks>
    /// A block restriction is an *allows* predicate, so "except by X" is the negation of "by X"
    /// and needs no new filter vocabulary: the phrase after "except by" goes through the same
    /// reader, and the answer is turned round. Sharing the reader is what keeps the two spellings
    /// from disagreeing about what "creatures with flying or reach" means.
    /// <para>
    /// An unreadable filter leaves the line unread, exactly as in the twin below. Here the
    /// failure would be the other way - a creature blockable by anything when the card says only
    /// fliers may - and it is no more visible for being the generous direction.
    /// </para>
    /// </remarks>
    private static bool TryCantBeBlockedExceptBy(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = CantBeBlockedExceptByLine().Match(line);
        if (!m.Success)
            return false;

        var what = m.Groups["what"].Value.Trim();
        if (ReadBlockRestriction(what) is not { } forbidden)
            return false;

        var who = m.Groups["who"].Value.Trim().ToLowerInvariant();
        var onSelf = who.Length == 0 || who == "~";

        if (!onSelf && who is not ("enchanted creature" or "equipped creature"))
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"only-block:{card.Name}:{what}",
            Layer = EffectLayer.Ability,
            Applies = (_, source, target) =>
                source is not null
                && (onSelf
                    ? target.Subject.Id == source.Id
                    : source.Permanent?.AttachedTo == target.Subject.Id),
            Apply = (_, _, builder) => builder.BlockRestrictions.Add(
                (state, abilities, attacker, blocker) =>
                    !forbidden(state, abilities, attacker, blocker)),
        });

        return true;
    }

    private static bool TryCantBeBlockedBy(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = CantBeBlockedByLine().Match(line);
        if (!m.Success)
            return false;

        if (ReadBlockRestriction(m.Groups["what"].Value.Trim()) is not { } restriction)
            return false;

        var who = m.Groups["who"].Value.Trim().ToLowerInvariant();
        var onSelf = who.Length == 0 || who == "~";

        if (!onSelf && who is not ("enchanted creature" or "equipped creature"))
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"no-block:{card.Name}:{m.Groups["what"].Value.Trim()}",
            Layer = EffectLayer.Ability,
            Applies = (_, source, target) =>
                source is not null
                && (onSelf
                    ? target.Subject.Id == source.Id
                    : source.Permanent?.AttachedTo == target.Subject.Id),
            Apply = (_, _, builder) => builder.BlockRestrictions.Add(restriction),
        });

        return true;
    }

    /// <summary>
    /// "All creatures able to block ~ do so" - a lure (CR 509.1c).
    /// </summary>
    /// <remarks>
    /// The first block requirement the compiler produces, and the reason the block check had to
    /// learn the difference between "may not" and "must": everything before this could only
    /// forbid a block, and this one compels one.
    /// <para>
    /// Only "all creatures" is read. The narrower printings - all Walls, all creatures with
    /// flying - would need the requirement to carry a filter, and reading them as the unfiltered
    /// version would compel blocks the card never asked for, which is worse than leaving the
    /// line unread. The "this turn" wordings are one-shots and are left alone for the same
    /// reason the additional-block ones are.
    /// </para>
    /// </remarks>
    private static bool TryMustBeBlocked(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = MustBeBlockedLine().Match(line);
        if (!m.Success)
            return false;

        // "All creatures able to block ~ do so" compels every creature that can; "~ must be
        // blocked if able" compels one. Two different requirements (CR 509.1c), told apart here
        // and checked apart in combat, because neither answer covers the other.
        var everyone = m.Groups["all"].Success;
        var who = m.Groups["who"].Value.Trim().ToLowerInvariant();
        var onSelf = who == "~";

        if (!onSelf && who is not ("enchanted creature" or "equipped creature"))
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"lure:{card.Name}",
            Layer = EffectLayer.Ability,
            Applies = (_, source, target) =>
                source is not null
                && (onSelf
                    ? target.Subject.Id == source.Id
                    : source.Permanent?.AttachedTo == target.Subject.Id),
            Apply = (_, _, builder) =>
            {
                if (everyone)
                    builder.MustBeBlockedByAll = true;
                else
                    builder.MustBeBlocked = true;
            },
        });

        return true;
    }

    /// <summary>
    /// "~ can block an additional creature each combat" and its relatives (CR 509.1a).
    /// </summary>
    /// <remarks>
    /// One reader for the whole family because the only thing that varies is the number: one
    /// more, several more, or "any number", which is the same characteristic with a number no
    /// board can reach rather than a separate unlimited flag.
    /// <para>
    /// Only the "each combat" wording is read here. The "this turn" forms are one-shot effects
    /// rather than static abilities, and would need a duration this static layer has no place to
    /// put - so they stay unread rather than being silently made permanent.
    /// </para>
    /// </remarks>
    private static bool TryExtraBlocks(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = ExtraBlocksLine().Match(line);
        if (!m.Success)
            return false;

        // "Any number" is a number no board can reach rather than a separate unlimited flag,
        // and small enough that the grants can still be added together without overflowing.
        var count = m.Groups["any"].Success
            ? 1_000_000
            : m.Groups["n"].Success
                ? NumberWordOrDigits(m.Groups["n"].Value)
                : 1;

        var who = m.Groups["who"].Value.Trim().ToLowerInvariant();
        var onSelf = who == "~";

        if (!onSelf && who is not ("enchanted creature" or "equipped creature"))
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"extra-blocks:{card.Name}:{count}",
            Layer = EffectLayer.Ability,
            Applies = (_, source, target) =>
                source is not null
                && (onSelf
                    ? target.Subject.Id == source.Id
                    : source.Permanent?.AttachedTo == target.Subject.Id),

            // Added rather than assigned: two of these on one creature let it block three, and
            // the rule says "an additional", which is an amount and not a state.
            Apply = (_, _, builder) => builder.ExtraBlocks += count,
        });

        return true;
    }

    /// <summary>
    /// "Creatures with power less than ~'s power can't block it" (CR 509.1b).
    /// </summary>
    /// <remarks>
    /// The same block restriction the "can't be blocked by" family produces, written the other
    /// way round - the creatures are the subject and this one is the object. What is new is that
    /// the test compares the two: it is the first restriction whose answer depends on the
    /// attacker as well as the blocker, and both powers are read computed so a pump on either
    /// side changes who may block.
    /// </remarks>
    private static bool TrySmallCreaturesCantBlock(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        if (!SmallCantBlockLine().IsMatch(line))
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"no-small-blockers:{card.Name}",
            Layer = EffectLayer.Ability,
            Applies = (_, source, target) => source is not null && target.Subject.Id == source.Id,
            Apply = (_, _, builder) => builder.BlockRestrictions.Add(
                (state, abilities, attacker, blocker) =>
                    State.Characteristics.Of(state, abilities, blocker).Power
                        >= State.Characteristics.Of(state, abilities, attacker).Power),
        });

        return true;
    }

    /// <summary>Which creatures a printed phrase forbids from blocking, or null if unread.</summary>
    private static BlockRestriction? ReadBlockRestriction(string phrase)
    {
        // "Creatures with greater power" — the only shape that compares the two creatures rather
        // than describing one of them, which is why it cannot go through the target grammar.
        if (GreaterPowerBlockers().IsMatch(phrase))
        {
            return (state, abilities, attacker, blocker) =>
                (Characteristics.Of(state, abilities, blocker).Power ?? 0)
                <= (Characteristics.Of(state, abilities, attacker).Power ?? 0);
        }

        var power = PowerBlockers().Match(phrase);
        if (power.Success)
        {
            var threshold = int.Parse(power.Groups["n"].Value, CultureInfo.InvariantCulture);
            var orLess = power.Groups["dir"].Value.StartsWith(
                "less", StringComparison.OrdinalIgnoreCase);

            return orLess
                ? (state, abilities, _, blocker) =>
                    (Characteristics.Of(state, abilities, blocker).Power ?? 0) > threshold
                : (state, abilities, _, blocker) =>
                    (Characteristics.Of(state, abilities, blocker).Power ?? 0) < threshold;
        }

        // Anything else is a description of one creature, which the target grammar reads. The
        // phrase is plural on the card and singular in the grammar, and that is the whole of the
        // translation.
        //
        // The plural is not always the last word. "Creatures with flying" carries its noun in
        // front of a qualifier, so stripping the final letter left "creatures with flying" for
        // the grammar to reject - and the grammar reads "target creature with flying" perfectly
        // well. Every "can't be blocked by creatures with <anything>" on every card fell in that
        // gap, in both spellings, and looked from the outside like a filter nobody had taught it.
        var head = phrase;
        var qualifier = string.Empty;
        var at = phrase.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
        if (at > 0)
        {
            head = phrase[..at];
            qualifier = phrase[at..];
        }

        var singular = (head.EndsWith('s') ? head[..^1] : head) + qualifier;

        if (EffectPhrase.Specs.Parse($"target {singular}") is not
            { Kind: Abilities.TargetKind.Permanent } spec)
        {
            return null;
        }

        // The filter is asked about the blocker with the blocker's own controller, because the
        // phrase describes a creature and not a creature belonging to anybody in particular.
        return (state, abilities, _, blocker) =>
            spec.ObjectFilter?.Invoke(state, abilities, blocker, blocker.ControllerId) != true;
    }

    /// <summary>
    /// "This permanent doesn't untap during your untap step" (CR 502.3).
    /// </summary>
    /// <remarks>
    /// A restriction, not an ability: there is no keyword flag for it and granting one would be
    /// the wrong shape, because the permanent it restricts is often not the one whose text says
    /// it. An Aura reading "enchanted creature doesn't untap during its controller's untap step"
    /// puts the restriction on something else entirely, and the layers are the only part of the
    /// engine that knows which permanent that is.
    /// <para>
    /// Whose untap step is not read, because it does not matter: a permanent untaps only in its
    /// own controller's untap step to begin with (CR 502.3), so "your" and "its controller's"
    /// name the same step from the two ends. Reading them as different would invent a case the
    /// rules do not have.
    /// </para>
    /// </remarks>
    private static bool TryDoesNotUntap(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = DoesNotUntapLine().Match(line);
        if (!m.Success)
            return false;

        // Lower-cased because the pattern is case-insensitive and the line starts a sentence:
        // the group comes back as "Enchanted creature", capital and all.
        var who = m.Groups["who"].Value.Trim().ToLowerInvariant();
        var onSelf = who.Length == 0 || who == "~";

        // The pattern has already checked that the subject is one this reads - repeating the
        // list here was a second copy of the same vocabulary, and it is what kept "enchanted
        // land doesn't untap" unread while "enchant land" on the line above was understood.
        //
        // And no type test on the target: the sentence names what this is attached to, and what
        // that is was settled when the Aura was cast. Asking again whether it is a creature is
        // how an Aura on a land came to hold nothing down.
        // "…if it has a depletion counter on it" is a static with a question in front of it, and
        // a static re-asks its question continuously - so unlike a "for as long as" duration this
        // belongs in Applies, where it stops applying and starts again as the counters come and
        // go. A condition that cannot be read leaves the line unread rather than holding the
        // permanent down unconditionally.
        Func<GameState, IAbilitySource, GameObject, bool>? when = null;
        if (m.Groups["when"].Success)
        {
            when = BoardConditions.Parse(m.Groups["when"].Value.Trim());
            if (when is null)
                return false;
        }

        bool Applies(GameState state, GameObject? source, CharacteristicsBuilder target) =>
            source is not null
            && (onSelf
                ? target.Subject.Id == source.Id
                : source.Permanent?.AttachedTo == target.Subject.Id)
            && (when is null || when(state, EmptyAbilities.Instance, source));

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"no-untap:{card.Name}:{(onSelf ? "self" : "attached")}:{m.Groups["when"].Value}",
            Layer = EffectLayer.Ability,
            Applies = Applies,
            Apply = (_, _, builder) => builder.DoesNotUntap = true,
        });

        return true;
    }

    /// <summary>
    /// "~ gets +1/+1 for each artifact you control" — a static whose size is counted (CR 613.4c).
    /// </summary>
    /// <remarks>
    /// The count is taken when the characteristic is computed, not when the permanent arrived,
    /// which is the whole reason characteristics are computed rather than stored: play a second
    /// artifact and the creature is bigger the instant it resolves, with nothing having to notice
    /// and go back to adjust it.
    /// <para>
    /// "You" is the controller of the <em>source</em>, and that is not always the controller of
    /// the thing being changed. An Aura reading "enchanted creature gets +1/+1 for each artifact
    /// you control" on an opponent's creature counts your artifacts, not theirs — which is why
    /// <see cref="ContinuousEffectDefinition.Apply"/> is handed the source at all.
    /// </para>
    /// <para>
    /// A group phrase the target grammar cannot read leaves the line unread rather than counting
    /// zero. A creature that says it grows and does not is worse than one the engine admits it
    /// cannot play.
    /// </para>
    /// </remarks>
    /// <summary>The card types a printed noun names, or null if the noun is not one.</summary>
    private static Domain.Enums.CardType? CardKindNamed(string noun) =>
        noun.ToLowerInvariant() switch
        {
            "card" => Domain.Enums.CardType.None,
            "creature card" => Domain.Enums.CardType.Creature,
            "land card" => Domain.Enums.CardType.Land,
            "artifact card" => Domain.Enums.CardType.Artifact,
            "enchantment card" => Domain.Enums.CardType.Enchantment,
            "instant card" => Domain.Enums.CardType.Instant,
            "sorcery card" => Domain.Enums.CardType.Sorcery,
            "planeswalker card" => Domain.Enums.CardType.Planeswalker,
            "permanent card" => Domain.Enums.CardType.Creature | Domain.Enums.CardType.Land
                | Domain.Enums.CardType.Artifact | Domain.Enums.CardType.Enchantment
                | Domain.Enums.CardType.Planeswalker,
            _ => null,
        };

    /// <summary>"gets +N/+N for each [kind] in your graveyard" — a count taken in a zone.</summary>
    private static void AddZoneCount(
        string line,
        CardDefinition card,
        ImmutableList<ContinuousEffectDefinition>.Builder into,
        Match m,
        Domain.Enums.CardType kind,
        string zone,
        string whose)
    {
        var power = int.Parse(
            m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var toughness = int.Parse(
            m.Groups["tough"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var attached = m.Groups["subject"].Value.StartsWith("En", StringComparison.Ordinal)
            || m.Groups["subject"].Value.StartsWith("Eq", StringComparison.Ordinal);
        var inGraveyard = zone.Equals("graveyard", StringComparison.OrdinalIgnoreCase);
        var everyone = whose.Equals("each", StringComparison.OrdinalIgnoreCase);

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"zone-count:{card.Name}:{power}/{toughness}:{kind}:{zone}:{whose}",
            Layer = EffectLayer.PowerToughnessModify,
            Applies = attached
                ? (_, source, target) =>
                    source?.Permanent?.AttachedTo is { } host && target.Subject.Id == host
                : (_, source, target) => source is not null && target.Subject.Id == source.Id,
            Apply = (state, source, builder) =>
            {
                var you = source is null
                    ? builder.ControllerId
                    : Characteristics.Of(state, EmptyAbilities.Instance, source).ControllerId;

                var many = 0;
                foreach (var playerId in state.TurnOrder)
                {
                    if (!everyone && playerId != you)
                        continue;

                    var player = state.GetPlayer(playerId);
                    var cards = inGraveyard ? player.Graveyard : player.Hand;

                    many += cards.Count(id =>
                        kind == Domain.Enums.CardType.None
                        || (state.GetObject(id).Card.CardTypes & kind) != 0);
                }

                builder.Modify(power * many, toughness * many);
            },
        });
    }

    /// <summary>
    /// "gets +N/+N for each Aura attached to it" — a count of what is attached to the permanent
    /// being pumped, rather than of a group on the battlefield.
    /// </summary>
    private static void AddAttachedCount(
        string line,
        CardDefinition card,
        ImmutableList<ContinuousEffectDefinition>.Builder into,
        Match m,
        string what)
    {
        var power = int.Parse(
            m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var toughness = int.Parse(
            m.Groups["tough"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var attached = m.Groups["subject"].Value.StartsWith("En", StringComparison.Ordinal)
            || m.Groups["subject"].Value.StartsWith("Eq", StringComparison.Ordinal);

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"attached-count:{card.Name}:{power}/{toughness}:{what}:{line.GetHashCode(StringComparison.Ordinal)}",
            Layer = EffectLayer.PowerToughnessModify,
            Applies = attached
                ? (_, source, target) =>
                    source?.Permanent?.AttachedTo is { } host && target.Subject.Id == host
                : (_, source, target) => source is not null && target.Subject.Id == source.Id,
            Apply = (state, _, builder) =>
            {
                var host = builder.Subject.Id;

                var many = state.Battlefield.Count(id =>
                {
                    var obj = state.GetObject(id);
                    return obj.Permanent?.AttachedTo == host
                        && obj.Card.Subtypes.Contains(what, StringComparer.OrdinalIgnoreCase);
                });

                builder.Modify(power * many, toughness * many);
            },
        });
    }

    private static bool TryCountingStatic(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = CountingStaticLine().Match(line);
        if (!m.Success)
            return false;

        var group = m.Groups["group"].Value.Trim();

        // "for each Aura attached to it" counts a *relation* to the permanent being pumped, not a
        // group on the battlefield, so the board-group reader has nothing to say about it. The
        // attachment is read off the printed card the same way CheckAuras reads it: asking the
        // computed subtypes of an Aura while computing the host's power is how a layer loop
        // starts, and an Aura that has become one by an effect is rare enough to record rather
        // than to risk that for.
        if (AttachedCountLine().Match(group) is { Success: true } attachment)
        {
            AddAttachedCount(line, card, into, m, attachment.Groups["what"].Value);
            return true;
        }

        // "for each creature card in your graveyard" counts cards in a zone rather than
        // permanents on the battlefield, so the board-group reader cannot answer it either. The
        // filter is read off the printed card types, because a card in a graveyard or a hand is
        // not a permanent and has no computed characteristics to ask (CR 109.3).
        if (ZoneCountLine().Match(group) is { Success: true } zoned
            && CardKindNamed(zoned.Groups["what"].Value.Trim()) is { } kind)
        {
            AddZoneCount(line, card, into, m, kind, zoned.Groups["zone"].Value,
                zoned.Groups["whose"].Value);
            return true;
        }

        if (EffectPhrase.Specs.ParseGroup(group) is not
            { Kind: Abilities.TargetKind.Permanent } counted)
        {
            return false;
        }

        var power = int.Parse(
            m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var toughness = int.Parse(
            m.Groups["tough"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var attached = m.Groups["subject"].Value.StartsWith("En", StringComparison.Ordinal)
            || m.Groups["subject"].Value.StartsWith("Eq", StringComparison.Ordinal);

        Func<GameState, GameObject?, CharacteristicsBuilder, bool> applies = attached
            ? (_, source, target) =>
                source?.Permanent?.AttachedTo is { } host && target.Subject.Id == host
            : (_, source, target) => source is not null && target.Subject.Id == source.Id;

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"counting:{card.Name}:{GenerativeEffects.PerEachPumpId(power, toughness, group)}",
            Layer = EffectLayer.PowerToughnessModify,
            Applies = applies,
            Apply = (state, source, builder) =>
            {
                // Falling back to the affected object's controller keeps a source-less effect
                // counting something rather than throwing; every compiled one has a source.
                var you = source is null
                    ? builder.ControllerId
                    : Characteristics.Of(state, EmptyAbilities.Instance, source).ControllerId;

                var many = state.Battlefield.Count(
                    id => counted.ObjectFilter?.Invoke(
                        state, EmptyAbilities.Instance, state.GetObject(id), you) != false);

                builder.Modify(power * many, toughness * many);
            },
        });

        return true;
    }

    /// <summary>
    /// "~'s power and toughness are each equal to the number of lands you control" (CR 613.4a).
    /// </summary>
    /// <remarks>
    /// A characteristic-defining ability, and it is layer 7a rather than 7b for a reason that
    /// shows: a creature whose power is defined this way and is then <em>set</em> to 3/3 is a
    /// 3/3, because defining happens first and setting overwrites it. The other order would make
    /// the definition win and the setting do nothing.
    /// <para>
    /// It also applies in every zone, not only on the battlefield — but the engine computes
    /// characteristics for permanents, so what shows is the permanent. Where that matters is a
    /// mana value or a power question asked of the card elsewhere, which nothing asks yet.
    /// </para>
    /// </remarks>
    private static bool TryDefinedPowerToughness(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = DefinedPowerToughnessLine().Match(line);
        if (!m.Success)
            return false;

        var phrase = m.Groups["what"].Value.Trim();
        if (DefinedCount(phrase) is not { } count)
            return false;

        // "~'s power is equal to the number of creatures you control" is the same sentence with
        // one stat in it, and only the pair was read. A creature that defines one stat keeps its
        // printed value for the other (CR 604.3), which is why the untouched half is read off
        // the card rather than left at whatever the builder happens to hold.
        var stat = m.Groups["stat"].Value;
        var definesPower = !stat.StartsWith("toughness", StringComparison.Ordinal);
        var definesToughness = !stat.StartsWith("power is", StringComparison.Ordinal);

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"cda-pt:{card.Name}:{stat}:{phrase}",
            Layer = EffectLayer.PowerToughnessCda,
            Applies = (_, source, target) => source is not null && target.Subject.Id == source.Id,
            Apply = (state, source, builder) =>
            {
                // "You" is the source's controller, for the same reason it is in a counting
                // static: the ability belongs to this permanent, and its controller is who the
                // sentence is about.
                var you = source is null
                    ? builder.ControllerId
                    : Characteristics.Of(state, EmptyAbilities.Instance, source).ControllerId;

                var many = count(state, you);

                builder.Set(
                    definesPower ? many : builder.Power ?? card.Power ?? 0,
                    definesToughness ? many : builder.Toughness ?? card.Toughness ?? 0);
            },
        });

        return true;
    }

    /// <summary>What a defining sentence counts, or null when it names something unread.</summary>
    /// <remarks>
    /// The zones are separate arms rather than one vocabulary because they are separate
    /// questions: the battlefield is filtered by the target grammar, which knows about control
    /// and computed types; a hand or a graveyard is a pile of cards, filtered by the search
    /// vocabulary, and control does not come into it.
    /// </remarks>
    /// <summary>How full a player's party is — one of each of the four classes (CR 700.9).</summary>
    /// <remarks>
    /// Subtypes are read computed, because a creature that has been made a Cleric is one for this
    /// as much as for anything else (CR 613). A creature with two of the classes fills only one
    /// place, and which is not a choice worth asking about: the size is the same either way, so
    /// the greedy walk below cannot get it wrong.
    /// </remarks>
    private static int PartySize(GameState state, Guid you)
    {
        var classes = new[] { "Cleric", "Rogue", "Warrior", "Wizard" };
        var filled = new bool[classes.Length];

        foreach (var id in state.Battlefield)
        {
            var obj = state.GetObject(id);
            var computed = Characteristics.Of(state, EmptyAbilities.Instance, obj);

            if (!computed.IsCreature || computed.ControllerId != you)
                continue;

            for (var i = 0; i < classes.Length; i++)
            {
                if (filled[i] || !computed.HasSubtype(classes[i]))
                    continue;

                filled[i] = true;
                break;
            }
        }

        return filled.Count(f => f);
    }

    private static Func<GameState, Guid, int>? DefinedCount(string phrase)
    {
        if (string.Equals(phrase, "your life total", StringComparison.OrdinalIgnoreCase))
            return (state, you) => Math.Max(0, state.GetPlayer(you).Life);

        // "Creatures in your party" is not a count of creatures at all (CR 700.9): a party is at
        // most one Cleric, one Rogue, one Warrior and one Wizard, so eight Clerics are a party of
        // one. Answered here rather than through the noun grammar, which counts what it matches
        // and would say eight.
        if (PartyLine().IsMatch(phrase))
        {
            return (state, you) => PartySize(state, you);
        }

        var counting = CountedThingLine().Match(phrase);
        if (!counting.Success)
            return null;

        var what = counting.Groups["what"].Value.Trim();

        if (counting.Groups["hand"].Success)
        {
            return string.Equals(what, "cards", StringComparison.OrdinalIgnoreCase)
                ? (state, you) => state.GetPlayer(you).Hand.Count
                : null;
        }

        if (counting.Groups["yard"].Success)
        {
            // "Creature cards in your graveyard" - the noun is a filter and the trailing word
            // "cards" is grammar, the same way it is in a tutor.
            // "For each creature card in your graveyard" says "card" and "the number of creature
            // cards" says "cards" - the same phrase counted two ways round, so both endings come
            // off before the filter vocabulary is asked.
            var noun = what;
            foreach (var ending in new[] { " cards", " card" })
            {
                if (noun.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                {
                    noun = noun[..^ending.Length];
                    break;
                }
            }

            var filter = EffectPhrase.SearchFilterFor(noun);

            return filter is null
                ? null
                : (state, you) => state.GetPlayer(you).Graveyard
                    .Count(id => Abilities.SearchFilters.Matches(
                        filter, state.GetObject(id).Card));
        }

        // "The number of creatures on the battlefield" - the same count with nobody's name on
        // it. Taken off before the grammar is asked, because that grammar reads ownership and
        // has no way to spell "everyone's"; what is left is a filter with no owner, which is
        // exactly what counts every one of them.
        if (what.EndsWith(" on the battlefield", StringComparison.OrdinalIgnoreCase))
            what = what[..^" on the battlefield".Length].Trim();

        return EffectPhrase.Specs.ParseGroup(what) is { Kind: Abilities.TargetKind.Permanent } group
            ? (state, you) => state.Battlefield.Count(
                id => group.ObjectFilter?.Invoke(
                    state, EmptyAbilities.Instance, state.GetObject(id), you) != false)
            : null;
    }

    private static bool TryConditionalStatic(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = ConditionalStaticLine().Match(line);
        if (!m.Success)
            return false;

        // "Can't attack unless defending player controls an Island" belongs to the attack
        // restriction below, not here: a continuous effect is asked before attackers are
        // declared, when there is no defending player to ask about. Declined rather than
        // answered with "nobody", which would read as a creature that may never attack.
        //
        // Only the attacking half. "Can't be blocked as long as defending player controls an
        // artifact" is asked while the creature is already attacking, so the defender is known
        // and this reader is the right home for it.
        if (m.Groups["cant"].Value.StartsWith("attack", StringComparison.OrdinalIgnoreCase)
            && m.Groups["cond"].Value.Contains("defending player", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (BoardConditions.Parse(m.Groups["cond"].Value.Trim()) is not { } parsed)
            return false;

        // "Can't attack unless you control another artifact" is "has defender as long as you
        // do *not*" - the same restriction with the condition turned round. Read as a negation
        // rather than given its own reader, so the whole condition vocabulary serves both.
        var holds = m.Groups["unless"].Success
            ? (state, abilities, source) => !parsed(state, abilities, source)
            : parsed;

        var keywords = m.Groups["kw"].Success ? EffectPhrase.Keywords(m.Groups["kw"].Value) : null;
        if (m.Groups["kw"].Success && keywords is null)
            return false;

        // "And can't block" - the same pair of restrictions the Aura reader maps, and the engine
        // already models both as keywords: defender is exactly "can't attack" (CR 702.3b).
        if (m.Groups["cant"].Success)
        {
            var stopped = m.Groups["cant"].Value.ToLowerInvariant() switch
            {
                "attack" => KeywordAbility.Defender,
                "block" => KeywordAbility.CantBlock,
                "be blocked" => KeywordAbility.CantBeBlocked,
                _ => KeywordAbility.Defender | KeywordAbility.CantBlock,
            };

            keywords = keywords is { } already ? already | stopped : stopped;
        }

        // "As long as enchanted permanent is a creature, it gets +1/+1" - "it" on an Aura is
        // the thing it is attached to and never the Aura itself, which is not a creature and
        // could not use the bonus if it were given one. The word is what decides: "~" is the
        // card saying its own name, and only the pronoun means the host.
        // "It", "enchanted creature", "equipped creature" - three ways of naming the thing an
        // Aura or Equipment is attached to, against "~", which is the card naming itself. The
        // "during your turn" arm had no subject slot at all and could only ever say "~".
        var subject = m.Groups["subject"].Value;
        var onHost = !subject.Equals("~", StringComparison.Ordinal);

        bool OnSelfWhile(GameState state, GameObject? source, CharacteristicsBuilder target)
        {
            if (source is null || !holds(state, EmptyAbilities.Instance, source))
                return false;

            if (!onHost)
                return target.Subject.Id == source.Id;

            // Attached to nothing means there is nothing this applies to, which is the right
            // answer rather than an error: an Aura in a graveyard has no host.
            return source.Permanent?.AttachedTo is { } host && target.Subject.Id == host;
        }

        if (m.Groups["p"].Success)
        {
            var power = int.Parse(
                m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var toughness = int.Parse(
                m.Groups["tough"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            into.Add(new ContinuousEffectDefinition
            {
                Id = $"while:{card.Name}:{GenerativeEffects.PumpId(power, toughness)}",
                Layer = EffectLayer.PowerToughnessModify,
                Applies = OnSelfWhile,
                Apply = (_, _, builder) => builder.Modify(power, toughness),
            });
        }

        if (keywords is { } granted)
        {
            into.Add(new ContinuousEffectDefinition
            {
                Id = $"while:{card.Name}:{GenerativeEffects.GrantId(granted)}",
                Layer = EffectLayer.Ability,
                Applies = OnSelfWhile,
                Apply = (_, _, builder) => builder.Keywords |= granted,
            });
        }

        return into.Count > 0;
    }

    /// <summary>
    /// "Creatures you control get +N/+N" and its relatives — a static over a group (CR 613.4c).
    /// </summary>
    /// <remarks>
    /// The lord. One shape covering the whole family: which creatures, and what they get. The
    /// group is read once here and the two things it can grant — a power/toughness change in
    /// layer 7c and a keyword in layer 6 — are separate effects, because they are separate layers
    /// and one effect cannot be in two (CR 613.1f, 613.4c).
    /// <para>
    /// The filter reads the characteristics <em>as computed so far</em> rather than the printed
    /// card, so a lord that pumps Goblins pumps something layer 4 turned into a Goblin. That is
    /// what <see cref="ContinuousEffectDefinition.Applies"/> is handed a builder for.
    /// </para>
    /// </remarks>
    private static bool TryMassStatic(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = MassStaticLine().Match(line);
        if (!m.Success)
            return false;

        // Through the shared singulariser, because the bare-tribe form is printed plural and
        // English is irregular: "other Elves you control" named the creature type "Elve", found
        // none however many Elves were on the battlefield, and buffed nothing.
        var subtype = m.Groups["subtype"].Success
            ? EffectPhrase.SingularWord(m.Groups["subtype"].Value.Trim())
            : null;

        // "Creatures of the chosen type get +1/+1" - the tribe is not printed on the card, it is
        // whatever this permanent named as it entered (CR 614.12). So it is read from the source
        // when the effect applies rather than baked in when the card compiles, which is the same
        // reason every other characteristic is computed.
        var chosenType = m.Groups["chosen"].Value.Equals("type", StringComparison.OrdinalIgnoreCase);
        var chosenColor = m.Groups["chosen"].Value.Equals("color", StringComparison.OrdinalIgnoreCase);
        var side = m.Groups["side"].Value.Trim().ToLowerInvariant();
        var otherOnly = m.Groups["scope"].Value.StartsWith("other", StringComparison.OrdinalIgnoreCase);

        // "Other" is what makes a lord not pump itself, and a lord that pumps itself is a
        // different card — so an unrecognised scope word has to leave the line unread.
        var yours = side is "" or "you control";
        var theirs = side is "your opponents control" or "an opponent controls";
        if (!yours && !theirs)
            return false;

        // "Other creatures you control with flying get +1/+1" - a keyword the creature has to
        // have before the lord sees it, which is the opposite direction from the keywords the
        // lord grants and reads with the same table.
        var needs = m.Groups["needs"].Success
            ? EffectPhrase.Keywords(m.Groups["needs"].Value)
            : null;

        if (m.Groups["needs"].Success && needs is null)
            return false;

        // "Each creature you control with a +1/+1 counter on it has trample" - the other thing
        // this slot says. A counter is not a characteristic, so it is asked of the permanent
        // rather than of the computed card: an effect can give a creature flying, and nothing
        // gives it a counter except something that put one there.
        var needsCounter = m.Groups["counter"].Success
            ? m.Groups["counter"].Value.Trim()
            : null;

        var keywords = m.Groups["kw"].Success
            ? EffectPhrase.Keywords(m.Groups["kw"].Value)
            : null;

        if (m.Groups["kw"].Success && keywords is null)
            return false;

        // A subtype filter is only meaningful when it names a creature type the card itself is
        // about; anything else is read literally, which is what the rules do too.
        bool Matches(GameState state, GameObject? source, CharacteristicsBuilder target)
        {
            if (!target.IsCreature)
                return false;

            if (otherOnly && source is not null && target.Subject.Id == source.Id)
                return false;

            // Nothing named yet - the permanent is arriving and the question has not been asked.
            // It buffs nothing until it has, which is the honest answer rather than everything.
            if ((chosenType || chosenColor) && source?.Chosen is null)
                return false;

            if (chosenColor
                && ColorNamed(source!.Chosen!) is { } wanted
                && !target.IsColor(wanted))
            {
                return false;
            }

            var tribe = chosenType ? source?.Chosen : subtype;
            if (tribe is not null && !target.IsEveryCreatureType && !target.HasSubtype(tribe))
                return false;

            // Asked of the computed characteristics, like everything else here: a creature given
            // flying by another effect is one of these, and one that has lost it is not.
            if (needs is { } wantedKeyword && !target.Keywords.HasFlag(wantedKeyword))
                return false;

            if (needsCounter is { } wantedCounter
                && target.Subject.Permanent?.Counters.GetValueOrDefault(wantedCounter) is not > 0)
            {
                return false;
            }

            // The *computed* controller, not the one stored on the object. Control-changing
            // effects are layer 2 and this is layer 6 or 7, so by the time a lord asks whose
            // creatures it sees, a theft has already happened (CR 613.1b). Reading the stored
            // controller made every control-change invisible to every lord — a creature stolen
            // and then looked at was still counted as its old controller's.
            var controller = source?.ControllerId ?? target.ControllerId;
            return yours
                ? target.ControllerId == controller
                : target.ControllerId != controller;
        }

        var describedAs = (chosenType || chosenColor
            ? "chosen-" + m.Groups["chosen"].Value.ToLowerInvariant()
            : subtype ?? "creatures")
            + (needs is { } named ? ":with-" + named : string.Empty)
            + (needsCounter is { } counted ? ":counter-" + counted : string.Empty);

        if (m.Groups["p"].Success)
        {
            var power = int.Parse(
                m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var toughness = int.Parse(
                m.Groups["tough"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            into.Add(new ContinuousEffectDefinition
            {
                Id = $"mass:{describedAs}:{card.Name}:{GenerativeEffects.PumpId(power, toughness)}",
                Layer = EffectLayer.PowerToughnessModify,
                Applies = Matches,
                Apply = (_, _, builder) => builder.Modify(power, toughness),
            });
        }

        if (keywords is { } granted)
        {
            into.Add(new ContinuousEffectDefinition
            {
                Id = $"mass:{describedAs}:{card.Name}:{GenerativeEffects.GrantId(granted)}",
                Layer = EffectLayer.Ability,
                Applies = Matches,
                Apply = (_, _, builder) => builder.Keywords |= granted,
            });
        }

        return into.Count > 0;
    }

    /// <summary>
    /// <c>Enchanted land has "{T}: Add {B}"</c> — a whole ability granted in layer 6 (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// The commonest thing in the corpus the compiler could not read: a quoted ability given to
    /// something else. The quoted text is compiled the same way any activated ability is — which
    /// is the point of having one reader for it — and the result is attached to whatever the
    /// sentence names, through the same static-ability grammar the lords use.
    /// <para>
    /// The granted ability's id is prefixed so it can never collide with a printed one on the
    /// permanent receiving it: a land with its own "{T}: Add {G}" and an Aura granting
    /// "{T}: Add {B}" has two abilities, and a player has to be able to say which they mean.
    /// </para>
    /// </remarks>
    private static bool TryGrantedAbility(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = GrantedAbilityLine().Match(line);
        if (!m.Success)
            return false;

        // The quoted text is an ability in its own right, read by the same matcher that reads a
        // printed one. Anything it cannot read leaves the whole line unread.
        var quoted = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();
        var quotedTriggers = ImmutableList.CreateBuilder<TriggeredAbilityDefinition>();
        var rejected = ImmutableList.CreateBuilder<string>();
        var inner = m.Groups["ability"].Value.Trim();

        // A quoted ability is as often a trigger as an activated one - 86 lines of the corpus to
        // 197 - and it is read by the same matcher that reads a printed trigger, for the same
        // reason the activated half is: one reader, so a sentence understood on a card is
        // understood inside quotation marks too.
        var modalAt = -1;
        var isTrigger = TryTrigger(inner, card, quotedTriggers, rejected, ref modalAt)
            && rejected.Count == 0
            && quotedTriggers.Count > 0;

        if (!isTrigger
            && !TryManaAbility(inner, quoted)
            && (!TryActivatedAbility(inner, card, quoted, rejected) || rejected.Count > 0))
        {
            return false;
        }

        if (quoted.Count == 0 && quotedTriggers.Count == 0)
            return false;

        var granted = quoted
            .Select(a => a with { Id = "granted:" + card.Name + ":" + a.Id })
            .ToImmutableList();

        var grantedTriggers = quotedTriggers
            .Select(t => t with
            {
                Id = "granted:" + card.Name + ":" + t.Id,

                // The trigger belongs to whoever it was given to, not to the card that granted
                // it: "enchanted creature has 'when this creature dies, draw a card'" draws for
                // the creature's controller. The predicate is handed the object it is on, so
                // nothing has to be rewritten - but the ability has to be looked up on that
                // object rather than on the granting card, which is what the granted-ability
                // table exists for.
                FunctionsFrom = t.FunctionsFrom,
            })
            .ToImmutableList();

        var attached = m.Groups["attached"].Success;

        // Through the shared singulariser, as the mass statics are and for the same reason:
        // "All Elves have ..." names the creature type "Elve" if the s is simply dropped, and a
        // static ability that names a type no card has grants its ability to nobody.
        var subtype = m.Groups["subtype"].Success
            ? EffectPhrase.SingularWord(m.Groups["subtype"].Value.Trim())
            : null;

        // "Lands you control have ..." is not "permanents you control have ...", and until the
        // noun was read this filter never asked: it checked the tribe and who controlled it and
        // handed the ability to every permanent that passed. Reading the noun and then ignoring
        // it would have been the worse half of both.
        var type = m.Groups["type"].Value.ToLowerInvariant() switch
        {
            "creature" => CardType.Creature,
            "land" => CardType.Land,
            "artifact" => CardType.Artifact,
            "enchantment" => CardType.Enchantment,
            _ => CardType.None,
        };

        var side = m.Groups["side"].Value.Trim().ToLowerInvariant();
        var yours = side is "" or "you control";
        var scope = m.Groups["scope"].Value.ToLowerInvariant();

        // "Commander creatures you own have ..." - a Background's whole text, and the only group
        // in the corpus picked out by something that is not a characteristic. A commander is a
        // designation its owner gave a card before the game began (CR 903.3), so the question is
        // asked of the player rather than of the permanent.
        var commanders = m.Groups["commander"].Success;

        // "All Slivers have ..." is every Sliver on the battlefield, including an opponent's.
        // The word was matched here and then never used, so the filter fell through to its
        // default and read the line as "Slivers you control" - a hive lord that quietly stopped
        // at the table edge.
        var everyone = scope is "all" or "each";

        // "Other" is the word that keeps a lord out of its own ability, and it was being
        // discarded in the same place for the same reason.
        var otherOnly = scope is "other";

        into.Add(new ContinuousEffectDefinition
        {
            Id = "grants:" + card.Name,
            Layer = EffectLayer.Ability,
            Applies = (state, source, target) =>
            {
                if (source is null)
                    return false;

                // "Enchanted"/"equipped" means the one permanent this is attached to (CR 701.3c);
                // everything else is a group, read the same way a lord's is.
                if (attached)
                    return source.Permanent?.AttachedTo == target.Subject.Id;

                if (subtype is not null && !target.IsEveryCreatureType && !target.HasSubtype(subtype))
                    return false;

                // A named tribe is a creature type, so a bare one carries the creature
                // requirement with it even when the line never says the word.
                if (type != CardType.None && !target.CardTypes.HasFlag(type))
                    return false;

                if (type == CardType.None && subtype is not null && !target.IsCreature)
                    return false;

                if (otherOnly && target.Subject.Id == source.Id)
                    return false;

                if (commanders)
                {
                    // "You own", not "you control": a commander stolen by an opponent is still
                    // its owner's commander, and a Background follows the card rather than the
                    // board.
                    return target.IsCreature
                        && target.Subject.OwnerId == source.ControllerId
                        && string.Equals(
                            state.GetPlayer(target.Subject.OwnerId).CommanderOracleId,
                            target.Subject.Card.OracleId,
                            StringComparison.Ordinal);
                }

                if (everyone)
                    return true;

                // Computed, for the same reason the mass statics use it: a granted ability has
                // to follow the creature to whoever controls it now.
                return yours
                    ? target.ControllerId == source.ControllerId
                    : target.ControllerId != source.ControllerId;
            },
            Apply = (_, _, builder) =>
            {
                builder.GrantedActivated.AddRange(granted);
                builder.GrantedTriggers.AddRange(grantedTriggers);
            },
        });

        return true;
    }

    /// <summary>"Equip {cost}" — attach this to a creature you control (CR 702.6).</summary>
    /// <remarks>
    /// Sorcery speed only, which the engine enforces for every activated ability that is not a
    /// mana ability; the restriction is not expressible here yet and is noted rather than faked.
    /// </remarks>
    private static bool TryEquip(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = EquipLine().Match(line);
        if (!m.Success)
            return false;

        var narrowed = m.Groups["what"].Value.Trim();
        var who = EffectPhrase.Specs.TargetCreatureYouControl;

        if (narrowed.Length > 0)
        {
            if (EffectPhrase.Specs.Parse($"target {narrowed} you control") is not { } only)
                return false;

            who = only;
        }

        into.Add(new ActivatedAbilityDefinition
        {
            // The narrowed form takes its own id, because a card may carry both: Blackblade
            // Reforged has "Equip legendary creature {3}" and "Equip {7}", and one id for the
            // two would leave the cheaper one unreachable - the board addresses an ability by
            // its id and the engine looks it up by the same.
            Id = narrowed.Length == 0
                ? "equip"
                : "equip-" + narrowed.Replace(' ', '-').ToLowerInvariant(),
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            Targets = [who],
            Effects = [new AttachSourceTo()],
        });

        return true;
    }

    /// <summary>
    /// "Morph [cost]" — may be cast face down for {3}, turned up later for this (CR 702.37a).
    /// </summary>
    /// <remarks>
    /// One matcher for all three, because they are one mechanic and two riders: megamorph turns
    /// up with a +1/+1 counter (CR 702.37e), disguise wards while face down (CR 702.168a), and
    /// everything else about them is identical. Reading either as plain morph would compile and
    /// play and be quietly wrong — by one counter, or by the whole of the card's protection.
    /// </remarks>
    private static bool TryMorph(
        string line,
        CardDefinition card,
        ImmutableList<TriggeredAbilityDefinition>.Builder triggers,
        ref ManaCostSpec? into,
        ref bool addsCounter,
        ref ManaCostSpec? wardsWhileHidden)
    {
        var m = MorphLine().Match(line);
        if (!m.Success)
            return false;

        var keyword = m.Groups["kw"].Value.ToLowerInvariant();
        into = ManaCostSpec.Parse(m.Groups["cost"].Value);
        addsCounter = keyword == "megamorph";

        // CR 702.168a: disguise's face-down side has ward {2}, always — the number is part of the
        // keyword rather than something the card chooses.
        if (keyword == "disguise")
        {
            var ward = ManaCostSpec.Parse("{2}");
            wardsWhileHidden = ward;

            triggers.Add(new TriggeredAbilityDefinition
            {
                Id = "disguise-ward",
                Text = $"Whenever {card.Name} becomes the target of a spell or ability an "
                    + "opponent controls, counter it unless that player pays {2}.",
                FunctionsFaceDown = true,

                // Only while face down: turned up, the card has whatever it printed and no more.
                Triggers = (e, state, source) =>
                    source.Permanent is { IsFaceDown: true }
                    && e is TargetsChosen aimed
                    && aimed.Targets.Any(t => t.Subject == source.Id)
                    && state.TryGetObject(aimed.StackId, out var aiming)
                    && aiming.ControllerId != source.ControllerId,

                Effects =
                [
                    new MayPay(
                        ward,
                        IfYouDo: [],
                        IfYouDont: [new CounterSubjectSpell()],
                        EffectIndex: 0,
                        AskSubjectPlayer: true),
                ],
            });
        }

        return true;
    }

    /// <summary>
    /// "Affinity for [things]" — one generic mana less for each of them you control (CR 702.40a).
    /// </summary>
    /// <remarks>
    /// The count goes through the same target grammar every other filter uses, by asking it to
    /// read "target [thing] you control" and keeping the filter rather than the target. That is
    /// what makes affinity for Equipment and affinity for Forests cost nothing extra to support:
    /// the grammar already tells a card type from a subtype, and this is not a second place that
    /// has to learn the difference.
    /// <para>
    /// The printed noun is plural and the grammar wants a singular, which is the whole of the
    /// translation. A noun it cannot read leaves the line unread — a spell quietly cheaper than
    /// printed is worse than one that does not compile.
    /// </para>
    /// </remarks>
    /// <summary>"Undaunted" — {1} less for each opponent you have (CR 702.125a).</summary>
    /// <remarks>
    /// Opponents you <em>have</em>, not opponents still playing: the rule counts the players, and
    /// a player who has lost is no longer one of them. That is why this asks the turn order and
    /// filters out the losers rather than counting seats, and it is a distinction only a
    /// multiplayer game can show - in a two-player game the discount is {1} until the game ends.
    /// </remarks>
    private static bool TryUndaunted(
        string line, ref Func<GameState, Guid, IReadOnlyList<Target>, int>? into)
    {
        if (!UndauntedLine().IsMatch(line))
            return false;

        into = static (state, playerId, _) => state.TurnOrder.Count(
            other => other != playerId && !state.GetPlayer(other).HasLost);

        return true;
    }

    private static bool TryAffinity(
        string line, ref Func<GameState, Guid, IReadOnlyList<Target>, int>? into)
    {
        var m = AffinityLine().Match(line);
        if (!m.Success)
            return false;

        var plural = m.Groups["what"].Value.Trim();
        var singular = plural.EndsWith('s') ? plural[..^1] : plural;

        if (EffectPhrase.Specs.Parse($"target {singular} you control") is not
            { Kind: Abilities.TargetKind.Permanent } counted)
        {
            return false;
        }

        into = (state, playerId, _) => state.Battlefield.Count(
            id => counted.ObjectFilter?.Invoke(
                state, EmptyAbilities.Instance, state.GetObject(id), playerId) != false);

        return true;
    }

    /// <summary>
    /// "~ costs {2} less to cast if it targets a tapped creature" (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// A flat discount with a condition on the spell's own targets rather than on the board.
    /// Answerable at the moment it is asked because CR 601.2c chooses targets before CR 601.2f
    /// works out the cost - a discount that depended on the targets would be unanswerable in the
    /// other order, and it is the reason the reduction is handed them at all.
    /// <para>
    /// The condition is checked with the target spec's own legality test, so "a tapped creature"
    /// and every other phrase the target grammar reads come for free.
    /// </para>
    /// </remarks>
    /// <summary>
    /// "~ costs {1} less to cast for each creature card in your graveyard" (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// The same discount with a count instead of a condition, and it counts through the reader
    /// the defining abilities already use — so a graveyard, a hand, a board or a life total all
    /// work here without this knowing how any of them are counted.
    /// <para>
    /// Clamped at zero and not at the spell's cost: a reduction larger than the cost simply pays
    /// all of the generic there is (CR 601.2f), and the payment code is where that is decided.
    /// </para>
    /// </remarks>
    private static bool TryCountedCostReduction(
        string line, ref Func<GameState, Guid, IReadOnlyList<Target>, int>? into)
    {
        var m = CountedCostReductionLine().Match(line);
        if (!m.Success)
            return false;

        if (DefinedCount("the number of " + m.Groups["what"].Value.Trim()) is not { } count)
            return false;

        var each = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into = (state, playerId, _) => Math.Max(0, each * count(state, playerId));
        return true;
    }

    /// <summary>
    /// "~ costs {1} less to cast if you control a Wizard" (CR 601.2f).
    /// </summary>
    private static bool TryConditionalCostReduction(
        string line,
        CardDefinition card,
        ref Func<GameState, Guid, IReadOnlyList<Target>, int>? into)
    {
        var m = ConditionalCostReductionLine().Match(line);
        if (!m.Success)
            return false;

        if (BoardConditions.Parse(m.Groups["cond"].Value.Trim()) is not { } holds)
            return false;

        var less = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        // The condition is about the board and wants an object to ask "you" about. The card is
        // not on the battlefield while it is being cast, so a stand-in carrying its controller is
        // what the question actually needs.
        into = (state, playerId, _) => holds(
            state,
            EmptyAbilities.Instance,
            new GameObject
            {
                Id = ObjectId.New(),
                Card = card,
                OwnerId = playerId,
                ControllerId = playerId,
                Zone = Zone.Stack,
                Timestamp = 0,
            })
            ? less
            : 0;

        return true;
    }

    private static bool TryTargetCostReduction(
        string line, ref Func<GameState, Guid, IReadOnlyList<Target>, int>? into)
    {
        var m = TargetCostReductionLine().Match(line);
        if (!m.Success)
            return false;

        if (EffectPhrase.Specs.Parse("target " + m.Groups["what"].Value.Trim()) is not { } wanted)
            return false;

        var less = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into = (state, playerId, targets) => targets.Any(
            target => wanted.IsLegal(state, EmptyAbilities.Instance, target, playerId))
            ? less
            : 0;

        return true;
    }

    /// <summary>
    /// "As an additional cost to cast this spell, [cost]" (CR 601.2f).
    /// </summary>
    /// <remarks>
    /// The mandatory twin of kicker, and read by the same <see cref="ReadCost"/> that reads an
    /// activated ability's cost line — an additional cost on a spell and a cost on an ability are
    /// one rule, and the reader had been able to lift "Sacrifice a creature" out of a cost for a
    /// while with nowhere on a spell to put the result.
    /// <para>
    /// Only the parts that need a <em>choice</em> are kept. A purely-mana additional cost is not
    /// expressible here — the engine charges one mana cost per spell — so it is left unread
    /// rather than silently charged as nothing, which would make the spell cheaper than printed.
    /// </para>
    /// </remarks>
    private static bool TryAdditionalCost(
        string line,
        ImmutableList<ChosenCost>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var m = AdditionalCostLine().Match(line);
        if (!m.Success)
            return false;

        if (ReadCost(m.Groups["cost"].Value.Trim()) is not { } paid
            || paid.Chosen.Count == 0
            || !paid.Mana.Symbols.IsEmpty
            || paid.RequiresTap
            || paid.Life > 0
            || paid.SelfCost is not SelfCost.None
            || paid.Counters is not null)
        {
            unhandled.Add(line);
            return true;
        }

        into.AddRange(paid.Chosen);
        return true;
    }

    /// <summary>"Kicker [cost]" — an optional additional cost (CR 702.33a).</summary>
    private static bool TryKicker(string line, ref ManaCostSpec? into)
    {
        var m = KickerLine().Match(line);
        if (!m.Success)
            return false;

        into = ManaCostSpec.Parse(m.Groups["cost"].Value);
        return true;
    }

    /// <summary>
    /// "If this spell was kicked, ..." — the half that only happens when it was (CR 702.33e).
    /// </summary>
    /// <remarks>
    /// The targets it needs are added to the spell either way, because targets are chosen as the
    /// spell is cast and the engine has one target list per spell. CR 702.33g says they should be
    /// chosen only if it was kicked; that is a known difference and shows up as an unkicked spell
    /// asking for a target it will not use.
    /// </remarks>
    private static bool TryIfKicked(
        string line,
        ImmutableList<IEffect>.Builder effects,
        ImmutableList<TargetSpec>.Builder targets)
    {
        var m = IfKickedLine().Match(line);
        if (!m.Success)
            return false;

        if (!EffectPhrase.TryParse(m.Groups["effect"].Value.Trim(), out var parsed)
            || parsed.Effects.Any(EffectPhrase.FindsItselfByIndex))
        {
            return false;
        }

        // The wrapped effects index into the spell's target list, so their indices have to be
        // shifted by whatever the spell already asks for.
        var offset = targets.Count;
        targets.AddRange(parsed.Targets);

        effects.Add(new IfKicked(
            [.. parsed.Effects.Select(e => EffectTargets.Shift(e, offset))]));

        return true;
    }

    /// <summary>
    /// "Choose one —" and the bulleted modes under it (CR 700.2).
    /// </summary>
    /// <remarks>
    /// The one family in the long tail that genuinely repeats: 556 playable cards offer modes,
    /// and the modes themselves are ordinary sentences the vocabulary already reads — "destroy
    /// target artifact" appears as a mode on forty-five different cards. So a mode is compiled by
    /// handing the bullet to the same phrase parser everything else uses.
    /// <para>
    /// The header and the bullets are separate lines, which is why this keeps state across the
    /// loop rather than matching a whole block: the header says how many to choose and the
    /// bullets say what they are.
    /// </para>
    /// </remarks>
    private static bool TryModal(
        string line, ImmutableList<SpellMode>.Builder into, ref int toChoose, ref int max)
    {
        var header = ModalHeader().Match(line);
        if (header.Success)
        {
            toChoose = header.Groups["n"].Value.ToLowerInvariant() switch
            {
                "two" => 2,
                "three" => 3,
                _ => 1,
            };

            // "Choose one or both" and "choose one or more" are a range, not decoration - and
            // they are not rare: 71 cards say one or the other, and every one of them could only
            // ever take a single mode while the header was read as a number.
            max = header.Groups["range"].Value.ToLowerInvariant() switch
            {
                "both" => 2,
                "more" => -1,
                _ => toChoose,
            };

            return true;
        }

        var bullet = ModalBullet().Match(line);
        if (!bullet.Success || toChoose == 0)
            return false;

        // A mode the parser cannot read leaves the *line* unread, which is what stops a modal
        // card from quietly offering fewer modes than it prints.
        if (!EffectPhrase.TryParse(bullet.Groups["mode"].Value.Trim(), out var parsed))
            return false;

        into.Add(new SpellMode(bullet.Groups["mode"].Value.Trim(), parsed.Targets, parsed.Effects));
        return true;
    }

    /// <summary>
    /// "Flashback [cost]" — permission to cast it from the graveyard, once (CR 702.34a).
    /// </summary>
    /// <remarks>
    /// Both halves of the keyword or neither: the permission is worth nothing without the exile,
    /// because a flashback card that returned to the graveyard could be cast again every turn.
    /// </remarks>
    /// <summary>
    /// "Mayhem [cost]" — cast it out of the graveyard, but only if you threw it away (CR 702.181a).
    /// </summary>
    /// <remarks>
    /// Flashback with a condition and without the exile, and the condition is the card. Cast this
    /// way it goes to the graveyard again afterwards like any other spell, which would make it
    /// castable for ever — except that it can only be cast on the turn it was discarded, and it
    /// is not discarded by being cast.
    /// </remarks>
    private static bool TryMayhem(string line, ref AlternativeCastZone? into)
    {
        var m = MayhemLine().Match(line);
        if (!m.Success)
            return false;

        into = new AlternativeCastZone(
            Zone.Graveyard,
            ManaCostSpec.Parse(m.Groups["cost"].Value),
            ExileOnResolve: false,
            OnlyIfDiscardedThisTurn: true);

        return true;
    }

    /// <summary>
    /// "Escape—{2}{B}, Exile four other cards from your graveyard" (CR 702.139a).
    /// </summary>
    /// <remarks>
    /// Flashback's shape with two differences, and both matter. It carries an additional cost -
    /// exiling other cards from the same graveyard - which the alternative cast already has a
    /// place for; and it does **not** exile the card on resolution, which is the whole point of
    /// the mechanic: a card that escaped and died can escape again, if the graveyard can pay for
    /// it a second time.
    /// <para>
    /// "Other" is not decoration: the escaping card is itself in that graveyard, and a cost that
    /// could exile it would eat the spell being cast.
    /// </para>
    /// </remarks>
    private static bool TryEscape(string line, ref AlternativeCastZone? into)
    {
        var m = EscapeLine().Match(line);
        if (!m.Success)
            return false;

        var many = NumberWordOrDigits(m.Groups["n"].Value);
        if (many <= 0)
            return false;

        into = new AlternativeCastZone(
            Zone.Graveyard,
            ManaCostSpec.Parse(m.Groups["cost"].Value),
            ExileOnResolve: false,
            Extra:
            [
                new ChosenCost(
                    ChosenCostKind.ExileFromGraveyard, many, ExcludesSource: true),
            ],
            Keyword: "escape");

        return true;
    }

    /// <summary>
    /// "Disturb {cost}" — cast from the graveyard, transformed (CR 702.146a).
    /// </summary>
    /// <remarks>
    /// Flashback's shape with one difference, and it is the whole mechanic: the card comes back
    /// as its <em>other</em> face. The permission itself was already here — a cost, a zone, and
    /// exile on resolution — so disturb is that permission plus the turn-over, and the turn-over
    /// is derived at resolution from the zone the spell was cast from rather than carried on an
    /// event of its own.
    /// <para>
    /// Exiled on resolution, like flashback: a disturbed card that could be disturbed again would
    /// be a card the front face never had. 31 faces carry this line, and each held back a whole
    /// card, because a card is playable only when every face of it reads.
    /// </para>
    /// </remarks>
    private static bool TryDisturb(string line, ref AlternativeCastZone? into)
    {
        var m = DisturbLine().Match(line);
        if (!m.Success)
            return false;

        into = new AlternativeCastZone(
            Zone.Graveyard,
            ManaCostSpec.Parse(m.Groups["cost"].Value),
            ExileOnResolve: true,
            Keyword: "disturb",
            Transformed: true);

        return true;
    }

    private static bool TryFlashback(string line, ref AlternativeCastZone? into)
    {
        var m = FlashbackLine().Match(line);
        if (!m.Success)
            return false;

        into = new AlternativeCastZone(
            Zone.Graveyard, ManaCostSpec.Parse(m.Groups["cost"].Value), ExileOnResolve: true);

        return true;
    }

    /// <summary>
    /// "Unearth [cost]" — an activated ability that works from the graveyard (CR 702.83a).
    /// </summary>
    /// <remarks>
    /// Nearly free by the time it was reached, which is the point of building mechanics rather
    /// than cards: an ability that functions from the graveyard already existed for the
    /// exile-from-graveyard costs, and the delayed exile already existed for the temporary-token
    /// effects. Unearth is those two and a keyword grant.
    /// </remarks>
    /// <summary>
    /// While a bestowed permanent is attached to something it is an Aura, not a creature
    /// (CR 702.103a).
    /// </summary>
    /// <remarks>
    /// Layer 4, where type-changing effects live (CR 613.1d), and conditional on the permanent
    /// still being attached - which is the whole of "it becomes a creature again if it is not
    /// attached to a creature". Nothing has to notice the host leaving: the condition simply
    /// stops holding and the creature type comes back on its own.
    /// <para>
    /// The state-based action that buries an unattached Aura reads the <em>printed</em> subtypes
    /// (CR 704.5m), and a bestow card does not print Aura - so it is left alone when it falls
    /// off, which is exactly what the keyword wants.
    /// </para>
    /// </remarks>
    private static ContinuousEffectDefinition AuraWhileAttached() => new()
    {
        Id = "bestow-aura",
        Layer = EffectLayer.Type,
        Applies = (_, source, target) =>
            source is { Permanent.AttachedTo: not null, WasBestowed: true }
            && target.Subject.Id == source.Id,
        Apply = (_, _, target) =>
        {
            target.CardTypes &= ~CardType.Creature;
            target.CardTypes |= CardType.Enchantment;

            if (!target.Subtypes.Contains("Aura", StringComparer.OrdinalIgnoreCase))
                target.Subtypes.Add("Aura");
        },
    };

    /// <summary>
    /// "Backup N" - counters on a creature, and this card's abilities with them (CR 702.164a).
    /// </summary>
    /// <remarks>
    /// The counters are the easy half. The other half is "it gains this creature's other
    /// abilities until end of turn", which means copying whatever else the card has onto
    /// something else - so backup is only read when everything else the card has is a
    /// <em>keyword</em>, which can be granted. A card whose other abilities are triggered or
    /// activated leaves the line unread rather than handing out half of it.
    /// <para>
    /// The printed condition "if you put them on another creature" is not checked, and does not
    /// need to be: granting a creature the keywords it already prints changes nothing, so aiming
    /// backup at itself behaves the same either way.
    /// </para>
    /// </remarks>
    private static bool TryBackup(
        string line,
        CardDefinition card,
        int count,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var others = into.Count > 0;

        if (others || card.Keywords == KeywordAbility.None)
        {
            unhandled.Add(line);
            return false;
        }

        var entered = TriggerConditions.Parse("~ enters");
        if (entered is null)
        {
            unhandled.Add(line);
            return false;
        }

        if (EffectPhrase.Specs.Parse("target creature") is not { } creature)
        {
            unhandled.Add(line);
            return false;
        }

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "backup",
            Text = $"When {card.Name} enters, put {count} +1/+1 counter(s) on target creature. If "
                + "you put them on another creature, it gains this creature's other abilities "
                + "until end of turn.",
            Triggers = entered,
            Targets = [creature],
            Effects =
            [
                new PutCounters(State.CounterKinds.PlusOnePlusOne, count),
                new PumpUntilEndOfTurn(GenerativeEffects.GrantId(card.Keywords), 0),
            ],
        });

        return true;
    }

    /// <summary>
    /// The creature casualty sacrifices, and how big it has to be (CR 702.153a).
    /// </summary>
    /// <remarks>
    /// The same mechanism as conspire with a different price: one creature of a stated power
    /// rather than two of a stated colour. Both buy exactly one copy of the spell.
    /// </remarks>
    private static ChosenCost? CasualtyCostFor(int power)
    {
        if (EffectPhrase.Specs.Parse("target creature you control") is not { } creature)
            return null;

        return new ChosenCost(
            ChosenCostKind.SacrificePermanents,
            1,
            creature with
            {
                Description = $"a creature you control with power {power} or greater",

                // Composed into the ordinary filter rather than the source-aware one: the cost
                // check that guards a payment consults only this, and the question does not
                // need to know what is being cast.
                ObjectFilter = (state, abilities, obj, controller) =>
                    creature.ObjectFilter?.Invoke(state, abilities, obj, controller) != false
                    && State.Characteristics.Of(state, abilities, obj).Power >= power,
            });
    }

    /// <summary>
    /// The two creatures conspire taps, and what they have to be (CR 702.78a).
    /// </summary>
    /// <remarks>
    /// "That share a color with it" is a question about the spell, and the spell's colours are
    /// known when the card is compiled - so the filter closes over them rather than asking at
    /// resolution. A colourless spell can never be conspired, and the line is left unread rather
    /// than compiled to a cost nothing could ever pay.
    /// </remarks>
    private static ChosenCost? ConspireCostFor(CardDefinition card)
    {
        if (card.Colors.Count == 0)
            return null;

        if (EffectPhrase.Specs.Parse("target creature you control") is not { } creature)
            return null;

        var colours = card.Colors.ToImmutableHashSet();

        return new ChosenCost(
            ChosenCostKind.TapPermanents,
            2,
            creature with
            {
                Description = "an untapped creature you control that shares a color with it",
                ObjectFilter = (state, abilities, obj, controller) =>
                    creature.ObjectFilter?.Invoke(state, abilities, obj, controller) != false
                    && Characteristics.Of(state, abilities, obj).Colors.Any(colours.Contains),
            });
    }

    /// <summary>
    /// The card's own sentences read as if every "target" said "each" (CR 702.96a).
    /// </summary>
    /// <remarks>
    /// Overload does not change what a spell does, only how many things it does it to, and the
    /// card says so itself: the reminder text is "you may cast this spell for its overload cost,
    /// [and if you do] change its text by replacing all instances of 'target' with 'each'". So
    /// the second reading is produced the same way - by rewriting the printed words and handing
    /// them to the same phrase parser.
    /// <para>
    /// Anything the rewrite cannot be read as leaves the list empty, and an empty list makes the
    /// engine resolve the ordinary effects instead. That is the wrong spell rather than no spell,
    /// so the overload line is only accepted when every sentence survives the rewrite.
    /// </para>
    /// </remarks>
    private static ImmutableList<IEffect> OverloadedEffects(CardDefinition card)
    {
        var effects = ImmutableList.CreateBuilder<IEffect>();

        foreach (var line in Lines(card))
        {
            if (OverloadLine().IsMatch(line))
                continue;

            var rewritten = TargetWord().Replace(line, "each");

            if (!EffectPhrase.TryParse(rewritten, out var parsed) || !parsed.Targets.IsEmpty)
                return [];

            effects.AddRange(parsed.Effects);
        }

        return effects.ToImmutable();
    }

    /// <summary>
    /// "Embalm [cost]" and "Eternalize [cost]" - a Zombie copy from the graveyard
    /// (CR 702.128a, 702.129a).
    /// </summary>
    /// <remarks>
    /// The same ability twice over: exile this card from your graveyard to make a token copy of
    /// it that is a Zombie with no mana cost. Eternalize differs only in that the copy is a 4/4
    /// and black rather than the card's own size and white.
    /// <para>
    /// The token is built when the card is compiled rather than when the ability resolves,
    /// because everything it copies is printed - a card in a graveyard has no continuous effects
    /// on it to change any of it.
    /// </para>
    /// </remarks>
    private static bool TryEternalize(
        string line,
        CardDefinition card,
        ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = EternalizeLine().Match(line);
        if (!m.Success)
            return false;

        var eternal = m.Groups["kind"].Value.StartsWith(
            "eternalize", StringComparison.OrdinalIgnoreCase);

        var token = Abilities.TokenCards.AsToken(
            card,
            power: eternal ? 4 : null,
            toughness: eternal ? 4 : null,
            oracleId: (eternal ? "token-eternalized-" : "token-embalmed-") + card.OracleId);

        into.Add(new ActivatedAbilityDefinition
        {
            Id = eternal ? "eternalize" : "embalm",
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            FunctionsFrom = Zone.Graveyard,
            SelfCost = SelfCost.ExileSelfFromGraveyard,

            // CR 702.128a: "Embalm only as a sorcery."
            Timing = ActivationTiming.SorceryOnly,
            Effects = [new CreateToken(ZombieCopy(token, eternal))],
        });

        return true;
    }

    /// <summary>The token an embalm or eternalize ability makes (CR 702.128a).</summary>
    private static CardDefinition ZombieCopy(CardDefinition token, bool eternal) => new()
    {
        OracleId = token.OracleId,
        Name = token.Name,
        CardTypes = token.CardTypes,
        Subtypes = [.. token.Subtypes.Append("Zombie")],
        Supertypes = token.Supertypes,
        OracleText = token.OracleText,
        Power = token.Power,
        Toughness = token.Toughness,
        Keywords = token.Keywords,

        // No mana cost at all, and one colour: whatever the card was, the copy is a Zombie of
        // the keyword's colour (CR 702.128a).
        ColorIdentity = [eternal ? ManaColor.Black : ManaColor.White],
        Colors = [eternal ? ManaColor.Black : ManaColor.White],
        ImageUriNormal = token.ImageUriNormal,
        ImageUriLarge = token.ImageUriLarge,
        ImageUriSmall = token.ImageUriSmall,
        ImageUriArtCrop = token.ImageUriArtCrop,
    };

    /// <summary>
    /// "Transmute [cost]" - trade this card in for one that costs the same (CR 702.49a).
    /// </summary>
    /// <remarks>
    /// An ability of the card while it is in hand, paid for by discarding it - unearth's shape
    /// moved one zone over. "The same mana value as this card" is fixed when the card is
    /// compiled, because a card in hand has only its printed cost (CR 202.3) and nothing on the
    /// battlefield reaches it.
    /// </remarks>
    private static bool TryTransmute(
        string line,
        CardDefinition card,
        ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = TransmuteLine().Match(line);
        if (!m.Success)
            return false;

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "transmute",
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            FunctionsFrom = Zone.Hand,
            SelfCost = SelfCost.DiscardSelf,

            // CR 702.49a: "Activate only as a sorcery."
            Timing = ActivationTiming.SorceryOnly,
            Effects =
            [
                new SearchLibrary(
                    SearchFilters.AnyCard,
                    Zone.Hand,
                    ExactManaValue: card.Cmc),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Ninjutsu [cost]" - swap an unblocked attacker for the ninja (CR 702.49a).
    /// </summary>
    /// <remarks>
    /// The only ability in the engine that is activated from hand and puts its own card onto the
    /// battlefield. Everything unusual about it is in the cost: returning an attacker is what
    /// makes room for the ninja, and what the returned creature was attacking is what the ninja
    /// arrives attacking.
    /// </remarks>
    private static bool TryNinjutsu(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = NinjutsuLine().Match(line);
        if (!m.Success)
            return false;

        if (EffectPhrase.Specs.Parse("target unblocked creature you control") is not
            { Kind: Abilities.TargetKind.Permanent } attacker)
        {
            return false;
        }

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "ninjutsu",
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            FunctionsFrom = Zone.Hand,
            ChosenCosts =
            [
                new ChosenCost(
                    ChosenCostKind.ReturnToHand,
                    1,
                    attacker with { Description = "an unblocked attacker you control" }),
            ],
            Effects = [new PutSourceOntoBattlefieldAttacking()],
        });

        return true;
    }

    /// <summary>The counter cumulative upkeep piles up (CR 702.24a).</summary>
    private const string AgeCounter = "age";

    /// <summary>
    /// "Cumulative upkeep [cost]" - a tax that grows every turn (CR 702.24a).
    /// </summary>
    /// <remarks>
    /// Echo's shape - put a counter on, then pay or sacrifice - with the one difference that
    /// makes the mechanic what it is: the cost is charged once per counter, so it climbs by the
    /// printed amount every upkeep until it is not worth paying.
    /// <para>
    /// The counter goes on first and the offer reads it afterwards, which is the order the rule
    /// gives and also the only order that works: on the first upkeep there would otherwise be no
    /// counters and the tax would be free.
    /// </para>
    /// </remarks>
    private static bool TryCumulativeUpkeep(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = CumulativeUpkeepLine().Match(line);
        if (!m.Success)
            return false;

        var atUpkeep = TriggerConditions.Parse("the beginning of your upkeep");
        if (atUpkeep is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "cumulative-upkeep",
            Text = $"At the beginning of your upkeep, put an age counter on {card.Name}, then "
                + $"sacrifice it unless you pay {m.Groups["cost"].Value} for each age counter "
                + $"on it.",
            Triggers = atUpkeep,
            Effects =
            [
                new PutCountersOnSource(AgeCounter, 1),
                new MayPay(
                    ManaCostSpec.Parse(m.Groups["cost"].Value),
                    IfYouDo: [],
                    IfYouDont: [new SacrificeSource()],
                    EffectIndex: 1,
                    TimesCounter: AgeCounter),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Scavenge [cost]" - trade the card in for counters (CR 702.97a).
    /// </summary>
    /// <remarks>
    /// Unearth's shape with a different effect: an ability that functions from the graveyard,
    /// paid for by exiling the card it is printed on.
    /// <para>
    /// The number of counters is the card's <em>printed</em> power, fixed when the card is
    /// compiled. That is not a shortcut: a card in a graveyard has only its printed
    /// characteristics, because CR 613 applies to permanents, and by the time this resolves the
    /// card has been exiled to pay for it and is a different object anyway (CR 400.7).
    /// </para>
    /// </remarks>
    private static bool TryScavenge(
        string line, CardDefinition card, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = ScavengeLine().Match(line);
        if (!m.Success)
            return false;

        if (EffectPhrase.Specs.Parse("target creature") is not { } creature)
            return false;

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "scavenge",
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            FunctionsFrom = Zone.Graveyard,
            SelfCost = SelfCost.ExileSelfFromGraveyard,

            // CR 702.97a: "Scavenge only as a sorcery."
            Timing = ActivationTiming.SorceryOnly,
            Targets = [creature],
            Effects =
            [
                new PutCounters(
                    State.CounterKinds.PlusOnePlusOne, Math.Max(0, card.Power ?? 0)),
            ],
        });

        return true;
    }

    private static bool TryUnearth(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = UnearthLine().Match(line);
        if (!m.Success)
            return false;

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "unearth",
            Text = line,
            ManaCost = ManaCostSpec.Parse(m.Groups["cost"].Value),
            FunctionsFrom = Zone.Graveyard,
            // CR 702.83a: "Activate only as a sorcery."
            Timing = ActivationTiming.SorceryOnly,
            Effects = [new UnearthSource()],
        });

        return true;
    }

    /// <summary>
    /// "+1: Draw a card" — a loyalty ability (CR 606.1).
    /// </summary>
    /// <remarks>
    /// Oracle text writes the minus with U+2212 MINUS SIGN rather than a hyphen, which is why the
    /// pattern accepts both: matching only the hyphen finds every plus ability and no minus one,
    /// which is worse than finding neither because it half-implements the card.
    /// </remarks>
    private static bool TryLoyaltyAbility(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = LoyaltyLine().Match(line);
        if (!m.Success)
            return false;

        if (!EffectPhrase.TryParse(m.Groups["effect"].Value.Trim(), out var parsed))
            return false;

        var sign = m.Groups["sign"].Value;
        var magnitude = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var cost = sign is "\u2212" or "-" ? -magnitude : magnitude;

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "loyalty" + Suffix(into.Count),
            Text = line,
            LoyaltyCost = cost,
            // CR 606.3: sorcery timing, and only one of them each turn.
            Timing = ActivationTiming.SorceryOnly,
            Targets = parsed.Targets,
            Effects = parsed.Effects,
        });

        return true;
    }

    /// <summary>
    /// "Convoke" and "Improvise" — tapping permanents to pay part of the cost (CR 702.51a, 702.56a).
    /// </summary>
    /// <remarks>
    /// The two differ only in what may be tapped and whether colour counts, so they are one
    /// matcher. Neither is an alternative cost: the cost is what it says and some of it is paid
    /// with something other than mana, which is why this sets a field on the spell definition
    /// rather than replacing <see cref="SpellDefinition.AlternateCost"/>.
    /// </remarks>
    private static bool TryTapToPay(string line, ref TapToPay? into)
    {
        var m = TapToPayLine().Match(line);
        if (!m.Success)
            return false;

        var convoke = m.Groups["kind"].Value.StartsWith("convoke", StringComparison.OrdinalIgnoreCase);

        var what = EffectPhrase.Specs.Parse(
            convoke ? "target untapped creature you control" : "target untapped artifact you control");

        if (what is null)
            return false;

        into = new TapToPay(what, ColorMatters: convoke);
        return true;
    }

    /// <summary>
    /// "Crew N" — tap creatures with total power N to animate a Vehicle (CR 702.122a).
    /// </summary>
    /// <remarks>
    /// An activated ability printed as a word and a number, and the only cost in the game whose
    /// size is measured rather than counted: any number of creatures, so long as their power adds
    /// up. The permanent it animates keeps every type it already had — a crewed Vehicle is an
    /// artifact creature and still a Vehicle.
    /// </remarks>
    /// <summary>"Station" — tap a creature to charge this up (CR 702.184a).</summary>
    /// <remarks>
    /// Crew's cost with a different consequence: one creature rather than enough of them, and
    /// counters that stay rather than an animation that ends. The number of counters is the
    /// tapped creature's power, which is known when the cost is paid and gone by the time the
    /// ability resolves - so it is carried on the stack object as the amount the ability is
    /// about, the same field a trigger uses for "that many".
    /// </remarks>
    private static bool TryStation(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        if (!StationLine().IsMatch(line))
            return false;

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "station",
            Text = line,
            Timing = ActivationTiming.SorceryOnly,
            ChosenCosts =
            [
                new ChosenCost(
                    ChosenCostKind.TapPermanents,
                    Count: 1,
                    What: EffectPhrase.Specs.Parse("target untapped creature you control"),

                    // CR 702.184a says "another": a Spacecraft cannot station itself, and it is
                    // not a creature to tap in the first place until it is charged.
                    ExcludesSource: true),
            ],
            Effects =
            [
                new PutCountersOnSource(
                    CounterKinds.Charge,
                    new Amount(1) { Counter = context => context.SubjectAmount ?? 0 }),
            ],
        });

        return true;
    }

    /// <summary>
    /// "N+ | [abilities]" — what a station card is once it is charged (CR 721.2a, 721.2b).
    /// </summary>
    /// <remarks>
    /// Two effects, not one. CR 721.2b: at the threshold the permanent has the abilities
    /// <em>and</em> "is a creature with base power and toughness [P/T] in addition to its other
    /// types" - and the printed P/T is the card's own, which is why this needs the card and the
    /// other keyword matchers do not.
    /// <para>
    /// A Spacecraft that gained its keywords without becoming a creature would sit there with
    /// flying and first strike and never attack, which is a card doing nothing while looking
    /// entirely correct - the failure this compiler keeps producing when only half a rule is
    /// read.
    /// </para>
    /// </remarks>
    private static bool TryStationThreshold(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = StationThresholdLine().Match(line);
        if (!m.Success)
            return false;

        if (EffectPhrase.Keywords(m.Groups["abilities"].Value) is not { } keywords)
            return false;

        var need = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        bool Charged(GameState _, GameObject? source, CharacteristicsBuilder target) =>
            source is not null
            && target.Subject.Id == source.Id
            && source.Permanent is { } permanent
            && permanent.Counters.GetValueOrDefault(CounterKinds.Charge) >= need;

        into.Add(new ContinuousEffectDefinition
        {
            Id = "station:" + need.ToString(CultureInfo.InvariantCulture) + ":types",
            Layer = EffectLayer.Type,
            Applies = Charged,
            Apply = (_, _, builder) => builder.CardTypes |= CardType.Creature,
        });

        // Layer 7b, and it has to be: "base power and toughness" sets rather than modifies, so a
        // +1/+1 counter on the Spacecraft still counts on top of it (CR 613.4b).
        if (card.Power is { } power && card.Toughness is { } toughness)
        {
            into.Add(new ContinuousEffectDefinition
            {
                Id = "station:" + need.ToString(CultureInfo.InvariantCulture) + ":pt",
                Layer = EffectLayer.PowerToughnessSet,
                Applies = Charged,
                Apply = (_, _, builder) =>
                {
                    builder.Power = power;
                    builder.Toughness = toughness;
                },
            });
        }

        into.Add(new ContinuousEffectDefinition
        {
            Id = "station:" + need.ToString(CultureInfo.InvariantCulture) + ":abilities",
            Layer = EffectLayer.Ability,
            Applies = Charged,
            Apply = (_, _, builder) => builder.Keywords |= keywords,
        });

        return true;
    }

    private static bool TryCrew(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = CrewLine().Match(line);
        if (!m.Success)
            return false;

        var power = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "crew",
            Text = line,
            ChosenCosts =
            [
                new ChosenCost(
                    ChosenCostKind.TapPermanents,
                    Count: 0,
                    What: EffectPhrase.Specs.Parse("target untapped creature you control"),
                    // CR 702.122a says "other": a Vehicle cannot crew itself.
                    ExcludesSource: true,
                    MinTotalPower: power),
            ],
            Effects =
            [
                new PumpSourceUntilEndOfTurn(
                    GenerativeEffects.BecomesId(CardType.Artifact | CardType.Creature)),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Living metal" — a Vehicle that is an artifact creature while it is your turn
    /// (CR 702.161a).
    /// </summary>
    /// <remarks>
    /// Crew's animation with the cost taken off and a condition put in its place: the same layer 4
    /// type change, granted by whose turn it is rather than bought by tapping creatures.
    /// <para>
    /// Written as a conditional static rather than as anything that has to be undone, which is
    /// what makes the other half of the keyword work for free — the Vehicle stops being a creature
    /// the moment the turn passes, with nothing to remember it by. A reading that animated it
    /// "until end of turn" would have played identically all through the controller's own turn and
    /// then let it block, which is a strictly better card than the one printed and the sort of
    /// thing coverage cannot see.
    /// </para>
    /// <para>
    /// The condition goes through <see cref="BoardConditions"/> instead of asking the state here,
    /// so "your turn" means one thing across the whole compiler. If that vocabulary ever stops
    /// reading the phrase this leaves the line unread rather than animating the Vehicle on
    /// nobody's terms, because a Vehicle that is a creature on the wrong turn attacks on turns it
    /// may not.
    /// </para>
    /// <para>
    /// 13 faces carry the line and every one is the back of a two-faced card, so each held back a
    /// whole card: a card is playable only when every face of it reads.
    /// </para>
    /// </remarks>
    private static bool TryLivingMetal(
        string line, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        if (!LivingMetalLine().IsMatch(line))
            return false;

        if (BoardConditions.Parse("during your turn") is not { } yourTurn)
            return false;

        into.Add(new ContinuousEffectDefinition
        {
            Id = "living-metal",

            // Type-changing is layer 4, and it has to run before the lord in layer 6 and the
            // pumps in layer 7 so that they see the creature it has just made (CR 613.1d).
            Layer = EffectLayer.Type,
            Applies = (state, source, target) =>
                source is not null
                && target.Subject.Id == source.Id
                && yourTurn(state, EmptyAbilities.Instance, source),

            // "An artifact creature in addition to its other types" - both words, even though
            // every card printing this is already an artifact. The rule says both, and a Vehicle
            // that had lost its artifact type to something else would otherwise be animated into
            // a bare creature that no artifact removal could touch.
            Apply = (_, _, builder) => builder.CardTypes |= CardType.Artifact | CardType.Creature,
        });

        return true;
    }

    /// <summary>
    /// "Saddle N" — tap creatures with total power N to saddle a Mount (CR 702.171a).
    /// </summary>
    /// <remarks>
    /// Crew's cost with a different consequence, and that is the whole of it: the same
    /// measured-rather-than-counted tap cost, and instead of animating the permanent it sets a
    /// status its own triggers ask about. Sorcery-speed, which crew is not - a Vehicle can be
    /// crewed at instant speed and a Mount cannot be saddled that way.
    /// </remarks>
    private static bool TrySaddle(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = SaddleLine().Match(line);
        if (!m.Success)
            return false;

        var power = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "saddle",
            Text = line,
            Timing = ActivationTiming.SorceryOnly,
            ChosenCosts =
            [
                new ChosenCost(
                    ChosenCostKind.TapPermanents,
                    Count: 0,
                    What: EffectPhrase.Specs.Parse("target untapped creature you control"),

                    // CR 702.171a says "other": a Mount cannot saddle itself.
                    ExcludesSource: true,
                    MinTotalPower: power),
            ],
            Effects = [new SaddleSource()],
        });

        return true;
    }

    /// <summary>
    /// "As ~ enters, you may pay N life. If you don't, it enters tapped."
    /// </summary>
    /// <remarks>
    /// The shockland, and the one shape in the corpus whose *two* outcomes are both different
    /// from the event: paying makes it arrive upright and declining makes it arrive tapped, while
    /// letting the arrival happen unchanged - upright and free - is the one thing the card never
    /// does. So it is an optional replacement with a <c>Decline</c>, which is what that hook
    /// exists for.
    /// <para>
    /// CR 118.4: a player may pay life only down to zero, so a player who cannot afford it is not
    /// offered the choice and the land simply arrives tapped. Asked in <c>Applies</c> rather than
    /// inside the payment, because an offer that cannot be taken is not an offer.
    /// </para>
    /// </remarks>
    /// <summary>
    /// "If ~ would be put into a graveyard from anywhere, exile it instead" (CR 614.1c).
    /// </summary>
    /// <remarks>
    /// A self-replacement, and the commonest line on the back face of a card that must not come
    /// back: 25 faces carry it, and every one of them left its whole card unplayable. "From
    /// anywhere" is not decoration — it is why <see cref="ReplacementEffectDefinition.FunctionsFrom"/>
    /// has to be null. The permanent is usually on the battlefield when this applies, but the same
    /// sentence catches the card being milled, discarded or countered, and a replacement that only
    /// functioned from the battlefield would let all three through.
    /// <para>
    /// The destination is the only thing that changes. Keeping the same new id means whatever was
    /// going to observe the move still observes one — the card is a new object either way
    /// (CR 400.7), and it is a new object in exile rather than in the graveyard.
    /// </para>
    /// </remarks>
    private static bool TryGraveyardReplacement(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = GraveyardReplacementLine().Match(line);
        if (!m.Success)
            return false;

        var bottom = m.Groups["where"].Value.StartsWith(
            "put it on the bottom", StringComparison.OrdinalIgnoreCase);

        var destination = bottom ? Zone.Library : Zone.Exile;

        into.Add(new ReplacementEffectDefinition
        {
            Id = bottom ? "graveyard-to-library" : "graveyard-to-exile",
            FunctionsFrom = null,
            Applies = (e, _, source) =>
                e is Events.ObjectMoved { To: Zone.Graveyard } bound && bound.OldId == source.Id,
            Replace = (e, _, _) =>
            {
                var move = (Events.ObjectMoved)e;

                return
                [
                    move with
                    {
                        To = destination,
                        Position = bottom ? ZonePosition.Bottom : ZonePosition.Top,
                    },
                ];
            },
        });

        return true;
    }

    private static bool TryShockland(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = ShocklandLine().Match(line);
        if (!m.Success)
            return false;

        var life = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        into.Add(new ReplacementEffectDefinition
        {
            Id = "shockland",
            FunctionsFrom = null,
            IsOptional = true,
            Applies = (e, state, source) =>
                Arriving(e, source) is not null
                && state.GetPlayer(source.ControllerId).Life >= life,

            // Applying is paying: the life goes, and the arrival happens as it would have.
            Replace = (e, state, source) =>
            [
                new LifeChanged(
                    source.ControllerId, -life, state.GetPlayer(source.ControllerId).Life - life),
                e,
            ],

            // Declining is the other half of the same sentence.
            Decline = (e, _, source) => [e, new PermanentTapped(Arriving(e, source)!.Value)],
        });

        return true;
    }

    /// <summary>
    /// "~ enters tapped" — a replacement applied to the move onto the battlefield (CR 614.1c).
    /// </summary>
    /// <remarks>
    /// The commonest line on lands, and a replacement rather than a trigger: the permanent is
    /// never untapped for an instant, so nothing can respond in between and no ability sees it
    /// arrive upright.
    /// </remarks>
    /// <summary>"~ enters prepared."</summary>
    /// <remarks>
    /// A replacement rather than an enters-the-battlefield trigger, so that the permanent is
    /// prepared from the moment it arrives rather than a priority window later. The difference is
    /// visible: a trigger uses the stack, and anything that answered it would be answering a
    /// permanent that was not yet holding its spell.
    /// </remarks>
    private static bool TryEntersPrepared(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        if (!PreparedLine().IsMatch(line))
            return false;

        into.Add(new ReplacementEffectDefinition
        {
            Id = "enters-prepared",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null,
            Replace = (e, _, source) => [e, new BecamePrepared(Arriving(e, source)!.Value)],
        });

        return true;
    }

    private static bool TryEntersTapped(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = EntersTapped().Match(line);
        if (!m.Success)
            return false;

        // "…unless you control two or more other lands" — the slow-land cycles. The condition is
        // read into a board query rather than matched as a whole line, because the same handful
        // of counting questions recur across dozens of lands with different nouns.
        Func<GameState, IAbilitySource, GameObject, bool>? unless = null;
        if (m.Groups["unless"].Success)
        {
            unless = BoardConditions.Parse(m.Groups["unless"].Value.Trim());
            if (unless is null)
                return false;
        }

        into.Add(new ReplacementEffectDefinition
        {
            Id = "enters-tapped",
            // Anywhere, because where the card is depends on how it is arriving: a land is played
            // from hand and never touches the stack (CR 305.1), while a permanent spell with the
            // same words is on the stack. Pinning this to the stack made every enters-tapped land
            // arrive untapped.
            FunctionsFrom = null,
            Applies = (e, state, source) =>
                Arriving(e, source) is not null
                // CR 614.1c: the replacement simply does not apply when the condition is met, so
                // the land arrives upright and nothing has to untap it afterwards.
                && (unless is null || !unless(state, EmptyAbilities.Instance, source)),
            Replace = (e, _, source) => [e, new PermanentTapped(Arriving(e, source)!.Value)],
        });

        return true;
    }

    /// <summary>
    /// "If damage would be dealt to ~, prevent that damage. Remove a +1/+1 counter from ~."
    /// </summary>
    /// <remarks>
    /// The Phantom cycle. The prevention is unconditional and the removal is what kills the
    /// creature: these are printed 0/0 and enter with counters, so running out of them is lethal
    /// by state-based action (CR 704.5f) rather than by the damage that was prevented. That is
    /// why nothing here checks whether a counter is there to remove — a creature with none is
    /// already dying, and adding a condition would make it immortal instead.
    /// <para>
    /// One counter however much damage is dealt, which is what the sentence says: the amount is
    /// not read, and the two clauses are one replacement rather than a prevention and a separate
    /// effect, because the removal must not happen when the damage is not being dealt.
    /// </para>
    /// </remarks>
    private static bool TryPhantomDamage(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        if (!PhantomDamageLine().IsMatch(line))
            return false;

        into.Add(new ReplacementEffectDefinition
        {
            Id = "phantom-damage",
            FunctionsFrom = Zone.Battlefield,
            Applies = (e, _, source) => e is DamageMarked marked && marked.Id == source.Id,
            Replace = (_, _, source) =>
                [new CountersChanged(source.Id, CounterKinds.PlusOnePlusOne, -1)],
        });

        return true;
    }

    /// <summary>When a "cast this only ..." line allows the spell to be cast (CR 601.3e).</summary>
    /// <remarks>
    /// Every arm is a question about the turn, and the ones that mention combat mean the phase
    /// rather than any particular step. "You've been attacked this step" is read from the combat
    /// state rather than from a tally: an attacker whose target is this player *is* the fact, and
    /// it is already recorded because the declaration put it there.
    /// </remarks>
    private static Func<GameState, Guid, bool>? CastRestriction(string when) => when switch
    {
        "during combat" => (state, _) => InCombat(state),
        "during the declare attackers step" =>
            (state, _) => state.CurrentStep == TurnStep.DeclareAttackers,
        "during the declare blockers step" =>
            (state, _) => state.CurrentStep == TurnStep.DeclareBlockers,

        "during combat before blockers are declared" =>
            (state, _) => InCombat(state) && state.CurrentStep < TurnStep.DeclareBlockers,
        "during combat after blockers are declared" =>
            (state, _) => InCombat(state) && state.CurrentStep > TurnStep.DeclareBlockers,

        "before the combat damage step" => (state, _) => state.CurrentStep < TurnStep.CombatDamage,
        "after combat" => (state, _) => state.CurrentStep > TurnStep.EndOfCombat,

        "during combat on an opponent's turn" =>
            (state, who) => InCombat(state) && state.ActivePlayerId != who,
        "during combat on your turn" =>
            (state, who) => InCombat(state) && state.ActivePlayerId == who,

        "if you've cast another spell this turn" =>
            (state, who) => state.GetPlayer(who).SpellsCastThisTurn > 0,

        "during the declare attackers step and only if you've been attacked this step" =>
            (state, who) => state.CurrentStep == TurnStep.DeclareAttackers
                && state.Combat.Attackers.Values.Any(target => target.DefendingPlayer == who),

        _ => null,
    };

    /// <summary>Whether the game is in the combat phase (CR 506.1).</summary>
    private static bool InCombat(GameState state) =>
        state.CurrentStep >= TurnStep.BeginningOfCombat
        && state.CurrentStep <= TurnStep.EndOfCombat;

    /// <summary>
    /// "If ~ was kicked, it enters with two +1/+1 counters on it" (CR 702.33e).
    /// </summary>
    /// <remarks>
    /// Readable only because the kicker flag survives the move onto the battlefield: the spell
    /// that was kicked and the permanent that arrives are different objects (CR 400.7), and
    /// CR 607.2 is the linked-ability rule that carries the fact across. A token has no such
    /// flag and was never kicked, so it arrives bare, which is the right answer rather than an
    /// omission.
    /// </remarks>
    private static bool TryKickedCounters(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = KickedCountersLine().Match(line);
        if (!m.Success)
            return false;

        var count = NumberWordOrDigits(m.Groups["n"].Value);

        into.Add(new ReplacementEffectDefinition
        {
            Id = "kicked-counters",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null && source.WasKicked,
            Replace = (e, _, source) =>
            [
                e,
                new CountersChanged(
                    Arriving(e, source)!.Value, CounterKinds.PlusOnePlusOne, count),
            ],
        });

        return true;
    }

    /// <summary>
    /// "~ enters with a +1/+1 counter on it for each time it was kicked" (CR 702.33c).
    /// </summary>
    /// <remarks>
    /// The same replacement as its once-only neighbour with a number in place of a constant, and
    /// the number is the one the spell recorded rather than anything re-derived: by the time the
    /// permanent arrives the spell has stopped existing (CR 400.7), so the count has to have been
    /// carried across with it (CR 607.2).
    /// <para>
    /// A spell that was not kicked at all arrives with nothing, and it gets there by the count
    /// being zero rather than by a separate check - which is also why this does not test the flag
    /// the way the once-only version does.
    /// </para>
    /// </remarks>
    private static bool TryMultikickedCounters(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        if (!MultikickedCountersLine().IsMatch(line))
            return false;

        into.Add(new ReplacementEffectDefinition
        {
            Id = "multikicked-counters",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null && source.TimesKicked > 0,
            Replace = (e, _, source) =>
            [
                e,
                new CountersChanged(
                    Arriving(e, source)!.Value,
                    CounterKinds.PlusOnePlusOne,
                    source.TimesKicked),
            ],
        });

        return true;
    }

    /// <summary>
    /// "Sunburst" — it enters with a counter for each colour of mana spent to cast it
    /// (CR 702.43a).
    /// </summary>
    /// <remarks>
    /// The counter is +1/+1 on a creature and a charge counter on anything else, which is the
    /// keyword's own rule rather than a reading of the type line. Colours spent, not colours in
    /// the cost: a generic symbol paid with a green mana is a green mana spent, and one paid
    /// with colourless is no colour at all (CR 106.1b) — so a sunburst permanent cast entirely
    /// with colourless enters bare, which is what the card says.
    /// </remarks>
    private static bool TrySunburst(
        string line, CardDefinition card, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var spelledOut = SpelledOutSunburstLine().IsMatch(line);
        if (!spelledOut && !SunburstLine().IsMatch(line))
            return false;

        // The keyword chooses the counter by type; the spelled-out sentence names +1/+1 itself,
        // and the card is what says which. Sharing the body rather than writing a second reader
        // is what stops the two spellings drifting apart on a rule they both describe.
        var kind = spelledOut || card.CardTypes.HasFlag(CardType.Creature)
            ? CounterKinds.PlusOnePlusOne
            : "charge";

        into.Add(new ReplacementEffectDefinition
        {
            Id = "sunburst",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null,
            Replace = (e, state, source) =>
            {
                var arrived = Arriving(e, source)!.Value;

                // Read off the object that was cast, which is the one still in the state before
                // the move is folded in - the permanent that arrives is a different object and
                // carries the colours only afterwards.
                var spent = e is ObjectMoved move && state.TryGetObject(move.OldId, out var cast)
                    ? cast.ManaSpent.Colored.Count(each => each.Value > 0)
                    : 0;

                return spent > 0
                    ? [e, new CountersChanged(arrived, kind, spent)]
                    : [e];
            },
        });

        return true;
    }

    /// <summary>
    /// The id this permanent will have if the event is it arriving on the battlefield.
    /// </summary>
    /// <remarks>
    /// A permanent arrives two ways and the card cannot tell which: a card moves there and gets a
    /// new id (CR 400.7), while a token is created there (CR 111.1) and has no earlier id at all.
    /// Every entry replacement was written against the move alone, so a token copy of a creature
    /// that enters with counters entered without them. One reader rather than five copies of the
    /// same cast, because the five were identical and wrong in the same way.
    /// </remarks>
    internal static ObjectId? Arriving(GameEvent e, GameObject source) => e switch
    {
        ObjectMoved { To: Zone.Battlefield } moved when moved.OldId == source.Id => moved.NewId,
        ObjectCreated { Zone: Zone.Battlefield } made when made.Id == source.Id => made.Id,
        _ => null,
    };

    /// <summary>A mana ability: a cost, then "Add" (CR 605.1a).</summary>
    /// <summary>
    /// Reads the text inside quotation marks as an ability, for a grant that has to be rebuilt
    /// from nothing but its own name.
    /// </summary>
    /// <remarks>
    /// The same three readers a printed ability goes through, so an ability understood on a card
    /// is understood inside quotation marks and again when a temporary grant is rebuilt from a
    /// generated definition id. A placeholder card carries the name: the tilde inside a granted
    /// ability means whatever the ability ends up on, which is resolved when it applies rather
    /// than when it is read.
    /// </remarks>
    internal static bool TryQuotedAbility(
        string text,
        out ImmutableList<ActivatedAbilityDefinition> activated,
        out ImmutableList<TriggeredAbilityDefinition> triggered)
    {
        activated = [];
        triggered = [];

        var quoted = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();
        var quotedTriggers = ImmutableList.CreateBuilder<TriggeredAbilityDefinition>();
        var rejected = ImmutableList.CreateBuilder<string>();

        var bearer = new CardDefinition { OracleId = "granted", Name = "~" };

        var modalAt = -1;
        var isTrigger = TryTrigger(text, bearer, quotedTriggers, rejected, ref modalAt)
            && rejected.Count == 0
            && quotedTriggers.Count > 0;

        if (!isTrigger
            && !TryManaAbility(text, quoted)
            && (!TryActivatedAbility(text, bearer, quoted, rejected) || rejected.Count > 0))
        {
            return false;
        }

        if (quoted.Count == 0 && quotedTriggers.Count == 0)
            return false;

        activated = quoted.ToImmutable();
        triggered = quotedTriggers.ToImmutable();
        return true;
    }

    private static bool TryManaAbility(
        string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        // "Activate only if you control a Plains" is printed as a sentence after the ability,
        // so it has to come off before the mana grammar sees the line. Left on, the line falls
        // through to the general activated-ability path and the card compiles to something that
        // uses the stack — which a mana ability must never do (CR 605.3b), and which an opponent
        // could then respond to.
        var printed = line;
        Func<GameState, IAbilitySource, GameObject, bool>? onlyIf = null;
        var timing = ActivationTiming.AnyTime;
        int? manaLimit = null;

        // "Activate only once each turn" is a cap on how often, not a condition on when - and
        // the timing pattern matches it too, because both start "Activate only". Lifted first for
        // that reason. Left to the timing branch it was refused for not being a board condition,
        // the whole line fell through to the general activated-ability path, and the card
        // compiled into an ability that **uses the stack** - which a mana ability must never do
        // (CR 605.3b) and which an opponent could then respond to. 110 corpus lines say it.
        var capped = ActivationLimit().Match(line);
        if (capped.Success)
        {
            manaLimit = capped.Groups["n"].Value.Length == 0
                ? 1
                : NumberWord(capped.Groups["n"].Value);

            line = ActivationLimit().Replace(line, string.Empty).Trim();
        }

        if (ActivationTimingLine().Match(line) is { Success: true } gated)
        {
            var when = gated.Groups["when"].Value.Trim();

            // "Activate only as a sorcery" is a phase restriction and belongs in the same field
            // it does on every other ability. It was refused here for the same reason the cap
            // was, and with the same result.
            if (TimingNamed(when) is { } named)
            {
                timing = named;
            }
            else if (when.StartsWith("if ", StringComparison.OrdinalIgnoreCase)
                && BoardConditions.Parse(when[3..]) is { } asked)
            {
                onlyIf = asked;
            }
            else
            {
                return false;
            }

            line = ActivationTimingLine().Replace(line, string.Empty).Trim();
        }

        var m = ManaAbilityLine().Match(line);
        if (!m.Success)
            return false;

        // The same cost reader every other activated ability uses. This path used to lift out a
        // self-sacrifice by hand and demand that whatever was left be payable mana, so a mana
        // ability whose cost was anything else - "Remove a charge counter from ~", "Tap an
        // untapped creature you control" - fell straight through and the line went unread, even
        // though the ability it would have produced was already expressible.
        if (ReadCost(m.Groups["cost"].Value) is not { } paid)
            return false;

        var alternatives = ManaWords.Alternatives(m.Groups["mana"].Value);

        // An alternative that produces nothing is not a mana ability, it is a misread one. The
        // pattern used to run to the end of the line, so "Add {C}{C}. This land doesn't untap
        // during your next untap step" arrived here whole: the words after the full stop are not
        // mana, the reader shrugged them off, and what came back was one alternative producing
        // no mana at all. The card compiled, looked complete, and tapped for nothing.
        if (alternatives.IsEmpty || alternatives.Any(a => a.IsEmpty))
            return false;

        // Whatever followed the mana is the rest of the ability - it happens on resolution like
        // any other effect (CR 605.3b). A tail the sentence vocabulary cannot read leaves the
        // whole line unread, because a land that pays and then does not pay the price back is a
        // strictly better card than the one printed.
        var rider = ImmutableList<IEffect>.Empty;
        ManaRestriction? restriction = null;
        if (m.Groups["rest"].Success)
        {
            var tail = m.Groups["rest"].Value.Trim();

            // "Activate only once each turn" is a restriction rather than an effect, and it is
            // printed at the end of a mana ability exactly as it is at the end of any other
            // (CR 602.5b). Lifted off first, so what remains is only the part that happens.
            var cappedTail = ActivationLimit().Match(tail);
            if (cappedTail.Success)
            {
                manaLimit ??= cappedTail.Groups["n"].Value.Length == 0
                    ? 1
                    : NumberWord(cappedTail.Groups["n"].Value);

                tail = ActivationLimit().Replace(tail, string.Empty).Trim();
            }

            // "Spend this mana only to cast creature spells" is a restriction on the mana rather
            // than something the ability does, so it is lifted off the same way (CR 106.6).
            if (EffectPhrase.RestrictionFor(tail) is { } only)
            {
                restriction = only;
                tail = string.Empty;
            }

            // A rider that wants a target is not one a mana ability can carry: an ability with a
            // target is not a mana ability at all (CR 605.1a), and it would have to use the
            // stack. Left unread rather than quietly stripped of the target.
            if (tail.Length > 0)
            {
                if (!EffectPhrase.TryParse(tail, out var parsedRider)
                    || parsedRider.Effects.IsEmpty
                    || !parsedRider.Targets.IsEmpty)
                {
                    return false;
                }

                rider = parsedRider.Effects;
            }
        }

        foreach (var produces in alternatives)
        {
            into.Add(new ActivatedAbilityDefinition
            {
                Id = "mana" + Suffix(into.Count),
                Text = alternatives.Count == 1 ? printed : printed + " — " + Describe(produces),
                RequiresTap = paid.RequiresTap,
                ManaCost = paid.Mana,
                SelfCost = paid.SelfCost,
                LifeCost = paid.Life,
                CounterCost = paid.Counters,
                CounterCostIsChosen = paid.CountersChosen,
                EnergyCost = paid.Energy,
                ChosenCosts = paid.Chosen,
                ActivateOnlyIf = onlyIf,
                Timing = timing,
                Produces = restriction is { } limited
                    ? [.. produces.Select(one => one with { Restriction = limited })]
                    : produces,
                Effects = rider,
                MaxActivationsPerTurn = manaLimit,
            });
        }

        return true;
    }

    /// <summary>Any other activated ability: "[cost]: [effect]" (CR 602.1).</summary>
    private static bool TryActivatedAbility(
        string line,
        CardDefinition card,
        ImmutableList<ActivatedAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled)
    {
        var m = ActivatedLine().Match(line);
        if (!m.Success)
            return false;

        // A quoted ability brings its own colon - "target creature gains \"{T}: Add {G}\"" - and
        // this pattern splits on the first one it finds. Splitting inside the quotation marks
        // tears the sentence in half and files the whole line as unread, before the spell parser
        // that would have understood it ever runs. An activation cost is never quoted, so a colon
        // that comes after a quotation mark is not this reader's colon.
        var firstQuote = line.IndexOf('"', StringComparison.Ordinal);
        if (firstQuote >= 0 && line.IndexOf(':', StringComparison.Ordinal) > firstQuote)
            return false;

        var cost = m.Groups["cost"].Value.Trim();
        var effectText = m.Groups["effect"].Value.Trim();

        // "Activate only once each turn" is a sentence tacked onto the end of the effect, not a
        // separate line — so it is lifted off before the effect itself is read (CR 602.5b).
        int? limit = null;
        var limited = ActivationLimit().Match(effectText);
        if (limited.Success)
        {
            limit = limited.Groups["n"].Value.Length == 0
                ? 1
                : NumberWord(limited.Groups["n"].Value);
            effectText = ActivationLimit().Replace(effectText, string.Empty).Trim();
        }

        // "Any player may activate this ability" — a sentence about who, tacked onto the end of
        // the effect the same way a timing restriction is (CR 602.2).
        var anyone = AnyPlayerLine().IsMatch(effectText);
        if (anyone)
            effectText = AnyPlayerLine().Replace(effectText, string.Empty).Trim();

        // "Activate only as a sorcery" and its relatives are the same shape: a restriction
        // printed as a sentence at the end of the effect rather than as part of the cost
        // (CR 602.5d). Lifted off here so the effect behind it can be read.
        var timing = ActivationTiming.AnyTime;
        Func<GameState, IAbilitySource, GameObject, bool>? onlyIf = null;
        var restricted = ActivationTimingLine().Match(effectText);
        if (restricted.Success)
        {
            var when = restricted.Groups["when"].Value.Trim();

            if (TimingNamed(when) is { } named)
            {
                timing = named;
            }
            else if (when.StartsWith("if ", StringComparison.OrdinalIgnoreCase)
                && BoardConditions.Parse(when[3..]) is { } asked)
            {
                // "Activate only if you control a Plains" is a question about the board rather
                // than about the phase, so it lands beside the timing rule instead of in it.
                onlyIf = asked;
            }
            else
            {
                // A restriction that cannot be honoured must not be dropped: an ability with no
                // rule where the card prints one is a strictly better card.
                unhandled.Add(line);
                return true;
            }

            effectText = ActivationTimingLine().Replace(effectText, string.Empty).Trim();
        }

        // A cost is a comma-separated list of items (CR 601.2f), and only some of them are mana.
        // Each recognised item is lifted out; whatever is left has to be mana and tap symbols,
        // because an ability whose cost is not fully charged is a free ability — a worse outcome
        // than an unimplemented one.
        if (ReadCost(cost) is not { } paid || !EffectPhrase.TryParse(effectText, out var parsed))
        {
            unhandled.Add(line);
            return true;
        }

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "a" + Suffix(into.Count),
            Text = line,
            RequiresTap = paid.RequiresTap,
            ManaCost = paid.Mana,
            SelfCost = paid.SelfCost,
            LifeCost = paid.Life,
            ChosenCosts = paid.Chosen,
            CounterCost = paid.Counters,
            CounterCostIsChosen = paid.CountersChosen,
            EnergyCost = paid.Energy,
            MaxActivationsPerTurn = limit,
            Timing = timing,
            ActivateOnlyIf = onlyIf,
            AnyPlayerMayActivate = anyone,
            // CR 602.5: an ability paid for by exiling the card from the graveyard is an ability
            // of the card in the graveyard, so that is where it has to function from. The two
            // have to agree or nothing can ever activate it.
            // And an ability that returns its own card from the graveyard is an ability of
            // that card in the graveyard for the same reason: it is the only place the card can
            // be for the ability to mean anything, and left on the battlefield it would compile
            // cleanly and never be offered.
            FunctionsFrom = paid.SelfCost switch
            {
                SelfCost.ExileSelfFromGraveyard => Zone.Graveyard,

                // CR 701.8a: a card is discarded from its owner's hand, so an ability whose cost
                // is discarding this card is an ability of the card *in hand*. Left on the
                // battlefield it compiles cleanly and can never be activated - which is why
                // cycling and its relatives each set this by hand, one reader at a time.
                SelfCost.DiscardSelf => Zone.Hand,

                _ => parsed.Effects.Any(e => e is ReturnSourceToBattlefield)
                    ? Zone.Graveyard
                    : Zone.Battlefield,
            },
            Targets = parsed.Targets,
            Effects = parsed.Effects,
        });

        return true;
    }

    /// <summary>Everything an activation cost charges, or null if some of it cannot be charged.</summary>
    private readonly record struct PaidCost(
        ManaCostSpec Mana,
        bool RequiresTap,
        SelfCost SelfCost,
        int Life,
        ImmutableList<ChosenCost> Chosen,
        (string Kind, int Count)? Counters,
        int Energy,
        bool CountersChosen = false);

    /// <summary>
    /// Reads an activation cost into the pieces the engine knows how to charge (CR 601.2f-h).
    /// </summary>
    /// <remarks>
    /// Written as "lift out what is understood, then require the remainder to be mana" rather
    /// than as a list of whole-cost patterns. The items combine freely — "{1}, {T}, Pay 2 life,
    /// Sacrifice this creature" is four of them — so matching whole costs would need one pattern
    /// per combination, which is the same product-not-sum mistake the target grammar fixed.
    /// <para>
    /// Costs that require a <em>choice</em> — sacrificing some other creature, discarding a card
    /// you pick — come out as <see cref="ChosenCost"/>s rather than being charged here, because
    /// the pick belongs to the player. It arrives with the action instead of being asked for
    /// during it: a suspended cost payment would be a continuation, and a log cannot rebuild one.
    /// </para>
    /// </remarks>
    private static PaidCost? ReadCost(string cost)
    {
        var self = SelfCost.None;
        var life = 0;
        var energy = 0;
        var chosen = ImmutableList.CreateBuilder<ChosenCost>();
        var remaining = cost;

        void Lift(Regex pattern) =>
            remaining = pattern.Replace(remaining, string.Empty).Trim().Trim(',').Trim();

        if (SacrificeSelfCost().IsMatch(remaining))
        {
            self = SelfCost.SacrificeSelf;
            Lift(SacrificeSelfCost());
        }
        else if (ReturnSelfCost().IsMatch(remaining))
        {
            self = SelfCost.ReturnSelfToHand;
            Lift(ReturnSelfCost());
        }
        else if (ExileSelfFromGraveyardCost().IsMatch(remaining))
        {
            self = SelfCost.ExileSelfFromGraveyard;
            Lift(ExileSelfFromGraveyardCost());
        }
        else if (DiscardSelfCost().IsMatch(remaining))
        {
            self = SelfCost.DiscardSelf;
            Lift(DiscardSelfCost());
        }

        // "Remove a charge counter from ~" — the storage pattern. Counters of any name already
        // live on the permanent; this is only a way to spend them.
        (string Kind, int Count)? counters = null;
        var chosenCount = false;
        var removed = RemoveCounterCost().Match(remaining);
        if (removed.Success)
        {
            // "Any number" is a number the player names as they activate it, so the count
            // recorded here is the floor - one - and the price arrives with the activation.
            chosenCount = removed.Groups["any"].Success;

            counters = (
                removed.Groups["kind"].Value.Trim().ToLowerInvariant(),
                chosenCount ? 0 : NumberWordOrDigits(removed.Groups["n"].Value));

            Lift(RemoveCounterCost());
        }

        // "Pay {E}{E}" — energy, which looks like mana and is not: it comes out before the rest
        // is handed to the mana parser, which would otherwise refuse a symbol it has never heard
        // of and lose the whole cost line.
        var paidEnergy = PayEnergyCost().Match(remaining);
        if (paidEnergy.Success)
        {
            energy = paidEnergy.Groups["e"].Value.Length / "{E}".Length;
            Lift(PayEnergyCost());
        }

        var paidLife = PayLifeCost().Match(remaining);
        if (paidLife.Success)
        {
            life = int.Parse(paidLife.Groups["n"].Value, CultureInfo.InvariantCulture);
            Lift(PayLifeCost());
        }

        // "Sacrifice a creature", "Discard a card" — a cost the player pays by choosing, which
        // arrives with the activation rather than being asked for during it.
        var sacrifice = SacrificeChosenCost().Match(remaining);
        if (sacrifice.Success)
        {
            if (SacrificeSpec(sacrifice.Groups["what"].Value) is not { } what)
                return null;

            chosen.Add(new ChosenCost(
                ChosenCostKind.SacrificePermanents,
                1,
                what,
                ExcludesSource: sacrifice.Groups["scope"].Value
                    .StartsWith("another", StringComparison.OrdinalIgnoreCase)));

            Lift(SacrificeChosenCost());
        }

        var tapped = TapChosenCost().Match(remaining);
        if (tapped.Success)
        {
            // "Tap two untapped creatures you control" - the same cost as tapping one, counted.
            // The noun is singularised because the target grammar names one thing; how many is
            // carried by the cost rather than by the words.
            var howMany = tapped.Groups["n"].Value.ToLowerInvariant() switch
            {
                "two" => 2,
                "three" => 3,
                "four" => 4,
                "five" => 5,
                _ => 1,
            };

            var noun = tapped.Groups["what"].Value.Trim();
            if (howMany > 1)
                noun = EffectPhrase.Singular(noun);

            if (EffectPhrase.Specs.Parse("target " + noun) is not { } what)
                return null;

            chosen.Add(new ChosenCost(ChosenCostKind.TapPermanents, howMany, what));
            Lift(TapChosenCost());
        }

        var exiled = ExileFromGraveyardCost().Match(remaining);
        if (exiled.Success)
        {
            var what = exiled.Groups["what"].Value.Trim();

            // The filter is optional and the count was not read at all: "exile two cards from
            // your graveyard" is the same cost as "exile a creature card" with the two halves
            // varied independently, and requiring a filter word left the plain form unread.
            var described = what.Length == 0
                ? "target card in your graveyard"
                : $"target {what} card in your graveyard";

            if (EffectPhrase.Specs.Parse(described) is not
                { Kind: Abilities.TargetKind.CardInGraveyard } fuel)
            {
                return null;
            }

            var howMany = exiled.Groups["n"].Value.StartsWith('a')
                ? 1
                : NumberWordOrDigits(exiled.Groups["n"].Value);

            chosen.Add(new ChosenCost(ChosenCostKind.ExileFromGraveyard, howMany, fuel));
            Lift(ExileFromGraveyardCost());
        }

        var discard = DiscardChosenCost().Match(remaining);
        if (discard.Success)
        {
            // "Discard a creature card" - the same cost with a filter on it, and the filter is
            // the whole difference between an ability anybody can pay and one that asks for
            // something in particular. Described as a card because that is what the player is
            // being asked to hand over, the way the retrace cost above is.
            TargetSpec? kind = null;
            var named = discard.Groups["what"].Value.Trim();

            if (named.Length > 0)
            {
                if (EffectPhrase.Specs.Parse("target " + named) is not { } filter)
                    return null;

                kind = filter with { Description = $"a {named} card" };
            }

            chosen.Add(new ChosenCost(
                discard.Groups["random"].Success
                    ? ChosenCostKind.DiscardAtRandom
                    : ChosenCostKind.DiscardCards,
                Count: 1,
                What: kind));

            Lift(DiscardChosenCost());
        }

        if (!PayableCost().IsMatch(remaining))
            return null;

        return new PaidCost(
            ManaCostSpec.Parse(StripTap(remaining)),
            remaining.Contains("{T}", StringComparison.Ordinal),
            self,
            life,
            chosen.ToImmutable(),
            counters,
            energy,
            chosenCount);
    }

    /// <summary>The timing a printed restriction names, or null when it names one we cannot keep.</summary>
    private static ActivationTiming? TimingNamed(string phrase) => phrase.ToLowerInvariant() switch
    {
        "any time you could cast a sorcery" or "as a sorcery" => ActivationTiming.SorceryOnly,
        "during your turn" => ActivationTiming.YourTurnOnly,
        "during your turn, before attackers are declared"
            or "before attackers are declared" => ActivationTiming.BeforeAttackersDeclared,
        "during combat" or "during your combat phase" => ActivationTiming.CombatOnly,
        "during your upkeep" => ActivationTiming.YourUpkeepOnly,
        _ => null,
    };

    /// <summary>
    /// What a sacrifice cost accepts — "a creature", "an artifact" — or null if it names
    /// something the target grammar cannot express.
    /// </summary>
    /// <remarks>
    /// Reuses the target grammar by rewriting the cost's phrasing into it. They describe the same
    /// sets in almost the same words: "sacrifice a creature" and "target creature" both mean any
    /// creature, and every filter one understands the other does too. A second vocabulary here
    /// would have to be kept in step with that one by hand.
    /// </remarks>
    private static TargetSpec? SacrificeSpec(string what)
    {
        var noun = what.Trim();
        if (noun.Length == 0)
            return null;

        // A sacrifice is always of something you control (CR 701.21a), and the engine checks that
        // separately, so the phrase is read without an ownership clause.
        return EffectPhrase.Specs.Parse("target " + noun);
    }

    /// <summary>A count written as digits or as a word.</summary>
    private static int NumberWordOrDigits(string word)
    {
        if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return n;

        return word.ToLowerInvariant() switch
        {
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            _ => 1,
        };
    }

    /// <summary>"once" / "twice" / "three times" as a number.</summary>
    private static int NumberWord(string word) => word.ToLowerInvariant() switch
    {
        "twice" => 2,
        "three times" => 3,
        _ => 1,
    };

    /// <summary>
    /// A bullet belonging to a trigger that said "choose one —" (CR 603.3c).
    /// </summary>
    /// <remarks>
    /// Read before the spell's modal reader, because the two are the same bullet and only the
    /// header a few lines up says which of them it belongs to. A mode the phrase parser cannot
    /// read leaves the bullet unread, exactly as it does for a spell - a modal ability that
    /// quietly offers fewer modes than it prints is a different card.
    /// </remarks>
    private static bool TryModalTriggerBullet(
        string line, ImmutableList<TriggeredAbilityDefinition>.Builder into, int at)
    {
        if (at < 0 || at >= into.Count)
            return false;

        var bullet = ModalBullet().Match(line);
        if (!bullet.Success)
            return false;

        var text = bullet.Groups["mode"].Value.Trim();
        if (!EffectPhrase.TryParse(text, out var parsed))
            return false;

        var trigger = into[at];
        into[at] = trigger with
        {
            Modes = trigger.Modes.Add(new SpellMode(text, parsed.Targets, parsed.Effects)),
        };

        return true;
    }

    /// <summary>A triggered ability: "When/Whenever/At ..., ..." (CR 603.1).</summary>
    private static bool TryTrigger(
        string line,
        CardDefinition card,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ImmutableList<string>.Builder unhandled,
        ref int modalAt)
    {
        // "When you control no Islands, sacrifice this creature" watches nothing: it is true or
        // it is not, and nothing has to happen for it to become true (CR 603.8). Read before the
        // ordinary trigger grammar, because that grammar reads "when" as "when this happens" and
        // there is no event here for it to find.
        var stateTrigger = StateTriggerLine().Match(line);
        if (stateTrigger.Success)
        {
            var holds = BoardConditions.Parse(stateTrigger.Groups["cond"].Value.Trim());
            if (holds is null
                || !EffectPhrase.TryParse(stateTrigger.Groups["effect"].Value.Trim(), out var run)
                || !run.Targets.IsEmpty)
            {
                unhandled.Add(line);
                return true;
            }

            into.Add(new TriggeredAbilityDefinition
            {
                Id = "s" + Suffix(into.Count),
                Text = line,
                StateCondition = holds,

                // It answers to no event, and saying so is the point: everything downstream asks
                // the event predicate first, and one that is never true keeps a state trigger out
                // of the ordinary path entirely.
                Triggers = (_, _, _) => false,
                Effects = run.Effects,
            });

            return true;
        }

        var m = TriggerLine().Match(line);
        if (!m.Success)
            return false;

        var predicate = TriggerConditions.Parse(m.Groups["when"].Value.Trim());

        // "This ability triggers only once each turn" is a sentence about the ability rather than
        // part of what it does, so it comes off before the effect is read.
        var effectText = m.Groups["effect"].Value.Trim();

        // CR 603.4: an intervening-if clause is checked when the ability would trigger and again
        // as it resolves. The condition is lifted off here and applied to both halves — the
        // predicate below and the effects it wraps — because a card that only checked once is a
        // different card, and which way it differs depends on what happened in between.
        Func<GameState, IAbilitySource, GameObject, bool>? intervening = null;
        var iffy = InterveningIf().Match(effectText);
        if (iffy.Success)
        {
            intervening = BoardConditions.Parse(iffy.Groups["cond"].Value.Trim());
            if (intervening is null)
            {
                unhandled.Add(line);
                return true;
            }

            effectText = iffy.Groups["effect"].Value.Trim();
        }

        var oncePerTurn = OncePerTurnLine().IsMatch(effectText);
        if (oncePerTurn)
            effectText = OncePerTurnLine().Replace(effectText, string.Empty).Trim();

        // The trigger decides whether a bare "that creature" in its effects has anything to mean.
        // Asked of the condition text rather than of the predicate, because a predicate is a
        // closure and cannot be interrogated - and answered by an allow-list, so the default is
        // the safe one.
        var namesAnObject = TriggerConditions.NamesAnObject(m.Groups["when"].Value.Trim());

        // "When ~ enters, choose one —" is a trigger whose effect is a menu. The menu is on the
        // lines after it, so the ability is built empty here and the bullets are added to it as
        // they arrive; a header with no bullets under it produces an ability that offers a mode
        // and has none, which the structural check below refuses.
        var modalHeader = ModalHeader().Match(effectText);
        if (predicate is not null && modalHeader.Success)
        {
            var takes = modalHeader.Groups["n"].Value.ToLowerInvariant() switch
            {
                "two" => 2,
                "three" => 3,
                _ => 1,
            };

            into.Add(new TriggeredAbilityDefinition
            {
                Id = "t" + Suffix(into.Count),
                Text = line,
                Triggers = predicate,
                ModesToChoose = takes,
                ModesMax = modalHeader.Groups["range"].Value.ToLowerInvariant() switch
                {
                    "both" => 2,
                    "more" => -1,
                    _ => takes,
                },
                OncePerTurn = oncePerTurn,
                FunctionsFrom = Zone.Battlefield,
            });

            modalAt = into.Count - 1;
            return true;
        }

        if (predicate is null
            || !EffectPhrase.TryParse(effectText, out var parsed, namesAnObject))
        {
            unhandled.Add(line);
            return true;
        }

        var effects = parsed.Effects;

        if (intervening is { } condition)
        {
            // A deferred question inside the wrapper could not find itself again, so the card is
            // left unread rather than half-run — the same rule every other branch follows.
            if (effects.Any(EffectPhrase.FindsItselfByIndex))
            {
                unhandled.Add(line);
                return true;
            }

            var inner = predicate;
            predicate = (e, state, source) =>
                inner(e, state, source) && condition(state, source.Abilities, source);

            effects = [new OnlyIf(condition, effects)];
        }

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "t" + Suffix(into.Count),
            Text = line,
            Triggers = predicate,
            Targets = parsed.Targets,
            Effects = effects,
            OncePerTurn = oncePerTurn,

            // A trigger functions from the battlefield unless its own words say otherwise, and
            // "when you cycle this card" says otherwise: the card is in hand as the ability is
            // activated and in the graveyard by the time it resolves (CR 702.29a). Left at the
            // default it compiled cleanly and never fired, which is the failure this whole
            // exercise keeps producing when a card reads correctly and plays as nothing.
            FunctionsFrom = CyclingTrigger().IsMatch(m.Groups["when"].Value.Trim())
                ? Zone.Hand
                : Zone.Battlefield,
        });

        return true;
    }

    /// <remarks>
    /// Deliberately narrow. "When" opens both kinds of trigger and only the condition tells them
    /// apart, so this reads the shapes that are conditions about the board and leaves everything
    /// else to the event grammar - a state trigger built from a condition that was really an
    /// event would fire on every settle for the rest of the game.
    /// </remarks>
    [GeneratedRegex(
        @"^When (?<cond>you control no [A-Za-z]+), (?<effect>.+)$",
        RegexOptions.None)]
    private static partial Regex StateTriggerLine();

    /// <summary>
    /// What an Aura or Equipment calls the thing it is attached to.
    /// </summary>
    /// <remarks>
    /// One vocabulary shared by every matcher that names it, because they were three copies that
    /// had drifted: the buff read only "creature", the untap restriction read "creature" too, and
    /// the granted-ability line had already learned "land" and "permanent". A card reading
    /// "enchanted land doesn't untap during its controller's untap step" was therefore half
    /// understood, depending on which of its lines you looked at.
    /// </remarks>
    private const string AttachedSubject =
        "(creature|land|permanent|artifact|planeswalker|player)";

    /// <summary>The ability that may ride along with a conditional bonus.</summary>
    private const string BUFF =
        @"( and (has (?<kw>[a-z ,]+?)|can't (?<cant>attack or block|attack|block|be blocked)))?";

    /// <summary>The colour a printed word names, or null if it is not one (CR 105.1).</summary>
    private static ManaColor? ColorNamed(string word) => word.ToLowerInvariant() switch
    {
        "white" => ManaColor.White,
        "blue" => ManaColor.Blue,
        "black" => ManaColor.Black,
        "red" => ManaColor.Red,
        "green" => ManaColor.Green,
        _ => null,
    };

    /// <summary>What one alternative adds, for the button that offers it.</summary>
    private static string Describe(IEnumerable<ManaProduction> produces) =>
        string.Concat(produces.Select(p =>
            p.Color is null ? "{C}" : "{" + p.Color.Value.ToString()[..1] + "}"));

    private static string Suffix(int n) =>
        n == 0 ? string.Empty : n.ToString(CultureInfo.InvariantCulture);

    private static string StripTap(string cost) =>
        cost.Replace("{T}", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();

    /// <summary>The five basic land types and the mana each one taps for (CR 305.6).</summary>
    private static readonly (string Subtype, (string Symbol, ManaProduction Production) Mana)[]
        BasicLandTypes =
        [
            ("Plains", ("{W}", new ManaProduction(ManaColor.White, 1))),
            ("Island", ("{U}", new ManaProduction(ManaColor.Blue, 1))),
            ("Swamp", ("{B}", new ManaProduction(ManaColor.Black, 1))),
            ("Mountain", ("{R}", new ManaProduction(ManaColor.Red, 1))),
            ("Forest", ("{G}", new ManaProduction(ManaColor.Green, 1))),
        ];

    /// <summary>Keyword words the engine models, by the name printed on the card.</summary>
    private static readonly Dictionary<string, KeywordAbility> KeywordNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Flying"] = KeywordAbility.Flying,
            ["Reach"] = KeywordAbility.Reach,
            ["First strike"] = KeywordAbility.FirstStrike,
            ["Double strike"] = KeywordAbility.DoubleStrike,
            ["Trample"] = KeywordAbility.Trample,
            ["Deathtouch"] = KeywordAbility.Deathtouch,
            ["Lifelink"] = KeywordAbility.Lifelink,
            ["Vigilance"] = KeywordAbility.Vigilance,
            ["Haste"] = KeywordAbility.Haste,
            ["Hexproof"] = KeywordAbility.Hexproof,
            ["Indestructible"] = KeywordAbility.Indestructible,
            ["Menace"] = KeywordAbility.Menace,
            ["Flash"] = KeywordAbility.Flash,
            ["Shroud"] = KeywordAbility.Shroud,
            ["Defender"] = KeywordAbility.Defender,
            ["Swampwalk"] = KeywordAbility.Swampwalk,
            ["Forestwalk"] = KeywordAbility.Forestwalk,
            ["Islandwalk"] = KeywordAbility.Islandwalk,
            ["Mountainwalk"] = KeywordAbility.Mountainwalk,
            ["Plainswalk"] = KeywordAbility.Plainswalk,
            ["Horsemanship"] = KeywordAbility.Horsemanship,
            ["Infect"] = KeywordAbility.Infect,
            ["Wither"] = KeywordAbility.Wither,
            ["Changeling"] = KeywordAbility.Changeling,
            ["Daybound"] = KeywordAbility.Daybound,
            ["Nightbound"] = KeywordAbility.Nightbound,
            ["Enlist"] = KeywordAbility.Enlist,

            // Protection names its colour, so each is its own word here. Without them a line
            // reading "Flying, protection from red" failed on its second half and went unread
            // whole, taking the flying with it.
            ["Protection from white"] = KeywordAbility.ProtectionFromWhite,
            ["Protection from blue"] = KeywordAbility.ProtectionFromBlue,
            ["Protection from black"] = KeywordAbility.ProtectionFromBlack,
            ["Protection from red"] = KeywordAbility.ProtectionFromRed,
            ["Protection from green"] = KeywordAbility.ProtectionFromGreen,
            ["Protection from artifacts"] = KeywordAbility.ProtectionFromArtifacts,
        };

    /// <summary>
    /// Whole lines that are a rule in themselves, and the keyword each one means.
    /// </summary>
    /// <remarks>
    /// These are not printed as keywords — the card says the sentence — but the engine models
    /// them as one, because the question they answer is asked in exactly one place: whether this
    /// creature may block that one.
    /// </remarks>
    private static readonly Dictionary<string, KeywordAbility> SentenceKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["~ can't be countered."] = KeywordAbility.CantBeCountered,

            // "Can't attack alone" and "can't block alone" are restrictions on the whole
            // declaration rather than on the creature, which is why they are modelled as
            // keywords and read by the set checks rather than by the per-creature ones.
            ["~ can't attack alone."] = KeywordAbility.CantAttackAlone,
            ["~ can't attack alone"] = KeywordAbility.CantAttackAlone,
            ["~ can't block alone."] = KeywordAbility.CantBlockAlone,
            ["~ can't block alone"] = KeywordAbility.CantBlockAlone,
            ["~ can't attack or block alone."] =
                KeywordAbility.CantAttackAlone | KeywordAbility.CantBlockAlone,
            ["~ can't attack or block alone"] =
                KeywordAbility.CantAttackAlone | KeywordAbility.CantBlockAlone,
            ["This spell can't be countered."] = KeywordAbility.CantBeCountered,
            ["Fear"] = KeywordAbility.Fear,
            ["Shadow"] = KeywordAbility.Shadow,
            ["Intimidate"] = KeywordAbility.Intimidate,
            ["Skulk"] = KeywordAbility.Skulk,

            // Banding, of which only the blocking half is enforced (CR 702.22j): a creature
            // blocked by one has its damage divided by the defending player. Declaring an
            // attacking band is not modelled at all - no client can say it and nothing in the
            // engine holds a band - so a banding card is read here and plays only part of what
            // it prints. That is recorded in GAME_ENGINE_FEATURE.md rather than left implicit.
            ["Banding"] = KeywordAbility.Banding,
            ["Protection from white"] = KeywordAbility.ProtectionFromWhite,
            ["Protection from blue"] = KeywordAbility.ProtectionFromBlue,
            ["Protection from black"] = KeywordAbility.ProtectionFromBlack,
            ["Protection from red"] = KeywordAbility.ProtectionFromRed,
            ["Protection from green"] = KeywordAbility.ProtectionFromGreen,
            // Lifelink, in the wording it had before the keyword existed (CR 702.15b). The
            // keyword covers all damage the source deals, which is what this sentence says.
            ["Whenever ~ deals damage, you gain that much life."] = KeywordAbility.Lifelink,
            ["Whenever ~ deals damage, you gain that much life"] = KeywordAbility.Lifelink,

            // Menace, spelled out. Cards printed before the keyword existed say it in full,
            // and it is the same rule (CR 702.111a) rather than an approximation of it.
            ["~ can't be blocked by more than one creature."] = KeywordAbility.Menace,
            ["~ can't be blocked by more than one creature"] = KeywordAbility.Menace,
            ["~ attacks each combat if able."] = KeywordAbility.MustAttack,
            ["~ attacks each combat if able"] = KeywordAbility.MustAttack,
            ["~ can't block and can't be blocked."] =
                KeywordAbility.CantBlock | KeywordAbility.CantBeBlocked,
            ["~ can't block and can't be blocked"] =
                KeywordAbility.CantBlock | KeywordAbility.CantBeBlocked,
            ["~ can't block."] = KeywordAbility.CantBlock,
            ["~ can't block"] = KeywordAbility.CantBlock,
            ["~ can't be blocked."] = KeywordAbility.CantBeBlocked,
            ["~ can't be blocked"] = KeywordAbility.CantBeBlocked,
            ["~ can block only creatures with flying."] = KeywordAbility.BlocksOnlyFlying,
            ["~ can block only creatures with flying"] = KeywordAbility.BlocksOnlyFlying,
        };

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Reminder();

    /// <summary>
    /// Prefixes that look exactly like an ability word and are not one.
    /// </summary>
    /// <remarks>
    /// An ability word is flavour with no rules meaning (CR 207.2c), and it is recognised by its
    /// <em>shape</em> - a capitalised phrase before an em dash - because there are 579 distinct
    /// ones in the corpus and a hand-kept list of them would be stale by the next set.
    /// <para>
    /// That shape is also the shape of several things that carry the whole meaning of their card.
    /// A Saga's chapter symbol is a Roman numeral, and "III" is three capitals before a dash, so
    /// chapter III of every Saga in the game was stripped as flavour and its effect left behind
    /// as a homeless sentence. A Case says "To solve -" and "Solved -", and both were eaten the
    /// same way, which is why no Case compiled until this list existed.
    /// <para>
    /// Twice is a pattern, so the exclusions are declared here and the pattern is built from
    /// them: a new structural prefix is added in one place and cannot silently disagree with the
    /// stripper. <c>A_structural_prefix_is_not_stripped_as_flavour</c> is what checks it.
    /// </para>
    /// </remarks>
    public static readonly ImmutableArray<string> StructuralPrefixes =
    [
        "To solve",
        "Solved",

        // An Attraction's two halves (CR 717). Nothing here implements Attractions - they need a
        // deck in the command zone, dice, physical stickers and in one case ten seconds of real
        // time - but the prefixes are declared anyway so the corpus reports the truth about why
        // they are unread. Left out, the stripper removed them and the report blamed "Draw a
        // card", which is a line the compiler has read for months.
        "Visit",
        "Prize",
    ];

    /// <summary>An ability word - flavour with no rules meaning at all (CR 207.2c).</summary>
    /// <remarks>
    /// The hyphen in the class is there for "Power-up", a real ability word on 37 cards that a
    /// class allowing only letters, apostrophes and spaces could not see. It admits a handful of
    /// hyphenated card names before an em dash too - but the pattern already admits every
    /// unhyphenated one, so this changes how much of that risk is taken, not whether.
    /// <para>
    /// The digits are there for "Descend 4" and "Descend 8", which are one ability word with a
    /// number in it rather than two - the same shape of miss as the hyphen, found the same way.
    /// </para>
    /// </remarks>
    private static readonly Regex AbilityWordRegex = new(
        @"^(?!(?:[IVX]+(?:, ?[IVX]+)*|"
            + string.Join('|', StructuralPrefixes.Select(Regex.Escape))
            + @") \u2014 )[A-Z][A-Za-z0-9' -]{2,24} \u2014 ",
        RegexOptions.CultureInvariant);

    private static Regex AbilityWord() => AbilityWordRegex;

    /// <remarks>
    /// The subtype words are here for the same reason the card types are: an Equipment says
    /// "whenever this Equipment enters", a Saga says "this Saga", and each is naming itself. They
    /// were missing, so those cards' triggers read as being about some other permanent and every
    /// one of them went unread — thirty-five Equipment and thirty Auras on the enters trigger
    /// alone.
    /// </remarks>
    /// <summary>
    /// The words a card uses to mean itself: "this Saga", "this Vehicle", "this Spacecraft".
    /// </summary>
    /// <remarks>
    /// A card refers to itself by one of its own types, and every printing since 2022 does so
    /// rather than repeating its name. Miss one and the reference is never turned into
    /// <c>~</c>, so a line that is otherwise ordinary - an enters trigger, a static - goes
    /// unread on every card of that type at once. Spacecraft, Contraption, Siege, Case,
    /// Attraction, Planet, Conspiracy and Realm were all missing, about 165 lines between them,
    /// and the only symptom was a set of newer cards being quietly harder to read than older
    /// ones saying the same thing.
    /// <para>
    /// Exposed, and the pattern built from it rather than restating it, because this is the
    /// third vocabulary list in this compiler to go stale and the other two ended up with a test
    /// that checks them against the corpus. <c>Every_type_a_card_calls_itself_by_is_understood</c>
    /// is this one's.
    /// </para>
    /// </remarks>
    public static readonly ImmutableArray<string> SelfReferenceTypeNames =
    [
        "creature", "permanent", "card", "spell", "land", "artifact", "enchantment",
        "planeswalker", "equipment", "aura", "vehicle", "saga", "token", "battle", "class",
        "room", "kindred", "spacecraft", "contraption", "siege", "case", "attraction",
        "planet", "conspiracy", "realm",
    ];

    private static readonly Regex SelfReferenceRegex = new(
        @"\bthis (" + string.Join('|', SelfReferenceTypeNames) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex SelfReference() => SelfReferenceRegex;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^prowess\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProwessLine();

    /// <remarks>
    /// Deliberately narrow. "Leaves the battlefield" and "put onto the battlefield" are different
    /// events and must survive untouched, and they do because neither contains this phrase.
    /// </remarks>
    [GeneratedRegex(@"enters the battlefield", RegexOptions.IgnoreCase)]
    private static partial Regex EntersTheBattlefield();

    [GeneratedRegex(@"^exalted\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ExaltedLine();

    [GeneratedRegex(@"^bushido (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BushidoLine();

    [GeneratedRegex(@"^training\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex TrainingLine();

    [GeneratedRegex(@"^Outlast (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex OutlastLine();

    [GeneratedRegex(@"^mobilize (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MobilizeLine();

    [GeneratedRegex(@"^melee\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MeleeLine();

    [GeneratedRegex(@"^increment\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex IncrementLine();

    [GeneratedRegex(@"^ascend\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AscendLine();

    [GeneratedRegex(@"^undaunted\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex UndauntedLine();

    /// <remarks>
    /// The em dash is the whole of what separates "Partner-Friends forever" from "Partner with
    /// Alisaie Leveilleur", and only the first of those is inert. Matching "Partner" followed by
    /// anything would take both.
    /// </remarks>
    [GeneratedRegex(
        @"^(partner(—[A-Za-z][A-Za-z &'-]*)?|choose a Background|Doctor's companion)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PartnerLine();

    [GeneratedRegex(@"^annihilator (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AnnihilatorLine();

    [GeneratedRegex(@"^renown (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RenownLine();

    [GeneratedRegex(@"^flanking\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex FlankingLine();

    [GeneratedRegex(
        @"^you may exert ~ as (it|he|she|they) attacks?\.( [Ww]hen you do, (?<then>.+?)\.?)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExertLine();

    [GeneratedRegex(@"^warp (?<cost>(\{[^}]+\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WarpLine();

    [GeneratedRegex(@"^encore (?<cost>(\{[^}]+\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EncoreLine();

    [GeneratedRegex(@"^exploit$", RegexOptions.IgnoreCase)]
    private static partial Regex ExploitKeywordLine();

    [GeneratedRegex(@"^decayed$", RegexOptions.IgnoreCase)]
    private static partial Regex DecayedLine();

    [GeneratedRegex(@"^reconfigure (?<cost>[^.]+?)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReconfigureLine();

    [GeneratedRegex(@"^job select$", RegexOptions.IgnoreCase)]
    private static partial Regex JobSelectLine();

    [GeneratedRegex(@"^provoke$", RegexOptions.IgnoreCase)]
    private static partial Regex ProvokeLine();

    [GeneratedRegex(@"^devoid\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DevoidLine();

    /// <remarks>
    /// Graft is spelled out on the card rather than printed as a bare keyword, which is why the
    /// long form is here beside the two short ones.
    /// </remarks>
    /// <remarks>
    /// Counters of any name, because the permanent can hold any name and the storage cycles —
    /// charge, depletion, storage — all arrive carrying some.
    /// </remarks>
    [GeneratedRegex(
        @"^(modular (?<n>\d+)|graft (?<n>\d+)"
            + @"|~ enters( tapped)? with (?<n>\d+|X|a|an|two|three|four|five) "
            + @"(?<kind>\+1/\+1|-1/-1|[a-z]+) counters? on it( if (?<when>[^.]+))?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntersWithCountersLine();

    /// <summary>"…enters with a +1/+1 counter on it for each [group]" (CR 614.1c).</summary>
    [GeneratedRegex(
        @"^~ enters with (?<n>\d+|a|an|two|three|four|five) "
            + @"(?<kind>\+1/\+1|-1/-1|[a-z]+) counters? on it for each (?<group>[^.]+)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntersWithCountersPerGroupLine();

    [GeneratedRegex(@"^undying\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex UndyingLine();

    [GeneratedRegex(@"^persist\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex PersistLine();

    [GeneratedRegex(@"^cycling (?<cost>(\{[^}]+\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CyclingLine();

    /// <summary>"Reinforce N—{cost}", with the em dash the cards print.</summary>
    [GeneratedRegex(
        @"^Reinforce (?<n>\d+|one|two|three|four|five)\s*[\u2014-]\s*(?<cost>(\{[^}]+\})+)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReinforceLine();

    /// <remarks>
    /// The type is matched case-sensitively at its first letter, because that capital is what
    /// tells a subtype from a card type everywhere else in the compiler — "Forestcycling" fetches
    /// a Forest, "Landcycling" fetches a land, and the distinction is exactly the one the search
    /// vocabulary already draws.
    /// </remarks>
    [GeneratedRegex(@"^(?<what>[A-Za-z][A-Za-z ]*?)cycling (?<cost>(\{[^}]+\})+)$")]
    private static partial Regex TypecyclingLine();

    [GeneratedRegex(
        @"^enchant (?<what>[a-z]+ or [a-z]+|[a-z]+ [a-z]+|[a-z]+)"
            + @"(?<own> you control| an opponent controls)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EnchantLine();

    /// <remarks>
    /// All four shapes in one pattern — a bonus, a keyword, or both, on an Aura or an Equipment.
    /// "Equipped creature gets +N/+N" alone is 167 playable cards, and the "and has [keyword]"
    /// variants another sixty; splitting them into separate matchers would mean four copies of
    /// the same attachment rule.
    /// </remarks>
    [GeneratedRegex(
        @"^(enchanted|equipped) " + AttachedSubject + " "
            + @"(gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)"
            + @"( and (has (?<kw>[a-z ,]+?)"
            + @"|can't (?<cant>attack or block|attack|block|be blocked)"
            + @"(?<silenced>,? and its activated abilities can't be activated)?"
            + @"|(?<must>attacks each combat if able)))?"
            + @"|has (?<kw>[a-z ,]+?)"
            + @"|can't (?<cant>attack or block|attack|block|be blocked)"
            + @"(?<silenced>,? and its activated abilities can't be activated)?"
            + @"|(?<must>attacks each combat if able))\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedBuffLine();

    /// <remarks>
    /// Three orders in one pattern, because the cards print it every way: the condition leads on
    /// "During your turn, ~ has first strike" and on "As long as you control a Swamp, ~ gets
    /// +2/+2", and trails on "~ gets +2/+2 as long as ...". The trailing form was read for a long
    /// time and the general leading one was not, so a whole shelf of cards went unread over word
    /// order alone.
    /// </remarks>
    [GeneratedRegex(
        // The bonus and the ability may arrive together - "as long as ... ~ gets +2/+0 and
        // has trample" - and the consumer has always emitted the two independently. Only
        // the pattern could not capture both at once, so a card that granted both went
        // unread while either alone was fine.
        @"^(?:[Dd]uring (?<cond>your turn), (?<subject>~|[Ee]nchanted [a-z]+|[Ee]quipped [a-z]+) "
                + @"(gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)" + BUFF + @"|has (?<kw>[a-z ,]+))"
            + @"|[Aa]s long as (?<cond>[^,]+), "
                + @"(?<subject>~|it|[Ee]nchanted [a-z]+|[Ee]quipped [a-z]+) "
                + @"(gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)" + BUFF + @"|has (?<kw>[a-z ,]+)"
                + @"|can't (?<cant>attack or block|attack|block|be blocked))"
            + @"|(?<subject>~|[Ee]nchanted [a-z]+|[Ee]quipped [a-z]+) "
                + @"(gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)" + BUFF + @"|has (?<kw>[a-z ,]+?)"
                + @"|can't (?<cant>attack or block|attack|block|be blocked)) "
                + @"(as long as|if|(?<unless>unless)) (?<cond>[^.]+))\.?$",
        RegexOptions.None)]
    private static partial Regex ConditionalStaticLine();

    [GeneratedRegex(
        @"^(~'s|~’s) (?<stat>power and toughness are each|power is|toughness is) "
            + @"equal to (?<what>[^.]+)\.?$",
        RegexOptions.None)]
    private static partial Regex DefinedPowerToughnessLine();

    /// <remarks>
    /// The zone is captured rather than matched three times, because "the number of" opens all
    /// three and only the tail says where to look.
    /// </remarks>
    [GeneratedRegex(
        @"^the number of (?<what>.+?)"
            + @"( in (?<hand>your hand)| in (?<yard>your graveyard))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CountedThingLine();

    /// <summary>"Creatures in your party", however the card spells the noun (CR 700.9).</summary>
    [GeneratedRegex(@"^(the number of )?creatures? in your party$", RegexOptions.IgnoreCase)]
    private static partial Regex PartyLine();

    /// <remarks>
    /// Case-sensitive on the subject, because "Enchanted creature" and "Equipped creature" are
    /// the two attached forms and a lower-case "enchanted" mid-sentence is not the subject of
    /// anything. The three subjects share one reader because they share one effect: only where
    /// it lands differs.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<subject>~|Enchanted creature|Equipped creature) gets "
            + @"(?<p>[+-]\d+)/(?<tough>[+-]\d+) for each (?<group>[^.]+)\.?$",
        RegexOptions.None)]
    private static partial Regex CountingStaticLine();

    /// <summary>"Aura attached to it" and its Equipment twin, inside a "for each" count.</summary>
    [GeneratedRegex(
        @"^(?<what>Aura|Equipment) attached to (it|~)$", RegexOptions.IgnoreCase)]
    private static partial Regex AttachedCountLine();

    /// <summary>"creature card in your graveyard" — a count taken in a zone, inside a "for each".</summary>
    [GeneratedRegex(
        @"^(?<what>[a-z ]+?) in (?<whose>your|each) (?<zone>graveyard|hand)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ZoneCountLine();

    /// <remarks>
    /// A lord names its tribe two ways - "Other Goblin creatures you control" and just "Other
    /// Goblins you control" - and the second is much the commoner. The bare-tribe alternative is
    /// deliberately case-<em>sensitive</em> inside an otherwise case-insensitive pattern: without
    /// that, "other artifacts you control" would read "artifact" as a creature subtype and
    /// compile to a lord that silently buffs nothing, which is worse than not reading the line.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<scope>all|other|each)?\s*"
            + @"(creatures?|(?<subtype>[A-Z][a-z]+)\s+creatures?"
            + @"|(?-i:(?<subtype>[A-Z][a-z]+(s|es|ves|ies))|(?<subtype>Merfolk)))"
            + @"(?<side>\s+you control|\s+your opponents control|\s+an opponent controls)?"
            + @"(\s+of the chosen (?<chosen>type|color))?"
            + @"(\s+with a (?<counter>[+-]\d/[+-]\d) counter on (it|them)"
            + @"|\s+with (?<needs>[a-z ]+?))?\s+"
            + @"(gets? (?<p>[+-]\d+)/(?<tough>[+-]\d+)( and (has|have) (?<kw>[a-z ,]+))?"
            + @"|(has|have) (?<kw>[a-z ,]+))\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MassStaticLine();

    /// <remarks>
    /// Case-sensitive on the subtype for the same reason the token pattern is: capitalisation is
    /// what separates "All Slivers have" from "All creatures have".
    /// </remarks>
    /// <remarks>
    /// The type words are case-insensitive and the tribe is not, which is the same division
    /// every other noun in this compiler draws and for the same reason: a sentence-initial
    /// capital is position, not meaning. Written case-sensitively, "Creatures you control have
    /// ..." matched nothing at all - the pattern was looking for a lowercase "creatures" and
    /// the only other thing it could try was the tribe slot, which then had no noun left to
    /// find.
    /// <para>
    /// A bare plural tribe is its own alternative, because "All Slivers have ..." never says
    /// the word creature - and the pattern required it, so twenty-seven Slivers went unread
    /// while the six that spell it out as "All Sliver creatures" were fine.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^((?<attached>[Ee]nchanted|[Ee]quipped) (creature|land|permanent|artifact)"
            + @"|(?i:(?<commander>commander) creatures? you own)"
            + @"|(?<scope>(?i:all|each|other))?\s*"
            + @"(?:(?<subtype>[A-Z][a-z]+)\s+(?i:(?<type>creature|permanent))s?"
            + @"|(?i:(?<type>creature|permanent|land|artifact|enchantment))s?"
            + @"|(?<subtype>[A-Z][a-z]+))"
            + @"(?<side>\s+you control|\s+your opponents control)?)"
            + @"\s+ha(s|ve) ""(?<ability>[^""]+)""\.?$",
        RegexOptions.None)]
    private static partial Regex GrantedAbilityLine();

    // "Equip legendary creature {3}" - the same ability with the target narrowed (CR 702.6e).
    // The qualifier goes through the target grammar rather than being a second reader, so
    // whatever it names is whatever that grammar already understands.
    [GeneratedRegex(
        @"^equip( (?<what>[a-z]+ creature))? (?<cost>(\{[^}]+\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EquipLine();

    [GeneratedRegex(
        @"^~ enters tapped( unless (?<unless>.+?))?\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EntersTapped();

    /// <remarks>
    /// The reminder text is stripped before the line reaches here, so this matches the sentence
    /// alone. It doubles as the layout test in <c>Compile</c>: a card that says this is a prepare
    /// card, and its second face is not a half.
    /// </remarks>
    [GeneratedRegex(@"^~ enters prepared\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex PreparedLine();

    /// <remarks>
    /// Matched anywhere in the line rather than anchored, because it is almost always the tail of
    /// a trigger the compiler reads separately ("Whenever ~ attacks, it becomes prepared").
    /// </remarks>
    [GeneratedRegex(@"\b(it|~|this creature) becomes prepared\b", RegexOptions.IgnoreCase)]
    private static partial Regex BecomesPreparedLine();

    [GeneratedRegex(
        @"^As ~ enters, you may pay (?<n>\d+) life\. If you don't, it enters tapped\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShocklandLine();

    /// <remarks>
    /// The cost is bounded so the pattern cannot run away over a whole card, and the bound is 72
    /// rather than 40 because a real cost reaches it: "{T}, Tap an untapped creature you control"
    /// is forty-one characters and was refused for being one too long. The length is a guard, not
    /// a grammar - what the cost actually says is <see cref="ReadCost"/>'s job, and it returns
    /// null for anything it cannot charge, so a pattern that grabs too much fails safe.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<cost>[^:]{1,72}):\s*Add\s+(?<mana>[^.]+?)\s*(\.\s+(?<rest>\S.*?))?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ManaAbilityLine();

    [GeneratedRegex(@"^(?<cost>[^:]{1,60}):\s*(?<effect>.+)$")]
    private static partial Regex ActivatedLine();

    /// <summary>
    /// "Activate only once each turn", and the wording that means the same thing (CR 602.5b).
    /// </summary>
    /// <remarks>
    /// "No more than twice" is the same restriction as "only twice" and the cards use both. The
    /// pattern required "only", so every card spelling it the other way had the limit dropped -
    /// which is worse than not reading the line at all, because an ability with no limit where
    /// the card prints one is strictly better than the card.
    /// </remarks>
    [GeneratedRegex(
        @"\s*Activate (only|no more than) (?<n>once|twice|three times)? ?each turn\.?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ActivationLimit();

    [GeneratedRegex(
        @"\s*Activate only (?<when>[^.]+?)\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ActivationTimingLine();

    [GeneratedRegex(
        @"\s*Any player may activate this ability\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AnyPlayerLine();

    /// <summary>"Is put into a graveyard from the battlefield" — the long spelling of dies (CR 700.4).</summary>
    [GeneratedRegex(
        @"\b(is|(?<are>are)) put into (a|their owner's|its owner's) graveyard "
            + @"from the battlefield\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiesTheLongWay();

    /// <summary>"Disturb {1}{W}" (CR 702.146a).</summary>
    [GeneratedRegex(@"^Disturb (?<cost>(\{[^}]+\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DisturbLine();

    /// <summary>A card that goes somewhere other than the graveyard it was headed for.</summary>
    [GeneratedRegex(
        @"^If (~|it) would be put into (a|its owner's|your) graveyard from anywhere, "
            + @"(?<where>exile it|put it on the bottom of its owner's library) instead\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GraveyardReplacementLine();

    [GeneratedRegex(
        @"\s*This ability triggers only once each turn\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OncePerTurnLine();

    [GeneratedRegex(@"^if (?<cond>[^,]+), (?<effect>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InterveningIf();

    /// <remarks>
    /// "Sacrifice it" as readily as "sacrifice ~": a cost written after another one refers back
    /// to the permanent by pronoun, as in "Remove three quest counters from ~ and sacrifice it".
    /// </remarks>
    [GeneratedRegex(
        @",?\s*(and\s+)?sacrifice (~|it)\s*,?", RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeSelfCost();

    [GeneratedRegex(@",?\s*exile ~ from your graveyard\s*,?", RegexOptions.IgnoreCase)]
    private static partial Regex ExileSelfFromGraveyardCost();

    [GeneratedRegex(
        @",?\s*(and\s+)?return (~|it) to its owner's hand\s*,?", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnSelfCost();

    [GeneratedRegex(@",?\s*discard ~\s*,?", RegexOptions.IgnoreCase)]
    private static partial Regex DiscardSelfCost();

    [GeneratedRegex(@",?\s*pay (?<n>\d+) life\s*,?", RegexOptions.IgnoreCase)]
    private static partial Regex PayLifeCost();

    [GeneratedRegex(@",?\s*pay (?<e>(\{E\})+)\s*,?", RegexOptions.IgnoreCase)]
    private static partial Regex PayEnergyCost();

    [GeneratedRegex(
        @",?\s*remove (?<n>\d+|a|an|two|three|four|five|(?<any>any number of)) "
            + @"(?<kind>[+-]\d/[+-]\d|[a-z]+) counters? from ~\s*,?",
        RegexOptions.IgnoreCase)]
    private static partial Regex RemoveCounterCost();

    [GeneratedRegex(
        @",?\s*sacrifice (?<scope>another|an?)\s+(?<what>[a-z ]+?)\s*(,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeChosenCost();

    [GeneratedRegex(
        @",?\s*discard an? (?<what>[a-z ]+? )?card(?<random> at random)?\s*(,|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiscardChosenCost();

    [GeneratedRegex(
        @",?\s*exile (?<n>an?|one|two|three|four|five|[0-9]+) "
            + @"(?<what>[a-z ]+? )?cards? from your graveyard\s*(,|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExileFromGraveyardCost();

    /// <remarks>
    /// Case-sensitive on what follows "an", so "Tap an untapped Ally you control" keeps the
    /// capital that tells the target grammar it is a creature type.
    /// </remarks>
    [GeneratedRegex(
        @",?\s*[Tt]ap (an?|(?<n>two|three|four|five))\s+(?<what>[A-Za-z' ]+?)\s*(,|$)",
        RegexOptions.None)]
    private static partial Regex TapChosenCost();

    [GeneratedRegex(@"^crew (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CrewLine();

    /// <summary>"Living metal" (CR 702.161a).</summary>
    [GeneratedRegex(@"^living metal\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex LivingMetalLine();

    [GeneratedRegex(@"^saddle (?<n>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SaddleLine();

    [GeneratedRegex(@"^(?<kind>convoke|improvise)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex TapToPayLine();

    [GeneratedRegex(@"^(?<sign>[+\u2212-])?(?<n>\d+):\s*(?<effect>.+)$")]
    private static partial Regex LoyaltyLine();

    [GeneratedRegex(@"^unearth (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex UnearthLine();

    [GeneratedRegex(@"^flashback (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex FlashbackLine();

    /// <summary>"Escape—{cost}, Exile N other cards from your graveyard" (CR 702.139a).</summary>
    [GeneratedRegex(
        @"^Escape—(?<cost>(\{[^}]+\})+), Exile "
            + @"(?<n>\d+|one|two|three|four|five|six|seven|eight) "
            + @"other cards? from your graveyard\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EscapeLine();

    [GeneratedRegex(
        @"^~ escapes with (?<n>an?|\d+|one|two|three|four|five) "
            + @"\+1/\+1 counters? on it\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EscapesWithCountersLine();

    [GeneratedRegex(@"^mayhem (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MayhemLine();

    /// <remarks>
    /// "To cast this spell" and "to cast it" are the same words after the name normalisation, and
    /// older printings say "to cast ~" with no pronoun at all.
    /// </remarks>
    [GeneratedRegex(@"^Soulshift (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SoulshiftLine();

    [GeneratedRegex(@"^Toxic (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ToxicLine();

    [GeneratedRegex(@"^Affinity for (?<what>[A-Za-z ]+?)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AffinityLine();

    /// <summary>A Saga chapter symbol and what it does (CR 714.2a).</summary>
    /// <remarks>
    /// The em dash is what makes this safe to match at the start of a line: a sentence beginning
    /// "I" does not carry one. Both dashes are accepted because the corpus is not consistent
    /// about which it prints.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<chapters>[IV]+(?:, ?[IV]+)*)\s*[\u2014\u2015-]\s*(?<effect>.+)$",
        RegexOptions.None)]
    private static partial Regex SagaChapterLine();

    /// <summary>A Class level bar: its cost and the level it reaches (CR 716.2a).</summary>
    [GeneratedRegex(@"^(?<cost>(\{[^}]+\})+): Level (?<n>[0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ClassLevelBar();

    /// <summary>"To solve - [condition]" (CR 719.3a).</summary>
    [GeneratedRegex(@"^To solve [\u2014\u2015-] (?<cond>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ToSolveLine();

    /// <summary>"Solved - [ability]" (CR 719.3c).</summary>
    [GeneratedRegex(@"^Solved [\u2014\u2015-] (?<ability>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SolvedLine();

    /// <summary>The station keyword, whose reminder text carries the rest (CR 702.184a).</summary>
    [GeneratedRegex(@"^Station$", RegexOptions.IgnoreCase)]
    private static partial Regex StationLine();

    /// <summary>The aftermath keyword (CR 702.127a).</summary>
    [GeneratedRegex(@"^Aftermath$", RegexOptions.IgnoreCase)]
    private static partial Regex AftermathLine();

    /// <summary>The fuse keyword (CR 702.102a).</summary>
    [GeneratedRegex(@"^Fuse$", RegexOptions.IgnoreCase)]
    private static partial Regex FuseLine();

    /// <summary>A station symbol and what it switches on (CR 721.2).</summary>
    [GeneratedRegex(
        @"^(?<n>[0-9]+)\+ \| (?<abilities>[A-Za-z, ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex StationThresholdLine();

    /// <summary>"When this Class becomes level N, ..." (CR 716.2a).</summary>
    [GeneratedRegex(
        @"^When (?:~|this Class) becomes level (?<n>[0-9]+), (?<effect>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClassLevelTriggerLine();

    /// <summary>"When you unlock this door, ..." (CR 709.5f).</summary>
    [GeneratedRegex(
        @"^When you unlock this door, (?<effect>.+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnlockTriggerLine();

    /// <summary>The flavour word some chapters print before their effect (CR 207.2c).</summary>
    [GeneratedRegex(@"^[A-Z][A-Za-z' !]{2,24}[—―-] ")]
    private static partial Regex ChapterFlavourWord();

    [GeneratedRegex(
        @"^Ward([ —―-]|—)((?<cost>(\{[^}]+\})+)|[Pp]ay (?<life>\d+) life"
            + @"|(?<verb>[Dd]iscard|[Ss]acrifice) an? (?<what>[A-Za-z' ]*?)\s*(cards?)?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex WardLine();

    [GeneratedRegex(@"^Bloodthirst (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BloodthirstLine();

    [GeneratedRegex(@"^Echo (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EchoLine();

    [GeneratedRegex(@"^Fading (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex FadingLine();

    [GeneratedRegex(@"^Living weapon\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex LivingWeaponLine();

    [GeneratedRegex(@"^Delve\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DelveLine();

    [GeneratedRegex(@"^Storm\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex StormLine();

    [GeneratedRegex(@"^Cascade\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CascadeLine();

    [GeneratedRegex(@"^Rebound\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReboundLine();

    [GeneratedRegex(@"^Start your engines!$", RegexOptions.IgnoreCase)]
    private static partial Regex StartYourEnginesLine();

    [GeneratedRegex(@"^Myriad\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MyriadLine();

    [GeneratedRegex(@"^Evolve\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EvolveLine();

    [GeneratedRegex(@"^Rampage (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RampageLine();

    [GeneratedRegex(@"^Afterlife (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AfterlifeLine();

    [GeneratedRegex(@"^Extort\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtortLine();

    [GeneratedRegex(@"^Unleash\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex UnleashLine();

    [GeneratedRegex(@"^Riot\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RiotLine();

    [GeneratedRegex(@"^Ingest\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex IngestLine();

    [GeneratedRegex(@"^Battle cry\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BattleCryLine();

    [GeneratedRegex(@"^Mentor\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MentorLine();

    [GeneratedRegex(@"^For Mirrodin!$", RegexOptions.IgnoreCase)]
    private static partial Regex ForMirrodinLine();

    [GeneratedRegex(
        @"^Creatures with power less than ~'s power can't block it\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SmallCantBlockLine();

    [GeneratedRegex(
        @"^(?<who>~|Enchanted creature|Equipped creature) can block "
        + @"(?:(?<any>any number of creatures)|an additional creature|"
        + @"up to (?<n>[a-z]+) additional creatures)(?: each combat)?[.]?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExtraBlocksLine();

    [GeneratedRegex(
        @"^((?<all>All creatures able to block )|)"
            + @"(?<who>~|enchanted creature|equipped creature)"
            + @"( do so| must be blocked if able)[.]?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MustBeBlockedLine();

    [GeneratedRegex(
        @"^You control enchanted (creature|permanent)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex StealHostLine();

    [GeneratedRegex(@"^Umbra armor\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex UmbraArmorLine();

    [GeneratedRegex(
        @"^~ costs \{(?<n>\d+)\} less to cast if it targets an? (?<what>.+?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetCostReductionLine();

    [GeneratedRegex(
        @"^~ costs \{(?<n>\d+)\} less to cast for each (?<what>[^.]+?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CountedCostReductionLine();

    /// <remarks>
    /// "If it targets ..." is a different question with its own reader, so it is excluded here -
    /// otherwise this would claim the sentence and answer it from the board, which is not where
    /// the answer is.
    /// </remarks>
    [GeneratedRegex(
        @"^~ costs \{(?<n>\d+)\} less to cast if (?!it targets)(?<cond>[^.]+?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConditionalCostReductionLine();

    [GeneratedRegex(@"^Retrace\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RetraceLine();

    [GeneratedRegex(@"^Fabricate (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex FabricateLine();

    /// <remarks>
    /// "Until ~ leaves the battlefield" is required rather than optional. Without it the line is
    /// a plain exile and belongs to a different matcher, and reading it as this one would give
    /// the exiled card back on a card that never promised to.
    /// </remarks>
    [GeneratedRegex(
        @"^When ~ enters, exile (?<t>target [^,]+?) until ~ leaves the battlefield\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExileUntilLeavesLine();

    [GeneratedRegex(
        @"^(?<who>~|enchanted creature|equipped creature) can't be blocked by (?<what>[^.]+?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CantBeBlockedByLine();

    /// <summary>"…can't be blocked except by X" — only X may block it (CR 509.1b).</summary>
    [GeneratedRegex(
        @"^(?<who>~|enchanted creature|equipped creature) can't be blocked except by (?<what>[^.]+?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CantBeBlockedExceptByLine();

    /// <summary>"…can't be blocked except by N or more creatures" (CR 509.1b).</summary>
    [GeneratedRegex(
        @"^~ can't be blocked except by (?<n>\d+|two|three|four|five|six|seven|eight|nine|ten) "
            + @"or more creatures\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MinimumBlockersLine();

    [GeneratedRegex(@"^creatures? with greater power$", RegexOptions.IgnoreCase)]
    private static partial Regex GreaterPowerBlockers();

    [GeneratedRegex(
        @"^creatures? with power (?<n>\d+) or (?<dir>less|greater|more)$", RegexOptions.IgnoreCase)]
    private static partial Regex PowerBlockers();

    [GeneratedRegex(
        @"^(?<kw>Morph|Megamorph|Disguise) (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MorphLine();

    [GeneratedRegex(
        @"^(?<who>~|(enchanted|equipped) " + AttachedSubject + @") doesn't untap during "
            + @"(your|its controller's|their controller's) untap step"
            + @"( if (?<when>[^.]+))?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DoesNotUntapLine();

    [GeneratedRegex(
        @"^As an additional cost to cast (~|this spell|it), (?<cost>.+?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AdditionalCostLine();

    [GeneratedRegex(@"^kicker (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex KickerLine();

    [GeneratedRegex(@"^Buyback (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BuybackLine();

    [GeneratedRegex(@"^Dash (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DashLine();

    [GeneratedRegex(@"^Blitz (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BlitzLine();

    [GeneratedRegex(@"^Surge (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SurgeLine();

    [GeneratedRegex(@"^Spectacle (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SpectacleLine();

    [GeneratedRegex(@"^Evoke (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EvokeLine();

    /// <summary>
    /// A small number as a word, because the target grammar reads "up to two" and not "up to 2".
    /// </summary>
    /// <remarks>
    /// Only the sizes support is printed at. Anything else is handed back unchanged, which leaves
    /// the line unread rather than producing a phrase nothing can parse.
    /// </remarks>
    private static string NumberInWords(string number) => number.ToLowerInvariant() switch
    {
        "1" => "one",
        "2" => "two",
        "3" => "three",
        "4" => "four",
        "5" => "five",
        _ => number,
    };

    /// <summary>"Two target creatures each get ..." - the distribution said out loud.</summary>
    [GeneratedRegex(
        @"(target [A-Za-z' ]+?) each (get|gain|deal|have)",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetsEach();

    /// <summary>"Support N", which is an instruction with its words left out (CR 701.41a).</summary>
    [GeneratedRegex(@"\bsupport (?<n>one|two|three|four|five|\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SupportWord();

    /// <summary>"Each of up to two target creatures" said the short way (CR 701.41a).</summary>
    [GeneratedRegex(
        @"each of ((up to )?(one|two|three|four|five|\d+) (other )?target)",
        RegexOptions.IgnoreCase)]
    private static partial Regex EachOfTargets();

    [GeneratedRegex(@"^Plot (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex PlotLine();

    [GeneratedRegex(@"^Replicate (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReplicateLine();

    [GeneratedRegex(@"^Offspring (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex OffspringLine();

    /// <remarks>
    /// The dash between the count and the cost is an em dash on the card, not a hyphen, and both
    /// are accepted because oracle text is not consistent about it.
    /// </remarks>
    [GeneratedRegex(
        @"^Suspend (?<n>\d+)\s*[—\-]\s*(?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SuspendLine();

    [GeneratedRegex(@"^Scavenge (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ScavengeLine();

    [GeneratedRegex(@"^Ninjutsu (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex NinjutsuLine();

    [GeneratedRegex(@"^Transmute (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex TransmuteLine();

    [GeneratedRegex(
        @"^(?<kind>Embalm|Eternalize) (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EternalizeLine();

    [GeneratedRegex(@"^Overload (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex OverloadLine();

    [GeneratedRegex(@"^Conspire\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ConspireLine();

    [GeneratedRegex(@"^Casualty (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CasualtyLine();

    [GeneratedRegex(@"^Backup (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupLine();

    [GeneratedRegex(@"^Bestow (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BestowLine();

    [GeneratedRegex(@"^Split second\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitSecondLine();

    /// <remarks>
    /// Word-bounded, so "targets" in a sentence about something else is left alone.
    /// </remarks>
    [GeneratedRegex(@"\btarget\b", RegexOptions.IgnoreCase)]
    private static partial Regex TargetWord();

    [GeneratedRegex(
        @"^Cumulative upkeep (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CumulativeUpkeepLine();

    /// <remarks>
    /// The land type keeps its capital letter, because that is what it is matched against: a
    /// subtype is a proper noun and <c>HasSubtype</c> is told the printed spelling.
    /// </remarks>
    [GeneratedRegex(
        @"^~ can't attack unless defending player controls an? (?<land>[A-Z][a-z]+)\.?$",
        RegexOptions.None)]
    private static partial Regex AttacksOnlyIfLine();

    [GeneratedRegex(@"^Foretell (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ForetellLine();

    [GeneratedRegex(@"^Madness (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MadnessLine();

    [GeneratedRegex(
        @"^if (~|this spell) was kicked, (?<effect>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex IfKickedLine();

    [GeneratedRegex(@"^bargain$", RegexOptions.IgnoreCase)]
    private static partial Regex BargainLine();

    [GeneratedRegex(
        @"^choose (?<n>one|two|three)( or (?<range>both|more))?\s*[—\-–]?$", RegexOptions.IgnoreCase)]
    private static partial Regex ModalHeader();

    [GeneratedRegex(@"^[•\u2022]\s*(?<mode>.+)$")]
    private static partial Regex ModalBullet();

    [GeneratedRegex(@"^Entwine (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EntwineLine();

    [GeneratedRegex(
        @"^Splice onto Arcane (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SpliceLine();

    [GeneratedRegex(@"^Squad (?<cost>(\{[^}]+\})+)[.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex SquadLine();

    [GeneratedRegex(@"^Assist[.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex AssistLine();

    [GeneratedRegex(@"^Dredge (?<n>\d+)[.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex DredgeLine();

    [GeneratedRegex(@"^Jump-start\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex JumpStartLine();

    [GeneratedRegex(@"^Afflict (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AfflictLine();

    [GeneratedRegex(@"^Firebending (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex FirebendingLine();

    [GeneratedRegex(@"^Cipher\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CipherLine();

    [GeneratedRegex(
        @"^Freerunning (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex FreerunningLine();

    [GeneratedRegex(
        @"^Prototype (?<cost>(\{[^}]+\})+)\s*[—―-]\s*(?<p>\d+)/(?<t>\d+)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PrototypeLine();

    [GeneratedRegex(
        @"^Miracle (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MiracleLine();

    [GeneratedRegex(
        @"^You may cast ~ as though it had flash if you pay (?<cost>(\{[^}]+\})+) more "
            + @"to cast it\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FlashSurchargeLine();

    [GeneratedRegex(
        @"^This effect doesn't remove [A-Za-z~' ]+\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EffectDoesNotRemoveLine();

    [GeneratedRegex(@"^Devour (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DevourLine();

    [GeneratedRegex(
        @"^You may have ~ assign its combat damage as though it weren't blocked\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AssignAsThoughUnblockedLine();

    [GeneratedRegex(@"^Dethrone\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DethroneLine();

    [GeneratedRegex(@"^Recover (?<cost>(\{[^}]+\})+)[.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex RecoverLine();

    [GeneratedRegex(@"^you cycle ~$", RegexOptions.IgnoreCase)]
    private static partial Regex CyclingTrigger();

    [GeneratedRegex(@"^Vanishing (?<n>\d+)[.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex VanishingLine();

    [GeneratedRegex(
        @"^(?<what>[A-Za-z ]+?)? ?spells you cast cost \{(?<n>\d+)\} less to cast\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CostReducerLine();

    /// <remarks>
    /// A permission, not an action: nothing happens when it is granted, and the only thing that
    /// changes is what its controller is shown. So it is answered by the view projection rather
    /// than by an effect, which is also the only place it *can* be answered - a library is hidden
    /// from everyone including its owner (CR 401.2), and the whole point of this line is that it
    /// stops being hidden from one of them.
    /// </remarks>
    [GeneratedRegex(
        @"^You may look at the top card of your library any time\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ShowTopOfLibraryLine();

    [GeneratedRegex(@"^you have no maximum hand size\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex NoMaximumHandSizeLine();

    [GeneratedRegex(
        @"^as (~|it) enters, choose a (?<what>color|creature type)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChooseAsEntersLine();

    [GeneratedRegex(
        @"^If damage would be dealt to ~, prevent that damage\. "
            + @"Remove a \+1/\+1 counter from ~\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PhantomDamageLine();

    [GeneratedRegex(
        @"^If ~ was kicked, it enters with (?<n>a|an|one|two|three|four|five|[0-9]+) "
            + @"[+]1/[+]1 counters? on it[.]?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex KickedCountersLine();

    [GeneratedRegex(
        @"^~ enters with a [+]1/[+]1 counter on it for each time it was kicked[.]?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MultikickedCountersLine();

    [GeneratedRegex(@"^Multikicker (?<cost>(\{[^}]+\})+)[.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex MultikickerLine();

    [GeneratedRegex(@"^Cast ~ only (?<when>[^.]+)[.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex CastOnlyLine();

    /// <remarks>
    /// "Another target" and "up to one target" are the commonest spellings of this line and the
    /// pattern took neither: Oblivion Ring and its family exile "another target nonland
    /// permanent", so the enters half went unread - and with it the return half, which is refused
    /// on purpose when its partner cannot be read. The phrase still goes to the target grammar,
    /// so a qualifier that grammar does not know leaves the line unread rather than exiling more
    /// than the card allows.
    /// </remarks>
    [GeneratedRegex(
        @"^When ~ enters, exile (?<t>(another |up to one )?target [^,.]+)\.?$",
        RegexOptions.None)]
    private static partial Regex EntersExileTargetLine();

    [GeneratedRegex(
        @"^When ~ leaves the battlefield, return the exiled cards? to the battlefield"
            + @"( under (its|their) owner's control)?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReturnExiledLine();

    [GeneratedRegex(@"^Sunburst$", RegexOptions.IgnoreCase)]
    private static partial Regex SunburstLine();

    /// <summary>Sunburst without the keyword — the sentence cards print when they spell it out.</summary>
    [GeneratedRegex(
        @"^~ enters with an? \+1/\+1 counter on it for each colou?r of mana spent to cast it\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpelledOutSunburstLine();

    /// <remarks>
    /// "An additional land" and "two additional lands" are the same permission with a count, so
    /// one pattern reads both and an absent number means one.
    /// </remarks>
    [GeneratedRegex(
        @"^you may choose not to untap ~ during your untap step\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MayDeclineUntapLine();

    [GeneratedRegex(@"^skip your draw step\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SkipDrawStepLine();

    [GeneratedRegex(
        @"^you may play (an|(?<n>one|two|three)) additional lands? "
            + @"(on each of your turns|each turn)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExtraLandDropLine();

    [GeneratedRegex(
        @"^Play with the top card of your library revealed\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RevealTopOfLibraryLine();

    [GeneratedRegex(@"^Escalate (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EscalateLine();

    /// <summary>
    /// A cost made only of mana symbols, the tap symbol and separators — or nothing at all.
    /// </summary>
    /// <remarks>
    /// Nothing at all is a real cost. "Sacrifice this creature: Draw a card" charges no mana, and
    /// once the sacrifice has been lifted out the remainder is the empty string — which the
    /// one-or-more form rejected, quietly costing every free sacrifice ability in the game.
    /// </remarks>
    [GeneratedRegex(@"^(\{[^}]+\}|,|\s)*$")]
    private static partial Regex PayableCost();

    [GeneratedRegex(
        @"^(When|Whenever|At)\s+(?<when>[^,]+),\s*(?<effect>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TriggerLine();
}

/// <summary>What a card compiled to, and what could not be read.</summary>
public sealed record CompiledCard
{
    public required string Name { get; init; }

    /// <summary>
    /// The partner ability this card carries, if any, exactly as printed (CR 702.124a).
    /// </summary>
    /// <remarks>
    /// Recorded rather than acted on, because there is nothing to act on: partner abilities
    /// "modify the rules for deck construction ... and they function before the game begins".
    /// A card whose only text is "Partner" is fully implemented for play the moment the line is
    /// read - it does nothing during a game, and pretending otherwise would be inventing an
    /// ability the card does not have.
    /// <para>
    /// Kept as a string rather than dropped so that deck construction has it when it is built.
    /// Reading a line and discarding what it said is how a fact goes missing without anybody
    /// noticing; the field is here so the next person looking for it finds it.
    /// </para>
    /// <para>
    /// <strong>"Partner with [name]" is deliberately not one of these.</strong> It is two
    /// abilities, not one (CR 702.124j): the deck-construction permission, and a real triggered
    /// ability that searches for the named card when this permanent enters. Sweeping it in here
    /// would compile a card that silently lost its tutor.
    /// </para>
    /// </remarks>
    public string? PartnerRule { get; init; }

    public SpellDefinition? Spell { get; init; }

    /// <summary>
    /// What this card does when it is cast as an Adventure (CR 715.2, 715.3b).
    /// </summary>
    /// <remarks>
    /// A second spell on the same card, and the card is not it: CR 715.4 says an adventurer card
    /// has only its normal characteristics in every zone except the stack, so this is reached
    /// only by choosing it as the card is cast.
    /// <para>
    /// Kept beside <see cref="Spell"/> rather than compiled as a separate card so that the pool
    /// finds it under the card it is printed on - which is the card a player is holding.
    /// </para>
    /// </remarks>
    public SpellDefinition? Adventure { get; init; }

    /// <summary>What the Adventure half costs, exactly as printed (CR 715.3a).</summary>
    public string? AdventureCostRaw { get; init; }

    /// <summary>
    /// The spell a prepared permanent offers a copy of, if this card has one.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="Spell"/> for the same reason the Adventure is, but reached
    /// differently: an Adventure is a way of casting the card from hand, while this is only ever
    /// available from the battlefield, off a permanent that is already prepared. It is not a
    /// half - nobody may cast it from hand for its cost - so it must be kept out of
    /// <see cref="Halves"/>, which is otherwise exactly what a second costed face compiles to.
    /// </remarks>
    public SpellDefinition? PreparedSpell { get; init; }

    /// <summary>What a prepared permanent's spell costs, exactly as printed.</summary>
    public string? PreparedCostRaw { get; init; }

    /// <summary>
    /// How many +1/+1 counters each creature sacrificed to devour is worth (CR 702.81a).
    /// </summary>
    public int DevourCount { get; init; }

    /// <summary>
    /// Whether the prepared spell may be cast at instant speed (CR 117.1a).
    /// </summary>
    /// <remarks>
    /// The front face's type decides nothing here: the permanent holding the spell is a creature,
    /// and what is being cast is the face behind it. Kept as a fact about the card because by the
    /// time it is cast the other face's type line is not reachable from the object on the
    /// battlefield.
    /// </remarks>
    public bool PreparedIsInstant { get; init; }

    /// <summary>Whether both halves may be cast together as one spell (CR 702.102a).</summary>
    public bool HasFuse { get; init; }

    /// <summary>
    /// The separately castable halves of a split card, or empty for an ordinary one.
    /// </summary>
    /// <remarks>
    /// Empty unless there are at least two, because one castable face is what every ordinary card
    /// has and calling that a half would put every card in the game down a path built for two.
    /// </remarks>
    public ImmutableList<CardHalf> Halves { get; init; } = [];

    public ImmutableList<ActivatedAbilityDefinition> Activated { get; init; } = [];

    public ImmutableList<TriggeredAbilityDefinition> Triggers { get; init; } = [];

    public ImmutableList<ContinuousEffectDefinition> Statics { get; init; } = [];

    public ImmutableList<ReplacementEffectDefinition> Replacements { get; init; } = [];

    /// <summary>What this permanent takes off its controller's spells (CR 601.2f).</summary>
    public ImmutableList<CostReducer> CostReducers { get; init; } = [];

    /// <summary>
    /// Whether it lets its controller look at the top of their library (CR 401.2).
    /// </summary>
    public bool ShowsTopOfLibrary { get; init; }

    /// <summary>
    /// Whether its controller has no maximum hand size (CR 402.2).
    /// </summary>
    public bool RemovesHandLimit { get; init; }

    /// <summary>What this permanent chooses as it enters, if anything (CR 614.12).</summary>
    public ChoiceOnEntry ChoosesOnEntry { get; init; }

    /// <summary>How many extra lands its controller may play each turn (CR 305.2).</summary>
    public int ExtraLandDrops { get; init; }

    /// <summary>Whether its controller may leave it tapped at untap (CR 502.3).</summary>
    public bool MayDeclineUntap { get; init; }

    /// <summary>Whether its controller skips their draw step (CR 504.1).</summary>
    public bool SkipsDrawStep { get; init; }

    /// <summary>
    /// Whether it plays with the top card revealed, which every player may see (CR 401.2).
    /// </summary>
    public bool RevealsTopOfLibrary { get; init; }

    /// <summary>
    /// Keywords the card's rules text grants it beyond those printed as keywords (CR 702).
    /// </summary>
    public KeywordAbility GrantedKeywords { get; init; } = KeywordAbility.None;

    /// <summary>Lines of rules text nothing recognised. Empty means the card was fully read.</summary>
    /// <summary>The land type this creature may only attack a controller of (CR 506.3).</summary>
    public string? AttacksOnlyIfDefenderControls { get; init; }

    public ImmutableList<string> Unhandled { get; init; } = [];

    /// <summary>Whether every line of the card was understood.</summary>
    public bool IsComplete => Unhandled.IsEmpty;

    /// <summary>Whether anything at all came out — a vanilla card compiles to nothing, legally.</summary>
    /// <remarks>
    /// The two flat fields are in here as well as the five lists, because they are abilities that
    /// happen to be stored as a value: a card whose only text is "~ can't attack unless defending
    /// player controls an Island" compiles to nothing but that string, and the combat rules read
    /// it. Leaving them out made this property say "inert" about eight cards the engine enforces.
    /// </remarks>
    public bool HasAbilities =>
        Spell is not null || !Activated.IsEmpty || !Triggers.IsEmpty
        || !Statics.IsEmpty || !Replacements.IsEmpty
        || GrantedKeywords != KeywordAbility.None
        || !CostReducers.IsEmpty
        || ShowsTopOfLibrary
        || RemovesHandLimit
        || ChoosesOnEntry != ChoiceOnEntry.None
        || ExtraLandDrops > 0
        || MayDeclineUntap
        || SkipsDrawStep
        || RevealsTopOfLibrary
        || AttacksOnlyIfDefenderControls is not null;
}
