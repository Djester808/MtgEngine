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
        // A battle that is not a Siege is still refused whole, fail-closed. The engine now plays
        // CR 310 — defense counters on entry, a protector, being attacked, the defeat that
        // exiles and recasts — but the protector rules vary by battle type (CR 310.9a, 310.12a)
        // and only the Siege's are implemented, because every battle printed into any legal
        // format is one. The two non-Siege printings (both "Battle — Control Point") are legal
        // nowhere, so their provisions are skipped the way the Ring and the dungeons skipped
        // unprintable rules — with this comment rather than with a guess.
        if ((card.CardTypes & CardType.Battle) != 0
            && !card.Subtypes.Contains("Siege", StringComparer.Ordinal))
        {
            return new CompiledCard
            {
                Name = card.Name,
                Unhandled = ["(battle - only the Siege battle type is implemented; CR 310.9a)"],
            };
        }

        if (IsClassWithLevels(card))
            return CompileClass(card);

        // A Case is the same shape as a Class with a simpler switch: one designation rather than
        // a ladder of levels, set by a condition rather than bought. The section under
        // "Solved -" must not function before it is (CR 719.3c), which is the same requirement
        // and gets the same answer.
        if (IsCase(card))
            return CompileCase(card);

        // A leveler is the Class problem with a different switch. Its bands are selected by a
        // number of counters rather than a bought designation - CR 711.4 says the two do not
        // interact - and the lines under a band must not function before the permanent has been
        // levelled that far (CR 711.3), which is the same requirement and gets the same answer.
        //
        // It answers null rather than being guarded by a type line, because nothing about a
        // leveler is a subtype: the only sign is the text, and a card whose bands this cannot
        // read has to fall through to the ordinary loop and be reported unread.
        if (CompileLeveler(card) is { } levelled)
            return levelled;

        // A cleave card is one card carrying two spells - the printed reading and the cleaved
        // one - so it is the adventurer problem again and gets the adventurer answer: compile
        // each reading as a card of its own, and record which is on the stack as it is cast.
        if (CompileCleave(card) is { } cloven)
            return cloven;

        // An instant or sorcery with gift is the same shape one mechanic along: the promise is
        // declared as the spell is cast (CR 702.174k), and everything it changes - the delivery,
        // the promised sentences, their targets - is a fact about which spell is on the stack.
        if (CompileGiftSpell(card) is { } gifted)
            return gifted;

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
                        // half compiled as an ordinary one that could be cast from hand. The
                        // reminder text comes off first, because this reads the face's raw text
                        // rather than Lines' cleaned output - every real printing says
                        // "Aftermath (Cast this spell only from your graveyard. ...)", so the
                        // bare-word match set the flag on zero corpus cards while fourteen
                        // "complete" split cards quietly became castable from hand twice.
                        HasAftermath = printed.OracleText
                            .Split('\n')
                            .Any(l => AftermathLine().IsMatch(
                                Reminder().Replace(l, string.Empty).Trim())),
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

                // Reminder text off first, as the aftermath flag above: every printing says
                // "Fuse (You may cast one or both halves ...)", and the bare-word match read
                // raw text and found nothing.
                HasFuse = card.Faces.Any(f => f.OracleText
                    .Split('\n')
                    .Any(l => FuseLine().IsMatch(Reminder().Replace(l, string.Empty).Trim()))),
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

        // CR 310.12b: every Siege has "When the last defense counter is removed from this
        // permanent, exile it, then you may cast it transformed without paying its mana cost."
        // Intrinsic, like the basic land types above: it is printed only as reminder text, which
        // this compiler strips as noise, so without this a defeated Siege would simply sit at
        // zero until the state-based action buried it and no flip side was ever cast. The rest
        // of CR 310 — entering with defense counters, the protector, being attacked — is what
        // the rules do for every battle and lives in the engine, not on the card.
        if (card.CardTypes.HasFlag(CardType.Battle)
            && card.Subtypes.Contains("Siege", StringComparer.Ordinal))
        {
            triggers.Add(SiegeRules.DefeatTrigger);
        }

        var isSpell = card.CardTypes.HasFlag(CardType.Instant)
            || card.CardTypes.HasFlag(CardType.Sorcery);

        var modes = ImmutableList.CreateBuilder<SpellMode>();
        var modesToChoose = 0;
        var modesMax = 0;
        var modesMayRepeat = false;
        ConditionalModes? extraModes = null;
        string? modalSpellHeader = null;

        // Whether the modes being read are spree's, which is what allows a priced bullet to be
        // read as one. Kept across the loop for the same reason the mode count is: the header
        // and the bullets are separate lines.
        var spree = false;

        // Which trigger, if any, the bullets that follow belong to. "When ~ enters, choose one —"
        // is a trigger whose effect is a menu, and the menu arrives on the lines after it - so
        // something has to remember, between lines, that the modes being read are the ability's
        // and not the spell's.
        var modalTriggerAt = -1;
        ManaCostSpec? entwine = null;
        ManaCostSpec? splice = null;
        ManaCostSpec? squad = null;
        var costModifiers = ImmutableList.CreateBuilder<CostModifier>();
        var showsTop = false;
        var noHandLimit = false;
        var chooses = ChoiceOnEntry.None;
        var devour = 0;
        var amplify = 0;
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
        var kickerCosts = ImmutableList.CreateBuilder<KickerOption>();
        var bargainDiscount = 0;
        FactModes? factModes = null;
        var modesFromX = false;
        string? partnerRule = null;
        var deckRules = ImmutableList.CreateBuilder<string>();
        ManaCostSpec? plot = null;
        ManaCostSpec? replicate = null;
        ManaCostSpec? offspring = null;
        ManaCostSpec? suspend = null;
        ManaCostSpec? overload = null;
        ManaCostSpec? awaken = null;
        string? awakenLine = null;
        var awakenCounters = 0;
        ManaCostSpec? sneak = null;
        ChosenCost? sneakReturn = null;
        ChosenCost? teamwork = null;

        // CR 702.155a: read ahead changes what a chapter ability may do on the turn the Saga
        // arrives, and the chapter lines are printed above it on every card that has it. So the
        // word is looked for before any line is read rather than hoping for a line order - a
        // chapter predicate built before the keyword was seen would be built without the rule.
        var readAhead = card.Subtypes.Contains("Saga", StringComparer.OrdinalIgnoreCase)
            && Lines(card).Any(l => ReadAheadLine().IsMatch(l));
        ChosenCost? conspire = null;
        var splitSecond = false;
        ManaCostSpec? bestow = null;
        TargetSpec? bestowTarget = null;
        ManaCostSpec? mutate = null;
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
        var hasGift = false;

        foreach (var line in Lines(card))
        {
            // Square brackets are cleave's marker (CR 702.148a): words a paid cost removes. The
            // cleave path strips them before either reading is compiled, so a bracket that
            // reaches this loop is on a line no cleave-aware reader claimed - and it must be
            // refused, not ignored. Ignored, "draw a card for each creature you control [with
            // flying]" read as the flier-less sentence: a card compiled better than printed,
            // which is the one class of error the fail-closed rule exists to prevent. The
            // corpus's only other brackets are loyalty costs inside granted-ability quotes and
            // dice results, and every one of those lines is unread anyway.
            if (line.Contains('[', StringComparison.Ordinal)
                || line.Contains(']', StringComparison.Ordinal))
            {
                unhandled.Add(line);
                continue;
            }

            if (TryGiftLine(line, triggers, ref hasGift))
                continue;

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

            var modesBefore = modes.Count;
            if (TryModal(
                line,
                modes,
                ref modesToChoose,
                ref modesMax,
                ref modesMayRepeat,
                ref extraModes,
                ref spree,
                ref factModes,
                ref modesFromX))
            {
                // A line the modal reader took that added no mode is the header. Remembered so
                // that a menu which turns out to have nothing on it can put its own words back
                // into the unread list rather than a reconstruction of them.
                if (modes.Count == modesBefore)
                    modalSpellHeader = line;

                continue;
            }

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

            if (TrySoulbond(line, card, triggers))
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

            if (TryRipple(line, card, triggers))
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

            if (TryChooseAsEnters(line, ref chooses))
                continue;

            if (TryEntersTappedChoosing(line, replacements, ref chooses))
                continue;

            // "Play with the top card of your library revealed" - the same card, shown to
            // everybody rather than to one player. Kept apart from the private permission
            // because the difference is the whole of what the line says.
            if (RevealTopOfLibraryLine().IsMatch(line))
            {
                revealsTop = true;
                continue;
            }

            if (TryCostModifier(line, costModifiers))
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

            if (TryRavenous(line, card, triggers, replacements))
                continue;

            if (TryAmplify(line, card, ref amplify))
                continue;

            if (TryGroupEntersWithAdditionalCounter(line, card, replacements))
                continue;

            if (TryEntersWithCountersPerGroup(line, card, replacements))
                continue;

            if (TryEntersWithCounters(line, card, replacements))
                continue;

            if (ReadAheadLine().IsMatch(line) && readAhead)
                continue;

            if (TrySagaChapter(line, triggers, unhandled, readAhead))
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

            // The same thing partner is, for the three other rules that are settled before a
            // game starts and say nothing about one: which cards may be your commander
            // (CR 903.3a), how many copies a deck may hold (CR 100.2a), and how a card is
            // drafted (CR 905.2c). Kept rather than dropped, for the same reason the partner
            // rule is - deck construction is the reader that wants them.
            if (DeckConstructionLine().IsMatch(line))
            {
                deckRules.Add(line.TrimEnd('.').Trim());
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

            if (TryStaticLine(line, card, statics))
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

            if (TryHarmonize(line, ref castFrom))
                continue;

            if (TryKicker(line, ref kicker))
                continue;

            // CR 702.33b: "Kicker [A] and/or [B]" is two kicker abilities, not one cost — the
            // caster may pay either, both, or neither. Each cost keeps its printed spelling,
            // because "if it was kicked with its [A] kicker" names it by exactly that
            // (CR 702.33f).
            if (KickerAndOrLine().Match(line) is { Success: true } twoKickers)
            {
                kickerCosts.Add(new KickerOption(
                    ManaCostSpec.Parse(twoKickers.Groups["a"].Value),
                    twoKickers.Groups["a"].Value.ToUpperInvariant()));
                kickerCosts.Add(new KickerOption(
                    ManaCostSpec.Parse(twoKickers.Groups["b"].Value),
                    twoKickers.Groups["b"].Value.ToUpperInvariant()));

                continue;
            }

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

            // CR 702.140a: "you may pay [cost] rather than pay this spell's mana cost. If you do,
            // it becomes a mutating creature spell and targets a non-Human creature with the same
            // owner as this spell." Both halves are the spell's business - the cost swap and the
            // target it acquires - so the line contributes nothing to the permanent, exactly as
            // bestow's does not.
            if (MutateLine().Match(line) is { Success: true } merging)
            {
                mutate = ManaCostSpec.Parse(merging.Groups["cost"].Value);
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

            if (TeamworkLine().Match(line) is { Success: true } together)
            {
                // CR 702.194a: "you may tap any number of creatures you control with total power
                // N or more" - crew's shape as an optional additional cost, and the same
                // MinTotalPower the crew cost already carries answers it.
                if (EffectPhrase.Specs.Parse("target creature you control") is not
                    { Kind: TargetKind.Permanent } crewmate)
                {
                    unhandled.Add(line);
                    continue;
                }

                teamwork = new ChosenCost(
                    ChosenCostKind.TapPermanents,
                    0,
                    crewmate with { Description = "a creature you control" },
                    ExcludesSource: false,
                    MinTotalPower: int.Parse(
                        together.Groups["n"].Value, CultureInfo.InvariantCulture));

                continue;
            }

            if (TryHideaway(line, card, triggers))
                continue;

            if (SneakLine().Match(line) is { Success: true } snuck)
            {
                // CR 702.190a: the mana is only half the price - an attacker goes back to hand
                // with it. Both halves are read here or neither is, because the cost alone is a
                // discount on the printed card rather than an alternative way to cast it.
                if (EffectPhrase.Specs.Parse("target unblocked creature you control") is not
                    { Kind: TargetKind.Permanent } attacker)
                {
                    unhandled.Add(line);
                    continue;
                }

                sneak = ManaCostSpec.Parse(snuck.Groups["cost"].Value);
                sneakReturn = new ChosenCost(
                    ChosenCostKind.ReturnToHand,
                    1,
                    attacker with { Description = "an unblocked attacker you control" });

                continue;
            }

            if (AwakenLine().Match(line) is { Success: true } roused)
            {
                awaken = ManaCostSpec.Parse(roused.Groups["cost"].Value);
                awakenCounters = int.Parse(
                    roused.Groups["n"].Value, CultureInfo.InvariantCulture);

                // Held so that the line can go back unread if the half cannot be built - see
                // the end of Compile, where the target phrase is read.
                awakenLine = line;
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

            // CR 702.119a: emerge is two static abilities in one word - an alternative cost paid
            // with mana *and* a creature, and a reduction of that cost by what the creature was
            // worth. Both halves ride the one offer: an emerge that charged the mana and not the
            // sacrifice would be a cheaper card, and one that took the sacrifice without the
            // discount would be a dearer one.
            if (EmergeLine().Match(line) is { Success: true } emerging
                && EffectPhrase.Specs.Parse("target creature you control") is { } sacrificed)
            {
                conditionalCost = new ConditionalCost(
                    "emerge",
                    "CR 702.119a",
                    ManaCostSpec.Parse(emerging.Groups["cost"].Value),

                    // No board question of its own. What gates the offer is whether there is a
                    // creature to give up, and the cost payment refuses that on its own terms -
                    // naming the creature that could not be sacrificed rather than the keyword.
                    static (_, _) => true)
                {
                    Payments = [
                        new ChosenCost(
                            ChosenCostKind.SacrificePermanents,
                            Count: 1,
                            What: sacrificed with { Description = "a creature you control" }),
                    ],
                    ReducedByManaValueSacrificed = true,
                };

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

            if (TryAlternativeManaCost(line, card, ref conditionalCost))
                continue;

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
            // (CR 702.33e). Only meaningful on a card that has a kicker to pay — either the
            // single form or the "and/or" pair, since paying any kicker cost kicks (CR 702.33d).
            if ((kicker is not null || kickerCosts.Count > 0)
                && TryIfKicked(line, spellEffects, spellTargets))
            {
                continue;
            }

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

            // "This spell costs {2} less to cast if it's bargained" — the CR 601.2f reduction
            // whose condition is the caster's own declaration (CR 601.2b, 702.166b), which only
            // the cast in progress can answer — the board-condition reductions cannot. Only
            // meaningful on a card that can be bargained at all, and the keyword line always
            // comes first on the ones that print it.
            if (bargain is not null
                && BargainDiscountLine().Match(line) is { Success: true } cheaper)
            {
                bargainDiscount = int.Parse(
                    cheaper.Groups["n"].Value, CultureInfo.InvariantCulture);

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

            if (TryKickedCounters(
                line,
                replacements,
                statics,
                kicker is not null || multikicker is not null,
                kickerCosts))
            {
                continue;
            }

            if (TryMultikickedCounters(line, replacements))
                continue;

            if (TrySunburst(line, card, replacements))
                continue;

            if (TryPhantomDamage(line, replacements))
                continue;

            // A shield a permanent's static ability puts up. Refused on an instant or sorcery,
            // where the same words are a one-shot effect that lasts the turn and belong to the
            // sentence parser instead — compiled here they would become a shield that never came
            // down, on a card that had already gone to the graveyard.
            if (!isSpell && TryStaticPrevention(line, replacements))
                continue;

            if (TryDamageAmount(line, replacements))
                continue;

            if (TryCounterAmount(line, replacements))
                continue;

            if (TryLifeGainAmount(line, replacements))
                continue;

            if (TryDiesReplacement(line, replacements))
                continue;

            if (TryExtraDie(line, replacements))
                continue;

            if (TryShockland(line, replacements))
                continue;

            if (TryGraveyardReplacement(line, replacements))
                continue;

            if (TryEntersPrepared(line, replacements))
                continue;

            if (TryEntersAsACopy(line, replacements))
                continue;

            if (TryEntersTapped(line, replacements))
                continue;

            if (TryLoyaltyAbility(line, activated))
                continue;

            if (TryManaAbility(line, activated))
                continue;

            // Before the general activated reader, which would take the colon and then fail on
            // the effect behind it — filing a line it very nearly understood as unread.
            if (TryLicid(line, activated))
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

            // Last, and only for a permanent. A line of static clauses about what this is
            // attached to, joined by commas and "and", where every clause on its own is one the
            // readers above understand. Placed here so it can only ever see a line nothing else
            // would take — a fold placed higher would claim clauses from its neighbours and
            // disable them while the coverage total still went up.
            if (!isSpell && TryAttachedConjunction(line, card, statics))
                continue;

            unhandled.Add(line);
        }

        // Read last, because whether backup can be honoured depends on what else the card turned
        // out to have (CR 702.164a).
        if (backupLine is not null)
            TryBackup(backupLine, card, backup, triggers, unhandled);

        // A spell that says "choose one —" and has no bullets under it offers a menu with
        // nothing on it - the same fault the modal trigger already guards against, in the other
        // place it can happen. The header goes back to unread and the spell stops being modal,
        // rather than compiling into a choice that cannot be made. Spree brought it within reach
        // of a real card: every one of its bullets carries a cost and an effect, so a card whose
        // effects the vocabulary cannot read keeps its keyword and loses its whole menu.
        if ((modesToChoose > 0 || modesMax > 0 || modesFromX) && modes.Count == 0)
        {
            unhandled.Add(modalSpellHeader ?? "choose one —");
            modesToChoose = 0;
            modesMax = 0;
            modesMayRepeat = false;
            extraModes = null;
            factModes = null;
            modesFromX = false;
        }

        // CR 702.113a: the awaken half is a whole spell ability of its own, and it is built here
        // rather than where the line was read because it targets a land the printed spell knows
        // nothing about. Its effects index into their own one-target slice, the way a mode's do,
        // so the card's own targets keep the indices they were compiled with.
        var awakenTarget = awaken is null
            ? null
            : EffectPhrase.Specs.Parse("target land you control");

        var awakenEffects = ImmutableList<IEffect>.Empty;

        if (awaken is not null && awakenTarget is not null)
        {
            awakenEffects =
            [
                new PutCounters(CounterKinds.PlusOnePlusOne, awakenCounters),

                // Four separate effects because they are four separate layers (CR 613.1): the
                // creature type is layer 4, the size is layer 7b and haste is layer 6, and one
                // effect cannot be in two of them. Adding the card type rather than replacing it
                // is what the rider "it's still a land" says.
                new PumpUntilEndOfTurn(GenerativeEffects.BecomesId(CardType.Creature))
                {
                    ForTheTurn = false,
                },
                new PumpUntilEndOfTurn(GenerativeEffects.GainsCreatureTypeId("Elemental"))
                {
                    ForTheTurn = false,
                },
                new PumpUntilEndOfTurn(GenerativeEffects.SetPowerToughnessId(0, 0))
                {
                    ForTheTurn = false,
                },
                new PumpUntilEndOfTurn(GenerativeEffects.GrantId(KeywordAbility.Haste))
                {
                    ForTheTurn = false,
                },
            ];
        }

        // A cost with nothing behind it would be a discount on the printed spell, which is a
        // strictly better card than the one that was printed. The line goes back unread instead.
        if (awakenLine is not null && awakenEffects.IsEmpty)
        {
            unhandled.Add(awakenLine);
            awaken = null;
        }

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
            ModesMayRepeat = modesMayRepeat,
            ExtraModes = extraModes,
            EntwineCost = entwine,
            SpliceCost = splice,
            SquadCost = squad,
            HasAssist = assist,
            EscalateCost = escalate,
            KickerCost = kicker,
            KickerCosts = kickerCosts.ToImmutable(),
            BargainCost = bargain,
            BargainDiscount = bargainDiscount,
            ModesOnFact = factModes,
            ModesFromX = modesFromX,
            BuybackCost = buyback,
            DashCost = dash,
            BlitzCost = blitz,
            MultikickerCost = multikicker,
            EvokeCost = evoke,
            ConditionalAlternativeCost = conditionalCost,
            HasSplitSecond = splitSecond,
            BestowCost = bestow,
            BestowTarget = bestowTarget,
            MutateCost = mutate,
            MutateTarget = mutate is null ? null : MutateTargetSpec,
            CopyingCost = conspire,
            OverloadCost = overload,
            OverloadEffects = overloadEffects,
            AwakenCost = awaken,
            AwakenTarget = awaken is null ? null : awakenTarget,
            AwakenEffects = awakenEffects,
            SneakCost = sneak,
            SneakReturn = sneakReturn,
            TeamworkCost = teamwork,
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

        // CR 702.166c, 702.194b, 702.33f: each read-back clause is linked to the cost printed on
        // the same card (CR 607.2), so a card that reads one without having the ability is not a
        // card this engine can play. The sentence grammar cannot see the rest of the card, so
        // the check is here, where it can — and it scans the triggers as well as the spell,
        // because the clause rides wherever a sentence does.
        var everySentenceEffect = spellEffects
            .Concat(triggers.SelectMany(t => t.Effects))
            .ToList();

        if (bargain is null
            && EffectTree.Flatten(everySentenceEffect).Any(e => e is IfBargained))
        {
            unhandled.Add("if this spell was bargained");
        }

        if (teamwork is null
            && EffectTree.Flatten(everySentenceEffect).Any(e => e is IfTeamwork))
        {
            unhandled.Add("if this spell was cast using teamwork");
        }

        foreach (var perCost in EffectTree.Flatten(everySentenceEffect).OfType<IfKickedWith>())
        {
            if (!kickerCosts.Any(k =>
                string.Equals(k.Printed, perCost.Cost, StringComparison.OrdinalIgnoreCase)))
            {
                unhandled.Add($"if it was kicked with its {perCost.Cost} kicker");
            }
        }

        // The same linkage for a mode count switched by a cast fact: "choose any number instead"
        // on a card with no kicker would be a menu no cast could ever widen, and reading it as
        // the plain count would be the wrong card in the other direction.
        if (factModes is { } gated
            && gated.Fact switch
            {
                CastFact.Kicked =>
                    kicker is null && multikicker is null && kickerCosts.Count == 0,
                CastFact.Teamwork => teamwork is null,
                _ => true,
            })
        {
            unhandled.Add("choose-instead clause with no matching cost");
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
        //
        // CR 702.155b replaces this ability outright on a Saga with read ahead: that one enters
        // with a *chosen* number of counters instead of one, which is a question and therefore
        // lives in the engine's owed-choice sweep rather than in a replacement effect here.
        var isSaga = card.Subtypes.Contains("Saga", StringComparer.OrdinalIgnoreCase)
            && triggers.Any(t => t.Chapter is not null);

        // The word on a card with no chapter this compiler could read has nothing to change, and
        // a Saga that started at a chapter it cannot run is worse than one left unread.
        if (readAhead && !isSaga)
            unhandled.Add("Read ahead");

        if (isSaga && !readAhead)
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
            DeckRules = deckRules.ToImmutable(),
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
            CostModifiers = costModifiers.ToImmutable(),
            ShowsTopOfLibrary = showsTop,
            RemovesHandLimit = noHandLimit,
            ChoosesOnEntry = chooses,
            DevourCount = devour,
            AmplifyCount = amplify,
            HasReadAhead = isSaga && readAhead,
            ExtraLandDrops = extraLandDrops,
            MayDeclineUntap = mayDeclineUntap,
            SkipsDrawStep = skipsDraw,
            RevealsTopOfLibrary = revealsTop,
            Statics = statics.ToImmutable(),
            GrantedKeywords = grantedKeywords,
            AttacksOnlyIfDefenderControls = attacksOnlyIf,
            HasGift = hasGift,
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
        Defense = card.Defense,
    };

    /// <summary>
    /// The same card saying something else: one reading of a card that carries more than one.
    /// </summary>
    /// <remarks>
    /// The oracle id gains a marker so the recursion guards in <see cref="Lines"/> and the
    /// compiled pool's cache never mistake a reading for the card itself.
    /// </remarks>
    private static CardDefinition WithText(CardDefinition card, string text, string reading) =>
        new()
        {
            OracleId = card.OracleId + "#" + reading,
            Name = card.Name,
            OracleText = text,
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
    /// Compiles a cleave card as its two readings (CR 702.148a).
    /// </summary>
    /// <remarks>
    /// "Cleave [cost]" is an alternative cost plus a text-changing effect: paying it removes
    /// every word in square brackets. So the card is compiled twice - once with the brackets
    /// dropped and the words kept, once with the bracketed words gone - and each reading goes
    /// through every matcher this file has, exactly as an Adventure's two faces do. The printed
    /// reading is the card; the cleaved one is swapped onto the stack only when the cleave cost
    /// was paid.
    /// <para>
    /// Fail-closed on both halves: a card either of whose readings has a line the compiler
    /// cannot read stays incomplete, because a cleave card that could only be cast one way is
    /// not the card that was printed. And a cleaved reading that compiled to anything besides a
    /// spell is refused outright - what is swapped in at cast is the spell alone, so an ability
    /// only that reading had would be silently lost.
    /// </para>
    /// </remarks>
    private static CompiledCard? CompileCleave(CardDefinition card)
    {
        if (card.Faces.Count > 1 || string.IsNullOrEmpty(card.OracleText))
            return null;

        var lines = card.OracleText.Split('\n');
        var costLine = lines
            .Select(l => CleaveLine().Match(Reminder().Replace(l, string.Empty).Trim()))
            .FirstOrDefault(m => m.Success);

        if (costLine is not { Success: true })
            return null;

        var rest = string.Join(
            '\n',
            lines.Where(l => !CleaveLine().IsMatch(Reminder().Replace(l, string.Empty).Trim())));

        var printed = Compile(WithText(
            card,
            rest.Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal),
            "printed"));

        var cleaved = Compile(WithText(card, BracketedWords().Replace(rest, string.Empty), "cleaved"));

        var unhandled = printed.Unhandled.AddRange(
            cleaved.Unhandled.Where(l => !printed.Unhandled.Contains(l, StringComparer.Ordinal)));

        // The swap carries a spell and nothing else, so a reading whose text compiled into a
        // trigger, a static or an activated ability has nowhere to put it - and a reading that
        // compiled to no spell at all would resolve as nothing. Both are refused rather than
        // shipped smaller than printed.
        if (unhandled.IsEmpty
            && (cleaved.Spell is null
                || !cleaved.Activated.IsEmpty
                || !cleaved.Triggers.IsEmpty
                || !cleaved.Statics.IsEmpty
                || !cleaved.Replacements.IsEmpty))
        {
            unhandled = unhandled.Add(
                "(cleave - the cleaved reading is not a single spell: " + rest + ")");
        }

        return printed with
        {
            Name = card.Name,
            CleaveSpell = unhandled.IsEmpty ? cleaved.Spell : null,
            CleaveCostRaw = costLine.Groups["cost"].Value,
            Unhandled = unhandled,
        };
    }

    /// <summary>
    /// What each kind of gift delivers, in the sentence CR 702.174d-j defines for it.
    /// </summary>
    /// <remarks>
    /// The delivery is synthesized from the rule's own sentence and parsed by the ordinary
    /// phrase grammar, so a token here is the same token the sentence would make anywhere else.
    /// "Gift an extra turn" (one card, blocked by its other text regardless) and "Gift a
    /// Rhystic Study" (one card; the kind is not defined by CR 702.174 at all) are deliberately
    /// absent - an absent kind leaves the gift line unread rather than delivering the wrong
    /// present.
    /// </remarks>
    private static readonly Dictionary<string, string> GiftDeliveries =
        new(StringComparer.Ordinal)
        {
            ["a card"] = "Draw a card.",
            ["a Food"] = "Create a Food token.",
            ["a tapped Fish"] = "Create a tapped 1/1 blue Fish creature token.",
            ["a Treasure"] = "Create a Treasure token.",
            ["an Octopus"] = "Create an 8/8 blue Octopus creature token.",
        };

    /// <summary>
    /// The effects that hand a promised gift to the chosen opponent, or null for a kind the
    /// engine cannot deliver.
    /// </summary>
    /// <remarks>
    /// Parsed as the controller's own sentence and then re-aimed, because "the chosen player"
    /// is not a phrase any printed rules text contains - it lives in reminder text and in
    /// CR 702.174, so teaching the shared grammar the words would teach it something no card
    /// says. Only the two effect shapes the six defined kinds produce are re-aimed; anything
    /// else refuses, so a future kind cannot quietly deliver to the caster instead.
    /// </remarks>
    private static ImmutableList<IEffect>? GiftDelivery(string what)
    {
        if (!GiftDeliveries.TryGetValue(what, out var sentence)
            || !EffectPhrase.TryParse(sentence, out var parsed)
            || !parsed.Targets.IsEmpty)
        {
            return null;
        }

        var delivery = ImmutableList.CreateBuilder<IEffect>();

        foreach (var effect in parsed.Effects)
        {
            switch (effect)
            {
                case DrawCards draws:
                    delivery.Add(draws with { Scope = PlayerScope.GiftRecipient });
                    break;
                case CreateToken token:
                    delivery.Add(token with { Scope = PlayerScope.GiftRecipient });
                    break;
                default:
                    return null;
            }
        }

        return delivery.ToImmutable();
    }

    /// <summary>
    /// Compiles an instant or sorcery with gift as its two readings (CR 702.174).
    /// </summary>
    /// <remarks>
    /// The promise is declared as the spell is cast (CR 702.174k), and everything it changes is
    /// a fact about which spell ends up on the stack: whether the delivery happens, whether the
    /// "if the gift was promised" sentences run, and whether their targets are even chosen
    /// (CR 702.174m). So the text is rewritten into an unpromised reading and a promised one,
    /// each compiled through every matcher this file has, with the delivery placed first in the
    /// promised reading because CR 702.174j puts the gift before anything else the spell does.
    /// A permanent with gift takes the other route - <see cref="TryGiftLine"/> builds its
    /// delivery as the enters trigger CR 702.174b spells out - because its own abilities have
    /// to read the promise later, off the permanent, rather than at cast.
    /// </remarks>
    private static CompiledCard? CompileGiftSpell(CardDefinition card)
    {
        if (card.Faces.Count > 1
            || string.IsNullOrEmpty(card.OracleText)
            || !(card.CardTypes.HasFlag(CardType.Instant) || card.CardTypes.HasFlag(CardType.Sorcery)))
        {
            return null;
        }

        var lines = card.OracleText.Split('\n');
        var giftLine = lines
            .Select(l => GiftLine().Match(Reminder().Replace(l, string.Empty).Trim()))
            .FirstOrDefault(m => m.Success);

        if (giftLine is not { Success: true })
            return null;

        // A kind CR 702.174 does not define, or a delivery the engine cannot make, leaves the
        // whole card to the ordinary loop: the gift line lands in Unhandled there, which is the
        // honest report.
        if (GiftDelivery(giftLine.Groups["what"].Value.Trim()) is not { } delivery)
            return null;

        var rest = Reminder().Replace(
            string.Join(
                '\n',
                lines.Where(l => !GiftLine().IsMatch(Reminder().Replace(l, string.Empty).Trim()))),
            string.Empty);

        if (GiftReadings(rest) is not { } readings)
            return null;

        var unpromised = Compile(WithText(card, readings.Unpromised, "unpromised"));
        var promised = Compile(WithText(card, readings.Promised, "promised"));

        var unhandled = unpromised.Unhandled.AddRange(
            promised.Unhandled.Where(l => !unpromised.Unhandled.Contains(l, StringComparer.Ordinal)));

        // The same guard the cleave swap makes, for the same reason: only a spell rides the
        // swap, so a promised reading that compiled into anything else would lose it silently.
        if (unhandled.IsEmpty
            && (!promised.Activated.IsEmpty
                || !promised.Triggers.IsEmpty
                || !promised.Statics.IsEmpty
                || !promised.Replacements.IsEmpty))
        {
            unhandled = unhandled.Add(
                "(gift - the promised reading is not a single spell: " + rest + ")");
        }

        // CR 702.174j: the delivery is the first thing the promised spell does. Prepending an
        // effect moves no target index - effects hold indices into the target list, and the
        // delivery has no targets to add.
        var promisedSpell = (promised.Spell ?? new SpellDefinition()) with
        {
            Effects = [.. delivery, .. promised.Spell?.Effects ?? []],
        };

        return unpromised with
        {
            Name = card.Name,
            HasGift = true,
            GiftSpell = unhandled.IsEmpty ? promisedSpell : null,
            Unhandled = unhandled,
        };
    }

    /// <summary>
    /// A gift card's text as its two readings: what resolves unpromised, and what resolves
    /// promised (CR 702.174b, 702.174m).
    /// </summary>
    /// <remarks>
    /// Sentence surgery, done by shape rather than by grammar: "If the gift was promised,
    /// instead X" replaces the sentence before it, the bare forms keep or drop their clause,
    /// and "Then if the gift was promised and C, X" keeps its real condition in the promised
    /// reading. Null when any mention of the gift survives the rewrite - a shape this does not
    /// know goes unread rather than half-read.
    /// </remarks>
    private static (string Unpromised, string Promised)? GiftReadings(string rest)
    {
        var unpromised = rest;
        var promised = rest;

        // "A. If the gift was promised, instead B." / "A. If the gift was promised, B instead."
        // - the promise swaps one instruction for another, targets included.
        var instead = GiftInstead().Match(unpromised);
        if (instead.Success)
        {
            var swapped = instead.Groups["b1"].Success
                ? instead.Groups["b1"].Value
                : instead.Groups["b2"].Value;

            unpromised = unpromised.Replace(
                instead.Value, instead.Groups["base"].Value + ".", StringComparison.Ordinal);
            promised = promised.Replace(
                instead.Value, Capitalise(swapped) + ".", StringComparison.Ordinal);
        }

        // "Then if the gift was promised and C, X." - the promise is settled at cast, the rest
        // of the condition is not, so the promised reading keeps the rest.
        unpromised = GiftCompoundIf().Replace(unpromised, string.Empty);
        promised = GiftCompoundIf().Replace(promised, m => "Then if " + m.Groups["kept"].Value);

        // "If the gift was promised, X." - an extra instruction the promise buys.
        unpromised = GiftIf().Replace(unpromised, string.Empty);
        promised = GiftIf().Replace(promised, m => Capitalise(m.Groups["then"].Value) + ".");

        // "If the gift wasn't promised, X." - the cost of declining, read the other way round.
        unpromised = GiftIfNot().Replace(unpromised, m => Capitalise(m.Groups["then"].Value) + ".");
        promised = GiftIfNot().Replace(promised, string.Empty);

        // "X if the gift was promised." - the same clause printed trailing.
        unpromised = GiftTrailingIf().Replace(unpromised, string.Empty);
        promised = GiftTrailingIf().Replace(promised, m => m.Groups["effect"].Value + ".");

        if (unpromised.Contains("gift", StringComparison.OrdinalIgnoreCase)
            || promised.Contains("gift", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (unpromised.Trim(), promised.Trim());
    }

    private static string Capitalise(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];

    /// <summary>
    /// "Gift a [something]" on a permanent: the promise delivered by the enters trigger
    /// CR 702.174b spells out.
    /// </summary>
    /// <remarks>
    /// "When this permanent enters, if its gift cost was paid, [effect]" - the predicate is the
    /// ordinary arrival, the intervening-if reads the promise off the arriving permanent (the
    /// chosen opponent rode the resolution move, as kicker's flag does), and the effect is the
    /// delivery aimed at that opponent. Wrapped in <see cref="OnlyIf"/> exactly as a printed
    /// intervening-if would be, so CR 603.4's second check happens here too.
    /// </remarks>
    private static bool TryGiftLine(
        string line,
        ImmutableList<TriggeredAbilityDefinition>.Builder into,
        ref bool hasGift)
    {
        var m = GiftLine().Match(line);
        if (!m.Success)
            return false;

        // An instant or sorcery never reaches here - CompileGiftSpell intercepts it - so this
        // is the permanent route or a kind nothing can deliver.
        if (GiftDelivery(m.Groups["what"].Value.Trim()) is not { } delivery)
            return false;

        var arrival = TriggerConditions.Parse("~ enters");
        var promised = BoardConditions.Parse("the gift was promised");

        if (arrival is null || promised is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "gift",
            Text = line,
            Triggers = (e, state, source) =>
                arrival(e, state, source) && promised(state, source.Abilities, source),
            Effects = [new OnlyIf(promised, delivery)],
            FunctionsFrom = Zone.Battlefield,
        });

        hasGift = true;
        return true;
    }

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

    /// <summary>One band of a leveler's text: the counts it covers and what it confers.</summary>
    /// <remarks>
    /// CR 711.3 makes each band a set of characteristics the permanent has while its level
    /// counters fall inside the range, so the range and what it grants belong together. Mutable
    /// because a band is filled in over three or four lines - the symbol, the size, then whatever
    /// the band gives - and the lines arrive one at a time.
    /// </remarks>
    private sealed class LevelBand
    {
        public required int From { get; init; }

        /// <summary>The last count in the band, or null for the open-ended "N+" one.</summary>
        public required int? To { get; init; }

        public int? Power { get; set; }

        public int? Toughness { get; set; }

        public KeywordAbility Keywords { get; set; }

        public List<string> Lines { get; } = [];

        /// <summary>Whether a permanent with this many level counters is in this band.</summary>
        public bool Covers(int levels) => levels >= From && (To is null || levels <= To);
    }

    /// <summary>How many level counters a permanent has (CR 711.2a).</summary>
    private static int LevelsOn(GameObject? permanent) =>
        permanent?.Permanent?.Counters.GetValueOrDefault(CounterKinds.Level) ?? 0;

    /// <summary>
    /// Whether this card is a leveler: it can be levelled up, and it prints level symbols
    /// (CR 711.1).
    /// </summary>
    /// <remarks>
    /// Both halves are required, and that is what makes the guard safe to run over the whole
    /// corpus. "Level up" without bands would be a permanent collecting counters that do
    /// nothing; a band without the ability could never be reached. Exactly 25 playable cards
    /// carry both, which is every leveler ever printed.
    /// </remarks>
    private static bool IsLeveler(CardDefinition card) =>
        card.Faces.Count <= 1
        && !card.OracleId.Contains('$', StringComparison.Ordinal)
        && (card.OracleText ?? string.Empty).Contains(
            "Level up", StringComparison.OrdinalIgnoreCase)
        && Lines(card).Any(line => LevelUpLine().IsMatch(line))
        && Lines(card).Any(line => LevelBandLine().IsMatch(line));

    /// <summary>
    /// Compiles a leveler: the level-up ability, and one section per level band (CR 711).
    /// </summary>
    /// <remarks>
    /// Each band is compiled as a card of its own and the abilities that come back are wrapped in
    /// a level test before they are merged, exactly as <see cref="CompileClass"/> does, so every
    /// matcher in this file is reused unchanged. The synthetic oracle id carries a "$" so that
    /// this does not recurse.
    /// <para>
    /// The one thing a Class does not need is the keywords, and without it this mechanic would
    /// have made cards <em>better</em> than printed. A card database lists a card's keywords from
    /// its whole rules text, so Student of Warfare arrives carrying first strike <em>and</em>
    /// double strike as printed flags - and a 1/1 with both before it has been levelled once is
    /// not the card anybody printed. The keywords named inside bands are therefore taken off in
    /// layer 6 and given back one band at a time, which is what CR 711.3 says the permanent has.
    /// </para>
    /// <para>
    /// Null when the card is not a leveler, and null again when it is one whose shape this cannot
    /// read - a band with no printed size, a section carrying something a level gate cannot wrap.
    /// The ordinary line loop then reports the level lines as unread, which is the honest answer:
    /// a leveler compiled with one band missing plays as a card nobody printed either.
    /// </para>
    /// </remarks>
    private static CompiledCard? CompileLeveler(CardDefinition card)
    {
        if (!IsLeveler(card))
            return null;

        var always = new List<string>();
        var bands = new List<LevelBand>();
        var levelUpCost = string.Empty;

        foreach (var line in Lines(card))
        {
            if (LevelUpLine().Match(line) is { Success: true } up)
            {
                levelUpCost = up.Groups["cost"].Value;
                continue;
            }

            if (LevelBandLine().Match(line) is { Success: true } symbol)
            {
                bands.Add(new LevelBand
                {
                    From = int.Parse(symbol.Groups["from"].Value, CultureInfo.InvariantCulture),
                    To = symbol.Groups["to"].Success
                        ? int.Parse(symbol.Groups["to"].Value, CultureInfo.InvariantCulture)
                        : null,
                });

                continue;
            }

            if (bands.Count == 0)
            {
                always.Add(line);
                continue;
            }

            var band = bands[^1];

            // The size is printed on the line straight after the symbol and nowhere else, so the
            // first bare "N/N" in a band is the band's own and anything later is a sentence.
            if (band.Power is null && PrintedSize().Match(line) is { Success: true } size)
            {
                band.Power = int.Parse(size.Groups["p"].Value, CultureInfo.InvariantCulture);
                band.Toughness = int.Parse(size.Groups["t"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (KeywordsOn(line, card) is { } printed)
            {
                band.Keywords |= printed;
                continue;
            }

            band.Lines.Add(line);
        }

        // CR 711.3 gives every band of a leveler creature a printed size. A band without one is a
        // shape this has misread, and merging the rest would leave the creature at its printed
        // size in a band the card says it grows in.
        if (card.CardTypes.HasFlag(CardType.Creature) && bands.Any(band => band.Power is null))
            return null;

        var inBands = KeywordAbility.None;
        foreach (var band in bands)
            inBands |= band.Keywords;

        var outsideBands = KeywordAbility.None;
        foreach (var line in always)
            outsideBands |= KeywordsOn(line, card) ?? KeywordAbility.None;

        // What only a band gives, the printed card must not have. A keyword named both inside a
        // band and outside one is kept: the card has it at every level and the band is repeating
        // it, so taking it off would lose it below the band.
        var stripped = inBands & ~outsideBands;

        var activated = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();
        var triggers = ImmutableList.CreateBuilder<TriggeredAbilityDefinition>();
        var statics = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();
        var replacements = ImmutableList.CreateBuilder<ReplacementEffectDefinition>();
        var unhandled = ImmutableList.CreateBuilder<string>();

        CompiledCard Section(IEnumerable<string> lines, string tag) => Compile(new CardDefinition
        {
            OracleId = card.OracleId + "$" + tag,
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
            Power = card.Power,
            Toughness = card.Toughness,
        });

        var granted = KeywordAbility.None;

        if (always.Count > 0)
        {
            var plain = Section(always, "always");
            if (!OnlyPermanentAbilities(plain))
                return null;

            unhandled.AddRange(plain.Unhandled);
            activated.AddRange(plain.Activated);
            triggers.AddRange(plain.Triggers);
            statics.AddRange(plain.Statics);
            replacements.AddRange(plain.Replacements);
            granted |= plain.GrantedKeywords;
        }

        foreach (var band in bands)
        {
            if (band.Lines.Count == 0)
                continue;

            var floor = band.From.ToString(CultureInfo.InvariantCulture);
            var section = Section(band.Lines, floor);

            // A band contributes abilities to a permanent and nothing else. A section that came
            // back carrying a spell, a cost modifier or one of the flat permissions has nowhere
            // to go: merged, it would function at every level; dropped, it would be lost
            // silently. Neither is worth having, so the card goes back unread instead.
            if (!OnlyPermanentAbilities(section))
                return null;

            unhandled.AddRange(section.Unhandled);

            // A sentence that means a keyword - "~ can't be blocked" - comes back as a granted
            // keyword rather than as an ability, and a granted keyword has no gate of its own.
            // It joins the band's own keywords, which the layer 6 effect below switches on and
            // off with the level. Without this the sentence would be read and then thrown away.
            band.Keywords |= section.GrantedKeywords;

            var reached = band;

            activated.AddRange(section.Activated.Select(a => a with
            {
                Id = "l" + floor + a.Id,
                ActivateOnlyIf = (state, abilities, self) =>
                    reached.Covers(LevelsOn(self))
                    && a.ActivateOnlyIf?.Invoke(state, abilities, self) != false,
            }));

            triggers.AddRange(section.Triggers.Select(t => t with
            {
                Id = "l" + floor + t.Id,
                Triggers = (e, state, source) =>
                    reached.Covers(LevelsOn(SelfOf(state, source.Id) ?? source))
                    && t.Triggers(e, state, source),
            }));

            statics.AddRange(section.Statics.Select(c => c with
            {
                Id = "l" + floor + c.Id,
                Applies = (state, source, built) =>
                    reached.Covers(LevelsOn(source)) && c.Applies(state, source, built),
            }));

            replacements.AddRange(section.Replacements.Select(r => r with
            {
                Id = "l" + floor + r.Id,
                Applies = (e, state, source) =>
                    reached.Covers(LevelsOn(source)) && r.Applies(e, state, source),
            }));
        }

        // CR 711.2a. The reminder text spells the whole ability out and is stripped before this
        // reads anything, so both halves of it are written here: the counter, and the timing.
        activated.Add(new ActivatedAbilityDefinition
        {
            Id = "levelup",
            Text = "Level up " + levelUpCost,
            ManaCost = ManaCostSpec.Parse(levelUpCost),
            Timing = ActivationTiming.SorceryOnly,
            Effects = [new PutCountersOnSource(CounterKinds.Level, 1)],
        });

        var sized = bands.FindAll(band => band.Power is not null);

        if (sized.Count > 0)
        {
            // CR 711.4 sets a level symbol's size in layer 7b, so a +1/+1 counter on a levelled
            // creature still counts on top of the band's numbers rather than under them.
            statics.Add(new ContinuousEffectDefinition
            {
                Id = "level:size",
                Layer = EffectLayer.PowerToughnessSet,
                Applies = (_, source, target) =>
                    source is not null
                    && target.Subject.Id == source.Id
                    && sized.Exists(band => band.Covers(LevelsOn(source))),
                Apply = (_, source, target) =>
                {
                    if (sized.Find(band => band.Covers(LevelsOn(source))) is { } band)
                        target.Set(band.Power!.Value, band.Toughness!.Value);
                },
            });
        }

        if (stripped != KeywordAbility.None
            || bands.Exists(band => band.Keywords != KeywordAbility.None))
        {
            // One effect rather than one per band, because its two halves have to happen in this
            // order and layer 6 offers no order between two effects of the same permanent: what
            // the bands give is taken off first, and only the band the permanent has reached puts
            // any of it back.
            statics.Add(new ContinuousEffectDefinition
            {
                Id = "level:abilities",
                Layer = EffectLayer.Ability,
                Applies = (_, source, target) =>
                    source is not null && target.Subject.Id == source.Id,
                Apply = (_, source, target) =>
                {
                    target.Keywords &= ~stripped;

                    foreach (var band in bands)
                    {
                        if (band.Covers(LevelsOn(source)))
                            target.Keywords |= band.Keywords;
                    }
                },
            });
        }

        return new CompiledCard
        {
            Name = card.Name,
            Activated = activated.ToImmutable(),
            Triggers = triggers.ToImmutable(),
            Statics = statics.ToImmutable(),
            Replacements = replacements.ToImmutable(),
            GrantedKeywords = granted,
            Unhandled = unhandled.ToImmutable(),
        };
    }

    /// <summary>
    /// Whether a compiled section holds only things a level gate can wrap.
    /// </summary>
    /// <remarks>
    /// The four ability lists and the granted keywords are gated on the way back; everything else
    /// a <see cref="CompiledCard"/> can carry is a fact about the card as a whole, with no level
    /// to attach it to. Asked so that a section carrying one of them takes the card back to
    /// unread rather than having it silently dropped - a card that compiles and plays as
    /// something else is the failure this whole design exists to avoid.
    /// </remarks>
    private static bool OnlyPermanentAbilities(CompiledCard section) =>
        section.Spell is null
        && section.Adventure is null
        && section.PreparedSpell is null
        && section.Halves.Count == 0
        && section.PartnerRule is null
        && section.CostModifiers.IsEmpty
        && section.DevourCount == 0
        && !section.ShowsTopOfLibrary
        && !section.RemovesHandLimit
        && section.ChoosesOnEntry == ChoiceOnEntry.None
        && section.ExtraLandDrops == 0
        && !section.MayDeclineUntap
        && !section.SkipsDrawStep
        && !section.RevealsTopOfLibrary
        && section.AttacksOnlyIfDefenderControls is null;

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

    /// <summary>
    /// Every spelling of its own name a card uses to refer to itself, longest first.
    /// </summary>
    /// <remarks>
    /// The printed name and the part before the comma, which is what a legendary card calls
    /// itself (CR 201.2b) — and both of those again with a leading <c>A-</c> taken off.
    /// <para>
    /// That prefix is how Alchemy marks a rebalanced card, and the rebalanced card's rules text
    /// names the <em>paper</em> card: "A-Armory Veteran" reads "As long as Armory Veteran is
    /// equipped". 216 playable cards carry the prefix and 116 of them name themselves without it,
    /// so on every one of those the name was never turned into <c>~</c> and every self-referring
    /// line went unread — an enters trigger, a static, whatever the card happened to say.
    /// </para>
    /// <para>
    /// The prefixed spelling is kept as well rather than replaced by the bare one: five of these
    /// cards do print their own full name, and dropping it would trade one miss for another. It is
    /// stripped only from the name used for <em>self-reference</em> — never from the oracle id,
    /// which is what keeps a rebalanced card and its paper original apart in the compiled pool.
    /// </para>
    /// <para>
    /// Longest first, and that is load-bearing rather than tidy: replacing "Armory Veteran" before
    /// "A-Armory Veteran" leaves the line reading "A-~", which no template matches — the same miss
    /// in a different disguise.
    /// </para>
    /// </remarks>
    private static ImmutableList<string> SelfNames(string printed)
    {
        var names = new List<string>();

        void Add(string name)
        {
            if (name.Length > 2 && !names.Contains(name, StringComparer.Ordinal))
                names.Add(name);

            var shortened = name.Split(',')[0].Split(" //", StringSplitOptions.None)[0].Trim();
            if (shortened.Length > 2 && !names.Contains(shortened, StringComparer.Ordinal))
                names.Add(shortened);
        }

        Add(printed);

        if (printed.StartsWith("A-", StringComparison.Ordinal))
            Add(printed[2..]);

        return [.. names.OrderByDescending(name => name.Length)];
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

        var selfNames = SelfNames(card.Name);

        // The line a results row is being folded into, when the line above called for a roll.
        string? held = null;

        foreach (var line in text.Split('\n'))
        {
            var cleaned = Reminder().Replace(line, string.Empty);

            foreach (var self in selfNames)
                cleaned = cleaned.Replace(self, "~", StringComparison.Ordinal);

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

            if (cleaned.Length == 0)
                continue;

            // CR 706.3b: a roll instruction, its modifiers and its results table are one
            // ability, printed across several lines. A row alone is half a sentence — "1—9 |
            // Scry 1." says nothing without the roll above it — so rows are folded into the
            // line that called for the roll and the compiler reads the ability whole. The guard
            // is one-way and deliberate: a Spacecraft's station bar shares the row shape exactly
            // ("10+ | Flying") and folds into nothing, because the line above it rolls no dice.
            if (held is not null && EffectPhrase.IsResultsRow(cleaned) && EffectPhrase.CallsForDice(held))
            {
                held = held + " " + cleaned;
                continue;
            }

            if (held is not null)
                yield return held;

            held = cleaned;
        }

        if (held is not null)
            yield return held;
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
    private static bool IsKeywordLine(string line, CardDefinition card) =>
        KeywordsOn(line, card) is not null;

    /// <summary>
    /// The keywords a line is made of, or null when it is not made only of keywords.
    /// </summary>
    /// <remarks>
    /// <see cref="IsKeywordLine"/> asks this same question and only wants a yes or a no, because
    /// in the ordinary case there is nothing to do with the answer: the keyword is already a flag
    /// on the card. A level band needs the flags themselves — it has to take them off the printed
    /// card and hand them back one band at a time — so the reading lives here and the older
    /// question is asked of it, rather than the two drifting apart.
    /// </remarks>
    private static KeywordAbility? KeywordsOn(string line, CardDefinition card)
    {
        var body = line.TrimEnd('.', ' ');
        if (body.Length == 0)
            return null;

        var found = KeywordAbility.None;

        foreach (var part in body.Split(','))
        {
            var word = part.Trim();
            if (word.Length == 0)
                continue;

            // A keyword the engine models, and one this card actually has — "Flying" on a card
            // without flying is granting it to something else, which is a different sentence.
            if (!KeywordNames.TryGetValue(word, out var flag) || !card.Keywords.HasFlag(flag))
                return null;

            found |= flag;
        }

        return found == KeywordAbility.None ? null : found;
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

        into.Add(WardTrigger(
            "ward",
            $"Whenever {card.Name} becomes the target of a spell or ability an opponent "
                + "controls, counter it unless that player "
                + (wardKind is not null
                    ? $"{m.Groups["verb"].Value.ToLowerInvariant()}s a {wardFilter}."
                    : wardLife > 0
                        ? $"pays {wardLife} life."
                        : $"pays {m.Groups["cost"].Value}."),
            cost,
            wardLife,
            wardKind,
            wardFilter));

        return true;
    }

    /// <summary>
    /// The ward triggered ability itself (CR 702.21a), shared by the printed keyword and the
    /// statics that grant it.
    /// </summary>
    /// <remarks>
    /// One construction rather than a copy per site, because the awkward parts travel with it:
    /// ward does not target — hexproof and shroud must not turn it off — so its "counter it" is
    /// the subject-spell counter and not the ordinary counter-target, and the tax is asked of
    /// the <em>subject</em> player, the one who aimed the spell, not of the ward's controller.
    /// A second copy is where one of those halves would go missing.
    /// </remarks>
    private static TriggeredAbilityDefinition WardTrigger(
        string id,
        string text,
        ManaCostSpec cost,
        int life,
        ChosenCostKind? kind,
        string filter)
    {
        // CR 702.21a: only an opponent's spell taxes. Your own targeting is free, which is what
        // makes ward a defensive ability rather than a drawback. For a granted ward the source
        // is the permanent the trigger was granted to, so a Star Whale's ward taxes spells
        // aimed at the creature standing under it rather than at the whale.
        static bool Triggers(GameEvent e, GameState state, TriggerSource source) =>
            e is TargetsChosen aimed
            && aimed.Targets.Any(t => t.Subject == source.Id)
            && state.TryGetObject(aimed.StackId, out var aiming)
            && aiming.ControllerId != source.ControllerId;

        return new TriggeredAbilityDefinition
        {
            Id = id,
            Text = text,
            Triggers = Triggers,
            Effects =
            [
                new MayPay(
                    cost,
                    IfYouDo: [],
                    IfYouDont: [new CounterSubjectSpell()],
                    EffectIndex: 0,
                    AskSubjectPlayer: true,
                    LifeCost: life,
                    ChosenKind: kind,
                    ChosenFilterId: filter),
            ],
        };
    }

    /// <summary>
    /// Reads a granted keyword list that may end in a ward cost — "flying and ward {2}".
    /// </summary>
    /// <remarks>
    /// Ward takes a cost, which a <see cref="KeywordAbility"/> flag cannot carry, so the grant
    /// slots that read a keyword list through <see cref="EffectPhrase.Keywords"/> could never
    /// say it — "Enchanted creature has ward {2}" and "Other creatures you control have ward
    /// {2}" were unread whole. The list is cut at its printed joins and each part is read as a
    /// flag or as a ward cost; a part that is neither leaves the whole line unread, which is
    /// the rule every keyword list here already keeps (half a list is a card that does half of
    /// what it prints).
    /// <para>
    /// Only the mana-cost shape of ward is granted. "Ward — pay 3 life" and the chosen-cost
    /// forms exist on printed cards and on no grant in the corpus, so admitting them here would
    /// be surface nothing reaches.
    /// </para>
    /// </remarks>
    private static bool TryKeywordsAndWard(
        string printed, out KeywordAbility keywords, out string? wardCost)
    {
        keywords = KeywordAbility.None;
        wardCost = null;

        // The plain list first, exactly as every caller read it before — so nothing the flag
        // vocabulary already understands changes hands, multi-word entries included.
        if (EffectPhrase.Keywords(printed) is { } plain)
        {
            keywords = plain;
            return true;
        }

        // Then the same list with a ward cost on the end, which is where the cards put it:
        // "ward {2}" alone, "deathtouch and ward {1}", "flying, ward {2}". Whatever stands in
        // front of the ward still has to read as a whole list, or the line stays unread.
        var ward = GrantedWardPart().Match(printed);
        if (!ward.Success)
            return false;

        wardCost = ward.Groups["cost"].Value;

        var rest = ward.Groups["rest"].Value.Trim().TrimEnd(',');
        if (rest.Length == 0)
            return true;

        if (EffectPhrase.Keywords(rest) is not { } flags)
        {
            wardCost = null;
            return false;
        }

        keywords = flags;
        return true;
    }

    [GeneratedRegex(
        @"^(?:(?<rest>.+?)(?:,? and |,\s*))?ward (?<cost>(\{[^}]+\})+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GrantedWardPart();

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
    /// "Soulbond" - the two pairing triggers (CR 702.95a).
    /// </summary>
    /// <remarks>
    /// Exploit's shape twice over: each trigger's resolution is one question, answered by naming
    /// a creature or declining. The intervening if is carried on the trigger predicate
    /// (CR 603.4's first check) and the second check lives where the question is asked, which is
    /// where CR 702.95c re-tests everything anyway. "Flying, soulbond" closes a keyword list, so
    /// the leading words are accepted on exactly the terms <see cref="TryKeywordList"/> accepts
    /// them: a keyword the engine models and the card actually has.
    /// </remarks>
    private static bool TrySoulbond(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = SoulbondLine().Match(line);
        if (!m.Success)
            return false;

        if (m.Groups["lead"].Success)
        {
            foreach (var part in m.Groups["lead"].Value.Split(','))
            {
                var word = part.Trim();
                if (word.Length == 0)
                    continue;

                if (!KeywordNames.TryGetValue(word, out var flag) || !card.Keywords.HasFlag(flag))
                    return false;
            }
        }

        var selfEnters = TriggerConditions.Parse("~ enters");
        var otherEnters = TriggerConditions.Parse("another creature you control enters");
        if (selfEnters is null || otherEnters is null)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "soulbond-this",
            Text = "When this creature enters, if you control both this creature and another "
                + "creature and both are unpaired, you may pair this creature with another "
                + "unpaired creature you control.",

            // A new object is considered against the state just after its own arrival, so the
            // source is on the battlefield here and the scan below excludes it by id.
            Triggers = (e, state, source) =>
                selfEnters(e, state, source)
                && source.Permanent?.PairedWithId is null
                && HasUnpairedPartnerFor(state, source),
            Effects = [new OfferSoulbondPair(WithEnteringCreature: false)],
        });

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "soulbond-other",
            Text = "Whenever another creature you control enters, if you control both that "
                + "creature and this one and both are unpaired, you may pair that creature "
                + "with this creature.",

            // The newcomer is a fresh object and cannot be paired; only this half needs asking.
            Triggers = (e, state, source) =>
                otherEnters(e, state, source) && source.Permanent?.PairedWithId is null,
            Effects = [new OfferSoulbondPair(WithEnteringCreature: true)],
        });

        return true;
    }

    /// <summary>
    /// Whether the source's controller has another unpaired creature to offer (CR 702.95a).
    /// </summary>
    /// <remarks>
    /// Computed characteristics on both sides, because the intervening if is a question about
    /// creatures: an animated land is a legal partner while it is animated, and a stolen
    /// creature is not the controller's to pair.
    /// </remarks>
    private static bool HasUnpairedPartnerFor(GameState state, TriggerSource source)
    {
        var mine = source.Now(state).ControllerId;

        foreach (var id in state.Battlefield)
        {
            if (id == source.Id)
                continue;

            var other = state.GetObject(id);
            if (other.Permanent is not { PairedWithId: null })
                continue;

            if (Characteristics.Of(state, source.Abilities, other) is { IsCreature: true } c
                && c.ControllerId == mine)
            {
                return true;
            }
        }

        return false;
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
    /// <summary>
    /// "Ripple N" &#8212; show the top N and cast the copies for nothing (CR 702.60a).
    /// </summary>
    /// <remarks>
    /// Storm's shape - a trigger that fires from the stack as the spell is cast - with cascade's
    /// resolution: exile what matched, offer it for free, and put the rest on the bottom of the
    /// library in a random order. Which name to match is read from the spell underneath at
    /// resolution, so nothing about the card travels in the definition.
    /// </remarks>
    private static bool TryRipple(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = RippleLine().Match(line);
        if (!m.Success)
            return false;

        if (TriggerConditions.Parse("you cast ~") is not { } cast)
            return false;

        var count = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (count <= 0)
            return false;

        into.Add(new TriggeredAbilityDefinition
        {
            Id = "ripple",
            Text = $"When you cast {card.Name}, you may reveal the top "
                + count.ToString(CultureInfo.InvariantCulture)
                + " cards of your library. If you do, you may cast any of those cards with the "
                + "same name as this spell without paying their mana costs, then put the rest on "
                + "the bottom of your library.",
            FunctionsFrom = Zone.Stack,
            Triggers = cast,
            Effects = [new Ripple(count)],
        });

        return true;
    }

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
    /// What a permanent does to somebody's spell or ability costs (CR 601.2f, 602.2b).
    /// </summary>
    /// <remarks>
    /// Not a continuous effect: what a spell costs is worked out once as it is cast and never
    /// recomputed, so this is read at cast time from whatever is on the battlefield then. The
    /// filter is the vocabulary searching already uses, which is what lets "instant and sorcery"
    /// be two filters rather than a phrase needing its own reader.
    /// <para>
    /// The grid is (whose) × (more or less) × (spells or abilities), and this reads five of its
    /// six cells. The sixth — <em>your opponents' spells cost less</em> — has no printing in the
    /// corpus, so there is nothing to read and no pattern for it. Two families that look like
    /// they belong here are refused instead, because the only available reading of each makes
    /// the card better than printed:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// A modifier naming a <em>keyword</em> ability — "Equip abilities you activate cost {1}
    /// less to activate", and the same for ninjutsu, cycling, boast, exhaust and loyalty (12
    /// cards). <see cref="CostModifier.FilterId"/> asks about the card the ability sits on, so
    /// the nearest thing it can say is "abilities you activate", which discounts every ability
    /// its controller has.
    /// </item>
    /// <item>
    /// "This ability costs {1} less to activate", which <see cref="CostModifier.SourceOnly"/>
    /// exists for. Every printing of it in the corpus carries a counted or conditional tail —
    /// "for each Shrine you control", "if you control a legendary creature" — and the bare
    /// sentence the flag models is printed on no card at all, so it stays unread rather than
    /// being read off a line that says something else.
    /// </item>
    /// </list>
    /// </remarks>
    private static bool TryCostModifier(string line, ImmutableList<CostModifier>.Builder into)
    {
        if (SpellCostModifierLine().Match(line) is { Success: true } spell)
            return ReadSpellCostModifier(spell, into);

        // The same six cells with one condition on top, and a reader of its own so the
        // unconditional pattern above keeps refusing every other "that target …" phrase. Only
        // "that target ~" is admitted: the permanent printing the line is the one thing a cost
        // modifier can ask about without a filter vocabulary over targets.
        if (TargetingSpellCostModifierLine().Match(line) is { Success: true } aimedHere)
            return ReadSpellCostModifier(aimedHere, into, targetsSource: true);

        if (AbilityCostModifierLine().Match(line) is { Success: true } ability)
            return ReadAbilityCostModifier(ability, into);

        return false;
    }

    /// <summary>"Creature spells you cast cost {1} less to cast" and its five siblings.</summary>
    private static bool ReadSpellCostModifier(
        Match m, ImmutableList<CostModifier>.Builder into, bool targetsSource = false)
    {
        if (CostFilterFor(m.Groups["what"].Value) is not { } filter)
            return false;

        // Who pays. The bare form names nobody and so taxes everybody, its own controller
        // included — that is the whole difference between Sphere of Resistance and a card that
        // says "your opponents".
        var who = m.Groups["who"].Value.Trim().ToLowerInvariant() switch
        {
            "you cast" => PlayerScope.You,
            "your opponents cast" => PlayerScope.EachOpponent,
            _ => PlayerScope.EachPlayer,
        };

        // CR 400.1. The zone goes on the modifier, which is the half the consumer reads; a
        // reduction carrying a zone nothing checked would come off from every zone at once.
        Zone? from = m.Groups["zone"].Success
            ? m.Groups["zone"].Value.ToLowerInvariant() switch
            {
                "graveyard" => Zone.Graveyard,
                "exile" => Zone.Exile,
                _ => Zone.Hand,
            }
            : null;

        into.Add(new CostModifier
        {
            FilterId = filter,
            Amount = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture),
            Change = ChangeFor(m.Groups["dir"].Value),
            Kind = CostModifierKind.Spells,
            Who = who,
            FromZone = from,
            TargetsSource = targetsSource,
        });

        return true;
    }

    /// <summary>
    /// "Activated abilities of Foods you control cost {1} less to activate" (CR 602.2b).
    /// </summary>
    /// <remarks>
    /// The two scopes answer different questions and both are set deliberately.
    /// <see cref="CostModifier.SourceController"/> is whose permanent the ability is printed on,
    /// which is what "you control" says; <see cref="CostModifier.Who"/> is who is paying, and
    /// this wording says nothing about that, so it stays <see cref="PlayerScope.EachPlayer"/>.
    /// Narrowing it to <see cref="PlayerScope.You"/> would answer the wrong question and stop
    /// applying the moment somebody else activated an ability of a permanent you control.
    /// </remarks>
    private static bool ReadAbilityCostModifier(
        Match m, ImmutableList<CostModifier>.Builder into)
    {
        // "Foods", "lands", "white enchantments" — the template is always plural, and the filter
        // vocabulary names kinds in the singular. A plural left as printed would read "Foods" as
        // a subtype, which nothing has: the modifier would compile, the card would look finished
        // and no ability would ever be cheaper.
        var what = m.Groups["what"].Value.Trim();
        var filter = CostFilterFor(Singular(what)) ?? CostFilterFor(what);
        if (filter is null)
            return false;

        into.Add(new CostModifier
        {
            FilterId = filter,
            Amount = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture),
            Change = ChangeFor(m.Groups["dir"].Value),
            Kind = CostModifierKind.ActivatedAbilities,
            Who = PlayerScope.EachPlayer,
            SourceController = m.Groups["whose"].Success ? PlayerScope.You : null,
        });

        return true;
    }

    /// <summary>Which way a printed modifier moves the cost (CR 601.2f).</summary>
    private static CostChange ChangeFor(string printed) =>
        printed.Equals("more", StringComparison.OrdinalIgnoreCase)
            ? CostChange.Increase
            : CostChange.Reduction;

    /// <summary>
    /// What a cost modifier says it applies to, in the shared filter vocabulary.
    /// </summary>
    /// <remarks>
    /// An empty phrase is every card — "Spells cost {1} more to cast" names no kind at all. The
    /// "and" is turned into an "or" for the reason the filter grammar gives: nothing is an
    /// instant and a sorcery at once, so "instant and sorcery spells" means either.
    /// </remarks>
    private static string? CostFilterFor(string phrase)
    {
        var what = phrase.Trim();

        return what.Length == 0
            ? SearchFilters.AnyCard
            : EffectPhrase.SearchFilterFor(
                what.Replace(" and ", " or ", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The same phrase with its last word singular, for a template printed plural.</summary>
    private static string Singular(string phrase) =>
        phrase.EndsWith('s') && phrase.Length > 1 ? phrase[..^1] : phrase;

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

            // Through the shared reader, like every other entry replacement here. Matching the
            // move alone accepts a card that arrived from another zone and refuses a permanent
            // created on the battlefield (CR 111.1) - so this was correct in a real game and
            // silently did nothing whenever a board was set up with `Game.Create`, which is the
            // path the behaviour suite and the corpus soak both take. Nothing failed: the
            // permanent simply arrived with no counters and then never left.
            Applies = (e, _, source) => Arriving(e, source) is not null,
            Replace = (e, _, source) =>
            {
                var arrived = Arriving(e, source)!.Value;
                return [e, new Events.CountersChanged(arrived, CounterKinds.Time, many)];
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
    /// "Ravenous" &#8212; X counters, and a card when X was big (CR 702.156a).
    /// </summary>
    /// <remarks>
    /// One word standing for a replacement effect and a triggered ability, both of which read the
    /// same number: the X paid for the creature. So the keyword is refused outright on a card
    /// whose mana cost has no {X} in it. Ravenous on such a card would be a creature that enters
    /// with nothing and never draws, which is not a card worth reading, and more to the point it
    /// would be reading a word the printed card cannot mean.
    /// <para>
    /// The draw's intervening-if is checked twice (CR 603.4), and both checks read X off the
    /// object &#8212; which is why <c>GameReducer</c> carries <see cref="GameObject.VariableValue"/>
    /// across the one move that turns a spell into a permanent, exactly as it already carries
    /// "was kicked" for the same rule (CR 607.2). Without that the second check finds a zero on
    /// the permanent and no ravenous creature in the game ever draws.
    /// </para>
    /// </remarks>
    private static bool TryRavenous(
        string line,
        CardDefinition card,
        ImmutableList<TriggeredAbilityDefinition>.Builder triggers,
        ImmutableList<ReplacementEffectDefinition>.Builder replacements)
    {
        if (!RavenousLine().IsMatch(line))
            return false;

        // CR 702.156a: "found on some creature cards with {X} in their mana cost". Both halves
        // of the keyword are about that X, so without one there is nothing here to read.
        if (!card.ManaCostRaw.Contains("{X}", StringComparison.OrdinalIgnoreCase))
            return false;

        if (TriggerConditions.Parse("~ enters") is not { } entered)
            return false;

        replacements.Add(new ReplacementEffectDefinition
        {
            Id = "ravenous",
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null,
            Replace = (e, state, source) =>
            {
                var arrived = Arriving(e, source)!.Value;

                // X rides on the object that was cast, which only a move has - a ravenous
                // creature put onto the battlefield by something else was never cast for an X
                // and arrives with nothing, which is what the rules say.
                var many = e is ObjectMoved move && state.TryGetObject(move.OldId, out var cast)
                    ? cast.VariableValue
                    : 0;

                return many <= 0
                    ? [e]
                    : [e, new CountersChanged(arrived, CounterKinds.PlusOnePlusOne, many)];
            },
        });

        bool BigX(GameState _, IAbilitySource __, GameObject source) => source.VariableValue >= 5;

        triggers.Add(new TriggeredAbilityDefinition
        {
            Id = "ravenous",
            Text = $"When {card.Name} enters, if X is 5 or more, draw a card.",
            Triggers = (e, state, source) =>
                entered(e, state, source) && BigX(state, source.Abilities, source),
            Effects = [new OnlyIf(BigX, [new DrawCards(new Amount(1))])],
        });

        return true;
    }

    /// <summary>
    /// "Amplify N" &#8212; counters bought by showing the rest of the tribe (CR 702.38a).
    /// </summary>
    /// <remarks>
    /// "As this creature enters, reveal any number of cards from your hand that share a creature
    /// type with it. It enters with N +1/+1 counters on it for each card revealed this way."
    /// <para>
    /// The share-a-type test is the card's own creature types, which the line does not print -
    /// so a card with no creature types has no amplify the engine can honour and the line is left
    /// unread rather than compiled into a keyword that can never find anything to reveal.
    /// </para>
    /// <para>
    /// Asked where devour is asked, and for the same reason: the reveal is a decision, and a
    /// decision cannot be taken inside a replacement effect. See <c>Game.AskOwedAmplify</c>.
    /// </para>
    /// </remarks>
    /// <summary>
    /// "Hideaway N" &#8212; look at the top N, put one aside, bury the rest (CR 702.75a).
    /// </summary>
    /// <remarks>
    /// The whole keyword is one sentence the shared vocabulary already reads, so it costs an
    /// effect and no new machinery: look at the top N, one goes to exile, the rest to the bottom
    /// of the library in a random order.
    /// <para>
    /// <strong>One deviation, and it is the losing one.</strong> The rule exiles the card face
    /// down and gives its controller permission to look at it; this engine has no face-down
    /// exile, so the card is exiled face up and every player can see it. That is information the
    /// printed card keeps from the opponents, so the reading is worse for the permanent's
    /// controller rather than better - which is the direction a reading is allowed to be wrong
    /// in. Nothing reaches it in a game either way: the second line every hideaway card prints,
    /// the one saying when the exiled card may be played, is not read yet.
    /// </para>
    /// <para>
    /// One card prints the word twice on one line ("Hideaway 3, hideaway 3"), and each instance
    /// is its own triggered ability (CR 702.75a) - so the line yields two.
    /// </para>
    /// </remarks>
    private static bool TryHideaway(
        string line, CardDefinition card, ImmutableList<TriggeredAbilityDefinition>.Builder into)
    {
        var m = HideawayLine().Match(line);
        if (!m.Success)
            return false;

        if (TriggerConditions.Parse("~ enters") is not { } entered)
            return false;

        foreach (Capture each in m.Groups["n"].Captures)
        {
            var count = int.Parse(each.Value, CultureInfo.InvariantCulture);
            if (count <= 0)
                return false;

            into.Add(new TriggeredAbilityDefinition
            {
                Id = "hideaway" + Suffix(into.Count),
                Text = $"When {card.Name} enters, look at the top "
                    + count.ToString(CultureInfo.InvariantCulture)
                    + " cards of your library. Exile one of them and put the rest on the bottom "
                    + "of your library in a random order.",
                Triggers = entered,
                Effects = [new LookAndTake(count, Zone.Exile)],
            });
        }

        return true;
    }

    private static bool TryAmplify(string line, CardDefinition card, ref int amplify)
    {
        var m = AmplifyLine().Match(line);
        if (!m.Success)
            return false;

        // CR 702.38a: what may be revealed is a card "that shares a creature type with it", and
        // the types are the card's own. A card with none has nothing the keyword can ask for, so
        // the word is left unread rather than compiled into a dead offer.
        if (card.Subtypes.Count == 0)
            return false;

        var each = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (each <= 0)
            return false;

        // CR 702.38b: multiple instances work separately, which is one reveal each rather than
        // one reveal. They are summed into a single question because the answers cannot differ
        // in a way that matters: a card revealed for one instance may be revealed again for the
        // next, so the best play is to show the same hand to both, and the sum is what that pays.
        amplify += each;
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
        ImmutableList<string>.Builder unhandled,
        bool readAhead = false)
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
                    && before + counters.Delta >= at

                    // CR 702.155a: a Saga with read ahead skips the chapters it was started
                    // past. Its counters arrive in one lump, so without this the whole run from
                    // chapter one would fire - which is the reading that makes the card better
                    // than the one printed, and the one the coverage number cannot see.
                    && (!readAhead
                        || !state.EnteredThisTurn(saga)
                        || before + counters.Delta == at),
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
            && CardKindOfEither(zoned.Groups["what"].Value.Trim()) is { } kind)
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

        // The shared counted-group vocabulary, asked last. The two branches above are a second
        // implementation of the question <see cref="EffectPhrase.Counting"/> already answers for
        // every other "for each" in the compiler, and the two had drifted: "creatures on the
        // battlefield" is seventy corpus cards that the shared reader learned to trim and this
        // one still refused. Asked after them rather than instead of them so nothing that reads
        // today reads differently tomorrow, and so a group both can answer keeps the answer it
        // has been giving.
        //
        // Without a source: the object this replacement belongs to is the spell, and the
        // permanent the phrase would mean by "it" does not exist yet (CR 400.7). A phrase that
        // needs one is refused rather than answered about the wrong object.
        if (EffectPhrase.Counting(group, hasSource: false) is { } shared)
            return (state, you) => shared(state, EmptyAbilities.Instance, you, default);

        return null;
    }

    /// <summary>
    /// "Each other [group] you control enters with an additional +1/+1 counter on it"
    /// (CR 614.1d).
    /// </summary>
    /// <remarks>
    /// The other direction of the enters-with-counters family: the permanent that carries the
    /// line is not the one arriving. CR 614.1d makes a continuous ability reading "[objects]
    /// enter the battlefield …" a replacement effect, and the pipeline already asks every object
    /// on the board for its replacements — so nothing new was needed except a reader that
    /// describes somebody <em>else</em> arriving.
    /// <para>
    /// The arriving permanent is not in the state yet (CR 400.7), so the description is asked of
    /// what the event carries: the card that is entering and the player it is entering under.
    /// That is the printed card rather than computed characteristics, which is what the shared
    /// filter vocabulary reads and the same question a tutor answers.
    /// </para>
    /// <para>
    /// Only <c>+1/+1</c> and <c>loyalty</c> are read, and the refusal is the point. This engine
    /// works power and toughness out from the two counter names in <see cref="CounterKinds"/>
    /// and loyalty from a third; a "vigilance counter" would be recorded under its printed name
    /// and read by nothing, so Tayam would compile complete and hand out counters that grant no
    /// vigilance. One corpus card is left unread by that and it is the right one to lose.
    /// </para>
    /// <para>
    /// The source never counts as the permanent arriving, and that is a rule rather than an
    /// arrangement: a static ability functions only while its permanent is on the battlefield,
    /// so as an object enters, its own "[objects] enter…" ability is not yet doing anything. It
    /// has to be said out loud here because the replacement pipeline builds an arriving token
    /// from its own event and offers it its own replacements — that branch exists so a token
    /// copy of a creature that enters with counters gets them — and without the identity check a
    /// token copy of Renata would hand itself the counter it only ever gives to others. So the
    /// check does not depend on the printed word "other": the scope word is matched so that one
    /// the reader does not recognise leaves the line unread, and both scopes it does recognise
    /// exclude the source for the same reason.
    /// </para>
    /// </remarks>
    private static bool TryGroupEntersWithAdditionalCounter(
        string line, CardDefinition card, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = GroupEntersWithAdditionalCounterLine().Match(line);
        if (!m.Success)
            return false;

        // "Warrior creatures" is one noun phrase whose last word is plural; the filter
        // vocabulary reads the singular, the same way the mass-static group reader does.
        var words = m.Groups["what"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var phrase = string.Join(
            ' ', words[..^1].Append(EffectPhrase.SingularWord(words[^1])));

        if (EffectPhrase.SearchFilterFor(phrase) is not { } filter)
            return false;

        var kind = string.Equals(m.Groups["kind"].Value, "+1/+1", StringComparison.Ordinal)
            ? CounterKinds.PlusOnePlusOne
            : CounterKinds.Loyalty;

        bool Applies(GameEvent e, GameState state, GameObject source) =>
            Entering(e, state) is { } arriving
            && arriving.Id != source.Id
            && arriving.ControllerId == source.ControllerId
            && SearchFilters.Matches(filter, arriving.Card);

        into.Add(new ReplacementEffectDefinition
        {
            // The group's own words and the counter, because a card printing two of these lines
            // is told apart by nothing else and two abilities sharing an id is what the
            // invariant suite reads as one ability twice.
            Id = $"group-enters-additional:{card.Name}:{filter}:{kind}",
            Applies = Applies,
            Replace = (e, state, _) =>
                [e, new Events.CountersChanged(Entering(e, state)!.Value.Id, kind, 1)],
        });

        return true;
    }

    /// <summary>
    /// The permanent this event is putting onto the battlefield, whichever way it got there.
    /// </summary>
    /// <remarks>
    /// <see cref="Arriving"/>'s question asked of somebody else's arrival: it answers "is this
    /// my source entering", and this answers "what is entering". The card and the controller
    /// come off the event rather than out of the state, because the object being described does
    /// not exist yet — a moved card is still the old object until the move is applied, and a
    /// token was never anywhere at all (CR 111.1).
    /// </remarks>
    private static (ObjectId Id, CardDefinition Card, Guid ControllerId)? Entering(
        GameEvent e, GameState state) => e switch
        {
            ObjectMoved { To: Zone.Battlefield } moved
                when state.TryGetObject(moved.OldId, out var was)
                => (moved.NewId, was.Card, moved.ControllerId),
            ObjectCreated { Zone: Zone.Battlefield } made
                => (made.Id, made.Card, made.ControllerId),
            _ => null,
        };

    /// <summary>
    /// "~ enters with a +1/+1 counter on it for each [group]" (CR 614.1c), said three ways.
    /// </summary>
    /// <remarks>
    /// The fixed-number and conditional forms were read already; this is the third, where the
    /// number is a count taken as the permanent enters. A group the reader cannot count leaves
    /// the line unread rather than entering with none - a creature that is quietly smaller than
    /// the card says is as wrong as one that is quietly bigger, and neither announces itself.
    /// <para>
    /// Magic writes that count three ways and only one of them was accepted. "for each creature
    /// you control", "with X +1/+1 counters on it, where X is the number of creatures you
    /// control" and "with a number of +1/+1 counters on it equal to the number of creatures you
    /// control" are one instruction in three spellings, and the two unread ones sit on cards
    /// spread across eighteen different shapes - which is why the shape-ranked work queue never
    /// showed them as a family. They share the count vocabulary rather than a reader each, so
    /// whatever <see cref="CountOf"/> learns to answer arrives in all three at once.
    /// </para>
    /// </remarks>
    private static bool TryEntersWithCountersPerGroup(
        string line, CardDefinition card, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = EntersWithCountersPerGroupLine().Match(line);
        if (!m.Success)
            return false;

        // The two counted spellings name no multiplier - "X, where X is the number of" is one
        // counter per thing - and an absent group reads as one, which is what they mean.
        var each = NumberWordOrDigits(m.Groups["n"].Value);
        if (each <= 0)
            return false;

        if (CountOf(m.Groups["group"].Value.Trim()) is not { } counted)
            return false;

        var kind = CounterKindPrinted(m.Groups["kind"].Value);

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

    /// <summary>
    /// A counter as the card names it, kept under that name (CR 122.1).
    /// </summary>
    /// <remarks>
    /// Two alphabets, and only one of them has case. "+1/+0" is a name the layers read as an
    /// instruction (CR 122.1c) and has to survive exactly as printed; "Charge" and "charge" are
    /// the same counter and are folded so a permanent cannot end up holding both.
    /// </remarks>
    private static string CounterKindPrinted(string printed)
    {
        var name = printed.Trim();
        return CounterKinds.PowerToughnessOf(name) is not null ? name : name.ToLowerInvariant();
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

        // Modular and graft name no counter because theirs is always +1/+1; anything else says
        // which kind it arrives with, and it arrives with that one.
        //
        // This branch used to read "if the printed kind is *not* +1/+1 or -1/-1, keep it, else
        // +1/+1" - which quietly turned every printed -1/-1 into its opposite. Thirty-two corpus
        // cards say "~ enters with N -1/-1 counters on it" and sixteen of them compiled complete,
        // which is to say the pool served them: Carnifex Demon arrived an 8/8 where the card says
        // 4/4, Grim Poppet a 7/7 where it says 1/1, Shrewd Hatchling a 10/10 where it says 2/2.
        // Nothing could see it. Those cards read, compiled complete, played without an error, and
        // were simply better than the cardboard - the one failure this compiler is built to make
        // impossible, sitting inside the reader for the family it belongs to.
        var kind = m.Groups["kind"].Success
            ? CounterKindPrinted(m.Groups["kind"].Value)
            : CounterKinds.PlusOnePlusOne;

        var tapped = m.Value.Contains(" tapped ", StringComparison.OrdinalIgnoreCase);

        // "...on it if a creature died this turn" is the same replacement with a question in
        // front of it. A condition the board reader cannot parse leaves the whole line unread
        // rather than dropping the "if": a creature that always entered with the counters would
        // be strictly better than the one printed, and nothing would say so.
        //
        // Adamant prints that question at the *other* end - "If at least three white mana was
        // spent to cast ~, ~ enters with a +1/+1 counter on it" - and asks it of the same
        // vocabulary. One reader for both spellings, so neither can drift from the other.
        var asked = m.Groups["given"].Success ? m.Groups["given"] : m.Groups["when"];

        // A card with a condition at both ends does not exist, and honouring one of the two
        // would make it better than printed. Left unread rather than half read.
        if (m.Groups["given"].Success && m.Groups["when"].Success)
            return false;

        Func<GameState, IAbilitySource, GameObject, bool>? when = null;
        if (asked.Success)
        {
            when = BoardConditions.Parse(asked.Value.Trim());
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
    /// "Enchanted artifact is a creature with base power and toughness 5/5 in addition to its
    /// other types" — the animation grammar, entered through an attachment (CR 613.1d, 613.4b).
    /// </summary>
    /// <remarks>
    /// Two layers and so two effects, exactly as the station reader builds them: the type is
    /// added in layer 4 and the power and toughness are <em>set</em> in 7b, so a +1/+1 counter on
    /// the animated artifact counts on top rather than being overwritten.
    /// <para>
    /// "In addition to its other types" is required by the pattern rather than assumed. The same
    /// family printed without it replaces the types instead, and an Ensoul Artifact that quietly
    /// stopped being an artifact would dodge artifact removal — the reading that makes the card
    /// better than printed, which is the one this compiler refuses.
    /// </para>
    /// </remarks>
    private static bool TryAttachedAnimation(
        string line, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = AttachedAnimationLine().Match(line);
        if (!m.Success)
            return false;

        var power = int.Parse(m.Groups["p"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        var toughness = int.Parse(
            m.Groups["tough"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        // The effect belongs to the Aura and changes something else, keyed on what it is attached
        // to rather than on the source (CR 701.3c) - the same predicate the attached buffs use.
        static bool OnTheHost(GameState _, GameObject? source, CharacteristicsBuilder target) =>
            source?.Permanent?.AttachedTo is { } host && target.Subject.Id == host;

        into.Add(new ContinuousEffectDefinition
        {
            Id = "attached:animates",
            Layer = EffectLayer.Type,
            Applies = OnTheHost,
            Apply = (_, _, builder) => builder.CardTypes |= CardType.Creature,
        });

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"attached:animates:{power}/{toughness}",
            Layer = EffectLayer.PowerToughnessSet,
            Applies = OnTheHost,
            Apply = (_, _, builder) =>
            {
                builder.Power = power;
                builder.Toughness = toughness;
            },
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
    /// Every reader that turns one line into a continuous effect, in the order they are tried.
    /// </summary>
    /// <remarks>
    /// One method rather than a run of <c>if</c>s in the compile loop, because there are now two
    /// callers: the loop, and <see cref="TryAttachedConjunction"/>, which re-offers the clauses of
    /// a conjoined line to exactly the same readers. Two copies of this order would be a list the
    /// compiler has to remember, and a clause the loop can read but the fold cannot is invisible —
    /// it looks like a card that simply does not compile.
    /// </remarks>
    private static bool TryStaticLine(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder statics)
        => TrySmallCreaturesCantBlock(line, card, statics)
            || TryExtraBlocks(line, card, statics)
            || TryMustBeBlocked(line, card, statics)
            || TryMinimumBlockers(line, card, statics)
            || TryCantBeBlockedExceptBy(line, card, statics)
            || TryCantBeBlockedBy(line, card, statics)
            || TryDoesNotUntap(line, card, statics)
            || TryAttachedSilencing(line, card, statics)
            || TryAttachedBuff(line, statics)
            || TryAttachedAnimation(line, statics)
            || TryAttachedTypeAddition(line, statics)
            || TryGrantedAbility(line, card, statics)
            || TryDefinedPowerToughness(line, card, statics)
            || TryCountingStatic(line, card, statics)
            || TrySoulbondStatic(line, card, statics)
            || TryConditionalStatic(line, card, statics)
            || TryMassStatic(line, card, statics);

    /// <summary>
    /// "Enchanted creature gets +2/+2, has flying, and is a Bird in addition to its other types."
    /// </summary>
    /// <remarks>
    /// A conjunction of static clauses about one subject, folded by re-offering each clause to
    /// <see cref="TryStaticLine"/> with the subject put back in front of it. The vocabulary is
    /// therefore whatever the compiler already reads — every clause added in future joins this
    /// grammar without anyone coming back here.
    /// <para>
    /// **It runs last, after every other matcher has refused the whole line**, which is what makes
    /// it safe. A reader placed before an existing one can claim a clause its neighbour handled
    /// and disable it silently while total coverage still rises; a reader that only ever sees
    /// lines nothing else would take cannot steal anything by construction.
    /// </para>
    /// <para>
    /// It fails closed twice over. Every clause must read or the whole line is left unread, so a
    /// card is never given the half of its text the compiler happened to understand — an Aura that
    /// pumps and silently drops "and doesn't untap" is strictly better than printed. And the
    /// longest join is tried first, so a keyword list ("has flying, first strike, and haste") is
    /// offered whole before the commas inside it are ever treated as joins.
    /// </para>
    /// <para>
    /// The subject is an attached one — CR 301.5f and CR 303.4m, the thing this permanent is
    /// attached to. "~" is deliberately excluded: it was measured across the corpus and folds
    /// nothing, and a card's own text reaching a static fold is how a one-shot effect on a spell
    /// would become a permanent one.
    /// </para>
    /// </remarks>
    private static bool TryAttachedConjunction(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = ConjoinedAttachedLine().Match(line);
        if (!m.Success)
            return false;

        var subject = m.Groups["subject"].Value;
        var pieces = ClauseJoin().Split(m.Groups["rest"].Value);

        // Split keeps the separators, so the odd entries are the joins and the even ones the
        // clauses. Rejoining a span has to use the separators that were printed between them —
        // rebuilding with " and " would turn a comma list into something no card says.
        var clauses = new string[(pieces.Length + 1) / 2];
        var joins = new string[clauses.Length - 1];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (i % 2 == 0)
                clauses[i / 2] = pieces[i];
            else
                joins[i / 2] = pieces[i];
        }

        if (clauses.Length < 2)
            return false;

        var folded = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();
        if (!ReadClauses(clauses, joins, 0, subject, card, folded))
            return false;

        into.AddRange(folded);
        return true;
    }

    /// <summary>Reads clauses <paramref name="from"/> onwards, longest join first.</summary>
    /// <remarks>
    /// It backtracks because longest-first is a preference and not a rule: "has flying and first
    /// strike" is one clause and "gets +1/+1 and doesn't untap …" is two, and only trying tells
    /// them apart. Nothing is written into <paramref name="into"/> until the whole remainder has
    /// been read, so an abandoned attempt leaves no effect behind on the card.
    /// </remarks>
    private static bool ReadClauses(
        string[] clauses,
        string[] joins,
        int from,
        string subject,
        CardDefinition card,
        ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        if (from == clauses.Length)
            return true;

        for (var take = clauses.Length - from; take >= 1; take--)
        {
            // The whole line in one piece is what every other matcher has already refused.
            if (from == 0 && take == clauses.Length)
                continue;

            var joined = clauses[from];
            for (var k = from + 1; k < from + take; k++)
                joined += joins[k - 1] + clauses[k];

            var head = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();
            if (!TryStaticLine($"{subject} {joined}.", card, head))
                continue;

            var tail = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();
            if (!ReadClauses(clauses, joins, from + take, subject, card, tail))
                continue;

            into.AddRange(head);
            into.AddRange(tail);
            return true;
        }

        return false;
    }

    /// <summary>
    /// "Equipped creature is a Knight in addition to its other types" — layer 4 (CR 613.1d).
    /// </summary>
    /// <remarks>
    /// The capital letter decides which half of CR 205.3 the word belongs to, the same way it
    /// does everywhere else in this compiler: a capitalised word is a subtype and a lowercase one
    /// is a card type. Anything that is neither leaves the line unread rather than being guessed
    /// at — reading an unknown capitalised noun as a creature type is the bug that made "for each
    /// Equipment you control" count zero while compiling perfectly.
    /// <para>
    /// A subtype is added <em>without</em> its implied card type. "Is a Knight in addition to its
    /// other types" on an equipped creature says nothing about card types (CR 205.1a), and adding
    /// Creature here would animate whatever the Equipment was on — which for a permanent that had
    /// lost its creature type is a strictly better card than the one printed.
    /// </para>
    /// </remarks>
    private static bool TryAttachedTypeAddition(
        string line, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = AttachedTypeAdditionLine().Match(line);
        if (!m.Success)
            return false;

        var printed = m.Groups["what"].Value;

        static bool OnTheHost(GameState _, GameObject? source, CharacteristicsBuilder target) =>
            source?.Permanent?.AttachedTo is { } host && target.Subject.Id == host;

        if (char.IsUpper(printed[0]))
        {
            into.Add(new ContinuousEffectDefinition
            {
                Id = "attached:subtype:" + printed,
                Layer = EffectLayer.Type,
                Applies = OnTheHost,
                Apply = (_, _, builder) =>
                {
                    if (!builder.Subtypes.Contains(printed, StringComparer.OrdinalIgnoreCase))
                        builder.Subtypes.Add(printed);
                },
            });

            return true;
        }

        if (EffectPhrase.Specs.PermanentTypes(printed) is not { Count: > 0 } types)
            return false;

        var added = types.Aggregate(CardType.None, (all, one) => all | one);

        into.Add(new ContinuousEffectDefinition
        {
            Id = "attached:cardtype:" + printed,
            Layer = EffectLayer.Type,
            Applies = OnTheHost,
            Apply = (_, _, builder) => builder.CardTypes |= added,
        });

        return true;
    }

    /// <summary>
    /// "Enchanted creature loses all abilities and has base power and toughness 1/1" (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// The clause is lifted out and the rest of the line is offered back to the attached readers
    /// as the card would have written it without one — "Enchanted creature has base power and
    /// toughness 1/1", which they have read for a long time. Twenty-six corpus lines print it on
    /// an Aura or an Equipment, paired with four different tails; teaching each of those readers
    /// to say "and loses all abilities" would have been four copies of one rule.
    /// <para>
    /// It is declared rather than applied, which is the whole reason
    /// <see cref="ContinuousEffectDefinition.RemovesAllAbilities"/> exists: the enchanted
    /// permanent's own static abilities have to stop being <em>offered</em>, a decision made
    /// before any effect is applied and about a permanent that is not the one being computed.
    /// An <c>Apply</c> that emptied the keywords would leave a silenced lord still pumping the
    /// board.
    /// </para>
    /// <para>
    /// The clause may sit on either side of the tail — "gets -5/-0 and loses all abilities" and
    /// "loses all abilities and doesn't untap" are both printed — but never on both, and a line
    /// claiming otherwise is refused rather than half-read.
    /// </para>
    /// </remarks>
    private static bool TryAttachedSilencing(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = AttachedLosesAllAbilitiesLine().Match(line);
        if (!m.Success || (m.Groups["before"].Success && m.Groups["after"].Success))
            return false;

        var rest = m.Groups["before"].Success
            ? m.Groups["before"].Value.Trim()
            : m.Groups["after"].Success ? m.Groups["after"].Value.Trim() : null;

        // Built aside, so a tail none of them can read leaves the line unread with nothing
        // half-applied — an Aura that silenced a creature and quietly dropped the rest of its
        // sentence is exactly the "half a card" failure this compiler refuses.
        var scratch = ImmutableList.CreateBuilder<ContinuousEffectDefinition>();

        if (rest is not null)
        {
            var rewritten = m.Groups["subject"].Value + " " + rest;

            if (!TryAttachedBuff(rewritten, scratch)
                && !TryDoesNotUntap(rewritten, card, scratch)
                && !TryAttachedAnimation(rewritten, scratch))
            {
                return false;
            }
        }

        into.Add(new ContinuousEffectDefinition
        {
            Id = $"attached:lose-abilities:{card.Name}",
            Layer = EffectLayer.Ability,
            RemovesAllAbilities = true,
            Applies = (_, source, target) =>
                source?.Permanent?.AttachedTo is { } host && target.Subject.Id == host,
            Apply = (_, _, _) => { },
        });

        into.AddRange(scratch);
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

        KeywordAbility? keywords = null;
        string? wardCost = null;

        // A keyword the engine cannot grant leaves the whole line unread: an Equipment that gave
        // the bonus but not the ability would look implemented and play as a weaker card. Ward
        // is the one entry in this slot that is not a flag — it carries a cost — so the list is
        // read through the reader that knows both.
        if (m.Groups["kw"].Success)
        {
            if (!TryKeywordsAndWard(m.Groups["kw"].Value, out var flags, out wardCost))
                return false;

            if (flags != KeywordAbility.None)
                keywords = flags;
        }

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

        // "Enchanted creature has base power and toughness 0/1" — layer 7b, which is the whole
        // difference from the pump above: setting comes before modifying, so a +1/+1 counter on
        // the shrunk creature still counts on top of it (CR 613.4b, 613.4c).
        if (m.Groups["basep"].Success)
        {
            var basePower = int.Parse(
                m.Groups["basep"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            var baseToughness = int.Parse(
                m.Groups["baset"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

            into.Add(new ContinuousEffectDefinition
            {
                Id = $"attached:base-pt:{basePower}/{baseToughness}",
                Layer = EffectLayer.PowerToughnessSet,
                Applies = OnTheHost,
                Apply = (_, _, builder) =>
                {
                    builder.Power = basePower;
                    builder.Toughness = baseToughness;
                },
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

        // "Enchanted creature is goaded" — the Impetus cycle. Goaded is a designation with a
        // player in it (CR 701.15b): goaded *by the Aura's controller*, who is the one player
        // the creature may not attack. That player is asked of layer 2 through the control-only
        // reader, because an Impetus that changes hands goads for its new controller and a full
        // computation from inside the layers is the CR 613.8 loop. The static form has no
        // duration — it lasts exactly as long as the attachment — which is what separates it
        // from the goad verb's floating effect and its "until your next turn".
        if (m.Groups["goaded"].Success)
        {
            into.Add(new ContinuousEffectDefinition
            {
                Id = "attached:goaded",
                Layer = EffectLayer.Ability,
                Applies = OnTheHost,
                Apply = (state, source, builder) =>
                {
                    if (source is null)
                        return;

                    builder.GoadedBy.Add(
                        Characteristics.ControllerOf(state, builder.Abilities, source));
                    builder.Keywords |= KeywordAbility.MustAttack;
                },
            });
        }

        // "Enchanted creature has ward {2}" — a whole triggered ability granted in layer 6, the
        // way a quoted one is, because ward is a triggered ability and not a flag (CR 702.21a).
        // Granting it was measured inert once — the trigger fired and its deferred question
        // found no definition — and the granted-ability lookups have since learned the source
        // id, so the grant goes through the same door and the behaviour test plays it.
        if (wardCost is { } tax)
        {
            var trigger = WardTrigger(
                "granted-ward:" + tax,
                "Whenever this permanent becomes the target of a spell or ability an opponent "
                    + $"controls, counter it unless that player pays {tax}.",
                ManaCostSpec.Parse(tax),
                life: 0,
                kind: null,
                SearchFilters.AnyCard);

            into.Add(new ContinuousEffectDefinition
            {
                Id = "attached:ward:" + tax,
                Layer = EffectLayer.Ability,
                Applies = OnTheHost,
                Apply = (_, _, builder) => builder.GrantedTriggers.Add(trigger),
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

    /// <summary>
    /// A named kind of card, however the sentence declined the noun (CR 109.3).
    /// </summary>
    /// <remarks>
    /// "For each creature card in your graveyard" says it singular and "the number of creature
    /// cards in your graveyard" says it plural, and they are the same count. The table above is
    /// written singular, so the plural spelling looked up nothing and the count came back
    /// unreadable — the same declension defect that once cost every "creatures with flying" line,
    /// found again in the other half of the vocabulary.
    /// <para>
    /// Only the noun's own "s" comes off, and only when the singular is a kind the table knows. A
    /// blanket trim would be right about "permanents" and "lands" in the same breath, and would
    /// also turn a noun it has never heard of into another noun it has never heard of — reporting
    /// the miss one word later than it happened.
    /// </para>
    /// </remarks>
    private static Domain.Enums.CardType? CardKindOfEither(string noun) =>
        CardKindNamed(noun)
        ?? (noun.EndsWith('s') ? CardKindNamed(noun[..^1]) : null);

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
    private static Func<GameState, Guid, int>? DefinedCount(string phrase)
    {
        if (string.Equals(phrase, "your life total", StringComparison.OrdinalIgnoreCase))
            return (state, you) => Math.Max(0, state.GetPlayer(you).Life);

        // "Creatures in your party" is not a count of creatures at all (CR 700.8a): a party is at
        // most one Cleric, one Rogue, one Warrior and one Wizard, so eight Clerics are a party of
        // one. Answered here rather than through the noun grammar, which counts what it matches
        // and would say eight.
        //
        // Delegated rather than answered twice. The copy that used to live here assigned each
        // creature to the first role it could fill and moved on, which CR 700.8b says is wrong
        // wherever it matters: a Cleric Rogue seen before a plain Cleric took the Cleric slot and
        // left the plain one nothing, reporting a party of one where the player is entitled to
        // count two. The shared matcher re-houses a role that is already taken, so it finds the
        // largest party the board allows.
        if (PartyLine().IsMatch(phrase))
        {
            return (state, you) =>
                EffectPhrase.PartySizeFor(state, EmptyAbilities.Instance, you);
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

    /// <summary>
    /// "As long as ~ is paired with another creature, ..." — soulbond's payoff (CR 702.95b).
    /// </summary>
    /// <remarks>
    /// Every soulbond card's second line, in the three forms the 24 of them print: "both
    /// creatures have &lt;keywords&gt;", "each of those creatures gets +N/+N", and "each of
    /// those creatures has "&lt;ability&gt;"". The two subjects mean the same two permanents —
    /// the source and whatever it is paired with — so one recipient test serves all three, and
    /// the condition is not parsed at all: being paired with another creature is what a pairing
    /// <em>is</em>, and the reader that answers it is the same one every other consumer of the
    /// status uses. A quoted ability is read exactly as <see cref="TryGrantedAbility"/> reads
    /// one, granted in layer 6 to both halves.
    /// </remarks>
    private static bool TrySoulbondStatic(
        string line, CardDefinition card, ImmutableList<ContinuousEffectDefinition>.Builder into)
    {
        var m = SoulbondStaticLine().Match(line);
        if (!m.Success)
            return false;

        // The recipient test: the computed permanent is one of the pair's two halves. Reads the
        // stored pairing plus the partner's zone, and deliberately nothing computed — this runs
        // inside the layers, where computing the partner's characteristics would be re-entrant.
        // The sweep in state-based actions severs a pairing the deeper questions have broken.
        static bool OnEitherHalf(GameState state, GameObject? source, CharacteristicsBuilder target)
        {
            if (source is null || state.PairedPartnerOf(source) is not { } partner)
                return false;

            return target.Subject.Id == source.Id || target.Subject.Id == partner.Id;
        }

        if (m.Groups["p"].Success)
        {
            var power = int.Parse(
                m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var toughness = int.Parse(
                m.Groups["tough"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            into.Add(new ContinuousEffectDefinition
            {
                Id = $"soulbond:{card.Name}:{GenerativeEffects.PumpId(power, toughness)}",
                Layer = EffectLayer.PowerToughnessModify,
                Applies = OnEitherHalf,
                Apply = (_, _, builder) => builder.Modify(power, toughness),
            });

            return true;
        }

        if (m.Groups["kw"].Success)
        {
            // Whole or not at all: "protection from Zombies" is a keyword the engine cannot
            // grant, and half a bond would be a different card.
            if (EffectPhrase.Keywords(m.Groups["kw"].Value) is not { } granted)
                return false;

            into.Add(new ContinuousEffectDefinition
            {
                Id = $"soulbond:{card.Name}:{GenerativeEffects.GrantId(granted)}",
                Layer = EffectLayer.Ability,
                Applies = OnEitherHalf,
                Apply = (_, _, builder) => builder.Keywords |= granted,
            });

            return true;
        }

        var quoted = ImmutableList.CreateBuilder<ActivatedAbilityDefinition>();
        var quotedTriggers = ImmutableList.CreateBuilder<TriggeredAbilityDefinition>();
        var rejected = ImmutableList.CreateBuilder<string>();
        var inner = m.Groups["ability"].Value.Trim();

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

        var abilities = quoted
            .Select(a => a with { Id = "granted:" + card.Name + ":" + a.Id })
            .ToImmutableList();

        var grantedTriggers = quotedTriggers
            .Select(t => t with { Id = "granted:" + card.Name + ":" + t.Id })
            .ToImmutableList();

        into.Add(new ContinuousEffectDefinition
        {
            Id = "soulbond-grants:" + card.Name,
            Layer = EffectLayer.Ability,
            Applies = OnEitherHalf,
            Apply = (_, _, builder) =>
            {
                builder.GrantedActivated.AddRange(abilities);
                builder.GrantedTriggers.AddRange(grantedTriggers);
            },
        });

        return true;
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

        // "Enchanted creature" and "equipped creature" name the host outright and "~" is the
        // card naming itself. "It" names neither: it is a pronoun, and what it points at is
        // whatever the condition in front of it just named.
        //
        // Reading it as the host regardless was wrong on 29 corpus lines across 27 cards, and
        // inert on every one. "As long as ~ is untapped, it gets +0/+2" is a creature talking
        // about itself, and a creature is not attached to anything - so Castle Raptors computed
        // 3/3 instead of 3/5, Fleecemane Lion never became hexproof, and the whole static half
        // of each card did nothing at all while the card compiled complete.
        //
        // The order of the two tests is the trap, and eight of those cards sit in it: "As long
        // as ~ is equipped, it has double strike" contains the word "equipped" and still means
        // the card. What the condition *names* decides, so "~" is asked first and the
        // attachment words only afterwards.
        //
        // A condition that names neither leaves the line unread. No corpus line does - all 50
        // name one or the other - and a pronoun with no antecedent is exactly the case where
        // guessing would put a bonus on the wrong permanent without saying so.
        var subject = m.Groups["subject"].Value;
        var condition = m.Groups["cond"].Value;

        bool onHost;
        if (subject.Equals("~", StringComparison.Ordinal))
            onHost = false;
        else if (!subject.Equals("it", StringComparison.OrdinalIgnoreCase))
            onHost = true;
        else if (condition.Contains('~', StringComparison.Ordinal))
            onHost = false;
        else if (condition.Contains("enchanted", StringComparison.OrdinalIgnoreCase)
            || condition.Contains("equipped", StringComparison.OrdinalIgnoreCase))
        {
            onHost = true;
        }
        else
        {
            return false;
        }

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
    /// The group a lord names — what its members must be, what tribe, and what they must look like.
    /// </summary>
    /// <remarks>
    /// <see cref="Types"/> is an intersection: "artifact creature token" is three tests and one
    /// noun. An empty list is the noun "permanent", which asks nothing of the card types at all.
    /// </remarks>
    private sealed record StaticGroup(
        IReadOnlyList<CardType> Types,
        string? Subtype,
        Func<GameState, CharacteristicsBuilder, bool>? Adjective,
        string Described);

    /// <summary>
    /// "Creature tokens", "artifact creatures", "White creatures", "Slivers" — the noun phrase a
    /// mass static names, resolved against the shared vocabulary.
    /// </summary>
    /// <remarks>
    /// The noun goes through <c>EffectPhrase.Specs.PermanentTypes</c>, the same table every
    /// target phrase reads, so the compiler has one answer to "what is a creature token" rather
    /// than two. Whatever is left in front of it is an adjective, and the tribe reading is the
    /// <em>last</em> thing tried rather than the first — which is the whole point of this method.
    /// Trying it first is what turned "Artifact creatures you control get +1/+1" into a lord for
    /// the creature type "Artifact" and "White creatures you control get +1/+1" into one for the
    /// type "White": 27 lines and 60 lines of the corpus respectively, all of which compiled,
    /// played, and buffed nothing at all.
    /// <para>
    /// Returning null leaves the line unread, which is the honest outcome for a group this cannot
    /// describe and strictly better than the silent no-op it replaces — a card a deck check
    /// refuses is a card somebody notices.
    /// </para>
    /// </remarks>
    private static StaticGroup? ReadStaticGroup(string printed, bool singularNoun = false)
    {
        var words = printed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Four words is "other artifact creature tokens" with the scope already lifted off, and
        // nothing printed is longer. The pattern is bounded to the same four so that a line the
        // group reader would reject anyway cannot make it backtrack across a whole sentence
        // first — this matcher is offered every unread line of 32,765 cards.
        if (words.Length is 0 or > 4)
            return null;

        // The longest tail the noun table knows, so "artifact creature tokens" is one noun rather
        // than an adjective in front of "creature tokens".
        var noun = -1;
        IReadOnlyList<CardType> types = [];

        for (var start = 0; start < words.Length; start++)
        {
            var tail = string.Join(
                ' ', words[start..^1].Append(EffectPhrase.SingularWord(words[^1])));

            if (EffectPhrase.Specs.PermanentTypes(tail.ToLowerInvariant()) is { } found)
            {
                noun = start;
                types = found;
                break;
            }
        }

        var described = printed.ToLowerInvariant().Replace(' ', '-');

        // No noun at all is the bare-tribe form: "Slivers you control get +1/+1" never says the
        // word creature. It has to be printed plural and capitalised, because a creature type is
        // a proper noun and dropping either test admits every adjective in the language. Through
        // the shared singulariser, because English is irregular: "other Elves you control" named
        // the creature type "Elve", found none however many Elves were out, and buffed nothing.
        if (noun < 0)
        {
            if (words.Length != 1 || !char.IsUpper(words[0][0]))
                return null;

            var one = EffectPhrase.SingularWord(words[0]);

            // Spelled the same in both numbers, and the singulariser leaves it alone, so it would
            // otherwise fail the plural test that keeps adjectives out. A caller that says the
            // noun arrives singular by grammar — "each other Human", where "each" takes the
            // singular — vouches for it instead, because a singular tribe fails the plural test
            // by construction and the test is guarding against something else.
            if (!singularNoun
                && string.Equals(one, words[0], StringComparison.Ordinal)
                && !string.Equals(words[0], "Merfolk", StringComparison.Ordinal))
            {
                return null;
            }

            return new StaticGroup([CardType.Creature], one, null, described);
        }

        string? subtype = null;
        Func<GameState, CharacteristicsBuilder, bool>? adjective = null;

        foreach (var word in words[..noun])
        {
            var (isAdjective, filter) = GroupAdjective(word);

            if (isAdjective)
            {
                if (filter is null)
                    return null;

                var earlier = adjective;
                adjective = earlier is null
                    ? filter
                    : (state, target) => earlier(state, target) && filter(state, target);

                continue;
            }

            // Not an adjective, so it is a tribe — and a tribe is a creature type, so the noun
            // has to be something that can be a creature. "Zombie tokens" is one; a group whose
            // noun is only a land or an artifact is not, and reading a tribe onto it would build
            // another lord that matches nothing.
            if (subtype is not null
                || !char.IsUpper(word[0])
                || (types.Count > 0
                    && !types.Contains(CardType.Creature)
                    && !types.Contains(CardType.Token)))
            {
                return null;
            }

            subtype = EffectPhrase.SingularWord(word);
        }

        return new StaticGroup(types, subtype, adjective, described);
    }

    /// <summary>
    /// An adjective in front of a lord's noun — "White creatures", "Attacking creatures".
    /// </summary>
    /// <remarks>
    /// Three answers rather than two, and the third is the one that matters. A filter reads the
    /// word; "adjective, and not one we can answer" leaves the whole line unread; and a word this
    /// does not recognise at all is handed on to the tribe reading. Without the middle answer
    /// "Modified creatures you control get +1/+1" falls through to a lord for the creature type
    /// "Modified", which is exactly the silent no-op this whole path exists to stop.
    /// <para>
    /// The words are the ones the corpus prints in this slot, counted rather than imagined:
    /// colours lead on 60 lines, "attacking" on 35, "legendary" on 11, "nontoken" on 7.
    /// </para>
    /// <para>
    /// It answers from the <see cref="CharacteristicsBuilder"/> instead of calling the shared
    /// adjective vocabulary, because it runs <em>inside</em> the layer loop: anything that reads a
    /// finished <see cref="ComputedCharacteristics"/> would re-enter the computation it is part
    /// of. That is the same reason the controller, tribe and keyword tests beside it are written
    /// out here rather than borrowed.
    /// </para>
    /// </remarks>
    private static (bool IsAdjective, Func<GameState, CharacteristicsBuilder, bool>? Filter)
        GroupAdjective(string printed)
    {
        var word = printed.ToLowerInvariant();

        if (ColorNamed(word) is { } colour)
            return (true, (_, target) => target.IsColor(colour));

        if (word.StartsWith("non", StringComparison.Ordinal))
        {
            var rest = word[3..].TrimStart('-');

            if (ColorNamed(rest) is { } without)
                return (true, (_, target) => !target.IsColor(without));

            // "Nonartifact creatures", "nontoken creatures" — the shared noun table again, read
            // negated, so the positive and negative readings of a type word cannot drift apart.
            if (EffectPhrase.Specs.PermanentTypes(rest) is [var excluded])
                return (true, (_, target) => !target.CardTypes.HasFlag(excluded));

            if (rest is "legendary")
                return (true, (_, target) => !target.IsLegendary);

            // Recognised as a negation and not answerable, which leaves the line unread rather
            // than letting "Nonhuman creatures" become a lord for the creature type "Nonhuman".
            return (true, null);
        }

        return word switch
        {
            // A supertype, so it is read off the printed card (CR 205.4a) — nothing in the engine
            // changes one, which is why the builder carries it rather than computing it.
            "legendary" => (true, (_, target) => target.IsLegendary),

            "attacking" => (true, (state, target) =>
                state.Combat.Attackers.ContainsKey(target.Subject.Id)),
            "blocking" => (true, (state, target) =>
                state.Combat.Blockers.Values.Any(blocking => blocking.Contains(target.Subject.Id))),
            "tapped" => (true, (_, target) => target.Subject.Permanent?.IsTapped == true),
            "untapped" => (true, (_, target) => target.Subject.Permanent?.IsTapped == false),

            // Colours after layer 5, counted rather than named (CR 105.2b, 105.2c).
            "colorless" => (true, (_, target) => target.Colors.Count == 0),
            "monocolored" => (true, (_, target) => target.Colors.Count == 1),
            "multicolored" => (true, (_, target) => target.Colors.Count > 1),

            // Named so they are refused rather than left to the tribe reading, which is what each
            // of them used to get: a lord for a creature type no card has. Every one is a real
            // description this filter has no way to ask about — "modified" wants counters, Auras
            // and Equipment (CR 700.9), "enchanted" and "equipped" want an attachment, and
            // "commander" is a designation made before the game began (CR 903.3).
            "modified" or "enchanted" or "equipped" or "unblocked" or "commander" or "historic"
                or "outlaw" or "premium" or "hosted" or "alliterative" => (true, null),
            _ => (false, null),
        };
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

        // The noun is captured whole and resolved here rather than alternated in the pattern,
        // and a bug is the reason rather than tidiness. The alternation this replaced guessed a
        // creature *subtype* from a capital letter, and every sentence begins with one: "Artifact
        // creatures you control get +1/+1" compiled to a lord for the creature type "Artifact",
        // which no card in the game has. 27 corpus lines buffed nothing while reading as
        // complete, and a reader that never fires looks exactly like a reader that works.
        // "Each" takes a singular noun — "Each other Human you control" — so a bare tribe under
        // it arrives without the plural the tribe arm otherwise demands.
        var scopeSaysEach = m.Groups["scope"].Value
            .StartsWith("each", StringComparison.OrdinalIgnoreCase);

        if (ReadStaticGroup(m.Groups["noun"].Value.Trim(), scopeSaysEach) is not { } group)
            return false;

        var subtype = group.Subtype;

        // "Creatures of the chosen type get +1/+1" - the tribe is not printed on the card, it is
        // whatever this permanent named as it entered (CR 614.12). So it is read from the source
        // when the effect applies rather than baked in when the card compiles, which is the same
        // reason every other characteristic is computed.
        var chosenType = m.Groups["chosen"].Value.Equals("type", StringComparison.OrdinalIgnoreCase);
        var chosenColor = m.Groups["chosen"].Value.Equals("color", StringComparison.OrdinalIgnoreCase);
        var side = m.Groups["side"].Value.Trim().ToLowerInvariant();

        // "Each other Human you control" spells the exclusion with two words, and the scope
        // slot read only one — so the line fell through with "other Human" for a noun and was
        // refused. Both spellings mean the same thing: not the permanent whose ability this is.
        var scopeWord = m.Groups["scope"].Value.ToLowerInvariant();
        var otherOnly = scopeWord is "other" or "each other";

        // "Other" is what makes a lord not pump itself, and a lord that pumps itself is a
        // different card — so an unrecognised scope word has to leave the line unread.
        //
        // A line with no ownership clause at all means every permanent that answers the
        // description, an opponent's included: Muscle Sliver's "All Sliver creatures get +1/+1"
        // pumps the Slivers across the table, Crusade pumps every white creature in the game and
        // Illness in the Ranks shrinks every token. This defaulted to "you control", which is the
        // identical bug already found and fixed in the granted-ability reader beside it — "a hive
        // lord that quietly stopped at the table edge" — and it was still here. **83 corpus cards
        // print a mass static with no ownership clause**, and every one of them was compiling as
        // complete and applying to half the board, which is worse than not reading the line:
        // nothing refuses, it just quietly does the wrong half.
        var everyone = side.Length == 0;
        var yours = side is "you control";
        var theirs = side is "your opponents control" or "an opponent controls";

        // "Creatures enchanted player controls get -1/-1" - a group defined by a relation to
        // the source rather than by anybody's seat at the table. The attachment is raw state,
        // not a characteristic (CR 303.4), so the filter can read it straight off the source
        // with no layer question asked; an unattached Curse names nobody and the group is
        // empty, which is the honest answer rather than everything.
        var enchantedPlayers = side is "enchanted player controls";

        if (!everyone && !yours && !theirs && !enchantedPlayers)
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

        // "With a counter on it" names no kind and means any at all (CR 122.1) — a different
        // question from the named form, not a default for it.
        var anyCounter = m.Groups["withcounter"].Success && needsCounter is null;

        KeywordAbility? keywords = null;
        string? massWard = null;

        if (m.Groups["kw"].Success)
        {
            // Through the reader that knows ward as well as the flags, because "Other creatures
            // you control have ward {2}" is this slot and a flag cannot carry a cost.
            if (!TryKeywordsAndWard(m.Groups["kw"].Value, out var flags, out massWard))
                return false;

            if (flags != KeywordAbility.None)
                keywords = flags;
        }

        // "Other Goblin creatures you control attack each combat if able" — the requirement the
        // single-creature form already reads, applied to a group (CR 508.1d). It joins the
        // keywords rather than becoming its own effect because the engine models it as a flag,
        // and the attack declaration reads that flag off the *computed* characteristics of every
        // creature the attacking player controls — so one granted in layer 6 is enforced exactly
        // as a printed one is, with no second path to keep in step.
        if (m.Groups["must"].Success)
        {
            keywords = keywords is { } alreadyGranted
                ? alreadyGranted | KeywordAbility.MustAttack
                : KeywordAbility.MustAttack;
        }

        // A subtype filter is only meaningful when it names a creature type the card itself is
        // about; anything else is read literally, which is what the rules do too.
        bool Matches(GameState state, GameObject? source, CharacteristicsBuilder target)
        {
            // The card types the group's noun asks for, read from the *computed* characteristics
            // for the same reason the tribe is: a land layer 4 animated into a creature is one of
            // "creatures you control". An empty list is the noun "permanent", which asks nothing.
            foreach (var required in group.Types)
            {
                if (!target.CardTypes.HasFlag(required))
                    return false;
            }

            if (group.Adjective is { } describes && !describes(state, target))
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

            if (anyCounter
                && target.Subject.Permanent?.Counters.Any(held => held.Value > 0) != true)
            {
                return false;
            }

            // Nobody's in particular: the description is the whole question, and who controls
            // the permanent does not come into it.
            if (everyone)
                return true;

            // Whoever the source is enchanting, read off the attachment rather than computed —
            // what an Aura is on is a fact of the state, not a characteristic.
            if (enchantedPlayers)
            {
                return source?.Permanent?.AttachedToPlayer is { } enchanted
                    && target.ControllerId == enchanted;
            }

            // The *computed* controller on both sides of the comparison. The target's is the
            // builder's — layer 2 has already run over it by the time a layer 6 or 7 effect
            // asks. The source's has to be asked of the layers too (CR 613.1b), and it is asked
            // through the control-only reader rather than a full computation, because computing
            // one lord's characteristics from inside another's is the CR 613.8 loop: with two
            // lords on the battlefield each filter would compute the other without bottom.
            // Reading the stored controller instead was the recorded defect — a stolen lord
            // kept buffing its old controller's creatures.
            var controller = source is null
                ? target.ControllerId
                : source.Id == target.Subject.Id
                    ? target.ControllerId
                    : Characteristics.ControllerOf(state, target.Abilities, source);

            return yours
                ? target.ControllerId == controller
                : target.ControllerId != controller;
        }

        // The group's own words go into the id, because two lords on one card are told apart by
        // nothing else: "White creatures you control get +1/+1" and "Black creatures you control
        // get +1/+1" are the same layer, the same bonus and the same card, and an id built from
        // the bonus alone would collide — which the invariant suite reads as one ability twice.
        var describedAs = (chosenType || chosenColor
            ? "chosen-" + m.Groups["chosen"].Value.ToLowerInvariant()
            : group.Described)
            + (enchantedPlayers ? ":enchanted-player" : string.Empty)
            + (needs is { } named ? ":with-" + named : string.Empty)
            + (needsCounter is { } counted ? ":counter-" + counted : string.Empty)
            + (anyCounter ? ":counter-any" : string.Empty);

        // "All creatures lose all abilities and have base power and toughness 1/1" — Humility, in
        // layer 6 and then in layer 7b (CR 613.1f, 613.4b). Declared rather than applied, because
        // the half that matters is not a characteristic at all: every creature on the board has to
        // stop *offering* its own static abilities, and that is decided before any of them runs.
        // An Apply that emptied the keywords would leave every lord on the table still pumping.
        if (m.Groups["lose"].Success)
        {
            into.Add(new ContinuousEffectDefinition
            {
                Id = $"mass:{describedAs}:{card.Name}:lose-abilities",
                Layer = EffectLayer.Ability,
                RemovesAllAbilities = true,
                Applies = Matches,
                Apply = (_, _, _) => { },
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
                Id = $"mass:{describedAs}:{card.Name}:{GenerativeEffects.PumpId(power, toughness)}",
                Layer = EffectLayer.PowerToughnessModify,
                Applies = Matches,
                Apply = (_, _, builder) => builder.Modify(power, toughness),
            });
        }

        // "Other creatures have base power and toughness 1/1" — layer 7b rather than the 7c the
        // pump above uses, which is the difference between setting and modifying: a creature
        // shrunk this way and then given a +1/+1 counter is 2/2 (CR 613.4b, 613.4c).
        if (m.Groups["basep"].Success)
        {
            var basePower = int.Parse(
                m.Groups["basep"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
            var baseToughness = int.Parse(
                m.Groups["baset"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);

            into.Add(new ContinuousEffectDefinition
            {
                Id = $"mass:{describedAs}:{card.Name}:base-pt:{basePower}/{baseToughness}",
                Layer = EffectLayer.PowerToughnessSet,
                Applies = Matches,
                Apply = (_, _, builder) =>
                {
                    builder.Power = basePower;
                    builder.Toughness = baseToughness;
                },
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

        // "Other creatures you control have ward {2}" — the whole triggered ability granted to
        // each member of the group in layer 6, the way the attached form grants it to its host
        // (CR 702.21a, 613.1f).
        if (massWard is { } wardTax)
        {
            var trigger = WardTrigger(
                "granted:" + card.Name + ":ward",
                "Whenever this permanent becomes the target of a spell or ability an opponent "
                    + $"controls, counter it unless that player pays {wardTax}.",
                ManaCostSpec.Parse(wardTax),
                life: 0,
                kind: null,
                SearchFilters.AnyCard);

            into.Add(new ContinuousEffectDefinition
            {
                Id = $"mass:{describedAs}:{card.Name}:ward-{wardTax}",
                Layer = EffectLayer.Ability,
                Applies = Matches,
                Apply = (_, _, builder) => builder.GrantedTriggers.Add(trigger),
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

        // Hoisted out of the effect below so the bonus that may ride with the ability can be
        // asked the same question. One predicate and two effects, because a bonus is layer 7c
        // and an ability is layer 6, and one effect cannot be in two (CR 613.1f, 613.4c).
        bool Receives(GameState state, GameObject? source, CharacteristicsBuilder target)
        {
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
                    // board. "You" is the Background's controller, asked of layer 2 the same
                    // way the group side below is.
                    return target.IsCreature
                        && target.Subject.OwnerId
                            == Characteristics.ControllerOf(state, target.Abilities, source)
                        && string.Equals(
                            state.GetPlayer(target.Subject.OwnerId).CommanderOracleId,
                            target.Subject.Card.OracleId,
                            StringComparison.Ordinal);
                }

                if (everyone)
                    return true;

                // Computed on both sides, for the same reason the mass statics compute both: a
                // granted ability has to follow the creature to whoever controls it now, and
                // "you" is whoever controls the *granting* permanent now (CR 613.1b) — through
                // the control-only reader, because a full computation from inside the layers is
                // the CR 613.8 loop.
                var granter = source.Id == target.Subject.Id
                    ? target.ControllerId
                    : Characteristics.ControllerOf(state, target.Abilities, source);

                return yours
                    ? target.ControllerId == granter
                    : target.ControllerId != granter;
            }
        }

        into.Add(new ContinuousEffectDefinition
        {
            Id = "grants:" + card.Name,
            Layer = EffectLayer.Ability,
            Applies = Receives,
            Apply = (_, _, builder) =>
            {
                builder.GrantedActivated.AddRange(granted);
                builder.GrantedTriggers.AddRange(grantedTriggers);
            },
        });

        // "Enchanted creature gets +2/+2 and has "{T}: Add {B}"" — the bonus and the quoted
        // ability, which the cards print together on 52 of them and which two complete grammars
        // could not say jointly: the attached-buff reader's keyword slot has no quotation mark in
        // it, and this reader's pattern went straight from the group to the word "has". Neither
        // needed anything new, only the same sentence read once instead of twice.
        if (m.Groups["p"].Success)
        {
            var power = int.Parse(
                m.Groups["p"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            var toughness = int.Parse(
                m.Groups["tough"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            into.Add(new ContinuousEffectDefinition
            {
                Id = $"grants:{card.Name}:{GenerativeEffects.PumpId(power, toughness)}",
                Layer = EffectLayer.PowerToughnessModify,
                Applies = Receives,
                Apply = (_, _, builder) => builder.Modify(power, toughness),
            });
        }

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
            || paid.Counters is not null

            // "Return a land you control to its owner's hand" reads, and on a *spell* the engine
            // sends it to the graveyard instead: the cast path's chosen-cost loop has arms for
            // tapping and for exiling from a graveyard and falls through to a graveyard move for
            // everything else, while the activation path beside it has the arm this needs. Cards
            // of this shape are therefore left unread rather than compiled into a spell that
            // destroys what it was supposed to pick up (CR 701.20a).
            || paid.Chosen.Any(cost => cost.Kind is ChosenCostKind.ReturnToHand))
        {
            unhandled.Add(line);
            return true;
        }

        into.AddRange(paid.Chosen);
        return true;
    }

    /// <summary>
    /// "You may pay [cost] rather than pay ~'s mana cost", and the same offer for nothing at all
    /// — an alternative cost (CR 118.9).
    /// </summary>
    /// <remarks>
    /// The keyword-less printing of what surge, spectacle and warp say with one word, and it goes
    /// through the same <see cref="ConditionalCost"/> for a reason that is the whole of this
    /// reader's safety: **an alternative cost is optional** (CR 118.9b), and the caster announces
    /// they are paying it (CR 601.2b). The engine's other route — <see cref="AlternativeCastZone"/>
    /// — is taken *unconditionally* whenever the card is in the zone that offers it, so compiling
    /// this as one would leave the card unable to be cast for its printed cost at all. That is why
    /// More Than Meets the Eye is still unread: it is this shape with a cost the caster must be
    /// able to decline, and there was nowhere to put it until this route was found.
    /// <para>
    /// Only a wholly-mana cost is read. The rest of the family pays with something else — "you may
    /// sacrifice a Mountain", "you may exile a black card from your hand" — and a
    /// <c>ConditionalCost</c> carries mana and nothing else, so charging one of those as free mana
    /// would make the card cheaper than printed.
    /// </para>
    /// <para>
    /// "You may cast ~ without paying its mana cost" is the second phrasing CR 118.9 gives for
    /// the same rule, and it arrives here rather than at a mechanism of its own because it is an
    /// alternative cost of nothing. Sixteen corpus cards print it, every one behind an "if":
    /// the ten of the free-spell cycle asking what two players control, five asking about a
    /// commander and one about a spell cast earlier in the turn. The offer stays optional the
    /// whole way down — the caster announces it (CR 601.2b) and the engine charges the printed
    /// cost when they do not — which is the difference that decides the mechanism: routing it
    /// through <see cref="AlternativeCastZone"/> would take the free cast <em>unconditionally</em>
    /// from hand and leave the card with no way to be cast for its printed cost at all.
    /// </para>
    /// <para>
    /// A leading "if" is a question about the board, answered by the shared condition vocabulary
    /// against a stand-in for the card being cast. A condition that vocabulary cannot read leaves
    /// the line unread rather than offering the cost unconditionally.
    /// </para>
    /// </remarks>
    private static bool TryAlternativeManaCost(
        string line, CardDefinition card, ref ConditionalCost? into)
    {
        var m = AlternativeManaCostLine().Match(line);
        if (!m.Success)
            return false;

        Func<GameState, Guid, bool> available = static (_, _) => true;

        if (m.Groups["when"].Success)
        {
            if (BoardConditions.Parse(m.Groups["when"].Value.Trim()) is not { } holds)
                return false;

            // The condition vocabulary reads "you" off a source object, and the source here is
            // the card being cast — which is not yet an object anywhere. A stand-in carrying the
            // would-be controller is what the cost-reduction readers do with the same problem
            // (CR 109.5: "you" on an object is its would-be controller while it is being cast).
            available = (state, playerId) => holds(
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
                });
        }

        // CR 118.9a does not say the price has to be mana, and 78 corpus cards take it at its
        // word: two Mountains, an Island back to hand, a blue card out of hand, four life. Each
        // half of the sentence is read on its own and a half nothing can read leaves the whole
        // line unread - a cost with a payment dropped out of it is a cheaper card than the
        // printed one, and that is the direction this must never fail in.
        var mana = ManaCostSpec.Free;
        var life = 0;
        var payments = ImmutableList.CreateBuilder<ChosenCost>();

        if (m.Groups["paid"].Success)
        {
            foreach (var part in m.Groups["paid"].Value.Split(" and ", StringSplitOptions.TrimEntries))
            {
                if (!TryAlternativePayment(part, ref mana, ref life, payments))
                    return false;
            }

            if (mana == ManaCostSpec.Free && life == 0 && payments.Count == 0)
                return false;
        }

        into = new ConditionalCost(
            "alternative cost",
            "CR 118.9a",

            // No captured cost is the "without paying its mana cost" phrasing. Nothing is a
            // price like any other here: the caster still has to announce they are paying it,
            // and a cost of no symbols is what the engine then charges.
            m.Groups["cost"].Success
                ? ManaCostSpec.Parse(m.Groups["cost"].Value)
                : mana,
            available)
        {
            LifeCost = life,
            Payments = payments.ToImmutable(),
        };

        return true;
    }

    /// <summary>
    /// One price in an alternative cost's list, or false if nothing here can read it.
    /// </summary>
    /// <remarks>
    /// The prices are joined by "and" and there are only ever two or three of them, so they are
    /// read one at a time and folded into the running cost. Everything a card can ask for here
    /// is already a <see cref="ChosenCost"/> the engine charges somewhere else — this only has to
    /// say which kind, and to hand the noun to the shared target vocabulary rather than growing a
    /// second one.
    /// </remarks>
    private static bool TryAlternativePayment(
        string part, ref ManaCostSpec mana, ref int life, ImmutableList<ChosenCost>.Builder into)
    {
        if (AlternativeManaPart().Match(part) is { Success: true } paying)
        {
            var more = ManaCostSpec.Parse(paying.Groups["cost"].Value);
            mana = mana with { Symbols = mana.Symbols.AddRange(more.Symbols) };

            return true;
        }

        if (AlternativeLifePart().Match(part) is { Success: true } bleeding)
        {
            life += int.Parse(bleeding.Groups["life"].Value, CultureInfo.InvariantCulture);
            return true;
        }

        if (AlternativeCardPart().Match(part) is { Success: true } spending)
        {
            var many = ModeCount(spending.Groups["n"].Value) ?? 1;
            if (SpecForCardPayment(spending.Groups["what"].Value, many) is not { } named)
                return false;

            // An exile has to say where from. "Exile a blue card" with no zone is not a price
            // this can charge, and guessing at the hand would be guessing at the card.
            var kind = (spending.Groups["verb"].Value.ToLowerInvariant(),
                    spending.Groups["zone"].Value.ToLowerInvariant()) switch
            {
                ("discard", "") => ChosenCostKind.DiscardCards,
                ("exile", "hand") => ChosenCostKind.ExileFromHand,
                ("exile", "graveyard") => ChosenCostKind.ExileFromGraveyard,
                _ => (ChosenCostKind?)null,
            };

            if (kind is not { } charging)
                return false;

            into.Add(new ChosenCost(charging, many, named));
            return true;
        }

        var giving = AlternativeGivingPart().Match(part);
        if (!giving.Success)
            return false;

        var count = ModeCount(giving.Groups["n"].Value) ?? 1;
        if (SpecForPayment(giving.Groups["what"].Value, count) is not { } what)
            return false;

        into.Add(new ChosenCost(
            giving.Groups["verb"].Value.ToLowerInvariant() switch
            {
                "sacrifice" => ChosenCostKind.SacrificePermanents,
                "return" => ChosenCostKind.ReturnToHand,
                _ => ChosenCostKind.TapPermanents,
            },
            count,
            what));

        return true;
    }

    /// <summary>Which colour a word names, for the prices that are paid in cards.</summary>
    private static readonly Dictionary<string, ManaColor> PaymentColors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["white"] = ManaColor.White,
            ["blue"] = ManaColor.Blue,
            ["black"] = ManaColor.Black,
            ["red"] = ManaColor.Red,
            ["green"] = ManaColor.Green,
        };

    /// <summary>
    /// The cards a price names, when the price is paid out of a hand or a graveyard.
    /// </summary>
    /// <remarks>
    /// The target vocabulary is written about permanents and reads a card's types wherever the
    /// card is, which is what retrace's land discard already leans on - but it has no noun for
    /// "a blue card", because no card targets one. So a colour is answered here and everything
    /// else is handed over: "a creature card" and "a Plains card" are the ordinary nouns with
    /// the word "card" taken off.
    /// <para>
    /// The kind on the returned spec is never consulted for a cost. What a cost asks of it is the
    /// filter and the description, and the description is what the player is told when they offer
    /// the wrong thing.
    /// </para>
    /// </remarks>
    private static TargetSpec? SpecForCardPayment(string noun, int count)
    {
        var phrase = noun.Trim();
        if (count > 1 && phrase.EndsWith("cards", StringComparison.OrdinalIgnoreCase))
            phrase = phrase[..^1];

        if (!phrase.EndsWith(" card", StringComparison.OrdinalIgnoreCase))
            return null;

        var head = phrase[..^" card".Length].Trim();

        if (PaymentColors.TryGetValue(head, out var colour))
        {
            return new TargetSpec
            {
                Kind = TargetKind.Permanent,
                Description = $"a {head} card",
                ObjectFilter = (state, abilities, obj, _) =>
                    Characteristics.Of(state, abilities, obj).Colors.Contains(colour),
            };
        }

        return EffectPhrase.Specs.Parse("target " + head) is { } spec
            ? spec with { Description = $"a {head} card" }
            : null;
    }

    /// <summary>
    /// The permanents a payment names, read through the shared target vocabulary.
    /// </summary>
    /// <remarks>
    /// The card writes the noun in the plural when it wants more than one — "two Mountains",
    /// "three black creatures" — and the target grammar is written in the singular, so the head
    /// noun is put back into the singular before it is handed over. The head is the word in front
    /// of "you control" when the phrase has one and the last word otherwise, which is what keeps
    /// "untapped creatures you control with flying" from being singularised on "flying".
    /// <para>
    /// Anything the vocabulary will not take comes back null and the whole line goes unread.
    /// </para>
    /// </remarks>
    private static TargetSpec? SpecForPayment(string noun, int count)
    {
        var phrase = noun.Trim();
        if (!phrase.EndsWith(" you control", StringComparison.OrdinalIgnoreCase)
            && !phrase.Contains(" you control ", StringComparison.OrdinalIgnoreCase))
        {
            phrase += " you control";
        }

        if (count > 1)
        {
            var head = phrase.IndexOf(" you control", StringComparison.OrdinalIgnoreCase);
            var before = phrase[..head].TrimEnd();
            if (before.EndsWith('s'))
                phrase = before[..^1] + phrase[head..];
        }

        return EffectPhrase.Specs.Parse("target " + phrase);
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
        string line,
        ImmutableList<SpellMode>.Builder into,
        ref int toChoose,
        ref int max,
        ref bool mayRepeat,
        ref ConditionalModes? extra,
        ref bool spree,
        ref FactModes? onFact,
        ref bool fromX)
    {
        // CR 700.2d + 601.2b: a count switched by a fact of this very cast — the CR's own
        // worked example is the kicked form on Inscription of Abundance. "Choose both instead"
        // and "choose any number instead" replace the printed count rather than widening it,
        // which is what separates these from the commander header's "you may" below. "Any
        // number" keeps a floor of one: the spell still has to do something.
        if (FactModalHeader().Match(line) is { Success: true } switched)
        {
            var everything = switched.Groups["what"].Value.Equals(
                "both", StringComparison.OrdinalIgnoreCase);

            toChoose = 1;
            max = 1;
            onFact = new FactModes(
                switched.Groups["team"].Success ? CastFact.Teamwork : CastFact.Kicked,
                everything ? 2 : 1,
                everything ? 2 : -1);

            return true;
        }

        // "Choose up to four. You may choose the same mode more than once." (CR 700.2d) — a
        // floor of zero under a printed ceiling. A mode count of zero with modes on offer is
        // exactly that; the legality check reads the two numbers and asks nothing else.
        if (UpToModalHeader().Match(line) is { Success: true } upTo)
        {
            if (ModeCount(upTo.Groups["n"].Value) is not { } atMost)
                return false;

            toChoose = 0;
            max = atMost;
            mayRepeat = upTo.Groups["repeat"].Success;
            return true;
        }

        // "Choose X. You may choose the same mode more than once." (CR 601.2b) — the count is
        // the X announced with the cast; the card gives the variable no definition of its own.
        if (ChooseXHeader().Match(line) is { Success: true } byX)
        {
            toChoose = 0;
            max = 0;
            fromX = true;
            mayRepeat = byX.Groups["repeat"].Success;
            return true;
        }
        // CR 702.172a: spree is a modal spell whose header is the keyword itself - "choose one
        // or more modes", with each chosen mode's own cost paid on top. The bullets are marked
        // with a plus rather than a dot, which is the visual reminder that they cost something
        // (CR 702.172b) and the reason they need a reader of their own.
        if (SpreeLine().IsMatch(line))
        {
            toChoose = 1;
            max = -1;
            spree = true;
            return true;
        }

        // CR 700.2d: the permission to repeat a mode has to be printed, because the default is
        // that a mode may be taken once. Reading the header without it would offer a card that
        // can point three of its modes at one creature, which is not the card.
        if (RepeatableModalHeader().Match(line) is { Success: true } repeatable)
        {
            if (ModeCount(repeatable.Groups["n"].Value) is not { } takes)
                return false;

            toChoose = takes;
            max = takes;
            mayRepeat = true;
            return true;
        }

        // CR 700.2d again, in the other direction: a maximum that is only sometimes on offer.
        // Compiled as a condition rather than as a number because either number alone is the
        // wrong card - two lets anybody take both modes, one never offers what is printed.
        if (CommanderModalHeader().IsMatch(line))
        {
            toChoose = 1;
            max = 1;
            extra = new ConditionalModes(2, "CR 903.3d", ControlsACommander);
            return true;
        }

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

        // CR 700.2h: a mode with a cost printed in front of it charges that cost when it is
        // chosen. Read only under a spree header, so that nothing else on any other card can be
        // taken for a priced mode - the shape is a plus and a mana cost, and a card that never
        // said "spree" has no modes for it to join.
        if (spree && SpreeBullet().Match(line) is { Success: true } priced)
        {
            var offer = priced.Groups["mode"].Value.Trim();
            if (!EffectPhrase.TryParse(offer, out var costly))
                return false;

            into.Add(
                new SpellMode(offer, costly.Targets, costly.Effects)
                {
                    Cost = ManaCostSpec.Parse(priced.Groups["cost"].Value),
                });

            return true;
        }

        var bullet = ModalBullet().Match(line);

        // A bullet belongs to a header. "Choose up to four" and "Choose X" put a floor of zero
        // under the count, so "no header yet" is no longer the same test as "minimum of zero" —
        // it is all three numbers still at rest.
        if (!bullet.Success || (toChoose == 0 && max == 0 && !fromX))
            return false;

        // A mode the parser cannot read leaves the *line* unread, which is what stops a modal
        // card from quietly offering fewer modes than it prints.
        if (!EffectPhrase.TryParse(bullet.Groups["mode"].Value.Trim(), out var parsed))
            return false;

        into.Add(new SpellMode(bullet.Groups["mode"].Value.Trim(), parsed.Targets, parsed.Effects));
        return true;
    }

    /// <summary>How many modes a written-out number asks for, or null if it is not one.</summary>
    /// <remarks>
    /// "Choose X" is deliberately not here. The count would be the X the spell was cast for,
    /// which is chosen after the modes are (CR 601.2b before 601.2f), so a card compiled with a
    /// fixed count would either offer too few modes or too many.
    /// </remarks>
    private static int? ModeCount(string word) => word.ToLowerInvariant() switch
    {
        "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        _ => null,
    };

    /// <summary>
    /// Whether this player controls a permanent that is somebody's commander (CR 903.3d).
    /// </summary>
    /// <remarks>
    /// "A commander", not "your commander". CR 903.3d reads controlling a commander as
    /// controlling a permanent that is one, whoever designated it — so a commander taken off an
    /// opponent answers this and the lieutenant cycle's question is a different one.
    /// <para>
    /// Control is computed rather than read off the object (CR 613.1b), because the stored
    /// controller is only where control started.
    /// </para>
    /// </remarks>
    private static bool ControlsACommander(GameState state, IAbilitySource abilities, Guid playerId)
        => state.Battlefield.Any(id =>
        {
            var obj = state.GetObject(id);

            return Characteristics.Of(state, abilities, obj).ControllerId == playerId
                && state.TurnOrder.Any(
                    seat => state.GetPlayer(seat).CommanderOracleId is { } theirs
                        && string.Equals(obj.Card.OracleId, theirs, StringComparison.Ordinal));
        });

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

    /// <summary>
    /// "Flashback {cost}" and "Flashback—{cost}, Pay N life" (CR 702.34a).
    /// </summary>
    /// <remarks>
    /// The life half was the whole of what kept four of these cards unread. CR 702.34a says the
    /// flashback cost is what is paid "rather than the card's mana cost", and a cost is allowed
    /// to be more than mana - the em-dash form is exactly the additional-cost spelling the rules
    /// use everywhere else. What was missing was somewhere on the permission to put it:
    /// <see cref="AlternativeCastZone.Extra"/> holds cards and permanents, and life is neither.
    /// <para>
    /// Nothing else about flashback changes. The permission, the zone and the exile on the way
    /// out of the stack are the same three static abilities they were, so a card printing no
    /// life clause compiles to exactly what it compiled to before.
    /// </para>
    /// </remarks>
    private static bool TryFlashback(string line, ref AlternativeCastZone? into)
    {
        var m = FlashbackLine().Match(line);
        if (!m.Success)
            return false;

        into = new AlternativeCastZone(
            Zone.Graveyard, ManaCostSpec.Parse(m.Groups["cost"].Value), ExileOnResolve: true)
        {
            LifeCost = m.Groups["life"].Success
                ? NumberWordOrDigits(m.Groups["life"].Value)
                : 0,
        };

        return true;
    }

    /// <summary>"Harmonize {4}{U}" — flashback with an optional discount (CR 702.180a).</summary>
    /// <remarks>
    /// CR 702.180a spells the keyword out as three static abilities: permission to cast the card
    /// from the graveyard "by paying [cost] and tapping <em>up to one</em> untapped creature you
    /// control rather than paying this spell's mana cost"; a reduction of the total cost by
    /// generic mana equal to that creature's power; and the exile on the way out of the stack.
    /// The first and third are flashback exactly, so that is what this compiles to.
    /// <para>
    /// <strong>The middle ability is not built, and the card is complete without it.</strong>
    /// "Up to one" means tapping nothing is a legal way to pay the harmonize cost, so a card
    /// compiled this way is a strict subset of the printed permission rather than a different
    /// card: every cast it allows is one the rules allow, at the printed price or dearer, never
    /// cheaper. That is the direction this compiler is allowed to be wrong in — the refusal it
    /// exists for is the card that comes out <em>better</em> than printed.
    /// </para>
    /// <para>
    /// What the discount would need is a reduction that belongs to the <em>zone permission</em>
    /// rather than to the spell, and that is why it is not here rather than an oversight.
    /// <see cref="TapToPay"/> sits on <c>SpellDefinition</c> and is applied to whatever cost the
    /// cast arrived at, so hanging harmonize's tap there would discount the card cast from hand
    /// for its printed cost as well; and it pays fixed symbols per permanent, where this pays one
    /// permanent's <em>power</em>. Both halves — an <c>AlternativeCastZone</c> that can carry a
    /// tap-to-reduce, and a reduce-by-power mode — are engine changes outside this file.
    /// </para>
    /// </remarks>
    private static bool TryHarmonize(string line, ref AlternativeCastZone? into)
    {
        var m = HarmonizeLine().Match(line);
        if (!m.Success)
            return false;

        into = new AlternativeCastZone(
            Zone.Graveyard,
            ManaCostSpec.Parse(m.Groups["cost"].Value),
            ExileOnResolve: true,
            Keyword: "harmonize");

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
    /// What a mutating creature spell targets: "a non-Human creature with the same owner as this
    /// spell" (CR 702.140a).
    /// </summary>
    /// <remarks>
    /// Written here rather than read from the target grammar, and the reason is the owner clause.
    /// The printed reminder text says "target non-Human creature you own", which the grammar
    /// would read as a question about the <em>chooser</em> - and that is a different card in the
    /// one case it matters, when a player is casting a spell whose card somebody else owns. The
    /// rule says the spell's owner, so the check needs the spell, and only a
    /// <see cref="TargetSpec.SourceFilter"/> gets one.
    /// <para>
    /// CR 702.73a decides the awkward half of "non-Human": a changeling is every creature type,
    /// so it is no more a non-Human than it is a non-Wall, and it may not be mutated onto.
    /// </para>
    /// <para>
    /// One shared instance rather than one per card. It closes over nothing, and a spec built per
    /// compile would be one more object per mutate card for no difference.
    /// </para>
    /// </remarks>
    private static readonly TargetSpec MutateTargetSpec = new()
    {
        Kind = TargetKind.Permanent,
        Description = "target non-Human creature you own",
        ObjectFilter = (state, abilities, obj, _) =>
        {
            var now = Characteristics.Of(state, abilities, obj);
            return now.IsCreature && !now.IsEveryCreatureType && !now.HasSubtype("Human");
        },

        // A caller that cannot say which spell is asking skips this, exactly as the protection
        // check does - never the creature test above, which is the half that keeps a mutate from
        // landing on something it may not touch.
        SourceFilter = (_, _, obj, source, _) => source is null || obj.OwnerId == source.OwnerId,
    };

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
    /// Surviving the host is a printed exception rather than a consequence of this effect
    /// (CR 702.103d), and it has to be. At the moment the host dies a bestowed permanent is still
    /// attached to it, so this is still applying and the permanent is still an Aura by every
    /// characteristic it has - indistinguishable from a Licid in the same position, which the
    /// rules bury. <c>StateBasedActions.BuriedWhenUnattached</c> is where the two are told apart.
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

    /// <summary>
    /// "You may have ~ enter as a copy of any creature on the battlefield" (CR 707.5) — Clone.
    /// </summary>
    /// <remarks>
    /// A replacement rather than a trigger, and the order inside it is the whole rule: the copy
    /// effect is emitted <em>before</em> the arrival it replaces. Triggers are collected against
    /// the state just after the event that caused them (CR 603.6), so a permanent that becomes a
    /// copy in the event after its own arrival has already been asked what its enters abilities
    /// are and answered with the copying card's. CR 707.5 says the copy's enters-the-battlefield
    /// abilities have a chance to trigger, and gives Wall of Omens as the example. Reversed, the
    /// permanent is still a copy with the right name and the right size and the trigger the rule
    /// promises simply never exists — correct-looking and silent, which is this family's failure
    /// mode throughout.
    /// <para>
    /// <b>Which</b> permanent is a question, and it falls in the middle of applying an event —
    /// the one place this engine had nothing to ask with. It is asked as a set of candidate
    /// replacements (see <see cref="ReplacementEffectDefinition.Branches"/>), which is a question
    /// CR 616.1 already has and which already halts the game and replays. "You may" is the same
    /// question's decline arm, so it costs nothing beyond <c>IsOptional</c>.
    /// </para>
    /// <para>
    /// The exception clauses are read into the <em>card</em> handed to the copy, which is what
    /// CR 707.9b prescribes: the modified characteristic becomes part of the copy's copiable
    /// values. Anything the exception grammar does not fully understand leaves the whole line
    /// unread rather than producing a copy without its exception — a Quicksilver Gargantuan that
    /// forgot it was 7/7 is a strictly better card than the one printed.
    /// </para>
    /// </remarks>
    private static bool TryEntersAsACopy(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = EntersAsACopyLine().Match(line);
        if (!m.Success)
            return false;

        if (CopyChoice(m.Groups["what"].Value) is not { } spec)
            return false;

        var exceptions = m.Groups["except"] is { Success: true } clauses
            ? CopyExceptions(clauses.Value)
            : Unchanged;

        if (exceptions is null)
            return false;

        var tapped = m.Groups["tapped"].Success;

        into.Add(new ReplacementEffectDefinition
        {
            Id = "enters-as-a-copy",

            // Null, not Battlefield: a permanent created on the battlefield was never anywhere
            // else, and one that resolves off the stack is replaced while it is still a spell
            // (CR 614.6).
            FunctionsFrom = null,
            IsOptional = true,

            // Once the copy effect naming this permanent exists, this effect is done with the
            // arrival - the branches not taken must not be offered again against the very event
            // the chosen one re-emitted.
            Applies = (e, state, source) =>
                Arriving(e, source) is { } arriving && !AlreadyACopy(state, arriving),

            // Never reached: an effect with branches is applied through one of them. It is the
            // same answer declining gives, which is the honest one for an effect that has not
            // been pointed at anything.
            Replace = (e, _, _) => [e],
            Branches = (e, state, abilities, source) =>
                CopyBranches(e, state, abilities, source, spec, exceptions, tapped),
        });

        return true;
    }

    /// <summary>Whether a copy effect already names this permanent (CR 707.2).</summary>
    private static bool AlreadyACopy(GameState state, ObjectId id) =>
        state.FloatingEffects.Any(
            f => f.AffectedIds.Contains(id) && GenerativeEffects.IsCopy(f.DefinitionId));

    /// <summary>One branch per permanent the arriving one could enter as a copy of.</summary>
    /// <remarks>
    /// The label is what the player picks from, so it names the card and who controls it — a
    /// board with two Grizzly Bears on it must not offer two identical buttons. It is also half
    /// the key CR 614.5 uses to stop an effect applying twice, so identical labels are numbered
    /// rather than left to collide.
    /// </remarks>
    private static List<ReplacementBranch> CopyBranches(
        GameEvent e,
        GameState state,
        IAbilitySource abilities,
        GameObject source,
        TargetSpec spec,
        Func<CardDefinition, CardDefinition> exceptions,
        bool tapped)
    {
        if (Arriving(e, source) is null)
            return [];

        var branches = new List<ReplacementBranch>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var id in state.Battlefield)
        {
            var candidate = state.GetObject(id);

            // Not a target: this is a choice made as a replacement applies, so hexproof and
            // shroud have nothing to say about it (CR 115.6). Accepts asks the phrase's own
            // filters and only those.
            if (!spec.Accepts(state, abilities, candidate, source.ControllerId, source))
                continue;

            // CR 707.3: the copiable values are what the permanent *is*, so a copy of a copy
            // copies the card the first one became rather than the card it was printed as.
            var now = Characteristics.Of(state, abilities, candidate);
            var copied = exceptions(now.Card);

            // The computed controller, not the stored one: control is layer 2 (CR 613.1b), and a
            // label naming the player a stolen permanent used to belong to is a label that
            // points at the wrong half of the table.
            var owner = state.Players.TryGetValue(now.ControllerId, out var who)
                ? who.Name
                : "?";

            var label = $"{copied.Name} ({owner})";
            var already = seen.GetValueOrDefault(label);
            seen[label] = already + 1;
            if (already > 0)
                label = string.Create(CultureInfo.InvariantCulture, $"{label} #{already + 1}");

            var name = GenerativeEffects.CopyId(copied);

            branches.Add(new ReplacementBranch(label, (ev, _, src) =>
            {
                if (Arriving(ev, src) is not { } arriving)
                    return [ev];

                var events = new List<GameEvent>
                {
                    // No duration: this is what the permanent is, not something done to it for
                    // a turn (CR 707.5).
                    new ContinuousEffectCreated(
                        Guid.NewGuid(), name, [arriving], UntilEndOfTurn: null),
                    ev,
                };

                if (tapped)
                    events.Add(new PermanentTapped(arriving));

                return events;
            }));
        }

        return branches;
    }

    /// <summary>An exception clause list that changes nothing.</summary>
    private static readonly Func<CardDefinition, CardDefinition> Unchanged = card => card;

    /// <summary>
    /// Which permanents "any creature on the battlefield" offers, as a target phrase.
    /// </summary>
    /// <remarks>
    /// Read through the same phrase grammar every target goes through, so "any nonland permanent
    /// on the battlefield", "a creature an opponent controls" and "any Equipment on the
    /// battlefield" all arrive working without this knowing what an Equipment is. What it adds is
    /// the two words the copy family spells differently — the article, and the "on the
    /// battlefield" that a target phrase leaves implicit.
    /// <para>
    /// Any phrase naming somewhere other than the battlefield is refused outright. Half of this
    /// family copies a card in a graveyard or in exile, and a copy of a card that is not a
    /// permanent is a different rule with a different zone; reading those as though they meant
    /// the battlefield would compile a card that plays something else.
    /// </para>
    /// </remarks>
    private static TargetSpec? CopyChoice(string phrase)
    {
        var what = phrase.Trim();

        foreach (var article in ChoiceArticles)
        {
            if (what.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                what = what[article.Length..];
                break;
            }
        }

        const string here = " on the battlefield";
        if (what.EndsWith(here, StringComparison.OrdinalIgnoreCase))
            what = what[..^here.Length];

        foreach (var elsewhere in NotTheBattlefield)
        {
            if (what.Contains(elsewhere, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return EffectPhrase.Specs.Parse("target " + what) is { Kind: TargetKind.Permanent } spec
            ? spec
            : null;
    }

    private static readonly string[] ChoiceArticles = ["any ", "an ", "a "];

    /// <summary>Words that mean the phrase is not about the battlefield.</summary>
    private static readonly string[] NotTheBattlefield =
        ["card", "graveyard", "exile", "library", "hand", "stack", "battlefield", "spell"];

    /// <summary>
    /// Reads "except it's an artifact in addition to its other types" (CR 707.9).
    /// </summary>
    /// <remarks>
    /// Returns null for anything it does not fully understand, which leaves the whole line
    /// unread. That is the direction to fail in: an exception silently dropped makes the copy
    /// better than the card that was printed, and 79 of the 135 printed copy sentences carry one
    /// of these clauses, so guessing would have been wrong at scale rather than occasionally.
    /// </remarks>
    private static Func<CardDefinition, CardDefinition>? CopyExceptions(string clauses)
    {
        var addTypes = default(CardType);
        var addSubtypes = new List<string>();
        var addKeywords = KeywordAbility.None;
        var dropLegendary = false;
        int? power = null;
        int? toughness = null;

        foreach (var raw in ExceptionClauses().Split(clauses))
        {
            var clause = raw.Trim().TrimEnd('.');
            if (clause.Length == 0)
                continue;

            if (NotLegendaryClause().IsMatch(clause))
            {
                dropLegendary = true;
                continue;
            }

            if (SetSizeClause().Match(clause) is { Success: true } size)
            {
                power = int.Parse(size.Groups["p"].Value, CultureInfo.InvariantCulture);
                toughness = int.Parse(size.Groups["t"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (InAdditionClause().Match(clause) is { Success: true } added)
            {
                if (!ReadCopyTypes(added.Groups["types"].Value, ref addTypes, addSubtypes))
                    return null;

                continue;
            }

            if (HasKeywordClause().Match(clause) is { Success: true } has)
            {
                if (ReadCopyKeyword(has.Groups["kw"].Value) is not { } keyword)
                    return null;

                addKeywords |= keyword;
                continue;
            }

            return null;
        }

        return card => GenerativeEffects.Excepting(
            card, addTypes, addSubtypes, dropLegendary, power, toughness, addKeywords);
    }

    /// <summary>"a Synth artifact creature" — card types and subtypes, mixed (CR 205).</summary>
    /// <remarks>
    /// Case is what separates them, which is not a heuristic: CR 205.2a prints card types in
    /// lower case and CR 205.3a prints subtypes capitalised, and every card in the corpus that
    /// says this says it that way. A lower-case word that is not a card type is something else
    /// entirely — "a Vehicle artifact with crew 3" — and refuses the clause.
    /// </remarks>
    private static bool ReadCopyTypes(string phrase, ref CardType types, List<string> subtypes)
    {
        var words = phrase.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return false;

        var start = words[0] is "a" or "an" ? 1 : 0;
        if (start >= words.Length)
            return false;

        for (var i = start; i < words.Length; i++)
        {
            var word = words[i];

            if (CopiableTypes.TryGetValue(word, out var type))
            {
                types |= type;
                continue;
            }

            if (!char.IsUpper(word[0]))
                return false;

            subtypes.Add(word);
        }

        return true;
    }

    /// <summary>The card types an exception may add (CR 205.2a) — permanents only.</summary>
    private static readonly Dictionary<string, CardType> CopiableTypes =
        new(StringComparer.Ordinal)
        {
            ["artifact"] = CardType.Artifact,
            ["creature"] = CardType.Creature,
            ["enchantment"] = CardType.Enchantment,
            ["land"] = CardType.Land,
            ["planeswalker"] = CardType.Planeswalker,
            ["battle"] = CardType.Battle,
        };

    /// <summary>"except it has flying" — one keyword the engine already models (CR 702).</summary>
    private static KeywordAbility? ReadCopyKeyword(string word)
    {
        var spelled = word.Replace(" ", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse<KeywordAbility>(spelled, ignoreCase: true, out var keyword)
            && keyword != KeywordAbility.None
            && Enum.IsDefined(keyword)
            ? keyword
            : null;
    }

    /// <summary>"As ~ enters, choose a color" — a choice made as it arrives (CR 614.12).</summary>
    private static bool TryChooseAsEnters(string line, ref ChoiceOnEntry into)
    {
        var m = ChooseAsEntersLine().Match(line);
        if (!m.Success)
            return false;

        into = m.Groups["what"].Value.Equals("color", StringComparison.OrdinalIgnoreCase)
            ? ChoiceOnEntry.Color
            : ChoiceOnEntry.CreatureType;

        return true;
    }

    /// <summary>"~ enters tapped. As it enters, choose a color."</summary>
    /// <remarks>
    /// Two facts the compiler already reads, printed on one line — the thirteen colour-fixing
    /// lands say them as a pair. Both halves matched on their own and the pair matched nothing,
    /// because every matcher here is anchored to a whole line and none of them splits sentences.
    /// <para>
    /// So this splits the pair and hands each half back to the reader that owns it, rather than
    /// restating either. That is the point of the shape: the enters-tapped replacement is built
    /// in exactly one place, and a card saying the two things separately and a card saying them
    /// together cannot come out different. Nothing below loses a line to it — neither existing
    /// reader could match this line at all.
    /// </para>
    /// <para>
    /// The five Thriving lands say "choose a color <em>other than</em> red", and this refuses
    /// them: <see cref="ChoiceOnEntry"/> names a kind of choice and has nowhere to put an
    /// excluded colour, so reading them here would offer red and hand the player a land strictly
    /// better than the one printed. Eight of the thirteen say it without the exclusion.
    /// </para>
    /// </remarks>
    private static bool TryEntersTappedChoosing(
        string line,
        ImmutableList<ReplacementEffectDefinition>.Builder replacements,
        ref ChoiceOnEntry chooses)
    {
        var m = EntersTappedChoosingLine().Match(line);
        if (!m.Success)
            return false;

        // Both halves or neither: the reader that owns each sentence decides, and a "no" from
        // either leaves the whole line unread rather than half of it applied.
        var choice = ChoiceOnEntry.None;
        if (!TryChooseAsEnters(m.Groups["choice"].Value, ref choice))
            return false;

        if (!TryEntersTapped(m.Groups["tapped"].Value, replacements))
            return false;

        chooses = choice;
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

    /// <summary>
    /// "Prevent all combat damage that would be dealt to and dealt by enchanted creature"
    /// (CR 615.1) — the shield a permanent's own static ability puts up.
    /// </summary>
    /// <remarks>
    /// A replacement effect rather than a <see cref="PreventDescribedDamage"/>, and the choice is
    /// forced: a described prevention is state the engine keeps until the turn ends, so one with
    /// no duration would sit on the board after the Aura holding it had been destroyed. A
    /// replacement lives on its permanent — <see cref="ReplacementEffectDefinition.FunctionsFrom"/>
    /// is the battlefield — so it stops the moment the permanent does, which is what a static
    /// ability means (CR 611.2c).
    /// <para>
    /// "Dealt by" is the half the countdown shield could never answer. Preventing what a creature
    /// deals is a question about the damage's source (CR 609.7), and the event carries it, so
    /// both directions are the same predicate asked of a different field. Damage to a player is
    /// its own event and has to be watched separately, or an Aura preventing what its host deals
    /// would still let it kill somebody.
    /// </para>
    /// </remarks>
    private static bool TryStaticPrevention(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var sentence = line;
        Func<GameState, IAbilitySource, GameObject, bool>? when = null;

        // "During your turn, prevent all damage that would be dealt to you" — a static ability
        // with a condition on it (CR 604.3). A replacement effect asks its question when the
        // event would happen, which is exactly when the condition has to hold, so this is one
        // more clause in Applies rather than anything with a duration. Through
        // BoardConditions so "your turn" means the same thing here as everywhere else; a
        // condition that vocabulary cannot read leaves the whole line unread rather than
        // producing an unconditional shield, which is a strictly better card than the printed
        // one.
        if (ConditionalPreventionLine().Match(line) is { Success: true } guarded)
        {
            when = BoardConditions.Parse(guarded.Groups["cond"].Value.Trim());
            if (when is null)
                return false;

            sentence = guarded.Groups["rest"].Value;
        }

        if (EffectPhrase.ReadPreventionSentence(sentence) is not { } read || read.ForTheTurn)
            return false;

        var built = new List<ReplacementEffectDefinition>();

        // "To and dealt by enchanted creature" is two shields around one noun, and they are
        // alternatives: damage reaching it is prevented, and so is damage it deals. One shield
        // with both slots filled would be the conjunction of the two — only the damage it dealt
        // to itself, which is a card that does nothing.
        var ok = read.BothWays
            ? StaticShields(read.Kind, read.Victims, null, when, built)
                && StaticShields(read.Kind, null, read.Sources, when, built)
            : StaticShields(read.Kind, read.Victims, read.Sources, when, built);

        if (!ok)
            return false;

        into.AddRange(built);
        return true;
    }

    /// <summary>Which object a static prevention's clause names, when it names one.</summary>
    private enum PreventionAnchor
    {
        /// <summary>A described set, or nothing at all — no single object is named.</summary>
        None,

        /// <summary>The permanent whose ability this is — "~".</summary>
        Self,

        /// <summary>What it is attached to — "enchanted creature", "equipped creature".</summary>
        Host,
    }

    /// <summary>
    /// The nouns a static prevention can use to name one object rather than describe a set.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="EffectPhrase.PreventVictim"/> because these three words mean
    /// nothing to a resolving spell: a permanent is the only thing that has a "this" or a host.
    /// The described-set vocabulary is shared with the sentence reader; this is the part that is
    /// only ever true of a static ability.
    /// </remarks>
    private static PreventionAnchor StaticPreventionAnchor(string phrase) =>
        phrase.Trim().ToLowerInvariant() switch
        {
            "~" => PreventionAnchor.Self,
            "enchanted creature" or "equipped creature" or "enchanted permanent"
                => PreventionAnchor.Host,
            _ => PreventionAnchor.None,
        };

    /// <summary>
    /// One prevention sentence's clauses, compiled to a shield each (CR 615.1).
    /// </summary>
    /// <remarks>
    /// "To you and creatures you control" is two shields and not one: a player and a set of
    /// permanents are covered by different fields, and one effect cannot hold both. Every part
    /// has to be readable — a sentence with one clause the vocabulary cannot say is left unread
    /// rather than shielded by the parts that were understood, because a prevention read wider
    /// than printed is what makes a creature invulnerable.
    /// </remarks>
    private static bool StaticShields(
        DamageKind kind,
        string? victims,
        string? sources,
        Func<GameState, IAbilitySource, GameObject, bool>? when,
        List<ReplacementEffectDefinition> built)
    {
        // A sentence naming neither what it shields nor whose damage it watches is a permanent
        // preventing every point of damage in the game for the rest of it. Nothing prints that,
        // so it is far likelier to be a mis-parse than a card, and it is refused.
        if (victims is null && sources is null)
            return false;

        var dealer = PreventionAnchor.None;
        (string? Filter, PlayerScope? Who) from = (null, null);

        if (sources is { } by)
        {
            dealer = StaticPreventionAnchor(by);

            if (dealer is PreventionAnchor.None)
            {
                if (EffectPhrase.PreventSource(by) is not { } described)
                    return false;

                from = described;
            }
        }

        if (victims is null)
        {
            built.Add(
                StaticShield(kind, PreventionAnchor.None, (null, null, false), dealer, from, when));
            return true;
        }

        var before = built.Count;

        foreach (var part in victims.Split(
            " and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (StaticPreventionAnchor(part) is var anchor and not PreventionAnchor.None)
            {
                built.Add(StaticShield(kind, anchor, (null, null, false), dealer, from, when));
                continue;
            }

            if (EffectPhrase.PreventVictim(part) is not { } who)
                return false;

            built.Add(StaticShield(kind, PreventionAnchor.None, who, dealer, from, when));
        }

        return built.Count > before;
    }

    /// <summary>
    /// One shield a permanent's static ability puts up (CR 615.1) — a replacement effect.
    /// </summary>
    /// <remarks>
    /// A replacement rather than a <see cref="PreventDescribedDamage"/>, and the choice is
    /// forced: a described prevention is state the engine keeps until the turn ends, so one with
    /// no duration would sit on the board after the Aura holding it had been destroyed. A
    /// replacement lives on its permanent — <see cref="ReplacementEffectDefinition.FunctionsFrom"/>
    /// is the battlefield — so it stops the moment the permanent does, which is what a static
    /// ability means (CR 611.2c).
    /// <para>
    /// What it shields is described by a <see cref="PreventionEffect"/> built fresh for each
    /// event, so the two kinds of prevention answer "does this cover that damage" through the
    /// one predicate in <see cref="Preventions"/>. Only the object slots are filled here, and
    /// only they can be: "~" and "enchanted creature" are ids that are not known until there is
    /// a board, and the same words on a spell name nothing at all.
    /// </para>
    /// <para>
    /// The controller is read through the control-only layer reader so that a stolen permanent
    /// shields its new controller's creatures (CR 613.1b). It is asked with no ability source,
    /// which is the compiler's standing compromise everywhere a replacement predicate needs one:
    /// <see cref="ReplacementEffectDefinition.Applies"/> is handed a state and an object and no
    /// abilities, so a control effect <em>granted</em> to a permanent rather than printed on it
    /// is invisible here. Threading an <c>IAbilitySource</c> through that signature is the fix,
    /// and it is forty-two call sites wide.
    /// </para>
    /// </remarks>
    private static ReplacementEffectDefinition StaticShield(
        DamageKind kind,
        PreventionAnchor victim,
        (string? Filter, PlayerScope? Who, bool Other) described,
        PreventionAnchor dealer,
        (string? Filter, PlayerScope? Who) from,
        Func<GameState, IAbilitySource, GameObject, bool>? when)
    {
        var template = new PreventionEffect
        {
            Id = Guid.Empty,
            ControllerId = Guid.Empty,
            Kind = kind,

            // A filter names permanents and a bare scope names players — "to creatures you
            // control" and "to you" are the two halves of the same slot and never both.
            PermanentFilter = described.Filter,
            PermanentController = described.Filter is null ? null : described.Who,
            Players = described.Filter is null ? described.Who : null,
            SourceFilter = from.Filter,
            SourceController = from.Who,
        };

        // The shield with its object slots filled from the board, or null when a slot names
        // something that is not there. An Aura that has come unattached shields nobody rather
        // than falling back to shielding itself.
        PreventionEffect? Bind(GameState state, GameObject source)
        {
            ObjectId? Anchored(PreventionAnchor which) => which switch
            {
                PreventionAnchor.Self => source.Id,
                PreventionAnchor.Host => source.Permanent?.AttachedTo,
                _ => null,
            };

            ObjectId? shielded = null;
            if (victim is not PreventionAnchor.None)
            {
                shielded = Anchored(victim);
                if (shielded is null)
                    return null;
            }

            ObjectId? dealing = null;
            if (dealer is not PreventionAnchor.None)
            {
                dealing = Anchored(dealer);
                if (dealing is null)
                    return null;
            }

            return template with
            {
                ControllerId = Characteristics.ControllerOf(state, EmptyAbilities.Instance, source),
                Permanent = shielded,
                Source = dealing,
                Excludes = described.Other ? source.Id : null,
            };
        }

        return new ReplacementEffectDefinition
        {
            Id = StaticShieldId(kind, victim, described, dealer, from),
            FunctionsFrom = Zone.Battlefield,
            Applies = (e, state, source) =>
            {
                if (when is not null && !when(state, EmptyAbilities.Instance, source))
                    return false;

                if (Bind(state, source) is not { } shield)
                    return false;

                return e switch
                {
                    Events.DamageMarked marked =>
                        Preventions.Watches(
                            shield, state, EmptyAbilities.Instance, marked.IsCombat,
                            marked.SourceId)
                        && state.TryGetObject(marked.Id, out var damaged)
                        && Preventions.Covers(shield, state, EmptyAbilities.Instance, damaged),
                    Events.PlayerDamaged hit =>
                        Preventions.Watches(
                            shield, state, EmptyAbilities.Instance, hit.IsCombat, hit.SourceId)
                        && Preventions.CoversPlayer(shield, state, hit.PlayerId),
                    _ => false,
                };
            },

            // Nothing comes back: the damage event is replaced by no events at all, which is
            // what preventing all of it means (CR 615.1). Every line read here says "prevent
            // all"; CR 615.10's numbered form is a differently-shaped sentence this does not
            // claim, so the amount is never partial.
            Replace = (_, _, _) => [],
        };
    }

    /// <summary>
    /// A name for one static shield, distinct from every other on the same permanent.
    /// </summary>
    /// <remarks>
    /// CR 614.5 keys an application on the permanent and this id, so Fog Bank's two shields
    /// would be one if they shared a name — and the second direction would never apply.
    /// </remarks>
    private static string StaticShieldId(
        DamageKind kind,
        PreventionAnchor victim,
        (string? Filter, PlayerScope? Who, bool Other) described,
        PreventionAnchor dealer,
        (string? Filter, PlayerScope? Who) from)
    {
        var to = victim switch
        {
            PreventionAnchor.Self => "self",
            PreventionAnchor.Host => "host",

            // Whose it is belongs in the name as much as what it is. "Creatures you control" and
            // "creatures your opponents control" are one word apart and would otherwise be the
            // same shield to CR 614.5, which keys an application on this string.
            _ => (described.Other ? "other-" : string.Empty)
                + (described.Filter ?? "all")
                + (described.Who is { } whose ? ":" + whose : string.Empty),
        };

        var by = dealer switch
        {
            PreventionAnchor.Self => "self",
            PreventionAnchor.Host => "host",
            _ => from.Filter is null && from.Who is null
                ? "any"
                : (from.Filter ?? "source") + (from.Who is { } who ? ":" + who : string.Empty),
        };

        return $"static-prevention:{kind.ToString().ToLowerInvariant()}:to={to}:by={by}";
    }

    /// <summary>
    /// "If [a source] would deal damage to [something], it deals [more] instead" (CR 614.1a).
    /// </summary>
    /// <remarks>
    /// The damage-amplifying replacement, and the largest single replacement family left in the
    /// corpus: 36 cards print one of these as their whole rules text. It is read compositionally
    /// rather than as whole lines because the three slots vary independently — whose damage, dealt
    /// to what, and by how much — and enumerating the product would be one pattern per card.
    /// <para>
    /// CR 614.5 is what makes "replace the event with a bigger one" safe to write: a replacement
    /// gets one opportunity per event and does not re-enter its own output. The rule's own example
    /// is this very card — two doublers make a 2-power creature deal 8, "not an infinite amount" —
    /// and the engine's <c>applied</c> set is what implements it.
    /// </para>
    /// <para>
    /// A line saying "this turn" is refused outright. Those are one-shot effects a resolving spell
    /// creates, and compiled here they would become a static ability that doubled damage for the
    /// rest of the game — a strictly better card than the one printed, reading as complete.
    /// </para>
    /// </remarks>
    private static bool TryDamageAmount(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = DamageAmountLine().Match(line);
        if (!m.Success)
            return false;

        if (line.Contains(" this turn", StringComparison.OrdinalIgnoreCase))
            return false;

        if (DamageDealer(m.Groups["src"].Value.Trim()) is not { } dealer)
            return false;

        var named = m.Groups["rec"].Success ? m.Groups["rec"].Value.Trim() : null;
        if (DamageRecipient(named) is not { } victim)
            return false;

        if (DamageScale(m) is not { } scale)
            return false;

        // "…to that permanent or player" is the sentence pointing back at the recipient it has
        // already named. Anything else in that slot is a *redirection* — "it deals that damage to
        // its controller" — which replaces who is damaged rather than how much, and is not this.
        var tail = m.Groups["again"].Success ? m.Groups["again"].Value.Trim() : string.Empty;
        if (tail.Length > 0
            && !tail.StartsWith("that ", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tail, "it", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool? combat = m.Groups["qual"].Value.Trim().ToLowerInvariant() switch
        {
            "combat" => true,
            "noncombat" => false,
            _ => null,
        };

        into.Add(new ReplacementEffectDefinition
        {
            Id = "damage-amount:" + m.Groups["src"].Value.Trim().ToLowerInvariant()
                + ":" + m.Groups["times"].Value + m.Groups["half"].Value
                + m.Groups["dir"].Value + m.Groups["n"].Value,

            FunctionsFrom = Zone.Battlefield,
            Applies = (e, state, source) =>
                DamageIn(e) is { } damage
                && damage.Amount > 0
                && (combat is null || combat == damage.IsCombat)
                && dealer(state, source, damage.SourceId)
                && victim(state, source, e),

            Replace = (e, _, _) => Rescaled(e, scale(DamageIn(e)!.Value.Amount)),
        });

        return true;
    }

    /// <summary>The damage an event is, or null when it is not one (CR 120.3).</summary>
    /// <remarks>
    /// Two events, because the rules keep two: damage to a player is life loss (CR 120.3c) and
    /// damage to a permanent is marked on it (CR 120.3a). Everything watching damage has to watch
    /// both, and a reader that watched only the permanent would silently skip every burn spell.
    /// </remarks>
    private static (int Amount, ObjectId SourceId, bool IsCombat)? DamageIn(GameEvent e) => e switch
    {
        PlayerDamaged hit => (hit.Amount, hit.SourceId, hit.IsCombat),
        DamageMarked marked => (marked.Amount, marked.SourceId, marked.IsCombat),
        _ => null,
    };

    /// <summary>The same damage event with a different amount, or nothing at all.</summary>
    /// <remarks>
    /// CR 614.7a: a source that would deal 0 damage deals none, so an amount reduced to zero is
    /// the event not happening rather than a zero-damage event. Returning the latter would let
    /// "whenever this deals damage" triggers fire off damage nobody took.
    /// </remarks>
    private static IReadOnlyList<GameEvent> Rescaled(GameEvent e, int amount)
    {
        if (amount <= 0)
            return [];

        return e switch
        {
            PlayerDamaged hit => [hit with { Amount = amount }],
            DamageMarked marked => [marked with { Amount = amount }],
            _ => [e],
        };
    }

    /// <summary>Whose damage a replacement watches, or null when the phrase is not read.</summary>
    /// <remarks>
    /// "A source" is every source there is, which is why it asks nothing about the object and does
    /// not require it to still exist — a spell that has finished resolving is gone by the time
    /// anything looks. The narrower phrases all need the object, and answer false without it,
    /// which errs towards doing nothing rather than towards doing it to everybody.
    /// </remarks>
    private static Func<GameState, GameObject, ObjectId, bool>? DamageDealer(string printed)
    {
        // "~" — this permanent's own damage, the one phrase that describes no group at all.
        if (string.Equals(printed, "~", StringComparison.Ordinal))
            return static (_, source, dealt) => dealt == source.Id;

        var m = DamageSourcePhrase().Match(printed);
        if (m.Success)
        {
            var spell = string.Equals(
                m.Groups["noun"].Value, "spell", StringComparison.OrdinalIgnoreCase);

            var yours = m.Groups["side"].Success;
            var other = m.Groups["scope"].Value.StartsWith(
                "another", StringComparison.OrdinalIgnoreCase);

            ManaColor? colour = null;
            if (m.Groups["adj"].Success)
            {
                // "A noncreature source", "an instant or sorcery source" — a description this
                // cannot ask about. Left unread rather than widened to every source, which would
                // be a strictly better card than the one printed.
                colour = ColorNamed(m.Groups["adj"].Value.Trim());
                if (colour is null)
                    return null;
            }

            if (!spell && !yours && !other && colour is null)
                return static (_, _, _) => true;

            return (state, source, dealt) =>
            {
                if (other && dealt == source.Id)
                    return false;

                if (!state.TryGetObject(dealt, out var dealer))
                    return false;

                // CR 109.5: a spell is a source only while it is on the stack, and that is the
                // whole difference between "a red spell" and "a red source".
                if (spell && dealer.Zone != Zone.Stack)
                    return false;

                var computed = Characteristics.Of(state, EmptyAbilities.Instance, dealer);

                if (colour is { } wanted && !computed.Colors.Contains(wanted))
                    return false;

                return !yours || computed.ControllerId == ControllerIn(state, source);
            };
        }

        // Anything else describes a permanent, and the target grammar already reads those: "a
        // creature you control", "a Wizard you control", "enchanted creature".
        var (spec, excludesSource) = GroupSpec(printed);
        if (spec is not { Kind: TargetKind.Permanent })
            return null;

        return (state, source, dealt) =>
            (!excludesSource || dealt != source.Id)
            && state.TryGetObject(dealt, out var dealer)
            && Answers(spec, state, source, dealer);
    }

    /// <summary>What the damage has to be dealt to, or null when the phrase is not read.</summary>
    private static Func<GameState, GameObject, GameEvent, bool>? DamageRecipient(string? printed)
    {
        // Nothing named, or the phrase that names every recipient there is.
        if (printed is null
            || string.Equals(printed, "a permanent or player", StringComparison.OrdinalIgnoreCase))
        {
            return static (_, _, _) => true;
        }

        // "An opponent or a permanent an opponent controls" is one question about two kinds of
        // recipient, and the target grammar has no phrase for it — a target is one or the other.
        // Eleven cards print it, so it is written out here rather than left unread.
        if (string.Equals(
            printed,
            "an opponent or a permanent an opponent controls",
            StringComparison.OrdinalIgnoreCase))
        {
            return static (state, source, e) =>
            {
                var mine = ControllerIn(state, source);

                return e switch
                {
                    PlayerDamaged hit => hit.PlayerId != mine,
                    DamageMarked marked =>
                        state.TryGetObject(marked.Id, out var hurt)
                        && hurt.Zone == Zone.Battlefield
                        && Characteristics.Of(state, EmptyAbilities.Instance, hurt).ControllerId
                            != mine,
                    _ => false,
                };
            };
        }

        var (spec, _) = GroupSpec(printed);
        if (spec is null)
            return null;

        return (state, source, e) => e switch
        {
            PlayerDamaged hit =>
                spec.Kind is TargetKind.Player or TargetKind.Any
                && (spec.PlayerFilter?.Invoke(state, hit.PlayerId, ControllerIn(state, source))
                    ?? true),

            DamageMarked marked =>
                spec.Kind is TargetKind.Permanent or TargetKind.Any
                && state.TryGetObject(marked.Id, out var hurt)
                && Answers(spec, state, source, hurt),

            _ => false,
        };
    }

    /// <summary>How much damage the sentence says is dealt instead, or null when unread.</summary>
    private static Func<int, int>? DamageScale(Match m)
    {
        if (m.Groups["times"].Success)
        {
            var factor = m.Groups["times"].Value.ToLowerInvariant() switch
            {
                "double" or "twice" => 2,
                "triple" => 3,
                _ => 0,
            };

            return factor == 0 ? null : amount => amount * factor;
        }

        // "Half that damage, rounded down" — integer division is the rounding the card names, and
        // rounding the other way is a different card, which is why the words are matched rather
        // than assumed.
        if (m.Groups["half"].Success)
            return static amount => amount / 2;

        var step = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);

        return m.Groups["dir"].Value.Equals("plus", StringComparison.OrdinalIgnoreCase)
            ? amount => amount + step
            : amount => amount - step;
    }

    /// <summary>
    /// "If one or more +1/+1 counters would be put on [a group], [that many, changed] instead."
    /// </summary>
    /// <remarks>
    /// CR 614.16 says this applies whether the counters come from a resolving spell, another
    /// replacement, or anything else — which is what makes one reader over the counter event
    /// enough, and why a creature that <em>enters</em> with counters gets the extra one too.
    /// <para>
    /// Only the two named kinds are read. The unqualified printing — "if one or more counters
    /// would be put on a permanent you control" — also catches a planeswalker's loyalty and a
    /// Saga's lore counters, and this cannot tell the card that means them from the card that does
    /// not, so it is left unread rather than guessed.
    /// </para>
    /// <para>
    /// The delta is required to be positive. A counter being <em>removed</em> is the same event
    /// with the sign flipped, and doubling it would take two counters off where the card says
    /// nothing at all.
    /// </para>
    /// </remarks>
    private static bool TryCounterAmount(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = CounterAmountLine().Match(line);
        if (!m.Success)
            return false;

        // The head and the tail name the same counter, and a card where they differed would be
        // saying something this cannot express.
        var kind = m.Groups["kind"].Value;
        if (!string.Equals(kind, m.Groups["kind2"].Value, StringComparison.OrdinalIgnoreCase))
            return false;

        // "…are put on it" / "on that creature" — the sentence pointing back at what it already
        // named. Anything else names a second permanent, which is a different effect.
        var where = m.Groups["where"].Value.Trim();
        if (!where.StartsWith("that ", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(where, "it", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Func<int, int>? adjust =
            (m.Groups["op"].Value.ToLowerInvariant(), m.Groups["less"].Success) switch
            {
                ("twice that many", false) => static many => many * 2,
                ("that many plus one", false) => static many => many + 1,
                ("that many", true) => static many => many - 1,
                _ => null,
            };

        if (adjust is null)
            return false;

        if (CounterGroup(m.Groups["group"].Value.Trim()) is not { } holds)
            return false;

        into.Add(new ReplacementEffectDefinition
        {
            Id = "counter-amount:" + kind.ToLowerInvariant()
                + ":" + m.Groups["group"].Value.Trim().ToLowerInvariant(),

            FunctionsFrom = Zone.Battlefield,
            Applies = (e, state, source) =>
                e is CountersChanged { Delta: > 0 } put
                && string.Equals(put.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && holds(state, source, put.Id),

            Replace = (e, _, _) =>
            {
                var put = (CountersChanged)e;
                var many = adjust(put.Delta);

                return many > 0 ? [put with { Delta = many }] : [];
            },
        });

        return true;
    }

    /// <summary>Which permanents a counter replacement watches, or null when it is not read.</summary>
    private static Func<GameState, GameObject, ObjectId, bool>? CounterGroup(string printed)
    {
        if (string.Equals(printed, "~", StringComparison.Ordinal))
            return static (_, source, id) => id == source.Id;

        var (spec, excludesSource) = GroupSpec(printed);
        if (spec is not { Kind: TargetKind.Permanent })
            return null;

        return (state, source, id) =>
            (!excludesSource || id != source.Id)
            && state.TryGetObject(id, out var held)
            && Answers(spec, state, source, held);
    }

    /// <summary>
    /// "If [somebody] would gain life, [they gain more, or none, or lose it] instead."
    /// </summary>
    /// <remarks>
    /// CR 119.3 makes this one event to watch, and the guard that matters is its sign: a life
    /// <em>loss</em> is the same event counted the other way, and a doubler that read it would
    /// double every payment and every drain the card says nothing about.
    /// <para>
    /// Who gains and who is spoken about afterwards have to agree. "If an opponent would gain
    /// life, <em>you</em> gain that much" is a different card from the one printed, and reading
    /// the two halves independently would have compiled it.
    /// </para>
    /// </remarks>
    private static bool TryLifeGainAmount(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = LifeGainAmountLine().Match(line);
        if (!m.Success)
            return false;

        var who = m.Groups["who"].Value.Trim().ToLowerInvariant();
        var mine = who is "you";

        // "You" in the second half means the player the first half named, and "that player" means
        // the one the event picked out. A sentence that swaps them says something else.
        if (mine != (m.Groups["subject"].Value.Trim().ToLowerInvariant() is "you"))
            return false;

        var losing = m.Groups["verb"].Value.StartsWith("lose", StringComparison.OrdinalIgnoreCase);
        var how = m.Groups["how"].Value.Trim().ToLowerInvariant();

        Func<int, int>? adjust = (losing, how) switch
        {
            (false, "twice that much life") => static much => much * 2,
            (false, "no life") => static _ => 0,
            (true, "that much life") => static much => -much,
            _ => null,
        };

        if (adjust is null && m.Groups["n"].Success && !losing)
        {
            var step = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            adjust = much => much + step;
        }

        if (adjust is null)
            return false;

        into.Add(new ReplacementEffectDefinition
        {
            Id = "life-gain-amount:" + who + ":" + how,
            FunctionsFrom = Zone.Battlefield,
            Applies = (e, state, source) =>
                e is LifeChanged { Delta: > 0 } gained
                && (who is "a player"
                    || (gained.PlayerId == ControllerIn(state, source)) == mine),

            Replace = (e, state, _) =>
            {
                var gained = (LifeChanged)e;
                var much = adjust(gained.Delta);

                // CR 119.3: a life total is adjusted by the amount, so the new total is read from
                // the player as they stand now rather than from the total the replaced event
                // carried — that one was worked out for a different number.
                return much == 0
                    ? []
                    : [new LifeChanged(
                        gained.PlayerId,
                        much,
                        state.GetPlayer(gained.PlayerId).Life + much)];
            },
        });

        return true;
    }

    /// <summary>
    /// "If you would roll one or more dice, instead roll that many dice plus one and ignore the
    /// lowest roll." — the grant-an-advantage replacement (CR 706.2b, 706.6).
    /// </summary>
    /// <remarks>
    /// Compared rather than matched, because every printing spells it exactly this way and there
    /// is no variable part. The replacement rewrites the roll request on its way into the log —
    /// one more die goes in, the settle keeps the highest and the ignored dice never happened,
    /// which is why the logged outcome is still one number. Two copies stack the way CR 614.5
    /// makes them: each applies once, each adds a die.
    /// </remarks>
    private static bool TryExtraDie(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        if (!line.Equals(
            "If you would roll one or more dice, instead roll that many dice plus one and"
                + " ignore the lowest roll.",
            StringComparison.Ordinal))
        {
            return false;
        }

        into.Add(new ReplacementEffectDefinition
        {
            Id = "extra-die",
            FunctionsFrom = Zone.Battlefield,
            Applies = (e, _, source) =>
                e is Events.DiceRollRequested roll && roll.PlayerId == source.ControllerId,
            Replace = (e, _, _) =>
            {
                var roll = (Events.DiceRollRequested)e;
                return [roll with { ExtraDice = roll.ExtraDice + 1 }];
            },
        });

        return true;
    }

    /// <summary>
    /// "If [something] would die, exile it instead" — a death replacement (CR 614.1a).
    /// </summary>
    /// <remarks>
    /// CR 700.4 defines "dies" as being put into a graveyard <em>from the battlefield</em>, which
    /// is the whole difference from the sentence its neighbour <see cref="TryGraveyardReplacement"/>
    /// reads: that one says "from anywhere" and catches a card milled, discarded or countered as
    /// well. Reading this one as "from anywhere" would take those too, on a card that says nothing
    /// about them.
    /// <para>
    /// The destination alternation is the same three the printed cards use, and nothing else: a
    /// destination this does not know leaves the line unread rather than defaulting to exile,
    /// which is the harshest of the three and would be wrong on two of the four cards.
    /// </para>
    /// </remarks>
    private static bool TryDiesReplacement(
        string line, ImmutableList<ReplacementEffectDefinition>.Builder into)
    {
        var m = DiesReplacementLine().Match(line);
        if (!m.Success)
            return false;

        var where = m.Groups["where"].Value.ToLowerInvariant();
        var bottom = where.Contains("bottom", StringComparison.Ordinal);
        var library = bottom || where.Contains("library", StringComparison.Ordinal);

        var who = m.Groups["who"].Value.Trim();
        Func<GameState, GameObject, ObjectId, bool> dying;

        if (string.Equals(who, "~", StringComparison.Ordinal))
        {
            dying = static (_, source, id) => id == source.Id;
        }
        else
        {
            var (spec, excludesSource) = GroupSpec(who);
            if (spec is not { Kind: TargetKind.Permanent })
                return false;

            dying = (state, source, id) =>
                (!excludesSource || id != source.Id)
                && state.TryGetObject(id, out var doomed)
                && Answers(spec, state, source, doomed);
        }

        into.Add(new ReplacementEffectDefinition
        {
            Id = "dies-to:" + (library ? bottom ? "library-bottom" : "library-top" : "exile")
                + ":" + who.ToLowerInvariant(),

            FunctionsFrom = Zone.Battlefield,
            Applies = (e, state, source) =>
                e is ObjectMoved { From: Zone.Battlefield, To: Zone.Graveyard } gone
                && dying(state, source, gone.OldId),

            Replace = (e, _, _) =>
            {
                var gone = (ObjectMoved)e;

                return
                [
                    gone with
                    {
                        To = library ? Zone.Library : Zone.Exile,
                        Position = bottom ? ZonePosition.Bottom : ZonePosition.Top,
                    },
                ];
            },
        });

        return true;
    }

    /// <summary>The controller of the permanent whose ability is asking, after layer 2 (CR 613.1b).</summary>
    /// <remarks>
    /// Every reader above needs "you", and "you" is whoever controls the ability now — not whoever
    /// controlled it when the permanent arrived. A stolen doubler doubles its new controller's
    /// damage, and reading the stored controller would have it still working for the player it was
    /// taken from.
    /// </remarks>
    private static Guid ControllerIn(GameState state, GameObject source) =>
        Characteristics.Of(state, EmptyAbilities.Instance, source).ControllerId;

    /// <summary>
    /// A printed group phrase read as a target spec, with the article and "another" lifted off.
    /// </summary>
    /// <remarks>
    /// The target grammar is the shared vocabulary for "which permanents", and a replacement wants
    /// exactly that question without the word "target" in front of it. "Another" is not part of
    /// the description — it is a fact about the source — so it comes out here and is answered by
    /// the caller, which is the same split the mass-static reader makes.
    /// </remarks>
    private static (TargetSpec? Spec, bool ExcludesSource) GroupSpec(string printed)
    {
        var m = GroupPhrase().Match(printed);
        if (!m.Success)
            return (null, false);

        var excludes = m.Groups["scope"].Value.StartsWith(
            "another", StringComparison.OrdinalIgnoreCase);

        return (EffectPhrase.Specs.Parse("target " + m.Groups["what"].Value.Trim()), excludes);
    }

    /// <summary>
    /// Whether a permanent answers a target phrase, without any of the targeting rules.
    /// </summary>
    /// <remarks>
    /// The phrase vocabulary is reused for the group a replacement watches; the <em>targeting</em>
    /// half of it deliberately is not. Hexproof and shroud stop a permanent being chosen
    /// (CR 702.11b, 702.18b) and a replacement effect chooses nothing, so asking
    /// <see cref="TargetSpec.IsLegal"/> here would exempt a hexproof creature from Furnace of Rath
    /// — which is not a rule anywhere.
    /// </remarks>
    private static bool Answers(
        TargetSpec spec, GameState state, GameObject source, GameObject subject)
    {
        if (subject.Zone != Zone.Battlefield)
            return false;

        var controller = ControllerIn(state, source);

        return (spec.ObjectFilter?.Invoke(
                state, EmptyAbilities.Instance, subject, controller) ?? true)
            && (spec.SourceFilter?.Invoke(
                state, EmptyAbilities.Instance, subject, source, controller) ?? true);
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
        string line,
        ImmutableList<ReplacementEffectDefinition>.Builder into,
        ImmutableList<ContinuousEffectDefinition>.Builder statics,
        bool hasPlainKicker,
        ImmutableList<KickerOption>.Builder kickerCosts)
    {
        var m = KickedCountersLine().Match(line);
        if (!m.Success)
            return false;

        // CR 607.2: the clause is linked to a kicker printed on the same card. The plain form
        // needs a kicker at all; "with its {1}{U} kicker" needs that exact cost on offer, in the
        // exact spelling the payment was recorded under (CR 702.33f). The keyword lines always
        // come first on the cards that print these, so the guard can be asked mid-loop.
        var cost = m.Groups["cost"].Success ? m.Groups["cost"].Value.ToUpperInvariant() : null;

        if (cost is null
            ? !hasPlainKicker && kickerCosts.Count == 0
            : !kickerCosts.Any(k =>
                string.Equals(k.Printed, cost, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        Func<GameObject, bool> paid = cost is null
            ? source => source.WasKicked
            : source => source.KickedWith.Contains(cost, StringComparer.OrdinalIgnoreCase);

        // "... and with flying", "... and with \"Pay 3 life: Regenerate this creature.\"" — the
        // volvers' other half: an ability the permanent has because of how it was cast. Not an
        // until-end-of-turn grant but a fact-conditioned static in layer 6 (CR 613.1f), because
        // the flag rides the permanent for as long as it lives (CR 607.2). Refused whole when
        // the gift is neither a grantable keyword nor a quoted ability the compiler reads — half
        // the printed line is not the card.
        Action<CharacteristicsBuilder>? gift = null;
        if (m.Groups["quote"].Success)
        {
            if (!TryQuotedAbility(
                m.Groups["quote"].Value, out var grantedActivated, out var grantedTriggers))
            {
                return false;
            }

            gift = builder =>
            {
                builder.GrantedActivated.AddRange(grantedActivated);
                builder.GrantedTriggers.AddRange(grantedTriggers);
            };
        }
        else if (m.Groups["kw"].Success)
        {
            if (EffectPhrase.Keywords(m.Groups["kw"].Value) is not { } keywords)
                return false;

            gift = builder => builder.Keywords |= keywords;
        }

        var count = NumberWordOrDigits(m.Groups["n"].Value);

        into.Add(new ReplacementEffectDefinition
        {
            Id = cost is null ? "kicked-counters" : "kicked-counters-" + cost,
            FunctionsFrom = null,
            Applies = (e, _, source) => Arriving(e, source) is not null && paid(source),
            Replace = (e, _, source) =>
            [
                e,
                new CountersChanged(
                    Arriving(e, source)!.Value, CounterKinds.PlusOnePlusOne, count),
            ],
        });

        if (gift is { } granting)
        {
            statics.Add(new ContinuousEffectDefinition
            {
                Id = cost is null ? "kicked-gift" : "kicked-gift-" + cost,
                Layer = EffectLayer.Ability,
                Applies = (state, source, builder) =>
                    source is not null
                    && builder.Subject.Id == source.Id
                    && paid(builder.Subject),
                Apply = (_, _, builder) => granting(builder),
            });
        }

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
        var m = MultikickedCountersLine().Match(line);
        if (!m.Success)
            return false;

        // "Two +1/+1 counters ... for each time" — the same replacement with a rate. A spell
        // kicked with both of an "and/or" pair's costs has been kicked twice (CR 702.33d), and
        // the count was carried across the move exactly as multikicker's is (CR 607.2).
        var per = NumberWordOrDigits(m.Groups["n"].Value);

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
                    per * source.TimesKicked),
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
            // "Activate only as a sorcery" is a phase restriction and belongs in the same field
            // it does on every other ability. It was refused here for the same reason the cap
            // was, and with the same result.
            if (ReadActivationRestrictions(gated.Groups["when"].Value.Trim()) is not { } read)
                return false;

            timing = read.Timing;
            onlyIf = read.OnlyIf;
            manaLimit = read.Limit ?? manaLimit;

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

    /// <summary>
    /// A Licid: a creature that turns itself into an Aura and can turn back (CR 205.1b, 613.1d).
    /// </summary>
    /// <remarks>
    /// One printed line doing four things at once — "{cost}, {T}: ~ loses this ability and becomes
    /// an Aura enchantment with enchant creature. Attach it to target creature. You may pay {cost}
    /// to end this effect." — and every piece of it already existed separately: layer 4 replaces
    /// the card types, <see cref="AttachSourceTo"/> puts the permanent on its host, and a floating
    /// effect with no duration runs until something ends it.
    /// <para>
    /// What is built is <strong>two</strong> abilities and one effect, which is the shape the
    /// reverse payment forces. The going-out half is an ordinary activated ability; the coming-back
    /// half is another one, charging the printed price and ending the effect the first made. They
    /// exclude each other through <see cref="ActivatedAbilityDefinition.ActivateOnlyIf"/>, which is
    /// how "loses this ability" is honoured: a Licid that is already an Aura cannot be activated to
    /// go and enchant something else, and one that is still a creature has nothing to come back
    /// from. Both questions are asked before any cost is paid (CR 602.5b), so a refusal is free.
    /// </para>
    /// <para>
    /// Reading "loses this ability" as a refusal to activate rather than as a layer-6 removal is a
    /// deliberate narrowing. The two are the same for an activated ability — an ability nobody may
    /// ever activate is an ability the permanent does not have — and the alternative on offer,
    /// <see cref="ContinuousEffectDefinition.RemovesAllAbilities"/>, removes far too much: it would
    /// take "enchanted creature has flying" off the Aura as well, which is the whole of what the
    /// card is for once it has attached.
    /// </para>
    /// <para>
    /// Coming back unattaches it, and that is not tidying up. The Aura's static ability is what
    /// grants the host its keyword, and a Licid that turned back into a creature while still
    /// attached would go on granting it — a strictly better card than the printed one, which is the
    /// one direction this may not fail in.
    /// </para>
    /// </remarks>
    private static bool TryLicid(string line, ImmutableList<ActivatedAbilityDefinition>.Builder into)
    {
        var m = LicidLine().Match(line);
        if (!m.Success)
            return false;

        // Mana and a tap, and nothing else. Every printed Licid costs exactly that, and a cost
        // this could not charge would be an ability given away for free.
        if (ReadCost(m.Groups["cost"].Value.Trim()) is not
            {
                SelfCost: SelfCost.None, Life: 0, Energy: 0, Counters: null,
            } paid
            || !paid.Chosen.IsEmpty)
        {
            return false;
        }

        if (EffectPhrase.Specs.Parse(m.Groups["what"].Value.Trim()) is not
            { Kind: TargetKind.Permanent } host)
        {
            return false;
        }

        var becoming = GenerativeEffects.BecomesAuraId();
        var back = ManaCostSpec.Parse(m.Groups["end"].Value);

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "a" + Suffix(into.Count),
            Text = line,
            RequiresTap = paid.RequiresTap,
            ManaCost = paid.Mana,
            Targets = [host],
            ActivateOnlyIf = (state, _, source) => !IsWearingTheAura(state, source),
            Effects =
            [
                new AttachSourceTo(0),

                // No duration: the effect runs until it is paid off. CR 611.2b - an effect that
                // says nothing about when it ends does not end.
                new PumpUntilEndOfTurn(becoming, Subject: EffectSubject.Source)
                {
                    ForTheTurn = false,
                },
            ],
        });

        into.Add(new ActivatedAbilityDefinition
        {
            Id = "a" + Suffix(into.Count),
            Text = $"You may pay {back} to end this effect.",
            ManaCost = back,
            ActivateOnlyIf = (state, _, source) => IsWearingTheAura(state, source),
            Effects = [new EndSourceEffect(becoming), new UnattachSource()],
        });

        return true;
    }

    /// <summary>Whether the Licid's own effect is currently making it an Aura (CR 611.2).</summary>
    private static bool IsWearingTheAura(GameState state, GameObject source) =>
        state.FloatingEffects.Any(f =>
            string.Equals(f.DefinitionId, GenerativeEffects.BecomesAuraId(), StringComparison.Ordinal)
            && f.AffectedIds.Contains(source.Id));

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
            // "Activate only if you control a Plains" is a question about the board rather than
            // about the phase, so it lands beside the timing rule instead of in it — and a line
            // may carry both at once, which is what the conjunction reader is for.
            if (ReadActivationRestrictions(restricted.Groups["when"].Value.Trim()) is not { } read)
            {
                // A restriction that cannot be honoured must not be dropped: an ability with no
                // rule where the card prints one is a strictly better card.
                unhandled.Add(line);
                return true;
            }

            timing = read.Timing;
            onlyIf = read.OnlyIf;
            limit = read.Limit ?? limit;

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
            // "Sacrifice two creatures" is the same cost counted, and the noun is singularised for
            // the same reason the tapping cost singularises its own: the target grammar names one
            // thing and how many is carried by the cost rather than by the words.
            var scope = sacrifice.Groups["scope"].Value;
            var howMany = scope.StartsWith('a') ? 1 : NumberWordOrDigits(scope);

            var noun = sacrifice.Groups["what"].Value.Trim();
            if (howMany > 1)
                noun = EffectPhrase.Singular(noun);

            if (SacrificeSpec(noun) is not { } what)
                return null;

            chosen.Add(new ChosenCost(
                ChosenCostKind.SacrificePermanents,
                howMany,
                what,
                ExcludesSource: scope.StartsWith("another", StringComparison.OrdinalIgnoreCase)));

            Lift(SacrificeChosenCost());
        }

        var returned = ReturnChosenCost().Match(remaining);
        if (returned.Success)
        {
            var count = returned.Groups["n"].Value;
            var howMany = count.StartsWith('a') ? 1 : NumberWordOrDigits(count);

            var noun = returned.Groups["what"].Value.Trim();
            if (howMany > 1)
                noun = EffectPhrase.Singular(noun);

            if (EffectPhrase.Specs.Parse("target " + noun) is not
                { Kind: TargetKind.Permanent } bounced)
            {
                return null;
            }

            chosen.Add(new ChosenCost(ChosenCostKind.ReturnToHand, howMany, bounced));
            Lift(ReturnChosenCost());
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

            var many = discard.Groups["n"].Value;

            chosen.Add(new ChosenCost(
                discard.Groups["random"].Success
                    ? ChosenCostKind.DiscardAtRandom
                    : ChosenCostKind.DiscardCards,
                Count: many.StartsWith('a') ? 1 : NumberWordOrDigits(many),
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

    /// <summary>What one "Activate only …" sentence restricts, however many clauses it has.</summary>
    /// <param name="Timing">The phase restriction, or <see cref="ActivationTiming.AnyTime"/>.</param>
    /// <param name="Limit">How often each turn, or null for no cap.</param>
    /// <param name="OnlyIf">The board question that has to answer yes, or null.</param>
    private readonly record struct ActivationRestrictions(
        ActivationTiming Timing,
        int? Limit,
        Func<GameState, IAbilitySource, GameObject, bool>? OnlyIf);

    /// <summary>
    /// "Activate only as a sorcery and only once each turn" — one sentence, two rules
    /// (CR 602.5b, 602.5d).
    /// </summary>
    /// <remarks>
    /// Each half of the conjunction reads perfectly well on its own and the join did not, so 92
    /// corpus lines carrying both went unread over the word "and". They are folded rather than
    /// alternated because the three things a clause can be — a phase, a cap, a board question —
    /// already live in three separate fields on the ability, and a card may print any two of them
    /// in either order.
    /// <para>
    /// Null when any clause names something that cannot be kept, which leaves the whole line
    /// unread: an ability that honours one of its two restrictions is a strictly better card than
    /// the one printed, and nothing downstream would notice. "Activate only … and only once" —
    /// nine corpus lines — is refused for exactly that reason: bare "once" is once per
    /// <em>game</em>, and <see cref="ActivatedAbilityDefinition.MaxActivationsPerTurn"/> can only
    /// say once per turn, which would hand the card an activation every turn after the first.
    /// </para>
    /// </remarks>
    private static ActivationRestrictions? ReadActivationRestrictions(string when)
    {
        var timing = ActivationTiming.AnyTime;
        int? limit = null;
        Func<GameState, IAbilitySource, GameObject, bool>? onlyIf = null;

        foreach (var clause in ActivationConjunction().Split(when))
        {
            var part = clause.Trim().TrimEnd('.');
            if (part.Length == 0)
                return null;

            if (ActivationLimitClause().Match(part) is { Success: true } capped)
            {
                // Two caps in one sentence is a shape no card prints, and folding them by taking
                // the smaller would be inventing a rule rather than reading one.
                if (limit is not null)
                    return null;

                limit = NumberWord(capped.Groups["n"].Value);
                continue;
            }

            if (TimingNamed(part) is { } named)
            {
                if (timing != ActivationTiming.AnyTime)
                    return null;

                timing = named;
                continue;
            }

            if (part.StartsWith("if ", StringComparison.OrdinalIgnoreCase)
                && BoardConditions.Parse(part[3..]) is { } asked)
            {
                // Both questions have to answer yes, which is what "and" means. Anded here rather
                // than kept as a list because everything downstream asks one predicate.
                var earlier = onlyIf;
                onlyIf = earlier is null
                    ? asked
                    : (state, abilities, self) =>
                        earlier(state, abilities, self) && asked(state, abilities, self);

                continue;
            }

            return null;
        }

        return new ActivationRestrictions(timing, limit, onlyIf);
    }

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
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
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

    /// <summary>
    /// A trigger's "when" clause, including the two-condition form (CR 603.1).
    /// </summary>
    /// <remarks>
    /// "Whenever an enchantment you control enters <em>and whenever</em> you fully unlock a Room"
    /// is one ability with two conditions, and an ability with two conditions fires on either of
    /// them. The join is read here rather than inside the shared condition grammar because it is a
    /// fact about the <em>line</em> — two whole clauses spliced together — and each half is then
    /// handed to that grammar unchanged, so nothing about the vocabulary is restated to support it.
    /// <para>
    /// Both halves have to read or the line does not. Keeping the half that parsed would build a
    /// card that triggers on less than it prints, which is quieter than an unread line and no
    /// more correct.
    /// </para>
    /// </remarks>
    private static Func<GameEvent, GameState, TriggerSource, bool>? ReadTriggerCondition(
        string when)
    {
        var joined = TwoTriggerConditions().Match(when);
        if (!joined.Success)
            return ReadOneTriggerCondition(when);

        if (ReadOneTriggerCondition(joined.Groups["first"].Value.Trim()) is not { } first
            || ReadOneTriggerCondition(joined.Groups["second"].Value.Trim()) is not { } second)
        {
            return null;
        }

        return (e, state, source) => first(e, state, source) || second(e, state, source);
    }

    /// <summary>One clause of a trigger condition.</summary>
    /// <remarks>
    /// The shared grammar answers first and answers almost all of it. What is left is the one
    /// wording the corpus prints <em>only</em> inside the two-condition join above — 17 lines
    /// carry it and not one card prints it standing alone — which makes it part of reading that
    /// line shape rather than a piece of the general vocabulary.
    /// </remarks>
    private static Func<GameEvent, GameState, TriggerSource, bool>? ReadOneTriggerCondition(
        string clause)
    {
        if (TriggerConditions.Parse(clause) is { } known)
            return known;

        // Compared rather than matched, because there is nothing here to parse: every one of the
        // 17 lines spells it exactly this way and there is no variable part. A pattern would only
        // add a shape the line-shape audit then has to be told to ignore.
        if (!clause.Equals("you fully unlock a Room", StringComparison.OrdinalIgnoreCase))
            return null;

        return (e, state, source) =>
        {
            if (e is not HalfUnlocked opening
                || !state.TryGetObject(opening.Id, out var room)
                || room.Permanent is not { } standing)
            {
                return false;
            }

            // "*You* fully unlock a Room": only a permanent's controller may unlock its doors
            // (CR 709.5e), so the Room's controller is the player who did it and no event records
            // it separately.
            if (room.ControllerId != source.ControllerId)
                return false;

            // A Room and not merely a permanent with halves, which is what the card says. The
            // test is the compiler's own: every face is a Room (CR 709.5).
            if (room.Card.Faces.Count == 0
                || !room.Card.Faces.All(f => f.Subtypes.Contains("Room", StringComparer.Ordinal)))
            {
                return false;
            }

            // CR 709.5i: fully unlocking is getting the *last* designation, not any of them — a
            // Room with one half already open is fully unlocked by the second, and one with
            // neither only when it has both. A trigger reads the state as it was *before* the
            // event (the Room's own door triggers beside this one turn on the same fact), so the
            // door being opened is added to the set before it is counted rather than found in it.
            var doors = source.Abilities.HalvesOf(room.Card);

            return doors.Count > 1
                && standing.UnlockedHalves.Add(opening.Half).Count == doors.Count;
        };
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

        var predicate = ReadTriggerCondition(m.Groups["when"].Value.Trim());

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

    /// <remarks>
    /// A subtype in the phrase — "is a Golem creature with base power and toughness 5/4" — leaves
    /// the line unread rather than being dropped: nothing here validates a printed word against
    /// the type table, and a creature quietly missing the type its Aura names is the shape of
    /// mistake this compiler has already paid for twice.
    /// </remarks>
    [GeneratedRegex(
        @"^(enchanted|equipped) " + AttachedSubject
            + @" is an? creature with base power and toughness (?<p>\d+)/(?<tough>\d+) "
            + @"in addition to its other types\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedAnimationLine();

    /// <summary>
    /// "Enchanted creature loses all abilities and has base power and toughness 1/1" (CR 613.1f).
    /// </summary>
    /// <remarks>
    /// The tail is captured whole on either side of the clause and handed back to the attached
    /// readers rather than enumerated here, so this pattern never has to know what an Aura can
    /// say next to it.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<subject>(enchanted|equipped) " + AttachedSubject + ")"
            + @"( (?<before>.+?) and)? loses all abilities( and (?<after>.+?))?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedLosesAllAbilitiesLine();

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

            // Phasing (CR 702.26a), which is a keyword here rather than a flag on the card
            // because effects grant it - "enchanted permanent has phasing" - and the untap step
            // asks for the computed answer.
            ["Phasing"] = KeywordAbility.Phasing,

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

    /// <summary>"Cleave {1}{B}{B}" (CR 702.148a), with the reminder text already stripped.</summary>
    [GeneratedRegex(@"^Cleave (?<cost>(?:\{[^}]+\})+)$")]
    private static partial Regex CleaveLine();

    /// <summary>
    /// A bracketed span and the space before it, so removing "[with flying]" from "you control
    /// [with flying]." leaves "you control." rather than "you control .".
    /// </summary>
    [GeneratedRegex(@"\s*\[[^\]\[]*\]")]
    private static partial Regex BracketedWords();

    /// <summary>"Gift a card", "Gift a tapped Fish" (CR 702.174a), reminder text stripped.</summary>
    [GeneratedRegex(@"^Gift (?<what>an? [^.(]+?)\s*$")]
    private static partial Regex GiftLine();

    /// <summary>"A. If the gift was promised, instead B." — a swap, not an addition.</summary>
    [GeneratedRegex(
        @"(?<base>[^.\n]+)\. If the gift was promised, (?:instead (?<b1>[^.\n]+)|(?<b2>[^.\n]+) instead)\.")]
    private static partial Regex GiftInstead();

    /// <summary>"Then if the gift was promised and C, X." — half the condition is settled at cast.</summary>
    [GeneratedRegex(@"\s*Then if the gift was promised and (?<kept>[^\n]+?\.)")]
    private static partial Regex GiftCompoundIf();

    [GeneratedRegex(@"\s*If the gift was promised, (?<then>[^.\n]+)\.")]
    private static partial Regex GiftIf();

    [GeneratedRegex(@"\s*If the gift wasn't promised, (?<then>[^.\n]+)\.")]
    private static partial Regex GiftIfNot();

    [GeneratedRegex(@"\s*(?<effect>[^.\n]+) if the gift was promised\.")]
    private static partial Regex GiftTrailingIf();

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

    /// <remarks>
    /// The keyword alone, or closing a keyword list — "Flying, soulbond" is the one list the
    /// corpus prints. The lead is captured whole and validated in code against the same table
    /// <see cref="TryKeywordList"/> reads, rather than alternated here.
    /// </remarks>
    [GeneratedRegex(@"^(?:(?<lead>[a-z][a-z ,']*), )?soulbond\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SoulbondLine();

    /// <remarks>
    /// The condition is fixed words rather than a captured clause, because every one of the 24
    /// cards prints exactly this condition — it names the status, not a question about the
    /// board. The keyword list is lower-case on purpose: "protection from Zombies" fails the
    /// match and leaves the line unread, which is the whole-or-nothing rule every keyword list
    /// here follows.
    /// </remarks>
    [GeneratedRegex(
        @"^[Aa]s long as ~ is paired with another creature, "
            + @"(?:both creatures have (?<kw>[a-z ,]+?)"
            + @"|each of those creatures (?:gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)"
            + @"|has ""(?<ability>[^""]+)""))\.?$",
        RegexOptions.None)]
    private static partial Regex SoulbondStaticLine();

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
    /// <remarks>
    /// The number runs to ten rather than to five, which is where it stopped: Savage Firecat
    /// enters with seven counters, Spike Hatcher and Surge Node with six, and the shared number
    /// reader had understood every one of those words all along. The counter kind is any
    /// power/toughness name rather than the two the layers used to know, now that they read the
    /// name (CR 122.1c). "On her" and "on him" because a card whose creature has a gender says
    /// so, and Big Bertha and Michelangelo were unread for the pronoun alone.
    /// </remarks>
    [GeneratedRegex(
        @"^(modular (?<n>\d+)|graft (?<n>\d+)"
            + @"|(If (?<given>[^,.]+), )?~ enters( tapped)? with "
            + @"(?<n>\d+|X|a|an|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"(?<kind>[+-]\d+/[+-]\d+|[a-z]+) counters? on (it|her|him)"
            + @"( if (?<when>[^.]+))?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntersWithCountersLine();

    /// <summary>
    /// "…enters with a +1/+1 counter on it for each [group]" (CR 614.1c), and its two synonyms.
    /// </summary>
    /// <remarks>
    /// One pattern rather than three readers, because the three branches differ only in how the
    /// card spells the number and all of them end in a group this compiler counts the same way.
    /// The last two name no multiplier, so <c>n</c> does not capture and the shared number reader
    /// answers one - which is what "X, where X is the number of" means.
    /// </remarks>
    [GeneratedRegex(
        @"^~ enters( tapped)? with ("
            + @"(?<n>\d+|a|an|one|two|three|four|five|six|seven|eight|nine|ten) "
            + @"(?<kind>[+-]\d+/[+-]\d+|[a-z]+) counters? on (it|her|him) "
            + @"for each (?<group>[^.]+)"
            + @"|X (?<kind>[+-]\d+/[+-]\d+|[a-z]+) counters? on (it|her|him), "
            + @"where X is the number of (?<group>[^.]+)"
            + @"|a number of (?<kind>[+-]\d+/[+-]\d+|[a-z]+) counters? on (it|her|him) "
            + @"equal to the number of (?<group>[^.]+)"
            + @")\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntersWithCountersPerGroupLine();

    /// <summary>
    /// "Each other [group] you control enters with an additional +1/+1 counter on it"
    /// (CR 614.1d).
    /// </summary>
    /// <remarks>
    /// A leading scope word is required, and that is what keeps the noun phrase's capitals
    /// meaningful: a capital is how this compiler tells a creature type from an ordinary word,
    /// and a phrase that started the sentence would be capitalised by position alone. "Legendary
    /// creatures you control enter with…" is the one corpus line that says it without a scope
    /// word, and reading it here would look for a creature type called "Legendary".
    /// <para>
    /// Bounded to three words and no punctuation so that a trailing clause the reader cannot
    /// honour — "of the chosen type", "that's a Wolf or a Werewolf", "for each Angel you already
    /// control" — fails the match rather than being dropped off the end.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<scope>Each other|Each|Other|All) (?<what>[A-Za-z'\-]+(?: [A-Za-z'\-]+){0,2}) "
            + @"you control enters? with an additional (?<kind>\+1/\+1|loyalty) "
            + @"counter on (?:it|them)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GroupEntersWithAdditionalCounterLine();

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
    /// <remarks>
    /// The subject is lifted out and put back in front of each clause, so the pattern only has to
    /// say where the subject ends. The tail must contain a join for the fold to have anything to
    /// do — a line with no "and" and no comma is one clause, and one clause is what every other
    /// matcher has already refused.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<subject>(enchanted|equipped) " + AttachedSubject + @") (?<rest>.+?(,| and ).+?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConjoinedAttachedLine();

    /// <summary>What joins two clauses about the same subject, kept so a span can be rebuilt.</summary>
    [GeneratedRegex(@"(, and |, | and )")]
    private static partial Regex ClauseJoin();

    /// <remarks>
    /// One word only. "Is a black Zombie in addition to its other colors and types" names a colour
    /// as well and says "colors and types" rather than "types", so it does not match here and is
    /// left unread — a Zombie that is quietly not black is a different card from the printed one.
    /// </remarks>
    [GeneratedRegex(
        @"^(enchanted|equipped) " + AttachedSubject
            + @" is an? (?<what>[A-Za-z][A-Za-z'-]*) in addition to its other types\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttachedTypeAdditionLine();

    [GeneratedRegex(
        @"^(enchanted|equipped) " + AttachedSubject + " "
            + @"(gets (?<p>[+-]\d+)/(?<tough>[+-]\d+)"
            + @"( and (has (?<kw>[a-z0-9{} ,]+?)"
            + @"|can't (?<cant>attack or block|attack|block|be blocked)"
            + @"(?<silenced>,? and its activated abilities can't be activated)?"
            + @"|(?<must>attacks each combat if able)))?"
            + @"|has base power and toughness (?<basep>\d+)/(?<baset>\d+)"
            + @"|has (?<kw>[a-z0-9{} ,]+?)"
            + @"|can't (?<cant>attack or block|attack|block|be blocked)"
            + @"(?<silenced>,? and its activated abilities can't be activated)?"
            + @"|(?<must>attacks each combat if able)"
            + @"|(?<goaded>is goaded))\.?$",
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
        //
        // "Gets an additional +1/+0" is the wording a card uses when its base bonus is printed
        // a sentence earlier - "Equipped creature gets +2/+0. As long as equipped creature is a
        // Human, it gets an additional +1/+0." The word changes nothing about what the effect
        // does: every 7c modification is additional (CR 613.4c), so it is admitted and the
        // numbers are read as they stand.
        @"^(?:[Dd]uring (?<cond>your turn), (?<subject>~|[Ee]nchanted [a-z]+|[Ee]quipped [a-z]+) "
                + @"(gets (an additional )?(?<p>[+-]\d+)/(?<tough>[+-]\d+)" + BUFF
                + @"|has (?<kw>[a-z ,]+))"
            + @"|[Aa]s long as (?<cond>[^,]+), "
                + @"(?<subject>~|it|[Ee]nchanted [a-z]+|[Ee]quipped [a-z]+) "
                + @"(gets (an additional )?(?<p>[+-]\d+)/(?<tough>[+-]\d+)" + BUFF
                + @"|has (?<kw>[a-z ,]+)"
                + @"|can't (?<cant>attack or block|attack|block|be blocked))"
            + @"|(?<subject>~|[Ee]nchanted [a-z]+|[Ee]quipped [a-z]+) "
                + @"(gets (an additional )?(?<p>[+-]\d+)/(?<tough>[+-]\d+)" + BUFF
                + @"|has (?<kw>[a-z ,]+?)"
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

    /// <remarks>
    /// Anchored on the whole conjunction rather than on " and ", because " and " joins parts of
    /// one clause at least as often as it joins two of them — "whenever this creature enters or
    /// attacks and you control a Sliver" is one condition. The second "whenever" is what says
    /// two abilities' worth of condition are being spliced onto one ability.
    /// </remarks>
    [GeneratedRegex(@"^(?<first>.+?) and whenever (?<second>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TwoTriggerConditions();

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
    /// A lord names its group four ways — "Other Goblin creatures you control", just "Other
    /// Goblins you control", a noun that is not a tribe at all ("Creature tokens", "Other
    /// permanents"), and an adjective in front of one ("White creatures", "Attacking creatures").
    /// The noun phrase is captured whole and handed to <see cref="ReadStaticGroup"/> rather than
    /// alternated here, because a noun list restated in a pattern is a list that drifts from the
    /// shared one — and this one had drifted into a bug. The alternation this replaced picked the
    /// tribe reading off a capital letter, so every sentence-initial type word became a creature
    /// subtype: "Artifact creatures you control get +1/+1" compiled to a lord for the creature
    /// type "Artifact", read as complete, and buffed nothing.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<scope>all|other|each other|each)?\s*"
            + @"(?<noun>[A-Za-z]+(?:\s+[a-z]+){0,3}?)"
            + @"(?<side>\s+you control|\s+your opponents control|\s+an opponent controls"
            + @"|\s+enchanted player controls)?"
            + @"(\s+of the chosen (?<chosen>type|color))?"
            // The counter arm is named so the any-counter form can be told from the named one,
            // and the name closes *before* the alternation - a group spanning both arms reports
            // success for "with flying" as well, and the any-counter reader would then demand a
            // counter of every keyword-qualified lord. That is exactly what it did: "Other
            // creatures you control with flying get +2/+2" buffed nothing, because every flier
            // was asked for a counter it did not have.
            + @"((?<withcounter>\s+with (an? )?((?<counter>[+-]\d/[+-]\d) )?counters? on (it|them))"
            + @"|\s+with (?<needs>[a-z ]+?))?\s+"
            + @"(gets? (?<p>[+-]\d+)/(?<tough>[+-]\d+)"
            + @"( and (has|have) (?<kw>[a-z0-9{} ,]+?)( and (?<must>attacks? each combat if able))?)?"
            + @"|(has|have) base power and toughness (?<basep>\d+)/(?<baset>\d+)"
            + @"|(has|have) (?<kw>[a-z0-9{} ,]+?)( and (?<must>attacks? each combat if able))?"
            + @"|(?<lose>loses? all abilities)"
            + @"( and (has|have) base power and toughness (?<basep>\d+)/(?<baset>\d+))?"
            + @"|(?<must>attacks? each combat if able))\.?$",
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
            + @"\s+((?i:gets?|have|has) (?<p>[+-]\d+)/(?<tough>[+-]\d+) and )?"
            + @"ha(s|ve) ""(?<ability>[^""]+)""\.?$",
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
    /// No dot inside either group, which is what refuses every exception carrying a quoted
    /// ability — "except it has \"{X}: This creature has base power and toughness X/X.\"" ends a
    /// sentence inside the quotation marks, and a granted ability is not something an exception
    /// clause can express here. Those lines stay unread rather than compiling to a copy that
    /// quietly lacks the ability.
    /// </remarks>
    [GeneratedRegex(
        @"^You may have ~ enter (?<tapped>tapped )?as a copy of (?<what>[^.]+?)"
            + @"(?:, except (?<except>[^.]+))?\.$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntersAsACopyLine();

    /// <remarks>
    /// "It's a Faerie Shapeshifter in addition to its other types <b>and</b> it has flying" is
    /// two clauses; so is "it isn't legendary, is an artifact …, <b>and</b> has myriad". A clause
    /// list this splits wrongly produces a part that matches nothing, which refuses the line.
    /// </remarks>
    [GeneratedRegex(@",\s*and\s+|,\s*|\s+and\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionClauses();

    [GeneratedRegex(@"^(?:it\s+)?isn't legendary$", RegexOptions.IgnoreCase)]
    private static partial Regex NotLegendaryClause();

    [GeneratedRegex(@"^it's (?<p>\d+)/(?<t>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SetSizeClause();

    [GeneratedRegex(
        @"^(?:it's|it is|is) (?<types>.+?) in addition to its other (?:card |creature )?types$",
        RegexOptions.IgnoreCase)]
    private static partial Regex InAdditionClause();

    [GeneratedRegex(@"^(?:it\s+)?has (?<kw>[a-z][a-z ]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex HasKeywordClause();

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
    /// A Licid's whole ability, all three sentences of it (CR 205.1b, 613.1d).
    /// </summary>
    /// <remarks>
    /// Written as one pattern rather than composed out of the sentence vocabulary because the
    /// three sentences are not independent: the second attaches what the first turned into an
    /// Aura, and the third ends what the first started. Read separately, "attach it to target
    /// creature" is an Equipment's trigger and "you may pay {W} to end this effect" names an
    /// effect that no longer exists. The twelve printed Licids write it identically apart from the
    /// two costs.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<cost>[^:]{1,60}): ~ loses this ability and becomes an Aura enchantment with "
            + @"enchant creature\. Attach it to (?<what>target [a-z' ]{1,40})\. "
            + @"You may pay (?<end>(\{[^}]{1,4}\}){1,6}) to end this effect\.$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LicidLine();

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

    /// <summary>One clause of a restriction sentence, as a cap on how often (CR 602.5b).</summary>
    /// <remarks>
    /// The whole-sentence form above keeps its own pattern: it is anchored to the end of the line
    /// and lifted before anything else reads it, while this one is handed a clause that has
    /// already been cut out. The count is required here where the other makes it optional,
    /// because a clause reading only "each turn" is not a printed phrase and defaulting it to
    /// once would be reading a rule the card does not say.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<n>once|twice|three times) each turn$", RegexOptions.IgnoreCase)]
    private static partial Regex ActivationLimitClause();

    /// <summary>What joins two restrictions in one "Activate only …" sentence (CR 602.5b).</summary>
    [GeneratedRegex(@"\s+and only\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ActivationConjunction();

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

    /// <summary>How a printed count is written, wherever a cost names one.</summary>
    /// <remarks>
    /// One alternation shared by the cost readers rather than a different list in each, which is
    /// how "exile six cards from your graveyard" came to be unread while five was fine: the
    /// counts had been written out per pattern and each stopped somewhere different.
    /// </remarks>
    private const string CountWords = "one|two|three|four|five|six|seven|eight|nine|ten|[0-9]+";

    [GeneratedRegex(
        @",?\s*sacrifice (?<scope>another|an?|" + CountWords + @")\s+(?<what>[a-z ]+?)\s*(,|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex SacrificeChosenCost();

    [GeneratedRegex(
        @",?\s*discard (?<n>an?|" + CountWords + @") (?<what>[a-z ]+? )?cards?"
            + @"(?<random> at random)?\s*(,|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiscardChosenCost();

    /// <summary>"Return a permanent you control to its owner's hand" — a cost, not an effect.</summary>
    /// <remarks>
    /// Ninjutsu's cost written out, and the only chosen cost that takes a permanent without
    /// spending it (CR 702.49a). It reads a count for the same reason its neighbours do; the
    /// additional-cost family prints only one, and the activated abilities beside it print more.
    /// </remarks>
    [GeneratedRegex(
        @",?\s*return (?<n>an?|" + CountWords + @")\s+(?<what>[A-Za-z' ]+?)"
            + @" to (its|their) owner'?s? hand\s*(,|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReturnChosenCost();

    [GeneratedRegex(
        @",?\s*exile (?<n>an?|" + CountWords + @") "
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

    /// <remarks>
    /// The separator is a space or an em dash because the cardboard uses both: Scryfall prints
    /// "Flashback {2}{R}" when the cost is mana alone and "Flashback\u2014{1}{U}, Pay 3 life."
    /// when it is not. The mana group is still required, so the em dash cannot let in
    /// "Flashback\u2014Sacrifice a Mountain", whose cost this permission has no way to charge.
    /// </remarks>
    [GeneratedRegex(
        @"^flashback[ \u2014](?<cost>(\{[^}]+\})+)(, Pay (?<life>\d+) life)?\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FlashbackLine();

    /// <summary>"Harmonize {X}{R}{R}" (CR 702.180a).</summary>
    [GeneratedRegex(@"^harmonize (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex HarmonizeLine();

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

    /// <summary>The level up keyword and what it costs (CR 711.2a).</summary>
    [GeneratedRegex(@"^Level up (?<cost>(\{[^}]+\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LevelUpLine();

    /// <summary>A leveler's level symbol: "LEVEL 2-4" or "LEVEL 5+" (CR 711.3).</summary>
    /// <remarks>
    /// Printed in capitals on every leveler ever made, and matched case-insensitively anyway
    /// because both ends are anchored: no sentence in the corpus is the word "level" and a range.
    /// </remarks>
    [GeneratedRegex(@"^LEVEL (?<from>[0-9]+)(?:-(?<to>[0-9]+)|\+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LevelBandLine();

    /// <summary>A bare printed size, which is a line of its own under a level symbol.</summary>
    [GeneratedRegex(@"^(?<p>[0-9]+)/(?<t>[0-9]+)$", RegexOptions.None)]
    private static partial Regex PrintedSize();

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

    /// <remarks>
    /// Each of the three is anchored whole. "Draft" in particular is a word that turns up inside
    /// real abilities — "as you draft a card, you may reveal it" is a triggered ability of a card
    /// being drafted — and a loose match would swallow one.
    /// </remarks>
    [GeneratedRegex(
        @"^(~ can be your commander"
            + @"|A deck can have any number of cards named ~"
            + @"|Draft ~ face up)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DeckConstructionLine();

    /// <remarks>
    /// The condition leads and the shield follows, which is the only order the cards print for
    /// this family — "During your turn, prevent …", "As long as you control a permanent of each
    /// color, prevent …". The trailing form ("… to ~ during your turn") is deliberately not read
    /// here: it would have to be cut out of the middle of the noun the shield is about, and the
    /// one card printing it is blocked by two other lines anyway.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<cond>[Dd]uring [^,]+|[Aa]s long as [^,]+), (?<rest>[Pp]revent all .+)$")]
    private static partial Regex ConditionalPreventionLine();

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

    /// <summary>A replacement that changes how much damage a source deals (CR 614.1a).</summary>
    /// <remarks>
    /// The arithmetic is alternated inside the line rather than lifted into a pattern of its own,
    /// which keeps this a whole-line shape a behaviour test can play. It is also the refusal: a
    /// sentence whose "instead" clause is anything other than these — "it deals that damage to its
    /// controller", "put that many -1/-1 counters on that creature" — does not match at all.
    /// </remarks>
    [GeneratedRegex(
        @"^If (?<src>.+?) would deal (?<qual>combat|noncombat)? ?damage"
            + @"(?: to (?<rec>.+?))?, (?:it|that source) deals "
            + @"(?:(?<times>double|triple|twice) that (?:much )?damage"
            + @"|(?<half>half) that damage, rounded down"
            + @"|that much damage (?<dir>plus|minus) (?<n>\d+))"
            + @"(?:,? to (?<again>[^,]+?))? instead\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageAmountLine();

    /// <summary>"A red source you control", "another source", "a spell" — whose damage it is.</summary>
    [GeneratedRegex(
        @"^(?<scope>an?|any|another) (?<adj>[a-z]+ )?(?<noun>source|spell)"
            + @"(?<side> you control)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DamageSourcePhrase();

    /// <summary>A replacement that changes how many counters are put on something (CR 614.16).</summary>
    [GeneratedRegex(
        @"^If one or more (?<kind>\+1/\+1|-1/-1) counters would be put on (?<group>.+?), "
            + @"(?<op>twice that many|that many plus one|that many) "
            + @"(?<kind2>\+1/\+1|-1/-1) counters(?<less> minus one)? are put on "
            + @"(?<where>[^,]+?) instead\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CounterAmountLine();

    /// <summary>A replacement that sends a dying permanent somewhere else (CR 700.4).</summary>
    [GeneratedRegex(
        @"^If (?<who>.+?) would die, (?<where>exile it"
            + @"|put it on (the )?(top|bottom) of its owner's library) instead\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiesReplacementLine();

    /// <summary>A replacement that changes how much life a player gains (CR 119.3).</summary>
    [GeneratedRegex(
        @"^If (?<who>you|an opponent|a player) would gain life, "
            + @"(?<subject>you|that player) (?<verb>gains?|loses?) "
            + @"(?<how>that much life plus (?<n>\d+)|twice that much life|that much life|no life)"
            + @" instead\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex LifeGainAmountLine();

    /// <summary>A group phrase with its article, as a replacement's subject clause prints it.</summary>
    [GeneratedRegex(
        @"^(?:(?<scope>another|an?|any|each|all) )?(?<what>[A-Za-z',/+\- ]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex GroupPhrase();

    /// <summary>An alternative cost with no keyword in front of it (CR 118.9).</summary>
    /// <remarks>
    /// Both of the phrasings CR 118.9 names, in one pattern because they are one rule: "you may
    /// [action] rather than pay [this object's] mana cost" and "you may cast [this object]
    /// without paying its mana cost". The second captures no cost, because the cost it names is
    /// nothing at all.
    /// </remarks>
    [GeneratedRegex(
        @"^(?:If (?<when>[^,]+), )?you may (?:pay (?<cost>(\{[^}]+\})+) "
            + @"rather than pay ~'s mana cost"
            + @"|(?<paid>.+?) rather than pay ~'s mana cost"
            + @"|cast ~ without paying its mana cost)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeManaCostLine();

    [GeneratedRegex(@"^pay (?<cost>(\{[^}]+\})+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeManaPart();

    [GeneratedRegex(@"^pay (?<life>\d+) life$", RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeLifePart();

    [GeneratedRegex(
        @"^(?<verb>sacrifice|tap) (?<n>a|an|two|three|four|five) (?:untapped )?(?<what>.+?)$"
            + @"|^(?<verb>return) (?<n>a|an|two|three|four|five) (?<what>.+?)"
            + @" to (?:its|their) owner'?s'? hand$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeGivingPart();

    [GeneratedRegex(
        @"^(?<verb>exile|discard) (?<n>a|an|two|three|four|five) (?<what>.+?)"
            + @"(?: from your (?<zone>hand|graveyard))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AlternativeCardPart();

    [GeneratedRegex(@"^kicker (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex KickerLine();

    /// <summary>"Kicker {1}{U} and/or {B}" — two kicker abilities on one line (CR 702.33b).</summary>
    [GeneratedRegex(
        @"^kicker (?<a>(\{[^}]+\})+) and/or (?<b>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex KickerAndOrLine();

    /// <summary>"This spell costs {2} less to cast if it's bargained." (CR 601.2f, 702.166b).</summary>
    [GeneratedRegex(
        @"^~ costs \{(?<n>\d+)\} less to cast if it's bargained\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BargainDiscountLine();

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

    /// <summary>
    /// "Sneak [cost]" (CR 702.190a).
    /// </summary>
    /// <remarks>
    /// Anchored on the whole line so that "Sneak Attack &#8212; whenever this creature attacks,
    /// ..." - an ability word that happens to begin with the same word - is not read as the
    /// keyword and cast for one mana.
    /// </remarks>
    [GeneratedRegex(@"^Sneak (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex SneakLine();

    /// <summary>
    /// "Awaken N&#8212;[cost]" (CR 702.113a).
    /// </summary>
    /// <remarks>
    /// The separator is an em dash on all fifteen printings, written as its code point rather
    /// than typed so that a hyphen cannot pass for it in an editor - and a hyphen is accepted
    /// beside it for the same reason reinforce accepts one.
    /// </remarks>
    [GeneratedRegex(
        @"^Awaken (?<n>\d+)\s*[\u2014\u2015-]\s*(?<cost>(\{[^}]+\})+)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AwakenLine();

    [GeneratedRegex(@"^Conspire\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ConspireLine();

    [GeneratedRegex(@"^Casualty (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CasualtyLine();

    [GeneratedRegex(@"^Backup (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupLine();

    [GeneratedRegex(@"^Bestow (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex BestowLine();

    /// <summary>"Mutate {cost}" (CR 702.140a), once the reminder text has been stripped.</summary>
    [GeneratedRegex(@"^Mutate (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex MutateLine();

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

    [GeneratedRegex(
        @"^choose (?<n>one|two|three|four|five)\. You may choose the same mode more than once\.$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RepeatableModalHeader();

    /// <summary>"Choose one. If this spell was kicked, choose any number instead." (CR 700.2d).</summary>
    [GeneratedRegex(
        @"^choose one\. If (~|this spell|it) was (?:(?<team>cast using teamwork)|kicked), "
            + @"choose (?<what>both|any number) instead\.$",
        RegexOptions.IgnoreCase)]
    private static partial Regex FactModalHeader();

    /// <summary>"Choose up to four. You may choose the same mode more than once." (CR 700.2d).</summary>
    [GeneratedRegex(
        @"^choose up to (?<n>one|two|three|four|five)\."
            + @"( (?<repeat>You may choose the same mode more than once)\.)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UpToModalHeader();

    /// <summary>"Choose X. You may choose the same mode more than once." (CR 601.2b).</summary>
    [GeneratedRegex(
        @"^choose X\.( (?<repeat>You may choose the same mode more than once)\.)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChooseXHeader();

    [GeneratedRegex(
        @"^choose one\. If you control a commander as you cast ~, you may choose both instead\.$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CommanderModalHeader();

    [GeneratedRegex(@"^Spree$", RegexOptions.IgnoreCase)]
    private static partial Regex SpreeLine();

    [GeneratedRegex(@"^\+ (?<cost>(\{[^}]+\})+) [—–\-] (?<mode>.+)$")]
    private static partial Regex SpreeBullet();

    [GeneratedRegex(@"^Emerge (?<cost>(\{[^}]+\})+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex EmergeLine();

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

    /// <summary>"Ravenous" (CR 702.156a). Printed alone; the reminder text carries the rest.</summary>
    [GeneratedRegex(@"^Ravenous\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RavenousLine();

    /// <summary>"Ripple N" (CR 702.60a).</summary>
    [GeneratedRegex(@"^Ripple (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RippleLine();

    /// <summary>"Read ahead" (CR 702.155a). Printed alone on every Saga that has it.</summary>
    [GeneratedRegex(@"^Read ahead\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadAheadLine();

    /// <summary>
    /// "Hideaway N", or the one card that prints two of them on a line (CR 702.75a).
    /// </summary>
    /// <remarks>
    /// The repeat is captured rather than alternated so that both numbers are read: a group that
    /// matched only the first would compile "Hideaway 3, hideaway 3" as one instance, which is
    /// half of what the card does and reads as a complete line.
    /// </remarks>
    [GeneratedRegex(
        @"^Hideaway (?<n>\d+)(?:, hideaway (?<n>\d+))*\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex HideawayLine();

    /// <summary>"Teamwork N" (CR 702.194a).</summary>
    [GeneratedRegex(@"^Teamwork (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex TeamworkLine();

    /// <summary>
    /// "Amplify N" (CR 702.38a).
    /// </summary>
    /// <remarks>
    /// Anchored on the whole line so the word "Amplify" beginning a sentence somewhere else
    /// cannot be read as the keyword. A card printing two instances prints them on two lines,
    /// and each is read separately and added.
    /// </remarks>
    [GeneratedRegex(@"^Amplify (?<n>\d+)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex AmplifyLine();

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

    /// <remarks>
    /// One pattern for every cell of the spell half of the cost grid, because the cells differ
    /// by two words each and splitting them into four patterns is four places to disagree about
    /// what "spells" means. <c>who</c> absent is the bare "Noncreature spells cost {1} more to
    /// cast", which taxes its own controller too (CR 601.2f).
    /// <para>
    /// Everything the anchors reject is rejected on purpose: "Spells your opponents cast
    /// <em>that target this creature</em> cost {2} more" is a condition on the spell rather than
    /// a filter on the card, and "spells you cast from <em>anywhere other than your hand</em>" is
    /// not a zone (CR 400.1). Both would have to be read as the unconditional form, which is a
    /// better card than the printed one.
    /// </para>
    /// </remarks>
    [GeneratedRegex(
        @"^(?<what>[A-Za-z ]+?)? ?spells(?<who> you cast| your opponents cast)?"
            + @"(?: from your (?<zone>graveyard|hand|exile))? cost \{(?<n>\d+)\} "
            + @"(?<dir>less|more) to cast\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpellCostModifierLine();

    /// <summary>
    /// "Spells your opponents cast that target ~ cost {2} more to cast" (CR 601.2c, 601.2f).
    /// </summary>
    /// <remarks>
    /// The condition the pattern above refuses, admitted on the one phrase that names something
    /// a modifier can ask about: the permanent printing it. Every other "that target …" wording
    /// in the corpus names a description - a creature you control, one or more commanders you
    /// control, a creature - and reading any of those here would need a filter vocabulary over
    /// chosen targets. "That target it", printed by a card talking about itself on the stack, is
    /// refused too: the spell being taxed and the object being targeted are the same object
    /// there, which is a different question from this one.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<what>[A-Za-z ]+?)? ?spells(?<who> you cast| your opponents cast)?"
            + @" that target ~ cost \{(?<n>\d+)\} (?<dir>less|more) to cast\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TargetingSpellCostModifierLine();

    /// <remarks>
    /// "Activated abilities of Foods you control cost {1} less to activate" (CR 602.2b). Only
    /// the word <em>Activated</em> opens this: "Loyalty abilities", "Equip abilities" and
    /// "Exhaust abilities" name a kind of ability rather than a kind of permanent, and
    /// <see cref="CostModifier.FilterId"/> is asked about the card the ability sits on.
    /// </remarks>
    [GeneratedRegex(
        @"^Activated abilities of (?<what>[A-Za-z ]+?)(?<whose> you control)? cost "
            + @"\{(?<n>\d+)\} (?<dir>less|more) to activate\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AbilityCostModifierLine();

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

    /// <summary>
    /// The two sentences the colour-fixing lands print as one line, captured separately so each
    /// goes back to the reader that already owns it.
    /// </summary>
    [GeneratedRegex(
        @"^(?<tapped>~ enters tapped\.) (?<choice>As (~|it) enters, choose a [a-z ]+\.)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntersTappedChoosingLine();

    [GeneratedRegex(
        @"^If damage would be dealt to ~, prevent that damage\. "
            + @"Remove a \+1/\+1 counter from ~\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PhantomDamageLine();

    /// <remarks>
    /// One pattern for the plain flag and the "and/or" pair's per-cost form (CR 702.33f), and
    /// for the volvers' tail: "and with flying", "and with \"Pay 3 life: Regenerate this
    /// creature.\"". The quoted alternative keeps its period inside the quotes, which is why the
    /// closing anchor cannot simply be <c>\.?$</c>.
    /// </remarks>
    [GeneratedRegex(
        @"^If ~ was kicked(?: with its (?<cost>(?:\{[^}]+\})+) kicker)?, "
            + @"it enters with (?<n>a|an|one|two|three|four|five|[0-9]+) "
            + @"[+]1/[+]1 counters? on it"
            + @"(?: and with (?:""(?<quote>[^""]+)""|(?<kw>[a-z][a-z ']*[a-z])))?[.]?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex KickedCountersLine();

    [GeneratedRegex(
        @"^~ enters with (?<n>a|an|one|two|three|four|five|[0-9]+) "
            + @"[+]1/[+]1 counters? on it for each time it was kicked[.]?$",
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

    /// <summary>
    /// The deck-construction rules this card prints, exactly as printed.
    /// </summary>
    /// <remarks>
    /// Partner's siblings: "~ can be your commander" (CR 903.3a), "a deck can have any number of
    /// cards named ~" (CR 100.2a) and "draft ~ face up" (CR 905.2c) are all settled before a game
    /// starts and none of them does anything during one, so reading them is reading them —
    /// there is nothing to build.
    /// <para>
    /// <strong>"If ~ is in your opening hand, you may begin the game with it on the battlefield"
    /// is deliberately not one of these.</strong> CR 103.6 is a real game rule and the Leylines
    /// genuinely start in play; swallowing it here would file eighteen cards as understood while
    /// silently removing the only thing they do.
    /// </para>
    /// </remarks>
    public ImmutableList<string> DeckRules { get; init; } = [];

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

    /// <summary>
    /// The cleaved reading — this card's text with the bracketed words removed (CR 702.148a).
    /// </summary>
    /// <remarks>
    /// A cleave card is one card carrying two spells, exactly as an adventurer card is, and it
    /// gets the same answer: each reading is compiled as a card of its own, and which one is on
    /// the stack is decided as it is cast. Held as a compiled spell rather than as edited text
    /// because CR 702.148b calls the removal a text-changing effect, and the engine's texts are
    /// code — the change has to happen at compile time or not at all.
    /// </remarks>
    public SpellDefinition? CleaveSpell { get; init; }

    /// <summary>What the cleaved cast costs, exactly as printed (CR 702.148a).</summary>
    public string? CleaveCostRaw { get; init; }

    /// <summary>
    /// The promised reading of an instant or sorcery with gift (CR 702.174).
    /// </summary>
    /// <remarks>
    /// The promise is made as the spell is cast (CR 702.174k) and settles three things at once:
    /// the delivery happens, the "if the gift was promised" sentences run, and their targets are
    /// chosen (CR 702.174m says only then). All three are questions about which spell is on the
    /// stack, so the card is compiled twice the way a cleave card is — <see cref="Spell"/> is
    /// the unpromised reading, this is the promised one, and the delivery is its first effect
    /// because CR 702.174j puts the gift before everything else the spell does.
    /// </remarks>
    public SpellDefinition? GiftSpell { get; init; }

    /// <summary>
    /// Whether this card offers a gift as it is cast (CR 702.174a).
    /// </summary>
    /// <remarks>
    /// True for permanents as well as spells, which is why it is not simply
    /// <c>GiftSpell is not null</c>: a permanent's delivery is a compiled enters trigger rather
    /// than a second spell, and the cast still has to know the offer exists to accept a chosen
    /// opponent.
    /// </remarks>
    public bool HasGift { get; init; }

    /// <summary>What a prepared permanent's spell costs, exactly as printed.</summary>
    public string? PreparedCostRaw { get; init; }

    /// <summary>
    /// How many +1/+1 counters each creature sacrificed to devour is worth (CR 702.81a).
    /// </summary>
    public int DevourCount { get; init; }

    /// <summary>
    /// How many +1/+1 counters each card revealed to amplify is worth (CR 702.38a).
    /// </summary>
    public int AmplifyCount { get; init; }

    /// <summary>
    /// Whether this Saga starts at a chapter its controller picks (CR 702.155b).
    /// </summary>
    /// <remarks>
    /// True only when the chapters themselves compiled: a Saga started at a chapter that does
    /// nothing is a Saga that walks to its own sacrifice, which is worse than one left unread.
    /// </remarks>
    public bool HasReadAhead { get; init; }

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

    /// <summary>
    /// What this permanent does to somebody's spell or ability costs (CR 601.2f, 602.2b).
    /// </summary>
    /// <remarks>
    /// This replaced <c>CostReducers</c> rather than joining it. A card that emitted both would
    /// be read down both of <c>Game.ModifiersOn</c>'s paths and discounted twice, and a doubled
    /// discount is exactly the kind of quietly-wrong game that is hardest to notice from a test.
    /// </remarks>
    public ImmutableList<CostModifier> CostModifiers { get; init; } = [];

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
        || !CostModifiers.IsEmpty
        || ShowsTopOfLibrary
        || RemovesHandLimit
        || ChoosesOnEntry != ChoiceOnEntry.None
        || DevourCount > 0
        || AmplifyCount > 0
        || HasReadAhead
        || ExtraLandDrops > 0
        || MayDeclineUntap
        || SkipsDrawStep
        || RevealsTopOfLibrary
        || AttacksOnlyIfDefenderControls is not null;
}
