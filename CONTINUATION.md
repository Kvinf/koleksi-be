# Koleksi.id — Continuation Handbook

**Purpose of this file.** If a session runs out of context, this document is enough to
pick the work back up without re-deriving anything. It records what exists, what is
deliberate, what is left, and the decisions that must not be quietly reversed.

Keep it current. Anyone finishing a slice should update the status table and the "next
step" section in the same commit as the code.

**Last updated:** 2026-08-23

---

## 1. What is being built

Koleksi.id — an Indonesian C2C marketplace for collectibles: trading cards, Gunpla and
diecast, sneakers, vinyl and retro games, coins and banknotes, watches and cameras.

Source of truth for the design is `design/` (a vendored copy of the handoff zip):

| Path | What it is |
|---|---|
| `design/README.md` | The written spec: 15 screens, flows, terminology |
| `design/design/koleksi.css` | Design tokens — colours, type scale, spacing, radii |
| `design/design/Koleksi.id.dc.html` | The static prototype, 3648 lines |
| `design/design/support.js` | **Do not port.** Proprietary prototype runtime |
| `design/screenshots/` | 18 PNGs at a 924px viewport |

Product characteristics that drive the architecture:

- **Three selling formats** — fixed price, accept-offers (*nego*), and timed auction (*lelang*).
- **Escrow-backed payments.** Funds are held until the buyer confirms delivery, or until
  a 3-day auto-release after delivery.
- **Bilingual, ID-primary.** Every user-facing string ships as `{id, en}` and the header
  toggle switches without a refetch.
- **Rupiah, whole units.** No sen. Money is a `long` of whole rupiah.

---

## 2. Stack and the decisions behind it

| Layer | Choice | Why this and not the obvious alternative |
|---|---|---|
| Frontend | React Router v7, framework mode, SSR | Marketplace pages must be crawlable and fast on Indonesian mobile. A client-only SPA would ship an empty shell to Googlebot. |
| Frontend hosting | Node Lambda behind CloudFront | Same account, same deploy, no second platform. |
| Backend | .NET 10, ASP.NET Core Minimal APIs on Lambda | User's requirement. Minimal APIs over MVC: less ceremony, faster cold start. |
| Lambda runtime | `provided.al2023`, self-contained, **arm64** | Works regardless of which managed .NET runtimes AWS offers, and Graviton is cheaper with better cold starts. |
| Auth | Amazon Cognito user pool | We hold no password material and no signing key. A compromise of the API cannot mint a token for anyone. |
| Data | DynamoDB, single table | **User's explicit choice**, overriding a PostgreSQL recommendation. Consequences are real and are spelled out in §7. |
| Search / facets | `IListingSearchIndex`, separate from the repository | DynamoDB cannot count facets natively. First implementation maintains counters in DynamoDB; an OpenSearch implementation can replace it without touching a handler. |
| Infra | AWS CDK (TypeScript) | Typed, testable, same language as the frontend. |

### Dependencies deliberately *not* taken

Each of these was considered and rejected. Do not add them back without a reason that
outweighs the one recorded here.

| Rejected | Instead | Reason |
|---|---|---|
| MediatR | Plain handler classes, explicitly registered | Commercial licence since v12, plus a layer of indirection that buys nothing at this size. |
| Serilog | `builder.Logging.AddJsonConsole` | CloudWatch already ingests structured JSON. Fewer dependencies in the request path. |
| Swashbuckle | Built-in `AddOpenApi()` | In-box since .NET 9. |
| FluentAssertions | Shouldly | FluentAssertions v8 moved to a commercial licence. |
| A third-party ULID package | `Guid.CreateVersion7()` + Crockford base-32 | In-box, and one less supply-chain edge. |
| Assembly scanning for DI | Explicit registration | Scanning registers whatever happens to be in the assembly, including a type someone added by accident. |

---

## 3. Status

Legend: ✅ done and verified · 🟡 partial · ❌ not started

### Backend

**The whole backend solution builds clean — 0 warnings, 0 errors.** 282 of 285 tests pass
— 90 domain + 62 infrastructure + 58 against a real DynamoDB + 132 of 135 API (see the
`Koleksi.Api.Tests` row below for the 3 pre-existing, cart-unrelated failures).

| Component | Status | Notes |
|---|---|---|
| `Koleksi.Domain` | ✅ | 0 warnings under `TreatWarningsAsErrors` |
| `Koleksi.Domain.Tests` | ✅ | **86/86 passing** |
| `Koleksi.Application` | ✅ | `BrowseListingsHandler`, `GetListingHandler`, `PlaceBidHandler`, `CreateListingHandler` |
| `Koleksi.Api` | ✅ | Composition root, middleware, and endpoints all in place |
| `Koleksi.Infrastructure` — options | ✅ | `Configuration/KoleksiOptions.cs`, fail-fast `Validate()` |
| `Koleksi.Infrastructure` — persistence | ✅ | Listing, auction, search index, categories, idempotency, user profile |
| `Koleksi.Infrastructure` — identity | ✅ | Cognito SRP + native email-OTP (`USER_AUTH`) + hosted-UI Google exchange; local dev stub mirrors all three, `#if DEBUG`-gated |
| `Koleksi.Infrastructure` — S3 media | ✅ | Presigned grants, allowlist, size cap, verify-after-upload; exposed generically at `POST /api/media/upload-grant` |
| `Koleksi.Infrastructure` — SNS phone verification | ✅ | `IPhoneVerificationService`; challenge stored in the same table with a TTL, one-shot on verify; local dev stub logs the code instead of sending an SMS |
| `Koleksi.Infrastructure` — SES email verification | ✅ | `IEmailVerificationService`, same shape as the phone one; profile's phone-verify step falls back to this when the reader has not entered a phone number yet, resolving the account's own email via `IIdentityService.GetEmailAsync` (a live `GetUser` call against the caller's own bearer token — Cognito access tokens carry no email claim) |
| `Koleksi.Infrastructure` — payments | 🟡 | Webhook verification complete and tested; `CreateSessionAsync` is a documented TODO |
| Offer / order repositories | ✅ | Registered and live — checkout, offers, and order fulfilment (`Checkout.cs`, `ConfirmOrderPayment.cs`, `FulfillOrder.cs`, `MakeOffer.cs`, `RespondToOffer.cs`) are real, not scaffolding. §7's "deliberately unregistered" note describes an earlier state and is now stale; see the cart entry below for what is still genuinely scaffolded (nothing, as of this pass) |
| Cart repository + handlers | ✅ | `ICartRepository` (`DynamoCartRepository`), `AddToCartHandler`, `RemoveFromCartHandler`, `GetCartHandler`, `CheckoutCartHandler` — see the new note below |
| `Koleksi.Api.Tests` | 🟡 | **132/135 passing.** The 3 failures (`BrowseEndpointTests` — a "grade" facet the `kartu`/`cards` category schema no longer resolves) predate this pass and are unrelated to the cart work; reproduced on `main` before touching anything cart-related. Not yet root-caused — worth a session of its own |
| `Koleksi.Infrastructure.Tests` | ✅ | **62/62 passing** — see §3a |
| `Koleksi.Infrastructure.IntegrationTests` | ✅ | **58/58 passing** against DynamoDB Local when Docker is up — unchanged by this pass; no new integration-test project covers `DynamoCartRepository` yet, though the manual live-verification below exercised it end-to-end through the real API against real DynamoDB Local |

**The cart is real end-to-end, not scaffolded — as of 2026-08-23.** `GET /api/cart`,
`POST /api/cart/items`, `DELETE /api/cart/items/{listingId}`, and `POST /api/cart/checkout`
are backed by a new single-table partition (`CART#{buyerId}` / `ITEM#{listingId}`, one row
per cart entry, no aggregate or version — see `DynamoTable`'s "Cart" region). A cart entry
is a live pointer to a listing, not a priced snapshot: `GetCartHandler` reads the listing,
its category, and its seller's browse-projection username fresh on every read, and quietly
sweeps out any entry whose listing has since sold, been archived, or stopped being
purchasable. `CheckoutCartHandler` groups the buyer's selected items by seller and creates
one `Order` per seller in a single `IOrderRepository.AddManyAsync` call — reusing the
multi-line-order support `Order` already had — then starts an independent payment session
per resulting order (see the handler's own remarks on why a truly *shared* payment across
several orders was not built: it would mean changing `ConfirmOrderPaymentHandler`'s
webhook-reference-to-one-order contract, and that is out of scope for a cart feature).
Live-verified end-to-end locally: add-to-cart from the item page, live cart totals grouped
by seller with independent per-seller courier pickers, remove-from-cart via `useFetcher`
(no nested `<form>` — see §11's existing trap about that), and a real two-seller checkout
that produced two real orders, visible afterward on the buyer's dashboard. **Not** built:
a *shared* payment session across a multi-seller checkout (see above) and an "Add to cart"
affordance on the auction page (auctions are never cart-eligible — you win by bidding).

**Sell → publish is real, and it is the line that populates browse.**
`POST /api/listings` (`CreateListingHandler`) creates a draft, and — when the seller hits
Publish rather than Save draft — publishes it, opens its `Auction` aggregate if it has one,
and calls `IListingSearchIndex.IndexAsync`, which nothing in the product had ever called
before this pass despite the search index having existed and worked the whole time. A
published listing now actually appears in browse. `POST /api/listings/{id}/archive` calls
`RemoveAsync` on the way out for the same reason. `GET /api/listings/mine` backs the
dashboard's Listings tab with a seller's real drafts/active/sold listings, including real
view/watcher counts. Live-verified locally end-to-end (create a draft with no photo → shows
up on the dashboard with real data → archive → status updates via fetcher revalidation, no
photo needed for any of that). Photo upload and the two paths that require at least one
verified photo (publishing, and publishing an auction) are covered by
`ListingEndpointTests` against the fake media storage rather than live-tested, same
real-S3-needed caveat as the profile phase's avatar upload.
**Search and the item page were already real before this pass** — `GET /api/catalog/browse`
and `GET /api/catalog/listings/{slug}-{id}` have worked end-to-end since the catalog phase;
the item page's four remaining fake sections (seller trust, shipping estimate, price
history, Q&A) are an explicit, user-confirmed scope cut, not an oversight — each needs its
own subsystem (reviews, shipping rates, messaging) this pass does not build.

**Authentication and profile are real end-to-end, not scaffolded.** Sign-up → email
confirmation → sign-in (email-OTP primary, password fallback) → Google SSO (hosted-UI PKCE,
inert until real Google credentials replace the `REPLACEME` placeholder in
`infra/lib/auth-stack.ts`) → profile edit, all backed by Cognito (or the local dev stub) and
a real `UserProfile` aggregate in DynamoDB. Every mutating endpoint is
`IIdempotencyStore.RunOnceAsync`-wrapped and every frontend button that triggers one shows a
`SubmitButton` spinner and disables itself while in flight. See §9 for the endpoint list.
Live-verified locally end-to-end for everything that doesn't require real AWS
infrastructure: register/confirm/sign-in, email-OTP, Google's PKCE round trip against the
local dev shortcut, profile save (name + addresses), phone-OTP verification, and the bank
account modal. **Not** live-verified locally: the real Cognito-sent email/Google flows and
the avatar/identity-document direct-to-S3 uploads — both need real AWS infrastructure
(`appsettings.Development.json`'s `Media` bucket names are placeholders with nothing behind
them locally), so they are code-complete and reviewed against the documented contracts
rather than claimed as tested.

### 3a. What the tests actually cover

Added 2026-08-16. They found five defects between them on their first runs, all recorded
in §11 — two of which only a real DynamoDB could have surfaced.

`Koleksi.Api.Tests` hosts the **real** `Program` over `WebApplicationFactory`. Two things
are substituted and nothing else: Cognito's published JWKS is replaced by a key the test
process owns, and the adapters that would reach AWS are replaced by in-memory fakes. The
JWT bearer handler and all of its validation parameters, the fallback authorization
policy, CORS, the security-header middleware, both rate limiters, output caching, routing,
model binding, JSON options, and the exception handler are all the production ones.

| File | Holds to account |
|---|---|
| `AuthenticationTests` | Correctly signed tokens that must still be refused: id token, wrong `client_id`, wrong issuer, ninety seconds expired, unknown key, HS256-over-the-public-key |
| `AuthEndpointTests` | Every unsuccessful sign-in answered identically regardless of cause; registration never hands back a session; idempotency replay on register/confirm |
| `ProfileEndpointTests` | Empty profile for a fresh account rather than 404; save-and-reload round trip; idempotent replay; phone-OTP request→verify and one-shot code reuse rejected; avatar attach refused when the key was never actually uploaded; bank account masked in every response; upload-grant key scoped to the caller's own id |
| `ListingVisibilityTests` | Draft / archived / suspended visibility, staff override, 404-not-403, the id guard on the route segment |
| `AuctionEndpointTests` | Proxy bidding end to end, and that no response ever carries the leader's maximum, identity, or `outbidUserId` — asserted against the raw body, not a typed model |
| `RateLimitTests` | The bidding bucket throttles, answers `Retry-After`, and partitions by subject so one account cannot spend another's budget |
| `BrowseEndpointTests` | Query-string normalisation: page and size clamped, invented facet keys and values dropped, search term truncated, reversed price range swapped |
| `SecurityHeaderTests` | The headers are on **every** response, including 401, 404 and 500 |
| `CorsTests` | Allowlist, no wildcard, no prefix match on a lookalike origin |
| `ErrorResponseTests` | An internal failure returns a correlation id and nothing about itself |

`Koleksi.Infrastructure.Tests` covers the pure parts of the storage layer, with no network
and no container: `DynamoTable` key composition, **pinned** shard values, `DynamoCursor`
round-tripping and refusal of anything malformed, `PaymentWebhookVerifier`, and
`KoleksiOptions.Validate`. The internals are reached through `InternalsVisibleTo` rather
than by widening them to public — nothing outside the infrastructure assembly may compose
a key.

`Koleksi.Infrastructure.IntegrationTests` runs the production repositories against a real
DynamoDB.

```bash
docker run -d --name koleksi-dynamodb-local -p 8000:8000 \
  amazon/dynamodb-local:latest -jar DynamoDBLocal.jar -inMemory -sharedDb
```

| File | Holds to account |
|---|---|
| `ListingRepositoryTests` | Every field's round trip through DynamoDB's type system; the create and version conditions; the seller index as a prefix query; cursor paging; atomic view counters surviving a concurrent edit |
| `AuctionRepositoryTests` | The bid transaction landing whole or not at all; history order; the close sweep across all four shards, its inclusive tick boundary, and that a swept auction arrives fully populated |
| `SearchIndexTests` | Padded price ordering; facet, price, city and free-text filters; a facet value carrying expression syntax matched literally; the sparse ending-soon index; counters rising and falling; numbered paging across page boundaries |
| `IdempotencyStoreTests` | One winner among concurrent claims; replay; a result refused against an unclaimed key; per-caller scoping; the TTL attribute in Unix seconds |
| `TableSchemaTests` | The test table against `infra/cdk.out/Koleksi-Data-dev.template.json` — index names, key attributes, `ProjectionType.ALL`, the TTL attribute, on-demand billing |

Three properties make this suite worth trusting:

- **A table per test, not per class.** Facet counters are per-category, so two tests
  indexing a card listing would read each other's totals.
- **`TableSchemaTests` closes the circle.** Without it the suite is tautological — it
  builds a table from the same constants the queries use, so it would pass even if the CDK
  stack declared something else, and the first sign would be a `ValidationException` in
  production naming an index that does not exist. Run `npx cdk synth -c stage=dev` first;
  without the template those seven tests skip.
- **Missing Docker skips, it does not fail.** `dotnet test` on a machine with no container
  reports `Passed: 7, Skipped: 49` and prints the `docker run` line. A suite that fails
  without a container is a suite people learn to ignore. The probe is narrow on purpose:
  only a failure to *connect* counts as absent, so a credential or configuration mistake
  surfaces as an error rather than as forty-nine tests quietly not running.

### Frontend

**`npm run typecheck` passes and `npm run build` succeeds. `npm audit`: 0 vulnerabilities.**

**All 15 screens in the handoff now exist as routes, and — as of 2026-08-18 — have been
brought to design fidelity.** A screen-by-screen audit against `design/` found the initial
build had shipped functionally-complete but visually simplified: no mega-menu, no
notifications, no toasts, a plain footer, a home page missing its hero carousel and
"Auctions ending soon" rail, an item page skipping half its sections. See §3b for what is
wired to a real endpoint and what is not.

| Component | Status | Notes |
|---|---|---|
| Global chrome | ✅ | Mega-menu (real per-category facet data), notifications bell + panel/sheet, typeahead search, tooltips, toast system (`NotificationProvider`), 3-column footer with payment/courier chips |
| Home | ✅ | Hero carousel (3 slides), ticking "Auctions ending soon" rail, `ItemCard` hover quick-view/watchlist wired |
| Category / search | ✅ | Breadcrumbs, grid/list density toggle, mobile filter bottom sheet |
| Item page | ✅ | Rebuilt onto `.detail-grid`/`.gallery`/`.buy-rail`; gallery with zoom + thumbs, ongkir estimate, price history chart, ask-the-seller thread, related items (real data), offer modal, seller trust block, mobile bottom bar |
| Auction page | ✅ | `.live-band` with watcher count, Lelang badge + lot number, bid history leader highlight, auto-bid max field, logged-out gating, mobile bottom bar |
| Dashboard, storefront, sell, profile, order, checkout | ✅ | Orders segmented Sales/Purchases, dashboard Listings tab backed by `GET /api/listings/mine` with a real row-menu archive action, storefront chat-from-listing + live countdowns, sell form posts to `POST /api/listings` with direct-to-S3 photo upload, profile avatar/address/ID/bank modals, order copy button + courier log, checkout live QRIS countdown + stage-3 fields |

New sample-data helpers (`sampleSellerTrust`, `samplePriceHistory`, `sampleQuestions`) carry
`TODO(api)` markers for the seller-trust, price-history, and Q&A endpoints the backend does
not expose yet — same boundary as §3b, not a new one. One real bug surfaced and was fixed
during this pass: the profile screen's phone-verification step used a `<form>` nested
inside the page's own `<Form>`, which is invalid HTML that silently merges into the outer
form — submitting it triggered a full page reload and wiped every bit of client state on
the screen. Fixed by replacing it with a plain `<div>` and a `type="button"` handler. If
another screen ever needs an inline multi-step control inside a route that is itself a
`<Form>`, do not reach for a nested `<form>` — this is exactly the trap.

| Component | Status | Notes |
|---|---|---|
| Tokens + component CSS | ✅ | Flattened from the prototype's four layered `:root` blocks |
| Type scale | ✅ | The handoff's exact scale — H1 `clamp(22px, 3vw, 30px)`, H2 22, H3 17 |
| Responsive breakpoints | ✅ | All four: 900, 700, 640, and `(hover: none)`. See §3c |
| i18n (ID/EN) | ✅ | Both languages in the bundle; toggle needs no refetch |
| `formatRupiah`, countdown, dates | ✅ | Hand-rolled money format, byte-identical to the API's |
| Typed API client | ✅ | `app/lib/api.server.ts` — server-only by filename |
| Session + auth gate | ✅ | `__Host-` cookie, `requireUser` redirects carrying `?next=` |
| Home · category · search · item · auction | ✅ | SSR, crawlable, works without JavaScript |
| Log in · sign up · sign out | ✅ | Real forms posting to real endpoints |
| Cart & offers · profile · sell | ✅ | Cart tab is real end-to-end (add/remove/checkout — see the backend note above); "My offers" tab still reads `sample-data.ts`, profile gate, live buyer preview |
| Checkout · order tracking · add tracking · review | ✅ | Three-stage checkout, role-aware order screen |
| Dashboard · storefront | ✅ | Five dashboard tabs, four storefront tabs |
| Lambda handler + bundle | ✅ | `server/lambda.ts` + esbuild step in `npm run build` |

### 3b. Which screens are wired, and which are drawn

Every screen exists, is responsive, and is bilingual. What differs is where its data comes
from, and that distinction is worth keeping visible.

| Reads real data | Reads `app/lib/sample-data.ts` |
|---|---|
| Home, category, search, item, auction, sell (categories), auth, cart (its Cart tab), checkout | Cart's "My offers" tab, order, ship, review, dashboard, storefront |

The right-hand column above is otherwise **as this file last recorded it, not freshly
re-audited this pass** — `checkout.tsx` in particular reads real data via `~/lib/api.server`
(`getListing`/`getOffer`/`getProfile`/`checkout`) with no `sample-data.ts` import at all, so
"checkout" in that column may already have been stale before this session touched anything.
Worth a real pass to re-verify every row here rather than trusting either column blindly.

Item and auction are the one partial exception: the listing itself, its related items, and
its categories are real API reads, but the seller-trust block, price-history chart, and
ask-the-seller thread fall back to `sample-data.ts` because `ListingDetailDto` does not yet
carry seller identity, and there is no price-history or Q&A endpoint. Same `TODO(api)`
convention, same one-line swap once those exist.

`sample-data.ts` says at the top what it is and what the rule is: **loaders read from it,
components never do.** Every screen that uses it carries a `TODO(api)` at its loader
naming the endpoint that will replace it. Swapping one for a real fetch is a one-line
change in one file, because the component already takes its data as props.

The offer, order, message, review and profile repositories are the scaffolded half of the
domain — see §7 — so this is the same boundary the backend already draws, not a new one.

### 3c. Responsive behaviour

The handoff specifies four breakpoints. All four are implemented in `app/styles/app.css`,
and the visibility utilities are named for intent (`.only-desktop`, `.only-mobile`) rather
than for a pixel count.

| Width | What changes |
|---|---|
| ≥901px | Full desktop: sticky rails, persistent filter sidebar, hover quick-view |
| ≤900px | Drawer nav, detail grid collapses to gallery → rail → rest, thumbnails scroll sideways, filters become a bottom sheet, fixed bottom action bar |
| ≤700px | The notification panel becomes a bottom sheet |
| ≤640px | Item grid drops to two columns, gaps to 12px, price steps to 14px |
| `(hover: none)` | The hover overlay is suppressed and a permanent 34px watchlist button appears |

Two details worth not undoing. The detail grid reorders on a phone so **price and buy come
before the description** — a `flex` column with explicit `order`, not a reflow. And
dashboard tables keep their columns and scroll sideways rather than reflowing into cards,
because a seller comparing views against watchers is reading across a row.

### Infrastructure and docs

**All three CDK stacks synthesize. `npm audit`: 0 vulnerabilities.**

| Component | Status |
|---|---|
| `DataStack` — table + 5 GSIs, two buckets | ✅ |
| `AuthStack` — Cognito pool, client, staff group | ✅ |
| `AppStack` — two Lambdas, HTTP API, CloudFront, WAF | ✅ |
| `README.md`, `docs/{architecture,data-model,security,configuration}.md` | ✅ |

---

## 4. Layout

```
Koleksi.id/
├─ CONTINUATION.md            ← this file
├─ design/                    ← vendored handoff, read-only reference
├─ backend/
│  ├─ Directory.Build.props   ← solution-wide hardening, see §6
│  ├─ Koleksi.slnx
│  ├─ src/
│  │  ├─ Koleksi.Domain/          entities, value objects, invariants. No dependencies.
│  │  ├─ Koleksi.Application/     handlers + the interfaces infrastructure implements
│  │  ├─ Koleksi.Infrastructure/  DynamoDB, S3, Cognito, payments
│  │  └─ Koleksi.Api/             composition root, endpoints, middleware
│  └─ tests/
│     ├─ Directory.Build.props    ← test-only relaxations (CA1707)
│     ├─ Koleksi.Domain.Tests/         invariants, no host, no I/O
│     ├─ Koleksi.Infrastructure.Tests/ keys, cursors, signatures, config validation
│     ├─ Koleksi.Infrastructure.IntegrationTests/  the repositories against real DynamoDB
│     └─ Koleksi.Api.Tests/            the real Program over WebApplicationFactory
├─ frontend/
│  ├─ app/
│  │  ├─ components/          shared UI, all presentational
│  │  ├─ lib/                 i18n, formatting, API client (.server = server-only)
│  │  ├─ routes/              one file per URL
│  │  ├─ styles/              tokens.css (flattened) + app.css
│  │  ├─ root.tsx             document shell, locale, error boundary
│  │  └─ routes.ts            the route table — URLs are product surface
│  └─ server/lambda.ts        hand-rolled API Gateway v2 adapter
├─ infra/
│  ├─ bin/koleksi.ts          stage selection, tagging
│  └─ lib/{data,auth,app}-stack.ts
├─ scripts/
│  └─ dev-table.cs            creates the local table from the synthesized CDK template
└─ docs/                      architecture · data-model · security · configuration
```

Dependency direction is **Domain ← Application ← Infrastructure ← Api**, enforced by
project references. Domain references nothing. If you find yourself wanting an AWS type
in Domain, the abstraction belongs in `Application/Abstractions` instead.

---

## 5. Domain rules that are easy to break by accident

These are encoded in tests. If a change makes one of them fail, the change is wrong until
proven otherwise.

**Money** (`Domain/Common/Money.cs`)
- `long Rupiah`, whole units, checked arithmetic, cannot be negative.
- `ToString()` pins its own `NumberFormatInfo` so `Rp1.250.000` renders identically
  regardless of ambient culture. Necessary because of `InvariantGlobalization`.

**Identifiers** (`Domain/Common/EntityId.cs`)
- UUIDv7 encoded big-endian as 26-char Crockford base-32.
- Time-ordered, so it works directly as a DynamoDB sort key, while remaining unguessable.
- `Slug.ExtractId` reads the id from the last hyphen segment of a URL slug and returns
  `null` for anything that is not a valid id — this is the path-traversal guard.

**Slugs** (`Domain/Common/Slug.cs`) — ⚠️ *this one bit us already*
- Uses an **explicit char→ASCII transliteration table**, not `String.Normalize`.
- Because `InvariantGlobalization=true`, `Normalize(NormalizationForm.FormD)` is a silent
  no-op in production. The obvious accent-stripping implementation passes every test on a
  developer machine with ICU and ships `pok-mon` for `Pokémon` to production.
- **Do not "simplify" this file back to `Normalize`.**

**Auctions** (`Domain/Auctions/Auction.cs`)
- `IncrementFor(price)` is a *function of the price it is applied to*, not of
  `CurrentPrice`. When a challenger leaps past the leader's maximum, the settling price
  derives from that maximum and must use that maximum's tier. Getting this wrong produced
  a real bug (Rp5.050.000 where Rp5.100.000 was correct).
- `LeadingMaximum` is **server-side only**. Never put it on a response model — leaking it
  lets a rival walk the price to one increment below it at zero risk.
- `PlaceBidResult.OutbidUserId` is likewise server-side only, for notification.
- Sellers cannot bid on their own auctions (shill floor).
- A bid inside the final 2 minutes extends the close time by 2 minutes.
- An auction with any bids cannot be cancelled by the seller.

**Orders / escrow** (`Domain/Orders/Order.cs`)
- `ConfirmPayment` is idempotent on an already-paid order and throws
  `order.amount_mismatch` on *any* amount mismatch. Webhooks are delivered at least once.
- Addresses, courier choice, and order lines are **snapshots**, copied not referenced.
  They are the evidence in a dispute; a seller editing a listing must not rewrite history.
- Auto-release is 3 days after delivery, and `Release` refuses while a dispute is open.
- Order numbers are `KLK-{last 6 of id}` — deliberately not sequential. Sequential numbers
  leak transaction volume and let anyone probe for other people's orders.

**Offers** (`Domain/Offers/Offer.cs`)
- 24-hour validity; a counter-offer restarts the window.
- Platform floor of 20% of asking, applied before the seller's own auto-decline
  threshold, and both are applied *immediately* so a lowball buyer is not left waiting a
  day for a rejection that was certain.

---

## 6. Security posture

The user's standing instruction is **"keep security above anything else."** Everything
below is load-bearing. Removing any of it needs an explicit decision, not a refactor.

**Build-level** (`backend/Directory.Build.props`)
- `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`.
- NuGet lock files, `RestoreLockedMode` in CI, `NuGetAudit` at `low` severity across all
  dependencies including transitive.
- `InvariantGlobalization=true` for cold start — with the standing consequence that
  **no ICU-dependent API may be used**. See the slug note in §5.

**Token validation** (`Api/Program.cs`)
- RS256 only. Leaving the algorithm list open invites `alg: none` and the
  HMAC-signed-with-the-public-key confusion attack.
- Issuer validated against the Cognito authority, which is *derived* from the pool id
  rather than configured separately, so it cannot be pointed elsewhere by one setting.
- `client_id` checked explicitly (Cognito access tokens do not use `aud`), so a token
  minted for another client of the same pool is rejected.
- `token_use == "access"` enforced — an **id token must never be accepted as a credential**.
- `ClockSkew` reduced from the 5-minute default to 30 seconds.
- `MapInboundClaims = false`, so the principal's claim names are the token's. See §11 —
  leaving the default on made every authenticated caller look anonymous.

Every one of these is asserted by `Koleksi.Api.Tests/AuthenticationTests.cs` against
tokens that are genuinely signed. Do not "simplify" that suite into a fake authentication
scheme: a stubbed handler proves only that the stub works.

**Authorization**
- `FallbackPolicy = RequireAuthenticatedUser()`. Every endpoint requires a token unless it
  carries an explicit `AllowAnonymous`, so an omission fails closed and opting out is a
  visible, reviewable line of code.
- Caller identity always comes from `ICurrentUser` (the validated principal), **never**
  from a request body. A body that names its own bidder lets anyone bid as anyone.

**Transport and browser**
- Explicit CORS origin allowlist from config; `*` is rejected at startup because the
  policy allows credentials. Origins must be bare (scheme/host/port) — a trailing path
  silently never matches.
- Security headers on *every* response including errors: `nosniff`, `DENY`,
  `default-src 'none'`, `strict-origin-when-cross-origin`, a `Permissions-Policy` denying
  camera/mic/geo/payment, default `Cache-Control: no-store`, and `Server` removed.

**Abuse**
- Rate limiting partitioned by `sub` when authenticated and by client IP otherwise.
  IP-only would let one user behind a shared mobile NAT exhaust a whole city's budget;
  identity-only would leave anonymous traffic unlimited.
- Bidding gets its own tighter bucket — most abusable write, most expensive to serve.
- `UseForwardedHeaders` must stay **first** in the pipeline. Without it every request
  partitions by the API Gateway's address, i.e. one bucket for the entire internet.

**Data handling**
- Errors: `DomainException` codes are safe to return; everything else yields an opaque
  message plus a correlation id, with the detail going only to the log.
- Media: uploads go straight to S3 via a presigned grant, never through Lambda. The server
  mints the key, pins the content type, and caps the length. **SVG is excluded from the
  image allowlist on purpose — it executes script, and serving one from our own origin is
  stored XSS.**
- Identity documents go to a *separate private bucket the CDN cannot reach*. Startup fails
  if it is configured to equal the public bucket.
- Payment webhook signatures are verified in **constant time**, and the shared key is read
  from Secrets Manager by ARN — never from config, env vars, or a template.
- The health check is **liveness only**. It deliberately does not probe DynamoDB or
  Cognito: a dependency blip must not make the load balancer pull every healthy instance.

---

## 7. The data model, and what it can and cannot do

One table, `pk` + `sk`, with four global secondary indexes. Keys are built **only** in
`Persistence/DynamoTable.cs` — a repository that composes its own key string is one typo
away from writing an item nothing can read back, and that typo is invisible in review.

| Entity | `pk` | `sk` |
|---|---|---|
| Listing | `LISTING#{id}` | `#META` |
| Auction | `LISTING#{id}` | `#AUCTION` |
| Bid | `LISTING#{id}` | `BID#{bidId}` |
| Browse projection | `LISTING#{id}` | `#BROWSE` |
| Facet counter | `FACETS#{categoryId}` | `{facetKey}#{value}` or `#TOTAL` |
| Idempotency claim | `IDEMPOTENCY#{scope}#{key}` | `#CLAIM` |

Everything for one listing shares a partition, which is what lets a bid and the auction it
changes be written in one transaction.

| Index | Partition | Sort | Serves |
|---|---|---|---|
| `gsi_seller` | `SELLER#{id}` | `{status}#{listingId}` | Dashboard tabs, as prefix queries |
| `gsi_browse_new` | `BROWSE#{categoryId}` | `{listingId}` | Newest first (ids are time-ordered) |
| `gsi_browse_price` | `BROWSE#{categoryId}` | `{paddedPrice}#{id}` | Price sort |
| `gsi_browse_ending` | `BROWSE_END#{categoryId}` | `{endsAt}#{id}` | "Ending soon" — sparse, live auctions only |
| `gsi_auction_end` | `AUCTION#Live#{shard}` | `{endsAt}#{id}` | Close sweep — sharded ×4, projects **ALL** |

### Things that will bite you here

- **`gsi_auction_end` must project ALL attributes.** The close sweep rehydrates a full
  aggregate from what it reads. Narrowing the projection returns auctions with a zeroed
  price and no error.
- **Sharding uses FNV-1a, not `string.GetHashCode()`**, which is randomised per process.
  With the runtime hash, two Lambda instances place the same listing in different shards
  and the auction appears twice in the sweep at two different close times. *Any hash in a
  persisted key must be stable across processes.*
- **Numbered paging walks from page 1.** DynamoDB has no offset. `BrowseQuery.MaxPageNumber`
  (200) is what turns that from an unbounded cost into a known one; `MaxQueryRoundTrips`
  (20) bounds it a second time so one unselective filter cannot hold a Lambda open.
- **Counts are approximate and err high.** Facet counters are per-category, so they answer
  "how many active PSA 10 cards exist", not "how many of your current results are PSA 10".
  Price, city, and text filters are not counted. Erring high keeps every result reachable;
  erring low would shorten the pager and hide listings.
- **Free text is a `contains` over a lowercased title.** No tokens, no stemming, no
  ranking — which is why `BrowseSort.Relevance` falls back to newest. This is the first
  thing OpenSearch replaces, and `IListingSearchIndex` exists precisely so it can be
  replaced without touching a handler.

### Offer, order, and cart repositories are registered and live

This section used to say these were "deliberately unregistered" scaffolding. That was true
at some earlier point this file did not get updated to reflect — by the time this pass
started, `IOfferRepository` and `IOrderRepository` were already registered in
`InfrastructureServiceCollectionExtensions.AddInfrastructure`, `MakeOfferHandler` /
`CheckoutHandler` / `ConfirmOrderPaymentHandler` were real and tested, and `OfferEndpoints`
/ `OrderEndpoints` were mapped in `Program.cs`. `ICartRepository` joined them in this pass.
The principle behind the old warning still holds, though: `builder.Host
.UseDefaultServiceProvider(options => options.ValidateOnBuild = true)` in `Program.cs` is
what turns "a handler was added without its repository registered" into a failed
deployment instead of a 500 mid-checkout — see the comment there. Don't paper over a
missing registration with a throwing stub; register the real thing.

## 8. Next step

In priority order. The first is now the gap that most weakens what already exists.

1. **Close the CSP gap** (`TODO(csp)` in `infra/lib/app-stack.ts`). `script-src` still
   carries `'unsafe-inline'` because React Router serialises loader data inline. Thread a
   per-request nonce through `entry.server` and drop it. This is the single weakest line in
   the security posture — see `docs/security.md` §11.
2. **Self-host Archivo** (`TODO(fonts)` in `app/root.tsx`). Removes a third-party request
   on every page and the `fonts.googleapis.com` entries from the CSP.
3. **Auction close sweep** — an EventBridge-scheduled Lambda over `ListDueForCloseAsync`.
   Auctions currently never close on their own.
4. Checkout, escrow, and cart checkout are real (see §3's notes at the top). What is left
   in this family: **reviews and messaging** are still unbuilt, and the dashboard's
   Offers/Messages/Payouts tabs (Listings and Orders are already real) still read
   `sample-data.ts` — wiring those up is straightforward now that `IOfferRepository` /
   `IOrderRepository` are registered and their handlers exist; it is a frontend-only
   remaining step, not a backend one.
5. **A shared payment session across a multi-seller cart checkout.** Today
   `CheckoutCartHandler` starts one independent `IPaymentGateway` session per resulting
   order. `PaymentRequest.ReferenceId`'s doc comment already anticipated "every order in
   one checkout" sharing a reference, but `ConfirmOrderPaymentHandler` resolves a webhook's
   reference to exactly one order by number — extending that to fan out to several orders
   touches the payment webhook's contract, the most security-sensitive path in the
   product, and was deliberately left alone this pass. See `CheckoutCartHandler`'s remarks.
6. **"My offers"** on `/keranjang` still reads `sample-data.ts`
   (`GET /api/offers?role=buyer` per the API surface note below) — the Cart tab on the same
   screen is real as of this pass; the Offers tab was not part of what this pass touched.

Auth, profile, sell/publish, search, the item page, and the dashboard's Listings tab
(previously items 3 and part of item 5 here) are done — see the notes at the top of §3.
What is still open on those fronts, if it comes up again:

- **Give Google sign-in real credentials.** `cdk deploy -c stage=dev -c
  googleClientId=<id> -c googleClientSecretArn=<arn>`, then read the `GoogleRedirectUri`
  stack output into the Google Cloud Console client, then set `Cognito:HostedUiDomain` on
  the API and `KOLEKSI_COGNITO_DOMAIN`/`KOLEKSI_COGNITO_CLIENT_ID` on the frontend. See
  `docs/configuration.md`'s "Google sign-in" section — it has the exact steps.
- **Addresses are one array field on `UserProfile`, not their own DynamoDB items or
  endpoints.** The originally-sketched plan called for `POST/PUT/DELETE
  /api/profile/addresses/{id}`; the shipped design instead has the whole address list ride
  along inside `PUT /api/profile`, because that is how the screen's own "Save profile"
  button already worked — one save, one button, one loading state. Revisit only if a
  reason emerges to edit one address without touching the rest of the form.
- **The bank account and ID-verification "Change"/"Start" actions never show an edit
  affordance once done** (`VerificationRow`'s `state === "done"` branch only ever renders a
  tag). This mirrors the original placeholder UI's behaviour exactly, not a regression —
  worth revisiting if sellers need to update a bank account after first linking one.
- **There is no edit-a-saved-draft flow.** `sell.tsx` only creates; the dashboard's "Edit"
  menu item and its bulk "Archive selected" button are still inert placeholders (per-row
  "Archive" is the one dashboard write that is real). A seller who saves a draft has to get
  it right in one sitting or start over.

Smaller, and worth doing alongside whichever of the above comes next:

- **Turn on minimal-API validation** (`builder.Services.AddValidation()`), then annotate
  request records and let malformed input answer `400` instead of the domain's `409`. Until
  that happens, **do not put a validation attribute on a request record** — minimal APIs do
  not run it, so it reads as a control while enforcing nothing. See §11.
- **`Retry-After` on `auction.too_busy`.** The bidder is told to try again with no hint of
  when, and a client that retries immediately makes the contention worse.

### Agreed scope for this pass

Complete end-to-end: **auth · profile · catalog · browse + facets · item page · sell/publish
· dashboard's Listings tab**.
Scaffolded with typed contracts and `TODO` markers: auction settlement, checkout, escrow,
the dashboard's Messages/Offers/Orders/Payouts tabs, reviews, messaging, payouts.

*(This paragraph describes the 2026-08-18 pass and was not rewritten for later ones — by
2026-08-23, checkout, escrow, and the cart are real; see the notes at the top of §3 for
what is current. Kept as-is rather than edited in place so this remains a true record of
what that pass actually agreed to, instead of being silently rewritten into something it
wasn't.)*

---

## 9. API surface as it stands

| Method | Route | Auth | Notes |
|---|---|---|---|
| `GET` | `/health` | anonymous | Liveness only — does not probe DynamoDB or Cognito |
| `POST` | `/api/auth/login` | anonymous | SRP against Cognito. One opaque failure for every cause; tightest rate-limit bucket in the product |
| `POST` | `/api/auth/register` | anonymous | Returns confirmation state, never tokens |
| `POST` | `/api/auth/confirm` | anonymous | Submits the code emailed at registration |
| `POST` | `/api/auth/confirm/resend` | anonymous | Always `200`, regardless of outcome |
| `POST` | `/api/auth/otp/request` | anonymous | Primary sign-in path. Always returns a session token, even for a non-existent address |
| `POST` | `/api/auth/otp/verify` | anonymous | Not idempotency-wrapped — the code is already single-use |
| `POST` | `/api/auth/google/exchange` | anonymous | PKCE authorization-code exchange against the hosted UI; `null` (→ `401`) until `Cognito:HostedUiDomain` is configured |
| `POST` | `/api/auth/refresh` | anonymous | Exchanges a refresh token; the web tier holds it, never the browser |
| `POST` | `/api/auth/logout` | anonymous | Revokes the refresh token at the provider |
| `GET` | `/api/catalog/categories` | anonymous | Output-cached 10 min |
| `GET` | `/api/catalog/browse` | anonymous | `?kategori=&q=&kota=&min=&max=&urut=&page=&size=&f.{key}=` |
| `GET` | `/api/catalog/listings/{slug}-{id}` | anonymous | **Not** output-cached — visibility varies by caller |
| `POST` | `/api/auctions/{listingId}/bids` | **required** | Tighter rate-limit bucket keyed on `sub` |
| `GET` | `/api/profile` | **required** | Empty profile, not `404`, for an account that never saved one |
| `PUT` | `/api/profile` | **required** | Full name + the whole address list, replaced wholesale |
| `POST` | `/api/profile/avatar` | **required** | Refuses a key `IMediaStorage.VerifyUploadedAsync` cannot confirm |
| `POST` | `/api/profile/bank-account` | **required** | Every response masks the account number to its last four digits |
| `POST` | `/api/profile/id-verification` | **required** | Moves `IdVerificationStatus` to `Pending`; an operator resolves it manually — no automated KYC |
| `POST` | `/api/profile/phone/otp` | **required** | Via SNS; falls back to SES on the caller's own account email when `phone` is blank. Local dev stubs log instead of sending |
| `POST` | `/api/profile/phone/otp/verify` | **required** | Not idempotency-wrapped, same reasoning as `/api/auth/otp/verify` |
| `POST` | `/api/media/upload-grant` | **required** | The one HTTP door onto `IMediaStorage.CreateUploadGrantAsync`; owner is always the caller, never client-supplied |
| `POST` | `/api/listings` | **required** | Creates a draft, or publishes immediately — see `CreateListingCommand.Publish`. The call that indexes a listing into browse |
| `POST` | `/api/listings/{id}/archive` | **required** | `.forbidden` code (not owner) maps to `403`, not the usual `409` — see `DomainExceptionHandler` |
| `GET` | `/api/listings/mine` | **required** | Backs the dashboard's Listings tab; up to 50 rows, no paging yet |
| `GET` | `/api/cart` | **required** | Live join against `IListingRepository` per item; sweeps out any entry that stopped being purchasable |
| `POST` | `/api/cart/items` | **required** | `{ listingId }`. Naturally idempotent (re-adding just refreshes `addedAt`) — not `IIdempotencyStore`-wrapped |
| `DELETE` | `/api/cart/items/{listingId}` | **required** | Deleting an absent row is already a no-op; same idempotency reasoning as the add |
| `POST` | `/api/cart/checkout` | **required** | One `Order` per seller among the selected items, sharing one address; see the cart note at the top of §3 for what it does and does not do |

Everything not listed requires a token, because the authorization fallback policy demands
one. The `AllowAnonymous` calls in `AuthEndpoints`/`CatalogEndpoints` are the complete list
of exceptions. **This table is not exhaustive** — `OfferEndpoints`, `OrderEndpoints`, and
`PaymentEndpoints` (`/api/offers/*`, `/api/orders/*`, `/api/payments/webhook`) all exist and
are real (see §7's corrected note) but were never added here; only the cart rows above were
added this pass. Worth a pass to fill the rest in.

---

## 10. Commands

```bash
# Build everything, warnings are errors
dotnet build backend/Koleksi.slnx

# Start DynamoDB Local first, or the storage tests skip (everything else still runs)
docker run -d --name koleksi-dynamodb-local -p 8000:8000 \
  amazon/dynamodb-local:latest -jar DynamoDBLocal.jar -inMemory -sharedDb

# Every test project: 90 domain + 62 infrastructure + 58 storage + 135 API (132 passing,
# 3 pre-existing BrowseEndpointTests failures — see §3) = 285
dotnet test backend/Koleksi.slnx

# Or one at a time
dotnet test backend/tests/Koleksi.Domain.Tests
dotnet test backend/tests/Koleksi.Infrastructure.Tests
dotnet test backend/tests/Koleksi.Infrastructure.IntegrationTests
dotnet test backend/tests/Koleksi.Api.Tests

# What CI does: fail the restore if a lock file does not match
CI=true dotnet restore backend/Koleksi.slnx

# Refresh lock files after changing package versions
dotnet restore backend/Koleksi.slnx --force-evaluate

# Run the whole thing locally. Order matters; see docs/configuration.md.
docker run -d --name koleksi-dynamodb-local -p 8000:8000 \
  amazon/dynamodb-local:latest -jar DynamoDBLocal.jar -inMemory -sharedDb
cd infra && npx cdk synth -c stage=dev >/dev/null && cd ..
dotnet run scripts/dev-table.cs                     # creates koleksi-dev from the template
dotnet run --project backend/src/Koleksi.Api        # http://localhost:5033
cd frontend
KOLEKSI_API_URL=http://localhost:5033 \
  SESSION_SECRET="$(openssl rand -base64 48)" npm run dev         # http://127.0.0.1:5173

# Frontend
cd frontend && npm run typecheck && npm run build && npm audit

# Infrastructure — must synth clean before any deploy
cd infra && npx tsc --noEmit && npx cdk synth -c stage=dev
```

Full deploy order (assets must exist before synth):

```bash
dotnet publish backend/src/Koleksi.Api -c Release -r linux-arm64 --self-contained \
  -p:PublishReadyToRun=true -o backend/publish/api
cd frontend && npm run build && cd ..
cd infra && npx cdk deploy -c stage=dev --all
```

Note: `dotnet add package` can exceed a 2-minute tool timeout on this machine. Run package
additions in the background with a longer timeout, or edit the `.csproj` directly and
restore.

---

## 11. Traps already hit — do not re-discover these

| Symptom | Cause | Fix |
|---|---|---|
| `[commonjs--resolver] Server-only module referenced by client` at build time | A route's **component** imported `~/lib/session.server`. React Router strips server code from `loader`/`action` only; anything a component touches must be client-safe | Pure helpers (`LOGIN_PATH`, `safeRedirectPath`) moved to `app/lib/navigation.ts`. The build catching this is the `.server` guard working — do not silence it |
| The whole frontend source was being published to the CDN | `vite.config.ts` set `build.sourcemap: true` with a comment claiming it applied to the server bundle only. It is not per-environment: 29 `.map` files were emitted beside the client assets, each with a `sourceMappingURL` pointing at it. Vite warns about this on every build | `sourcemap: false`. Server traces still map, because the Lambda bundle gets its own map from the `build:lambda` esbuild step, and that file is never served to a browser |
| Every auth test after the first gets `429` | The sign-in bucket keys on the caller's address, and `TestServer` leaves `RemoteIpAddress` null — so every request shared one partition | `LoopbackConnectionFilter` gives requests a loopback peer, which is inside the default known-networks range, so `X-Forwarded-For` is honoured by the real `UseForwardedHeaders` config and each test can present its own address |
| A seller edits their listing and its view and watcher counts silently reset to zero | `UpdateAsync` used `PutItem`, which **replaces the whole item**. `ListingItem.ToItem` deliberately omits the counters because they are maintained by atomic adds — so the put deleted them every time. Writing them from the aggregate instead would have rolled back every view counted while the seller typed | `UpdateItem` with a `SET` over the mapped attributes: assign what this layer owns, leave what it does not. **Auction and browse rows keep using `PutItem` on purpose** — they rely on replace semantics to *drop* sparse index keys, which is how a closed auction leaves the sweep's index |
| `UpdateAsync` on a listing that was never created quietly creates one | The version-0 branch read `attribute_not_exists(#v) OR #v = :expected`, which is true for a row that does not exist — so the update was an upsert that bypassed every check `AddAsync` makes. The legacy-row case it was defending against cannot occur: every write path has always written `version` | `attribute_exists(pk) AND #v = :expected`, with no special case. A future migration that adds a field must backfill rather than rely on absence |
| Every authenticated call answers `401 auth.required` with a valid token | `JwtBearerOptions.MapInboundClaims` defaults to **true**, renaming `sub` to a WS-Federation URI. `ICurrentUser.UserId` reads `sub` and finds nothing, so the caller looks anonymous — and the rate limiter partitions everyone by IP | `options.MapInboundClaims = false` in `Program.cs`. Found by `AuthenticationTests`, not by review |
| A bid that loses four races answers `503`, and no warning is logged | `catch (…) when (attempt < MaxAttempts)` let the exception escape on the final attempt, so `LogBidContentionExhausted` and `auction.too_busy` were both unreachable | Catch unconditionally and `break` on the last attempt — `PlaceBid.cs` |
| `[Range(1, long.MaxValue)]` on a request record enforces nothing | Minimal APIs do not run data annotations without `AddValidation()` | Attribute removed; the real check is `Money.TryFromRupiah` plus the auction rules. **Do not add one back** until validation is switched on — see §8 |
| `Pokémon` slugs to `pok-mon` in prod, passes locally | `InvariantGlobalization` makes `String.Normalize` a no-op | Explicit transliteration table in `Slug.cs` |
| Auction settles at Rp5.050.000 instead of Rp5.100.000 | Increment derived from `CurrentPrice`, not from the price it settles at | `static IncrementFor(Money)` applied at both settlement sites |
| `MSB4024`, "An XML comment cannot contain '--'" | A `--flag` written inside an XML comment in `Directory.Build.props` | Reword the comment in prose |
| `CA1707` on ~80 test methods | Underscored test names | `tests/Directory.Build.props` adds `NoWarn` — scoped to tests only, not globally |
| `CA2264` on `ArgumentNullException.ThrowIfNull` | Argument was a readonly record struct, so the check was dead | Delete the check; the analyzer was right |
| `StatusCodes.Status499ClientClosedRequest` does not exist | It is not a real constant | `StatusCodesExtra.ClientClosedRequest` in `DomainExceptionHandler.cs` |
| `CA1050` on the policy classes | Top-level `Program.cs` puts trailing types in the global namespace | Moved to `Api/Security/Policies.cs` under a namespace |
| CDK: `DependencyCycle` between Data and App stacks | Passing the bucket *construct* to `S3BucketOrigin` writes a policy in Data naming the distribution in App, while App already reads the table from Data | Import the bucket by name in App; grant OAC in Data conditioned on `AWS:SourceAccount`. Documented at both ends. |
| CDK: `TS5025 Unknown compiler option` | `"//comment"` keys are fine at the top level of a JSON config but **not** inside `compilerOptions` | Use a real JSONC `//` comment |
| CDK: `Cannot find module '../lib/app-stack.js'` | The CDK CLI runs the app through ts-node in **CommonJS**, so ESM `.js` specifiers do not resolve | `module: CommonJS`, extensionless imports |
| npm audit: high-severity path traversal in Vite | `vite <= 6.4.2` | Pinned `6.4.3` |
| npm audit: `@architect/functions` advisory | Pulled in by `@react-router/architect` | Dropped the adapter; `server/lambda.ts` replaces it in ~80 lines we own |
| `ConfigureAppConfiguration` in a test host has no effect | `Program` reads and validates the `Koleksi` section while the builder is still being constructed, before a test-host configuration callback can reach it | The test factory sets `KOLEKSI__…` environment variables instead — which is also how the CDK app supplies them, so the binding path under test is the real one |
| The API starts but every catalogue read is a 500: "Failed to resolve AWS credentials" | Against DynamoDB Local there is no role to borrow from, but the SDK still signs the request — so a developer with no AWS profile could not run the API at all | `BuildDynamoClient` supplies static placeholders when `Dynamo:ServiceUrl` is set. Safe because validation now refuses that setting outside development, so the branch is unreachable in a deployed stage |
| `Dynamo:ServiceUrl` could point a deployed stage anywhere | The comment claimed "configuration validation rejects it in a deployed stage". It did not — nothing checked. A comment asserting a control that does not exist is worse than no comment | `Validate(allowLocalEndpoints)`, passed `builder.Environment.IsDevelopment()` from the composition root |
| `ServiceUrl = "localhost:8000"` passes validation and then fails to connect | `Uri.TryCreate(…, UriKind.Absolute)` **succeeds**: it reads `localhost` as the scheme. Absoluteness alone is not a URL check | Require the scheme to be `http` or `https`, as the other URL rules already do |
| DynamoDB Local answers "The Access Key ID or security token is invalid" | Version 3 checks that the access key is *shaped* like an AWS one, even though it never checks that it exists | Use AWS's documentation placeholders — `AKIAIOSFODNN7EXAMPLE` / `wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY` |
| An `Edit` cannot match a line the `Read` clearly shows | Invisible control characters the write tools emitted earlier (§11 has bitten this project before) | Rewrite the whole file with `Write` rather than fighting the match |

---

## 12. Working agreements from the user

Carry these forward verbatim into any continuation prompt:

> "make sure to implement the best practice and production ready, also apply clean code
> throughout the project"

> "make sure to always asking yourself if this edit that you make is the best choice"

> "keep it mind that i wan you to always keep security above anything else"

> "make sure to make a md file so if the token runsout i can continue the woek"
