# E5g — an Aura that enchants a land: the engine is right, the scenario aims at the wrong card

Card: `cards/706ae742-1807-44b7-a4fa-f2e26f61519a.json` ("enchant land", verify says
`AttachedToIs was not attached, in Graveyard`).

## What is not wrong

CR 704.5m is not the cause, and neither is the query's land filter. Four front-door tests in
`tests/MtgEngine.Rules.Tests/StateBasedActionTests.LandAuras.cs` play real games on
`Subsystems.Wave2()` through the scenario runner:

- a made-up Aura with `Enchant, Of: {types:[Land]}` cast at a land on the battlefield **is
  attached and stays attached** through the settle;
- the same Aura placed already attached to a land **survives** the state-based action check;
- an Aura with `Of: {types:[Creature]}` attached to a land **is still buried** by 704.5m.

`StateBasedActions.AttachedLegally` (`MtgEngine.Rules/Sba/StateBasedActions.Permanents.cs:200`)
and `Query.Matches` read the land filter correctly. Nothing in `Sba/` or `Views/` was changed.

## What is wrong: the smoke board's target name is claimed by a card in hand

The fourth test is the smoke scenario's board in miniature and reproduces the graveyard exactly.
The only difference from the passing first test is three `Scenario Filler Land` in hand.

1. `MtgEngine.Rules/Scenarios/ScenarioRunner.cs:116` places the **hand** before the battlefield
   (`ScenarioRun.Build`), and `ScenarioRun.Name` at line 381 is first-wins:
   `if (!_objects.ContainsKey(key)) _objects[key] = id;`.
2. The smoke board for an "enchant land" Aura places the host with
   `SmokeBoard.Aimed` (`MtgEngine.Cards.Pipeline/Scenarios/SmokeBoard.cs:66`), which places the
   card **under its plain name** and adds that same plain name to the targets, via
   `SmokeScenario.Enchant` (`MtgEngine.Cards.Pipeline/Scenarios/SmokeScenario.cs:338`) — and the
   smoke hand is stocked with filler **lands**.
3. So `CastStep(..., targets: ["Scenario Filler Land"])` names the copy in **hand**. The Aura is
   announced at a card that is not on the battlefield, and CR 608.2b counters it as it resolves
   (`CastingSystem.AuraAttachment`, `MtgEngine.Rules/Casting/CastingSystem.cs:303`), which is the
   graveyard verify reported. Only the land case collides: the creature host has no namesake in
   the smoke hand.

**Ask (P-series, the pipeline's `Scenarios/` folder):** give `SmokeBoard.Aimed` an alias, the way
`SmokeBoard.Stocks` (line 95) already does for a stocked library card — place
`new CardRef(card, Alias: "aimed-N")` and return the alias, so the target and the
`AttachedToIs` expectation name the object the board placed rather than a printed name the hand
also holds. One file, no engine change, and it fixes every later card whose enchant filter or
target names a filler the hand also carries.

## Second, independent gap: CR 601.2c does not check an Aura's object target

`CastingSystem.RequireLegalAuraTarget`
(`MtgEngine.Rules/Casting/CastingSystem.Instructed.cs:100`) returns immediately when
`command.TargetPlayers` is empty, so **only the player half of an Aura's target is checked at
announcement**. An Aura announced at an object its enchant ability does not admit — or, as here,
at a card that is not on the battlefield at all — is accepted, put on the stack, and countered by
CR 608.2b at resolution. CR 601.2c says that is not a legal announcement: the same rule the
player half already cites two lines below ("a spell that could never have been announced is not a
permanent that briefly existed").

**Ask (E3-series, `Casting/`):** extend `RequireLegalAuraTarget` to the object half — for an Aura
with `command.Targets` non-empty, refuse at CR 601.2c when the target is not on the battlefield or
does not match `enchant.Of` under `query.Matches`. `AuraAttachment` already computes exactly that
judgement at resolution, so the two would share one reading.

Test `An_aura_cast_at_a_name_the_hand_also_holds_is_countered_rather_than_attached` asserts today's
behaviour with the gap in its comment; flip it when either half lands.
