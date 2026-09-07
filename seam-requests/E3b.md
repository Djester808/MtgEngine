# E3b — casting, wave 3: what casting still needs from other folders

Everything below is a field on someone else's record. Each one is a rule the casting system
computes correctly today and then has to refuse, because there is nowhere to put the answer.

## 1. `PlayLand.FaceIndex` and `IZoneSystem.Move(..., int? faceIndex)` — CR 712.12

`Commands/Command.cs` has `PlayLand(PlayerId Player, ObjectId Card)`. A modal double-faced
card's land face is *played*, not cast, and the player chooses which face (CR 712.11b, 712.12).
`CastSpell` already carries a `FaceIndex`; `PlayLand` needs the same field.

Choosing the face is only half of it. `CastingSystem.PlayLand` puts the card onto the
battlefield with `zones.Move`, and `Move` builds the new object (CR 400.7), so only it can set
which face is up. `ObjectMoved` carries the whole new `GameObject`, so nothing else can reach
in afterwards without inventing a second writer of the same rule.

Requested, both in one change:

```csharp
public sealed record PlayLand(PlayerId Player, ObjectId Card, int FaceIndex = 0) : Command(Player);

// Engine/Seams.cs
Outcome Move(GameState state, ObjectId what, ZoneKey to, Placement where = Placement.Default,
             bool tapped = false, PlayerId? controller = null, int? faceIndex = null);
```

`faceIndex` defaults to null, meaning "the face it already had", so every existing caller is
unchanged. Until both exist, `CastingSystem.LandFace` picks the land face (there is exactly one
on every modal land printed so far) and then refuses with CR 712.12 rather than putting the
permanent onto the battlefield showing the wrong face. `FaceCastingTests.A_land_face_of_a_modal_card_is_not_cast`
asserts the refusal; it becomes the play when the seam lands.

## 2. Multikicker has no repeat count — CR 702.32b

`KeywordKind` is generated from the rulebook, which has one **Kicker** entry; multikicker is
CR 702.32b inside it. The vocabulary therefore cannot say "kicked three times": `CastSpell`
carries `ImmutableList<KeywordKind> OptionalCosts`, and a repeated `Kicker` entry is
indistinguishable from a caller sending the same keyword twice by mistake, because nothing on
the printed `KeywordAbility` says whether the card's kicker is the repeatable one.

Smallest thing that closes it: a `bool Repeatable` (or an `Amount? Times`) on `KeywordAbility`,
set by the translator when the printed word is "multikicker". Casting then pays the cost that
many times and records one entry per payment. Until then a second `Kicker` payment is refused
naming CR 702.32b.

## 3. The alternative costs whose riders live elsewhere

Flashback is paid in full here: the cost, the zone (CR 601.3) and the exile on resolution
(CR 702.34a) are all casting's. The others are refused by name with their own CR number,
because each rider belongs to a subsystem that has not been asked for it yet:

| Keyword | Rider | Whose |
|---|---|---|
| Evoke, CR 702.72 | the sacrifice trigger when it enters | E6 triggers |
| Dash, CR 702.109 | haste, and the return at the next end step | E7 layers + a delayed trigger |
| Escape, CR 702.129 | "exile N other cards from your graveyard" is not in the printed `Cost` string | vocabulary: the keyword needs a `Cost` record, not a mana string |
| Foretell, CR 702.143 | the card must already be exiled face down by the foretell activation | E13 choices + a per-object flag |
| Madness, CR 702.35 | the card must have been exiled by a discard replacement | E8 replacement |

Escape is the one that is a vocabulary gap rather than a scheduling one: `KeywordAbility.Cost`
is a `string?` parsed as mana, so a cost with a non-mana component cannot be represented at all.
An `IReadOnlyList<Cost>? Costs` beside it would take escape, and the "sacrifice a creature"
half of several other keywords with it.

## 4. Convoke, improvise and delve are not refused, and that is deliberate

CR 702.51a, 702.126a and 702.66a are all "you **may** tap/exile". With no channel on
`CastSpell` naming the permanents or cards to use, casting pays the full printed cost, which is
a legal choice a player is allowed to make — not a dropped rule. When the choice system can ask
"which permanents do you tap for convoke?", it wants a field on the command
(`ImmutableList<ObjectId>? Convoked`) or a pending choice; either is fine, and casting reads
the keyword off the face today, so only the payment plumbing changes.
