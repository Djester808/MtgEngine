# E5c — seam requests

The state seam this builder was waiting on had already landed: `GameObject.AttachedToPlayer`
and the `ObjectChanged.AttachedToPlayer` fold are on the wave-8 base, so CR 704.5m accepting a
player attachment, the view projecting it and the query reading it are all inside `Sba/`,
`Views/` and `Query/`. What follows is the one thing that could not be done there, and it is a
vocabulary request, not an engine one.

---

## An Aura cannot say it enchants a player, and no card can name the player it is on

Two records are missing, and they are two halves of the same hole: nothing in
`MtgEngine.Cards` can **say** "enchant player", and nothing can **read** "enchanted player".

### 1. `KeywordAbility` cannot spell "enchant player"

`MtgEngine.Cards/Abilities.cs`. The enchant restriction is `ObjectFilter? Of`, so every enchant
ability either names an object filter or names nothing at all. CR 702.5b's "enchant player" has
no place to go.

`StateBasedActions.AttachedLegally` (CR 704.5m) has to decide whether an Aura on a player is
legally attached, so it needs an answer today. It reads the only honest one the vocabulary
offers: **an enchant ability that names no object filter admits a player**, and one that names a
filter is a restriction to objects and refuses a player. `StateBasedActions.EnchantsAPlayer`
carries the reasoning and the pointer to this file. That reading is conservative — an
object-restricted Aura is never called legal on a player — but it is coarse in the other
direction: a bare `Enchant` with no filter now also admits a player, and a card meaning
"enchant permanent" would be spelled that way by a careless translation.

The record that would close it:

```csharp
public sealed record KeywordAbility(
    KeywordKind Keyword, string? Cost = null, Amount? N = null, ObjectFilter? Of = null,
    string? Detail = null, Condition? Condition = null, IReadOnlyList<Cost>? Costs = null,
    bool Repeatable = false,
    /// <summary>"Enchant player", "enchant opponent": what this Aura may enchant when it is a
    /// player rather than an object, CR 702.5b. Both null is "enchant anything".</summary>
    [RuleRef("702.5b")] PlayerFilter? OfPlayer = null) : Ability;
```

`PlayerFilter` already exists (`Players.cs`) and already carries `Opponent`, so "enchant
opponent" comes free. `AttachedLegally` then reads `enchant.OfPlayer` for the player half and
`enchant.Of` for the object half, and the null/null case is the only ambiguous one left.

The linter would want the pair rule: an `Enchant` with both halves null is "enchant anything",
which almost no printed card says; a warning there is what stops a dropped filter from reading
as a legal player attachment.

### 2. There is no "enchanted player" `PlayerRef`

`MtgEngine.Cards/Players.cs`. Every `PlayerRef` reaches a player through the controller, the
turn, a target, a trigger or an object's controller/owner. None of them is "the player this
source is attached to", which is what an "enchant player" Aura's own abilities say almost every
time — "enchanted player loses 1 life", "enchanted player can't untap".

`Query.Selectors.Players` therefore has no arm to add: a card that means it cannot be written
at all, so the gap is in the translator's reference before it is in the engine.

```csharp
/// <summary>"Enchanted player": the player this Aura or attachment is attached to, CR 303.4f.</summary>
[RuleRef("303.4f")]
public sealed record AttachedPlayer : PlayerRef;
```

The query arm is one line and is `Query/`'s to write the day the record lands:

```csharp
Cards.AttachedPlayer => context.Source is { } source && state.Objects.TryGetValue(source, out var self)
    && self.AttachedToPlayer is { } enchanted ? [enchanted] : [], // CR 303.4f
```

Naming note, following V5's `AttachedToIs`: `Attached` and `AttachedTo` are both taken in
`Selectors.cs`, so `AttachedPlayer` is offered rather than `EnchantedPlayer` — it is the same
word the object-side selector uses and it is true of an Equipment too, should one ever attach to
a player. If the vocabulary would rather read as the cards read, `EnchantedPlayer` collides with
nothing; either name is one arm.

**Until it lands:** an "enchant player" Aura stays on its player through every state-based
action check (`StateBasedActionTests.An_aura_that_enchants_a_player_stays_on_that_player`), both
players see the attachment
(`HiddenInformationTests.An_aura_attached_to_a_player_shows_that_attachment_to_both_players`),
and no ability it has can name the player it is on
(`QueryTests.An_aura_attached_to_a_player_is_attached_and_names_no_object`, which asserts the
gap rather than leaving it silent).
