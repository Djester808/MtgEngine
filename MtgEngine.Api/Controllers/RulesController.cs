using Microsoft.AspNetCore.Mvc;

namespace MtgEngine.Api.Controllers;

// ---- DTOs ---------------------------------------------------

public sealed record KbKeyword(
    string Name,
    string Status,       // "implemented" | "partial" | "stub"
    string Description,
    string RulesRef);

public sealed record KbStep(string Name, string Description);

public sealed record KbMechanic(
    string Name,
    string Description,
    KbStep[]? Steps = null);

public sealed record KbSba(string RulesRef, string Description, string Status);

public sealed record KbDto(
    KbKeyword[] Keywords,
    KbMechanic[] Mechanics,
    KbSba[] StateBasedActions);

// ---- Controller ---------------------------------------------

[ApiController]
[Route("api/[controller]")]
public sealed class RulesController : ControllerBase
{
    // GET /api/rules
    [HttpGet]
    public ActionResult<KbDto> Get() => Ok(_kb);

    // GET /api/rules/keywords
    [HttpGet("keywords")]
    public ActionResult<KbKeyword[]> GetKeywords() => Ok(_kb.Keywords);

    // GET /api/rules/mechanics
    [HttpGet("mechanics")]
    public ActionResult<KbMechanic[]> GetMechanics() => Ok(_kb.Mechanics);

    // GET /api/rules/sba
    [HttpGet("sba")]
    public ActionResult<KbSba[]> GetSba() => Ok(_kb.StateBasedActions);

    // ---- Static KB data -------------------------------------

    private static readonly KbDto _kb = new(
        Keywords:
        [
            new("Flying",        "implemented", "Can only be blocked by creatures with flying or reach.",                                                                   "CR 702.9"),
            new("Reach",         "implemented", "Can block creatures with flying.",                                                                                        "CR 702.17"),
            new("First Strike",  "implemented", "Deals combat damage before creatures without first strike. Participates only in the first-strike damage step.",            "CR 702.7"),
            new("Double Strike", "implemented", "Deals combat damage in both the first-strike and regular combat damage steps.",                                            "CR 702.4"),
            new("Trample",       "implemented", "Excess damage beyond lethal for each blocker carries over to the defending player. Interacts correctly with deathtouch.",  "CR 702.19"),
            new("Deathtouch",    "implemented", "Any amount of damage this creature deals is enough to destroy the creature it damages. 1 damage counts as lethal.",        "CR 702.2"),
            new("Lifelink",      "implemented", "Damage dealt by this creature causes its controller to gain that much life simultaneously.",                               "CR 702.15"),
            new("Vigilance",     "implemented", "Attacking does not cause this creature to tap.",                                                                           "CR 702.20"),
            new("Haste",         "implemented", "This creature can attack and activate tap abilities the turn it enters the battlefield.",                                  "CR 702.10"),
            new("Indestructible","implemented", "Effects that say 'destroy' don't destroy this permanent. Lethal damage doesn't destroy it.",                              "CR 702.12"),
            new("Flash",         "implemented", "You may cast this spell any time you could cast an instant, overriding the sorcery-speed restriction.",                   "CR 702.8"),
            new("Menace",        "partial",     "This creature can't be blocked except by two or more creatures. Validation skeleton exists; full enforcement pending.",    "CR 702.110"),
            new("Hexproof",      "stub",        "This permanent can't be the target of spells or abilities your opponents control. Defined but not yet enforced in targeting.", "CR 702.11"),
            new("Shroud",        "stub",        "This permanent can't be the target of spells or abilities. Defined but not yet enforced.",                                 "CR 702.18"),
            new("Protection",    "stub",        "This permanent can't be damaged, enchanted, equipped, blocked, or targeted by anything with the stated quality.",          "CR 702.16"),
            new("Ward",          "stub",        "Whenever this permanent becomes the target of an opponent's spell or ability, counter it unless they pay an additional cost.", "CR 702.21"),
        ],

        Mechanics:
        [
            new("Turn Structure",
                "A full turn consists of five phases (Beginning, Pre-Combat Main, Combat, Post-Combat Main, Ending) divided into twelve steps. Steps execute in order; each has entry and exit actions before the next begins.",
                Steps:
                [
                    new("Untap",               "All permanents the active player controls untap. Summoning sickness is cleared. No priority is granted."),
                    new("Upkeep",              "Priority window. Triggered abilities that trigger 'at the beginning of upkeep' would go on the stack here."),
                    new("Draw",                "Active player draws a card. Skipped by the first player on turn 1. Drawing from an empty library is tracked for loss condition."),
                    new("Main (Pre/Post)",     "Priority phase. Active player may play one land per turn and cast sorcery-speed spells. Flash spells may also be cast."),
                    new("Beginning of Combat", "Priority window before attackers are declared."),
                    new("Declare Attackers",   "Active player declares which creatures attack and which player or planeswalker they attack. Taps attackers unless they have vigilance."),
                    new("Declare Blockers",    "Defending player assigns blockers. Flying and reach are enforced. Multiple blockers per attacker are supported."),
                    new("First Strike Damage", "Combat damage for first-strike and double-strike creatures. Skipped entirely if no such creatures are in combat."),
                    new("Combat Damage",       "Regular combat damage for all creatures (and double-strike creatures again). Trample, deathtouch, and lifelink are resolved here."),
                    new("End of Combat",       "Priority window. Combat state is preserved until cleanup."),
                    new("End Step",            "Priority window before cleanup. Triggered abilities that trigger 'at the beginning of your end step' go here."),
                    new("Cleanup",             "Active player discards to maximum hand size (7). Damage is cleared from all permanents. Mana pools empty."),
                ]),

            new("Zones",
                "All seven Magic zones are modelled. Zone transitions are validated (e.g. you can only play a land from hand). The Command zone is defined but not used in Phase 1.",
                Steps:
                [
                    new("Library",    "Shuffled at game start. Cards are drawn from the top."),
                    new("Hand",       "Cards drawn go here. Maximum 7 at end of turn (cleanup)."),
                    new("Battlefield","Permanents live here. Creatures, lands, planeswalkers, artifacts, enchantments."),
                    new("Graveyard",  "Destroyed and sacrificed permanents, and resolved instants/sorceries, go here."),
                    new("Exile",      "Permanents removed from the game. Tracked but distinct from graveyard."),
                    new("Stack",      "LIFO stack for spells and abilities. Both players must pass with an empty stack to advance the step."),
                    new("Command",    "Defined in the zone enum. Not yet used in Phase 1 game rules."),
                ]),

            new("Mana System",
                "ManaCost and ManaPool are immutable value objects. Colored pips are satisfied first; generic mana can be paid with any color. Mana pools empty at end of each step.",
                Steps:
                [
                    new("Tap for Mana",   "Basic lands tap to add one mana of their color to the pool ({T}: Add {C})."),
                    new("Pay Cost",       "CanBePaidBy() validates the pool before deducting. Colored requirements checked before generic."),
                    new("Undo Tap",       "An untapped land can have its mana activation reversed within the same priority window."),
                    new("Pool Clearing",  "Mana pools empty at the end of every step and phase (cleanup step)."),
                ]),

            new("Stack & Priority",
                "A simplified two-player priority model. The active player receives priority first each step. Both players passing with a non-empty stack resolves the top object; both passing with an empty stack advances the step.",
                Steps:
                [
                    new("Cast Spell",        "Spell goes to the stack. Mana is paid at cast time. Flash overrides sorcery-speed restriction."),
                    new("Pass Priority",     "Passing transfers priority to the opponent. When both pass consecutively the engine either resolves the top of stack or advances the step."),
                    new("Resolve",           "Top of stack resolves LIFO. Permanents enter the battlefield; instants/sorceries go to the graveyard. State-based actions run after each resolution."),
                    new("Activated Ability", "Mana abilities resolve immediately without using the stack. Other activated abilities go on the stack (Phase 2)."),
                ]),

            new("Combat",
                "Full combat phase implementation with attacker and blocker declaration, blocker ordering, and damage assignment. All first-strike, trample, deathtouch, and lifelink interactions are handled.",
                Steps:
                [
                    new("Declare Attackers", "Creatures must be untapped and free of summoning sickness (or have haste). They tap on declaration unless they have vigilance."),
                    new("Declare Blockers",  "Defenders assign blockers. Non-flying creatures cannot block flyers unless they have reach. Multiple blockers per attacker are legal."),
                    new("Blocker Order",     "When multiple creatures block one attacker, the attacking player assigns a damage order for the blockers."),
                    new("Damage Assignment", "Damage assigned in blocker order. Trample carries overflow damage to the player. Deathtouch makes 1 damage lethal. Lifelink triggers life gain simultaneously."),
                ]),

            new("State-Based Actions",
                "Checks defined in CR 704 run after every game action and loop until no more SBAs apply. They are not spells or abilities and cannot be responded to."),

            new("Permanents & Counters",
                "Permanents track tapped/untapped state, summoning sickness, marked damage, deathtouch-damage flag, and a dictionary of counter types. Effective power/toughness = base + net +1/+1 and -1/-1 counters.",
                Steps:
                [
                    new("+1/+1 Counters",   "Increase effective power and toughness by 1 each."),
                    new("-1/-1 Counters",   "Decrease effective power and toughness by 1 each."),
                    new("Loyalty Counters", "Used by planeswalkers. A planeswalker at 0 loyalty is put into the graveyard by SBA."),
                    new("Poison Counters",  "Track poison on players. 10 or more is a loss condition (SBA CR 704.5c)."),
                    new("Other Counters",   "Charge, Fade, Time, Age, Feather, Lore, and Verse counters are tracked but not yet mechanically relevant."),
                ]),
        ],

        StateBasedActions:
        [
            new("CR 704.5a",  "A player with life total 0 or less loses the game.",                                           "implemented"),
            new("CR 704.5c",  "A player with 10 or more poison counters loses the game.",                                     "implemented"),
            new("CR 704.5f",  "A creature with toughness 0 or less is put into its owner's graveyard.",                      "implemented"),
            new("CR 704.5g",  "A creature with damage marked on it equal to or greater than its toughness is destroyed.",     "implemented"),
            new("CR 704.5g†", "A creature dealt damage by a creature with deathtouch is destroyed, regardless of toughness.", "implemented"),
            new("CR 704.5i",  "An Aura attached to an illegal permanent is put into its owner's graveyard.",                  "stub"),
            new("CR 704.5j",  "If a player controls two or more legendary permanents with the same name, that player chooses one and the rest go to the graveyard.", "implemented"),
            new("CR 704.5q",  "A planeswalker with loyalty 0 is put into its owner's graveyard.",                            "implemented"),
        ]);
}
