# Original Compleat feature port

The Angular app retains the Compleat name, original palette, logo, wordmark, sigil, and startup video. Internal project folders and API namespaces remain compatible.

## Account management

Account & security is at /account/security; password recovery is at /recover-account. Email verification and reset codes expire after ten minutes and allow five attempts. Changing credentials and deleting an account revoke active sessions. Session management supports individual-device revocation and sign out everywhere. Refresh tokens rotate in a Secure, HttpOnly cookie; replay revokes the session family. Existing PBKDF2 password hashes remain valid.

Production email requires Email:Smtp:Host, Port, User, Password, UseStartTls, and Email:From configuration. Development without SMTP writes mail to the local development log. Production without SMTP reports that email is unavailable. Account deletion anonymizes the account and removes private collections and contributions; shared match history can remain.

## Settings and owned cards

/settings has searchable categories, 85–160% text sizing, high contrast, reduced motion, a startup destination, and desktop navigation preferences. Accessibility reset has Undo. Preferences persist on this device.

Collection card sheets have an Owned details tab for language, condition, purchase price/currency, and tags. Language separates printing rows. Purchase information is available only from owner endpoints and never enters shared card DTOs. Bulk tag, untag, condition, and removal actions are atomic; their owner-scoped Undo expires after five seconds and refuses to overwrite newer edits.

## Explore

Community → Explore opens /community/explore. Commander and card pages retain the original EDHREC parsers and recorded fixtures, preserving sections, published deck counts, inclusion ratios, synergy, salt, related cards, themes, budget variants, and available community breakdowns. Average decks retain quantities and commanders. Their cards can be added individually or the entire deck copied with one transaction. Missing catalog cards prevent a partial whole-deck copy. Lists support search, section filters, sorting, and grid/list views.

EDHREC responses are validated before a 24-hour cache write. Failed or malformed responses are not cached as missing pages. Missing pages have a distinct empty state. Bracket variants depend on EDHREC publishing that page; some cuts may be unavailable. Shared upstream pacing also covers the existing deck-suggestion pool. Figures are EDHREC community data, distinct from Compleat's locally published-deck statistics.

## Collection sharing

/account/sharing controls a default-private public summary at /u/:username/collection. Sharing exposes only aggregate copies, unique cards and pinned printings, and counts by color, type, rarity, set, finish, and mana value. Decks, individual card names, notes, tags, collection names, purchase information, and monetary value are excluded. Revocation takes effect on the next request; responses are not publicly cached. Public-profile collection totals follow the same visibility setting.

## Motion

The original free-board position-to-position glide is implemented in a shared directive keyed by stable card and column identities. It animates visible items only, discards old positions after a render, and cancels work on teardown. Drag and drop, card flights, and reordering honor both the saved and system reduced-motion preference.

## Validation

API suite: 693 passing tests, including original parser fixtures, invalid paths/themes, cache failures, private metadata, safe Undo, refresh/session lifecycle, and sharing privacy. Client verification: 1,654 passing tests; formatting, lint, shared-style checks, UI registration, changed-file coverage, and production build pass. The existing card-sheet style budget remains an advisory warning.

Live phone (375×667) and desktop checks cover account management, settings, owned metadata, bulk editing/Undo, real EDHREC commander/card/average pages, single-card additions, complete average-deck copying, public summary revocation, pointer drag, original reordering glide, and reduced-motion behavior. Disposable browser accounts are removed after successful runs. Browser scripts are in e2e/verify-account.js, verify-settings.js, verify-collection-tools.js, verify-explore-sharing.js, and verify-ported-motion.js.

Final visual audit: 100 route captures refreshed across iPhone SE, iPhone 15 Pro, Pixel 8, iPad mini, and desktop. The regression gate passes for 186 registered surfaces. Newly added screens have measured baselines; every pre-existing threshold is retained. Existing small targets in shared navigation and legacy desktop controls remain tracked. Capture helpers skip the intro before interacting and preserve previous results during filtered runs. The original intro and brand assets also pass their dedicated phone/desktop verification.
