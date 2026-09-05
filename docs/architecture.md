# Architecture

## The shape of a request

```
Browser
  │
  ├─ HTML / assets ─────────────► CloudFront ──► Web Lambda (Node 22, arm64)
  │                                                   │  React Router SSR
  │                                                   │  loaders call the API server-side
  │                                                   ▼
  └─ (never calls the API directly)              HTTP API + WAF
                                                      │
                                                 API Lambda (.NET 10, provided.al2023, arm64)
                                                      │
                                    ┌─────────────────┼─────────────────┐
                                    ▼                 ▼                 ▼
                               DynamoDB            S3 (2×)          Cognito JWKS
```

The browser never calls the API. Every read goes through a React Router loader running in
the web Lambda, which means the access token lives in a server-side session rather than
somewhere JavaScript on the page can reach it, and it means one round trip from a phone in
Bandung instead of two.

## Backend layering

**Domain ← Application ← Infrastructure ← Api**, enforced by project references.

| Project | Holds | Depends on |
|---|---|---|
| `Koleksi.Domain` | Entities, value objects, invariants | **Nothing** |
| `Koleksi.Application` | Use-case handlers, and the interfaces infrastructure implements | Domain |
| `Koleksi.Infrastructure` | DynamoDB, S3, Secrets Manager, payments | Application |
| `Koleksi.Api` | Composition root, endpoints, middleware | All of the above |

`Koleksi.Domain` referencing nothing is the load-bearing part. It means the auction rules,
the escrow state machine, and the money type can be tested with no AWS, no HTTP, and no
mocks — which is why there are 86 domain tests that run in under 200 milliseconds. If you
find yourself wanting an AWS type in Domain, the abstraction belongs in
`Application/Abstractions` instead.

The layering shows up in the test projects, one per boundary:

| Project | Runs | Needs |
|---|---|---|
| `Koleksi.Domain.Tests` | Invariants | Nothing |
| `Koleksi.Infrastructure.Tests` | Key composition, cursors, signatures, config validation | Nothing |
| `Koleksi.Infrastructure.IntegrationTests` | The repositories against a real DynamoDB | DynamoDB Local, or it skips |
| `Koleksi.Api.Tests` | The real `Program` over `WebApplicationFactory` | Nothing |

Only one of them wants anything installed, and it skips rather than fails without it. That
is deliberate: a suite with a mandatory setup step is a suite that stops being run, so
`dotnet test` has to stay green on a clean machine while still reporting, in its skip
count, exactly what was not exercised.

The storage suite earns its container. Two bugs it found on its first run — a seller edit
deleting a listing's view counters, and an update quietly creating a listing that no create
path had validated — are both invisible to a fake, because both are consequences of what
DynamoDB does with a request rather than of what our code intends.

### Vertical slices, not a mediator

Each use case is a plain handler class — `BrowseListingsHandler`, `GetListingHandler`,
`PlaceBidHandler` — resolved directly by the endpoint that needs it and registered
explicitly.

No MediatR. Beyond its licence change, an in-process dispatcher costs compile-time
knowledge of which handler serves which endpoint and makes every stack trace pass through
a generic pipeline. At this size it decouples two layers that ship together anyway.

Handlers are **scoped**, not singletons, because they depend on `ICurrentUser`, which is
per-request. A singleton would capture one caller's identity and hand it to everyone else
on the same warm Lambda. `ValidateScopes` is on so that mistake fails at startup.

### Two aggregates for one auction

A `Listing` is the descriptive half — title, photos, category. An `Auction` is the
transactional half — current price, bid history, close time. They share an id.

They are split because bids are written orders of magnitude more often than descriptions,
and a conditional write on the auction must not contend with a seller fixing a typo.

## Reads and writes

**Writes** are conditional, always. Every aggregate carries a `Version` and every write
asserts the value that was read. `PlaceBidHandler` retries a lost race up to four times,
re-reading and re-running the domain rules each time — never merging, because merging two
bids means inventing a price neither bidder offered.

**Reads** split by question, not by entity:

- `IListingRepository` answers *"give me this listing by id"* from the table.
- `IListingSearchIndex` answers *"page 3 of PSA 10 cards in Bandung under Rp5.000.000,
  with a count beside every facet option"* from a projection.

They are separate interfaces because DynamoDB serves the first well and the second badly.
The projection's real limits are documented in `CONTINUATION.md` §7 rather than hidden, and
the split is what lets an OpenSearch implementation replace it without touching a handler
or an endpoint.

## Frontend

React Router 7 in framework mode with SSR. A marketplace lives on inbound search traffic; a
client-only SPA ships an empty shell to a crawler and a blank screen to a shopper on a slow
connection.

- **URLs are Indonesian and are part of the product**: `/kartu/{slug}-{id}`,
  `/lelang/{slug}-{id}`, `/toko/{username}`, `/cari`. Changing one is a breaking change
  that needs a redirect.
- **The id resolves a listing; the slug is cosmetic.** A stale slug still works and is
  301'd to the canonical form, so a title change cannot break an existing link.
- **Filters and pagination live in the query string**, so a filtered result set is
  shareable, restorable with the back button, and crawlable.
- **Everything works without JavaScript.** Search is a GET form, filters are a GET form,
  pagination and the language toggle are links.

Caching is decided per route by the loader, and CloudFront honours it rather than
overriding it:

| Route | Policy | Why |
|---|---|---|
| Home, category, search | `public, max-age`, with `stale-while-revalidate` | Identical for every reader. |
| Item | `private, no-store` | Varies by caller — a seller sees their own draft. |
| Auction | `no-store` | The price changes with every bid. |

## Money and time

`Money` is a `long` of whole rupiah — there are no sen — with checked arithmetic and a
pinned number format, so `Rp1.250.000` renders identically regardless of ambient culture.
The frontend's `formatRupiah` is hand-rolled to match it byte for byte, because a
server/client disagreement about where the dots go is a hydration error on the most common
element on the site.

Identifiers are UUIDv7 in Crockford base-32 — time-ordered, so they work directly as
DynamoDB sort keys, while remaining unguessable.

Auction close times render in `Asia/Jakarta`, pinned. Rendering them in the server's UTC or
the viewer's local zone would make two bidders disagree about when a lot ends.

## Why these platform choices

| Choice | Reason |
|---|---|
| Lambda over containers | Traffic is spiky — an auction closing produces a burst that provisioned capacity would either throttle or overpay to absorb all week. |
| `provided.al2023` custom runtime | Cannot be broken by a managed-runtime deprecation, and the published output is exactly what was built and tested. |
| arm64 / Graviton | Cheaper per millisecond, better cold starts for .NET. |
| No VPC | No NAT gateway cost and no ENI cold-start penalty. The API is authenticated on every call regardless of who is calling. |
| `ap-southeast-3` (Jakarta) | Data residency, not latency. Indonesian identity documents and payment records should stay in-region. |
| `InvariantGlobalization` | Faster cold starts — at the cost that **no ICU-dependent API may be used**. This has already caused one production-shaped bug; see `CONTINUATION.md` §11. |
