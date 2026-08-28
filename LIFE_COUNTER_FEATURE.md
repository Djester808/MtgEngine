# Life Counter

A counter for a game of Magic played with real cards, on a real table, by up to eight
people sharing one device. It lives under **Tools** (`/tools/life-counter`) rather than
under Play, because Play is the engine playing a game and this is a phone lying next to a
game nobody's software is refereeing.

Read this before changing `LifeMatchService`, `MatchesController`, `TokenService`, or the
client's `tools/life-counter` components.

## The one rule this feature is built around

**A seat counts towards an account only when that account signed in at that seat.**

The device is shared and the request that files a game is a claim by whoever is holding the
phone. If that claim were believed, anyone could write losses onto a stranger's record — so
`LifeMatchSeat.UserId` is set from a **token the seat's own player produced**, never from a
user id or username in the request body. A seat without a token is stored as a guest: a
display name, `UserId = null`, and no effect on anybody's standing.

What this does and does not buy:

- A player who never signs in on your device cannot have anything written against them.
- A fabricated match can still distort **the fabricator's own** numbers. That is unavoidable
  for a self-reported tally and is why this record is deliberately separate from the
  profile's derived stats — it is "games this person told us about", not a ladder.

`TokenService.TryReadUserId` validates a seat's token exactly as the auth middleware
validates the caller's: same key, same lifetime, and `ValidAlgorithms` pinned to HS256 so a
token declaring `alg: none` is not taken on its own word.

A seat's token is also what **authenticates the call** when the device itself has no app
session — four friends around somebody's phone, its owner signed out, two of them signed in
at their own seats, is the normal case and must not need a third login. `authInterceptor`
overwrites `Authorization` whenever the app does hold a token, so this never displaces a
real session.

**Seat tokens live in `localStorage` until that seat signs out**, so a reload mid-game does
not throw everyone out. That is a deliberate trade: the device already stores its own
`auth_token` there, and a shared counter that forgot who was sitting where on every refresh
would not be usable. `newGame`/`rematch` keep them on purpose — the same people usually play
again — so signing a seat out is the only thing that clears one.

An unreadable token is a **400, not a downgrade to guest**. Silently dropping the
attribution is the worse failure: the player watches themselves sign in, wins, and finds
nothing on their record with nothing having reported a problem. The message names the seat
number so the client can send that one player back through the sign-in.

## What gets tracked, and the rule that says so

The counter exists for the second half of CR 104.3 — a game is lost four ways and only one
of them is the life total.

| Number | Rule | Ends the game at |
|---|---|---|
| Life | CR 104.3b / 704.5a | 0 or less |
| Poison counters | CR 104.3d / 704.5c / 122.1f | 10 |
| Commander damage, **per commander** | CR 104.3j / 903.10a | 21 |
| Energy `{E}` | CR 107.14 | never — a resource |
| Experience | CR 122.1 | never — a resource |
| Rad | CR 122.1i / 728.1 | never, but it mills and drains |
| Ticket `{TK}` | CR 107.17 | never — a resource |

Three of those the counter works out for itself, because it holds the figures. The rest —
drawing from an empty library (CR 704.5b), an effect (CR 104.3e), conceding (CR 104.3a) —
are declared by the player and stored as `MatchLossReason`.

Two things every hand-rolled tracker gets wrong, both pinned by tests:

- **Commander damage is damage.** Seven from a commander is seven off the life total *and*
  seven on that commander's tally (CR 903.10a). `adjustCommanderDamage` does both, and
  clamping the tally at zero adjusts the life by the amount actually applied, not the
  amount asked for.
- **Poison is not.** Infect and toxic deal poison counters *instead of* life loss
  (CR 702.90b), so poison never touches the life total.

Partners get two commanders, counted separately (CR 702.124d): 20 from each is 40 damage
and not a loss. Dropping a player back to one commander deletes every other seat's tally
for the second one, in the same commit as the count — a hidden number that still counted
towards 21 would be worse than a lost one.

## Model

`MtgEngine.Domain/Models/LifeMatch.cs` — `LifeMatch` (when, starting life, who filed it) and
`LifeMatchSeat` (seat number, optional `UserId`, display name, won, `MatchLossReason`, final
life). `LossReason` is stored as its **name** via `HasConversion<string>()`, so a raw dump
stays readable and reordering the enum cannot silently rewrite history.

Two unique indexes: `(MatchId, Seat)`, and `(MatchId, UserId)` filtered to non-null — the
second is what stops a record being padded by seating one account twice.

Migration: `20260822154101_LifeCounterMatches`.

## Endpoints — `MatchesController`, `[Authorize]`

| Endpoint | Notes |
|---|---|
| `POST /api/matches` | `RecordMatchRequest`. 2–8 seats, at most one winner. Returns the usernames actually credited, so the client can say who was banked and who stayed a guest. |
| `GET /api/matches/me` | `PlayerRecordDto` — played/wins/losses over **all** rows, plus the last `RecentMatches` (20) games. |

The caller must be signed in (so a stream of these has an owner) but need not be seated —
one person holding the phone for the table is the normal case. `StartedAt` is believed only
if it is in the past and within `MaxBacklog` (2 days); anything else becomes now.

Seat sign-in reuses **`POST /api/auth/login`** rather than adding an endpoint. It is already
`[AllowAnonymous]` and rate-limited under the `auth` policy, and a second credential-checking
endpoint would be a second thing to get wrong.

## Client

| File | Role |
|---|---|
| `models/life-counter.models.ts` | The counter kinds and their rules, in one table the UI reads from. |
| `tools/life-counter/life-counter.service.ts` | **All** the state and every rule. Pure, signal-based, unit-tested without a DOM. |
| `tools/life-counter/life-counter.component.*` | Setup, the grid, the result. |
| `tools/life-counter/player-panel/` | One seat: the tap zones, the chips, the gain/loss animation. |
| `tools/life-counter/seat-sheet/` | Counters, commander damage, backdrop, sign-in, leaving by hand. |
| `tools/life-counter/background-picker/` | Card art or a picture off the device. |
| `services/match-api.service.ts` | `/api/matches`. |
| `tools/tools-hub/` | `/tools`. |

**No route guard, deliberately.** The whole point is four people at a kitchen table with
nobody signed in; a guard would put a login form in front of a screen whose job is to work
without one. Card-art search is the one thing that needs an account (`/api/cards` is
`[Authorize]`), so the picker offers the device instead when there is no session.

### Storage

`localStorage`, split in two on purpose:

- `mtg.life-counter.state.v1` — the table **with the pictures stripped out**, rewritten on
  every tap.
- `mtg.life-counter.art.v1.<seatId>` — one backdrop, written only when it changes.

Eight backdrops inline would mean re-serialising well over a megabyte on every single life
tap. Backdrops are downscaled to 640px / 180 KB by `utils/avatar-image.ts` — the same
`prepareImage` the avatar upload uses, which is why it takes its limits as arguments and is
named for images rather than avatars. A quota failure surfaces a warning rather than losing
the picture silently.

A stored table is read back **partially typed**: it was written by whatever version of the
counter the browser last ran, so missing counter kinds are filled from a blank seat rather
than left as `undefined` in an arithmetic path.

### Layout

Panels are dealt into a grid: one column for a duel on a phone, two for anything else,
wider only above `$bp-nav`. Below `$bp-nav` the rows in the top half are rotated 180° so the
players across the table read their own totals the right way up — the tap zones rotate with
the content, so the half by each player's left hand still takes life away. It is a switch
(`Face across`) and not a rule, because a desktop monitor stands up and two upside-down
panels on one is nonsense.

Capture harness: `shoot.js` routes `tools` and `life-counter` (both anonymous, since the
counter must be measured without credentials), and `shoot-states.js` states `tools-hub`,
`life-counter-setup`, `life-counter-2`, `life-counter-8`, `life-counter-seat-sheet`,
`life-counter-commander-damage`, `life-counter-seat-tab` and `life-counter-backdrop`. The
eight-player state reports the smallest rendered life-total font size: the table divides its
height rather than scrolling, so "nothing overflowed" is not the same question as "this is
still readable". `life-counter-seat-tab` signs a seat in when `e2e/.env` has credentials, so
the record block is captured; without them it still captures the same tab signed out.

Each of those states **clears localStorage and deals a fresh table through the setup form**
(`dealTable` in `shoot-states.js`). Without it the second state in a run opens onto the
table the first one left behind and never sees the form — which is the counter working
correctly, and is exactly how three of these states failed the first time they were driven.

## Boundary with profiles

This feature **does not touch** `ProfileService` or the profile DTOs. A self-reported tally
is not the same kind of fact as "how many decks this person has built", and putting it on a
public profile would need a decision about whether an unrefereed number belongs there.
`GET /api/matches/me` is owner-only for the same reason. If it ever does reach a profile,
`USER_PROFILE_FEATURE.md` is the document that has to say so.
