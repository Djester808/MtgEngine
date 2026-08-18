# User Profiles

Accounts have a face: a self-authored profile (photo, name, tagline, bio, pinned
commander) and a derived one (what someone's decks, collection and comments say about
them). Public profiles are browsable without a login; the owner gets a superset.

Read this before changing `ProfileService`, `ProfileController`, `UsersController`,
`AvatarImage`, or the client's `user-profile` / `profile-edit` components.

## The one rule this feature is built around

**Counts are public. Money is not.**

A public profile says how many cards someone owns, how many decks they have built, what
they play. It never says what any of it is worth. Collection value is on
`MyProfileDto.privateStats`, returned only by `/api/profile/me`, and the public DTO has
nowhere to carry it — that is deliberate, so leaking it takes a schema change and not an
oversight. Publishing what a stranger's cards are worth advertises a target, and it is not
something a profile page should disclose on its owner's behalf.

The same instinct governs deck names: a public profile's "recently worked on" rail lists
**published decks only**. An unpublished deck is private work in progress. The owner's own
view (`includePrivateDecks: true`) lists everything.

## Model

Profile text lives on `User` (`MtgEngine.Domain/Models/User.cs`): `DisplayName`,
`Tagline`, `Bio`, `FavoriteFormat`, `FavoriteCommanderOracleId`, `ProfileUpdatedAt`,
`AvatarUpdatedAt`. All nullable — a profile nobody has filled in is the normal case.

Avatar bytes are a **separate table**, `UserAvatar`, keyed and foreign-keyed on `UserId`
with cascade delete. They are not columns on `User` because EF materialises every mapped
property of an entity it loads: a blob there would ride along on the login lookup, the
preferences read, and every profile projection that only wanted a username.
`User.AvatarUpdatedAt` mirrors the timestamp so building a profile — or a whole page of
them — never touches the blob table just to decide whether to emit a URL.

Migration: `20260818141952_UserProfiles`.

## Endpoints

### Public — `UsersController`, `[AllowAnonymous]`

| Endpoint | Returns |
|---|---|
| `GET /api/users?limit=100` | `PlayerSummaryDto[]` — the community directory, most active first. Capped at `MaxPlayerLimit` (200). |
| `GET /api/users/{username}` | `UserProfileDto` — the whole public profile. 404 via `ResourceNotFoundException` when no such user. |
| `GET /api/users/{username}/comments?page=1&pageSize=20` | `UserCommentPageDto` — comment history, newest first. `pageSize` clamped to `MaxCommentPageSize` (50). |
| `GET /api/users/{username}/avatar` | The image bytes, or 404. |

`UserProfileDto` embeds the first `EmbeddedCommentCount` (10) comments, so a profile load
is one request; the paged endpoint serves the rest.

### Owner — `ProfileController`, `[Authorize]`

| Endpoint | Notes |
|---|---|
| `GET /api/profile/me` | `MyProfileDto` = the public projection + email + `privateStats`. |
| `PUT /api/profile/me` | `UpdateProfileRequest`. Blank or omitted fields **clear** the value. |
| `GET /api/profile/me/avatar/limits` | `AvatarLimitsDto`, so the client can shrink before it uploads. |
| `PUT /api/profile/me/avatar` | multipart, field name **`file`** (binds to `IFormFile file`). |
| `DELETE /api/profile/me/avatar` | Falls the profile back to initials. |

A pinned commander that the card lookup cannot resolve is **rejected** (400), not stored:
the profile projection skips oracle ids it cannot resolve, so saving it unchecked would
"succeed" and then render nothing.

## Avatars

`Services/AvatarImage.cs` decides whether an upload is storable. **The declared
`Content-Type` and the filename are never consulted** — the format is read out of the
bytes, and the sniffed type is what gets stored and later served.

- JPEG, PNG and WebP (lossy, lossless and extended). Everything else is refused.
- ≤ `MaxBytes` (512 KB), ≤ `MaxDimension` (1024px), ≥ `MinDimension` (16px).
- The dimension cap is not redundant with the byte cap: a decompression bomb is small on
  the wire and enormous once decoded.

It does **not** decode pixels, so it is not a re-encode and will not strip a payload
appended past the image data. Three things carry that weight instead:

1. The format and its dimensions must genuinely parse — a renamed `.exe` fails here.
2. `GET .../avatar` pins the sniffed content type and sends `X-Content-Type-Options:
   nosniff` with `Content-Disposition: inline`, so no browser reinterprets a stored blob
   as script or markup.
3. The size cap keeps anything smuggled small.

If a decode ever becomes worth its dependency, re-encode inside `AvatarImage.TryValidate`
and no caller changes.

**Caching.** The avatar URL carries `?v={ticks}` from `AvatarUpdatedAt`, so a replaced
avatar is a new URL and the response can be `immutable` for a year. Conditional GETs are
answered from a strong ETag over the stored bytes.

The **client shrinks before uploading** (`utils/avatar-image.ts`): a camera-roll photo is
several megabytes and thousands of pixels wide, so without it the feature would fail for
most real photos on a phone. That is a usability measure, never a security one — the
server re-sniffs whatever arrives, because anything a browser can send, something else can
send too.

## Derived stats

Computed per request in `ProfileService.BuildProfileAsync`. Points worth knowing:

- **Card counts exclude decks.** Decks and collections share the `Collections` table;
  `IsDeck` separates "decks I built" from "cards I own", and copies sum
  `Quantity + QuantityFoil`.
- **`CommentsReceived` excludes the author's own replies**, or anyone could inflate it by
  talking to themselves.
- **Most-played cards drop basic lands.** Without that the answer is Forest, Island,
  Mountain, Plains, Swamp for every player alive. "Is a basic land" is a property of the
  card definition, not the row, so the query over-fetches and filters after resolving.
- **Colour spread is derived from commanders**, because a deck's colour identity is not
  stored and deriving it from every card in every deck would cost far more than the stat
  is worth. Decks without a resolvable commander are simply absent from it.
- **`JoinedAt` is `User.CreatedAt`.** The previous implementation derived the entire
  profile from `ForumPosts`, so a member who had never published 404'd on their own page
  and a join date meant "first post".
- **Collection value** (owner only) prices foils at the foil price and counts
  `CopiesValued` separately, so a total is never mistaken for complete when a printing has
  no listing. It stops at `MaxRowsValued` (20,000) rather than scanning without bound.
- **"Edited" is not `UpdatedAt > CreatedAt`.** `ForumComment` initialises both from
  separate `DateTime.UtcNow` reads, so brand-new comments differ by ticks and were being
  labelled edited at random. `WasEdited` allows a one-second threshold.

## Client

| File | Role |
|---|---|
| `models/profile.models.ts` | Mirrors the DTOs. The C# side is the definition. |
| `services/profile-api.service.ts` | Both halves — `/api/users` and `/api/profile`. |
| `components/user-avatar/` | **The** avatar: picture, or initial on a hashed colour. players-list and user-profile each had their own before this, so one person was a different shape and colour depending on the page. New callers belong here, not in a third copy. |
| `community/user-profile/` | The public page, at `/u/:username`. |
| `profile/profile-edit/` | The account page, at `/account`, auth-guarded. |
| `utils/avatar-image.ts` | Client-side downscale + JPEG re-encode. |

`/account` is what the navbar's "Profile" item had always linked to; until this shipped
there was no route behind it and the item did nothing.

Capture harness: `shoot.js` routes `profile` and `account` (both ride the `auth: true`
credentials — the public profile needs a real username to point at, not a login), and
`shoot-states.js` states `players-grid`, `profile-decks` and `profile-comments`.
`user-avatar` is in `e2e/ui-coverage-allow.json` with its reason: it has no layout of its
own and is captured inside the three surfaces that do.

## Boundary with the collection domain

Profiles **read** collection data and never write it; nothing here changes the shapes in
`CARD_COLLECTION_FEATURE.md`. Adding a stat that needs new collection surface means
updating that document too.
