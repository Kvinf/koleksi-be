# Security

The standing instruction on this project is to keep security above anything else. This
document records the controls that exist and, more usefully, *what each one stops* — so
that anyone considering removing one knows what they are trading away.

Everything here is implemented. Where something is not, it says so.

---

## 1. Identity

**Cognito holds every credential; this service holds none.** No password hashes, no
signing keys. A full compromise of the API cannot mint a token for anyone, because there
is nothing here to mint one with.

Token validation (`backend/src/Koleksi.Api/Program.cs`):

| Control | What it stops |
|---|---|
| `ValidAlgorithms = [RsaSha256]` | The `alg: none` attack, and HMAC-signed-with-the-public-key confusion. An open algorithm list is the classic JWT hole. |
| Issuer derived from the pool id, not configured separately | Nobody can point the API at an issuer we do not control by editing one setting. |
| `client_id` checked explicitly | Cognito access tokens carry the app client in `client_id`, not `aud`. Without this check, a token minted for *any other client of the same pool* is accepted — including one an attacker registered. |
| `token_use == "access"` | An **id token must never be accepted as a credential**. It is a description of a user handed to a client, and it is far more freely available. |
| `ClockSkew = 30s` (default is 5 min) | Shrinks the window a stolen, just-expired token stays usable by an order of magnitude. |
| `MapInboundClaims = false` | Keeps the principal's claim names identical to the token's. The default rewrites `sub` into a WS-Federation URI while leaving Cognito's own claims alone, so `ICurrentUser.UserId` finds nothing, every authenticated caller is treated as anonymous, and the rate limiter partitions them all by IP. |

Every row above is held to account by `backend/tests/Koleksi.Api.Tests/AuthenticationTests.cs`,
which presents correctly signed, structurally valid tokens that must still be refused — an
id token, a token for another app client, a token from another issuer, one that expired
ninety seconds ago, and an HS256 token signed with the public key. The test host swaps the
key Cognito publishes for one it owns and changes nothing else, so the handler under test
is the one that runs in production.

The `MapInboundClaims` row was found by that suite rather than by review.

Password policy (`infra/lib/auth-stack.ts`) is 8 characters with all four character
classes — uppercase, lowercase, digit, symbol. This is a product decision and it runs
against current guidance: NIST withdrew the "one of each class" advice because it produces
`Password1!` — which satisfies this policy exactly — and drives people to write passwords
down. What keeps it defensible is Cognito's `FeaturePlan.PLUS` breached-password check,
which refuses `Password1!` and its whole family however well they satisfy the ladder. That
check is worth more than every composition rule combined.

The same five rules are mirrored in `frontend/app/lib/password.ts`, which draws the live
checklist on the sign-up form and re-checks the submitted value in the route action.
Neither is a control — the pool is — but a form that promises something the pool does not
honour is worse than no form validation at all.

MFA is optional and **authenticator-app only**. SMS is excluded deliberately: SIM-swap is
the dominant account-takeover route in this market, and an SMS second factor is one a
SIM-swap defeats entirely.

`preventUserExistenceErrors` and code-style verification close the account enumeration
channel — a sign-in for an address that does not exist fails identically to a wrong
password.

### The local identity provider

`backend/src/Koleksi.Api/Development/` holds an in-process stand-in for the user pool, so
sign-in works on a laptop with no AWS account — the same reason DynamoDB Local stands in
for the table. It is an authentication bypass, so it is gated three ways, and any one of
them failing takes the Cognito path:

1. **`#if DEBUG`.** The Lambda publish is `-c Release`, so no deployed binary contains the
   code at all. That guarantee is the compiler's, not a reviewer's.
2. **`IHostEnvironment.IsDevelopment()`.** The test host runs as `Testing` and so never
   sees it.
3. **Placeholder pool ids.** Configuring a real pool switches the provider back with no
   other change.

What it does *not* replace is the verification path. Tokens are genuine RS256 JWTs with
Cognito's claim shape, checked by the same `JwtBearer` handler under the same parameters —
issuer, `client_id`, `token_use`, algorithm pinning, clock skew. Only the signing key is
swapped, exactly as `Koleksi.Api.Tests` swaps it. A local mode that stubbed the
authentication handler would prove the stub works and hide every mistake in the
configuration that actually ships.

Accounts live in memory for the life of the process, and passwords are checked against the
same policy the pool applies. The breached-password check is the one rule with no local
equivalent: it is a Cognito service feature rather than a rule this codebase holds.

---

## 2. Authorization

**The fallback policy requires authentication.** Every endpoint needs a valid token unless
it carries an explicit `AllowAnonymous`. This inverts the usual failure mode: forgetting to
protect a route fails *closed*, and opting out is a visible line of code that shows up in
review.

The complete set of anonymous routes is in `CatalogEndpoints` — categories, browse, and the
item page. They are anonymous because the handoff requires item and auction pages to be
crawlable, and a crawler carries no token.

**Caller identity always comes from `ICurrentUser`, never from a request body.** `CurrentUser`
reads only from the validated `ClaimsPrincipal`; there is no path in it that reads a header,
a query parameter, or a body field. A bid request that named its own bidder would let
anyone bid as anyone, so `PlaceBidCommand` carries an amount and nothing else.

**Not-found and not-permitted return the same answer.** `GetListingHandler` answers 404 both
for a listing that does not exist and for one the caller may not see. Distinguishing them
turns the endpoint into an oracle for which listings a moderator has removed.

---

## 3. Secrets

There is exactly one real secret in the system: the payment webhook signing key.

It is stored in Secrets Manager and referenced **by ARN**. It is never in configuration,
never in an environment variable, never in a CloudFormation template, and never in a build
artefact. Configuration validation refuses to start if `Payment:SecretArn` is not a
Secrets Manager ARN, so it cannot be "temporarily" inlined.

Everything else in `KoleksiOptions` — bucket names, table names, pool ids — is an
identifier, not a credential. What the Lambda may do with them is decided by its IAM role.

---

## 4. Payment webhooks

This is the highest-stakes code path in the backend. A forged webhook that verifies is a
free order: it moves an escrow order to *paid* without money having moved, and the seller
ships against it.

`PaymentWebhookVerifier` (`backend/src/Koleksi.Infrastructure/Payments/`) is deliberately a
small, dependency-free, separately testable type with no network access, no configuration
lookup, and no logging — nothing that could be stubbed out in a test that then proves
nothing.

- **Constant-time comparison** via `CryptographicOperations.FixedTimeEquals`. A byte-by-byte
  comparison that returns on first mismatch leaks, through response timing, how many leading
  bytes of a guessed signature were correct — enough to forge one in a few thousand requests.
- **The raw body is verified, before parsing.** Re-serialising parsed JSON changes whitespace
  and key order and would never match.
- **Unknown statuses map to `Failed`.** The default must never be "settled": an unrecognised
  status that defaults to paid is an order shipped for free.
- **A verified-but-unparseable payload is refused**, not acted on. Acting on a payload we
  cannot read is how an order gets marked paid by accident.

Duplicate deliveries are handled by `IIdempotencyStore`, whose claim is a *conditional write*
rather than a read-then-write — two concurrent deliveries must not both observe "unclaimed".
Keys are scoped per caller, so one caller can neither collide with nor probe for another's.

---

## 5. Media

Bytes never pass through the API. The client requests a grant, uploads straight to S3, and
returns only the object key. That keeps multi-megabyte attacker-controlled bytes out of our
execution environment entirely.

The grant is what makes it safe, and it is safe only because **the server decides every
part of it**:

- **The key is minted server-side.** If the client supplied any part of it, a key of
  `../../someone-else/avatar.jpg` — or simply another user's prefix — would be a grant to
  overwrite someone else's media.
- **The content type is signed.** Unsigned, a client could take a grant for a JPEG and
  upload `text/html`, and the bucket would serve it back as a page on our own origin.
- **The content length is signed.** Otherwise the size check only constrains what the client
  *claims*.
- **`VerifyUploadedAsync` re-checks the result** before a key is attached to a listing.

**SVG is excluded from the image allowlist on purpose.** It is a document format that
executes script; serving one from our own origin is stored XSS against every visitor to
that page. This is an allowlist, never a blocklist.

**Identity documents go to a separate private bucket with no CloudFront distribution and no
CORS rule at all.** There is no configuration change to a CDN that could expose them,
because there is no CDN. Startup fails if it is configured to equal the public bucket, and
`PublicUrl` throws rather than returning a URL for one. The API's IAM role can `PutObject`
and read metadata there — it cannot read the bytes back, so it cannot be made to hand one
to a caller.

---

## 6. Browser-facing controls

Applied twice, in the application (`SecurityHeadersMiddleware`) and at the edge
(`AppStack.securityHeadersPolicy`). Not redundant: a response served from CloudFront's
cache, or an error page CloudFront produces itself, never reaches the application's
middleware.

`nosniff` · `X-Frame-Options: DENY` · `frame-ancestors 'none'` · `object-src 'none'` ·
`base-uri 'self'` · `form-action 'self'` · `Referrer-Policy: strict-origin-when-cross-origin`
· `Permissions-Policy` denying camera, microphone, geolocation, and payment · HSTS for a
year with subdomains · `Server` removed.

The API's own CSP is `default-src 'none'` — a JSON response has no legitimate reason to
load a script, a frame, or an image.

**CORS is an explicit origin allowlist from configuration.** `*` is rejected at startup
because the policy allows credentials, and origins must be bare scheme/host/port — a
trailing path silently never matches, which presents as "CORS is broken" long after the
deployment that caused it.

**The frontend never sends cookies to the API** (`credentials: "omit"`). The API
authenticates with a bearer token and nothing else, which makes CSRF against it structurally
impossible rather than merely mitigated.

**JSON-LD is escaped for script context.** `JSON.stringify` alone leaves `<` intact, so a
seller-controlled title containing `</script>` would close the element and turn the rest of
the payload into markup. `StructuredData.tsx` escapes `<`, `>`, `&`, and the U+2028/U+2029
line terminators.

### Known gap

The page CSP includes `script-src 'unsafe-inline'`. React Router serialises loader data into
an inline script on every SSR page, and removing it requires threading a per-request nonce
through `entry.server` and every framework-emitted `<script>`. **This is the weakest line in
the posture and is marked `TODO(csp)` in `app-stack.ts`.** The policy still blocks external
script origins, which is the larger half of the protection — but it is a gap, not a
non-issue.

---

## 7. Abuse and denial of service

Three layers, each doing a different job:

1. **WAF**, in front of the API: AWS managed common rules, known-bad-inputs, IP reputation,
   and a coarse 2000-request-per-IP ceiling. This refuses traffic before it reaches a
   Lambda, so a flood costs a WAF evaluation rather than an invocation. The IP limit is
   deliberately generous — Indonesian mobile carriers put large numbers of users behind
   shared NAT, so a tight per-IP limit would block a whole city.
2. **Application rate limiting**, partitioned by `sub` when authenticated and by client IP
   otherwise. IP-only would let one user behind shared NAT exhaust everyone's budget;
   identity-only would leave anonymous traffic unlimited.
3. **A tighter bucket on bidding** — the most abusable write in the product and the most
   expensive to serve under contention.

`UseForwardedHeaders` must stay **first** in the pipeline. Without it every request
partitions by the API Gateway's own address: one bucket for the entire internet.

Input bounds exist at both the frontend and the API. The API's are the ones that matter for
correctness; the frontend's (`browse-query.server.ts`) exist so a hostile URL is trimmed
before it costs a round trip. `BrowseQuery.MaxPageSize` (48) and `MaxPageNumber` (200) turn
`?pageSize=100000` from a free denial-of-service primitive into a clamp.

---

## 8. Data integrity

**Optimistic concurrency everywhere state matters.** Every aggregate carries a `Version`,
and every write is conditioned on the value that was read. `Listing` gained its version
specifically because `UpdateAsync` documented a concurrency conflict with nothing to
condition on — and conditioning on `UpdatedAt` instead would be a timestamp doing a
concurrency token's job, failing only when two writes land in the same tick, which is
exactly the case it is meant to catch.

**Bids are written transactionally.** The auction and its history rows go in one
`TransactWriteItems` conditioned on the version. An auction whose price moved but whose
history row was lost is an auction where the winner is charged an amount no bid accounts
for — precisely what a losing bidder disputes, and what we would have no evidence to answer.

**Bid history is append-only.** No update path, no delete path. A row that can be rewritten
is not a record.

**Order snapshots are copied, not referenced.** Addresses, courier choice, and line items
are the evidence in a dispute; a seller editing a listing must not rewrite history.

**Order numbers are not sequential** (`KLK-{last 6 of id}`). Sequential numbers leak
transaction volume and let anyone probe for other people's orders.

---

## 9. Privacy

**Bidder handles are masked at the read boundary**, in `GetListingHandler`, not in a view
helper. Bid history is public on every auction page; an unmasked history would publish who
is spending what, let rivals target a known collector, and make shill-bidding rings trivial
to coordinate.

**Two values never leave the server**, and both are load-bearing:

- `Auction.LeadingMaximum` — leaking it lets a rival walk the price to exactly one
  increment below it at no risk, which destroys the entire proxy-bidding model.
- `PlaceBidResult.OutbidUserId` — it exists for notification. A bidder has no business
  learning another bidder's identity.

`AuctionDto` on the frontend carries neither. `viewerIsLeading` is a boolean about *you*,
which is all the design needs to render "You are the highest bidder".

**Error responses leak nothing.** `DomainException` codes are safe and useful to return;
everything else yields an opaque message plus a correlation id, with the detail going only
to the log. A forged webhook is logged *without* its body and offered signature —
attacker-controlled text does not belong in front of whoever reads the logs.

---

## 10. Supply chain

- `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, `EnforceCodeStyleInBuild`.
- NuGet lock files with `RestoreLockedMode` in CI.
- `NuGetAudit` at **low** severity across all dependencies including transitive.
- `npm audit` clean in both `frontend/` and `infra/` as of the last check.

Dependencies are actively declined rather than accumulated. `@react-router/architect` was
dropped because it pulls a flagged `@architect/functions`; Vite was pinned past a
high-severity path traversal; MediatR, Serilog, Swashbuckle, FluentAssertions, and a
third-party ULID package were all rejected for licence changes or in-box equivalents.

DI registration is explicit, never assembly-scanned — scanning registers whatever happens
to be in the assembly, including something nobody meant to expose. `ValidateOnBuild` and
`ValidateScopes` are on, so a missing registration is a failed deployment rather than a 500
during a checkout, and a singleton capturing per-request identity is caught at startup
rather than serving one caller's identity to everyone on a warm Lambda.

---

## 11. What is not done

Stated plainly, because an undocumented gap is worse than a known one.

- **CSP `'unsafe-inline'` for scripts** — §6. The most significant outstanding item.
- **Fonts load from Google Fonts** — a third-party request on every page and a leak of every
  visitor's IP. The font is OFL-licensed; self-hosting is a matter of committing two files.
- **No CAPTCHA or bot challenge** on sign-up. Rate limiting alone will not stop a determined
  account-farming operation.
- **Shill-bidding detection is a floor, not a ceiling.** Sellers cannot bid on their own
  auctions; related-account rings need a fraud service.
- **No customer-managed KMS key** for DynamoDB. The AWS-managed key encrypts at rest; a CMK
  would add rotation control and its own audit trail, at the cost of a KMS call per read.
- **No request-model validation at the boundary.** Minimal APIs do not run data annotations
  unless validation is added to the service collection, so malformed input is refused by the
  domain and answered `409` where `400` would be correct. Safe, but the wrong code — and an
  annotation left on a request record would look like a control while enforcing nothing.
- **No penetration test.** Nothing here has been tested by anyone but its author.
