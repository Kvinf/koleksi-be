# Data model

One DynamoDB table, `pk` + `sk`, with five global secondary indexes.

Keys are built in exactly one place — `Koleksi.Infrastructure/Persistence/DynamoTable.cs`.
A repository that composes its own key string is one typo away from writing an item nothing
can ever read back, and that typo is invisible in review.

---

## Items

| Entity | `pk` | `sk` |
|---|---|---|
| Listing | `LISTING#{id}` | `#META` |
| Auction | `LISTING#{id}` | `#AUCTION` |
| Bid | `LISTING#{id}` | `BID#{bidId}` |
| Browse projection | `LISTING#{id}` | `#BROWSE` |
| Facet counter | `FACETS#{categoryId}` | `{facetKey}#{value}` |
| Category total | `FACETS#{categoryId}` | `#TOTAL` |
| Idempotency claim | `IDEMPOTENCY#{scope}#{key}` | `#CLAIM` |

Everything belonging to one listing shares a partition. That is what lets a bid and the
auction it changes be written in a single transaction — and a bid whose price landed but
whose history row did not is an auction where the winner is charged an amount no bid
accounts for.

Bid sort keys are the bid's own id. Ids are UUIDv7 in Crockford base-32, so they are
fixed-width and ordered by time: sorting on the id sorts chronologically, with no separate
timestamp attribute and no way for two bids in the same millisecond to collide.

Idempotency claims carry `expiresAt` and are collected by DynamoDB's TTL, so nothing has to
remember to prune them.

---

## Indexes

| Index | Partition | Sort | Serves |
|---|---|---|---|
| `gsi_seller` | `SELLER#{id}` | `{status}#{listingId}` | Dashboard tabs |
| `gsi_browse_new` | `BROWSE#{categoryId}` | `{listingId}` | Newest first |
| `gsi_browse_price` | `BROWSE#{categoryId}` | `{paddedPrice}#{id}` | Price sort |
| `gsi_browse_ending` | `BROWSE_END#{categoryId}` | `{endsAt}#{id}` | "Ending soon" — sparse |
| `gsi_auction_end` | `AUCTION#Live#{shard}` | `{endsAt}#{id}` | Close sweep — sharded ×4 |

**Status leads the seller sort key** so the dashboard's "Active" tab is a key-condition
prefix query rather than a filtered read of every listing that seller has ever created.

**Prices are zero-padded to 16 digits.** DynamoDB sorts strings lexicographically, so
without padding `Rp9.000` sorts after `Rp10.000.000`.

**Browse partitions are per-category, never one `ALL` partition.** A single partition for
the whole catalogue would be a write hotspot and would cap browse throughput at one
partition's limits. Cross-category search fans out across the six category partitions in
parallel instead.

**The two auction indexes answer different questions.** `gsi_browse_ending` is partitioned
by category, for a shopper narrowing to cards ending today. `gsi_auction_end` is partitioned
by shard, for a sweep that must see every auction in the marketplace regardless of category.

### Two things here will bite you

**`gsi_auction_end` must project `ALL`.** `ListDueForCloseAsync` rehydrates a complete
`Auction` aggregate straight from what the index returns. Narrowing the projection makes it
return auctions with a zeroed price — and no error at all.

**Shard assignment uses FNV-1a, not `string.GetHashCode()`.** The runtime's string hash is
randomised per process. With it, two Lambda instances place the same listing in different
shards: the second instance writes a fresh index entry and orphans the first, so the auction
appears twice in the sweep at two different close times. *Any hash used in a persisted key
must be stable across processes and across deployments.*

Four shards exists because every bid rewrites its auction item and therefore its index
entry. One partition would funnel every bid in the marketplace through a single partition's
write limit, and the last minute of a popular lot is exactly when that limit matters.

---

## The browse projection

One row per publicly visible listing, holding exactly what a result card draws and what a
filter matches on.

Narrower than the listing itself, and that narrowness is a control rather than an
optimisation: browse is served to anonymous visitors, so a field added to `Listing` cannot
start leaking into public results unless someone deliberately adds it here too.

Facet values are flattened into `f_{key}` attributes so a filter expression can match them
without loading a nested map. Keys come from the category schema, which the domain validates
against an allowlist — a seller cannot invent an attribute name and mint a new indexed
column, or invent a `PSA 11` grade to sort above every honest listing.

Drafts, archived, and suspended listings are **absent** from the projection rather than
present-and-filtered. A row that exists but must never be returned is one forgotten filter
expression away from being returned.

---

## What this design genuinely cannot do

DynamoDB was the user's explicit choice over PostgreSQL. It sorts and filters well; it does
not search. Stated plainly:

**Sorting is exact** — each sort order has its own index.

**Filtering is exact but post-read.** Price, city, and facet filters are `FilterExpression`s
applied after items are read, so a heavily filtered query pays to read rows it discards.

**Free text is a case-normalised substring match** over a lowercased title. No tokens, no
stemming, no ranking — which is why `BrowseSort.Relevance` falls back to newest-first.

**Numbered paging walks from page 1.** DynamoDB has no offset. `MaxPageNumber` (200) turns
that from an unbounded cost into a known one, and `MaxQueryRoundTrips` (20) bounds it again
so one unselective filter cannot hold a Lambda open for its whole timeout.

**Facet counts are approximate and err high.** Counters are maintained per category, so they
answer *"how many active PSA 10 cards exist"* — not *"how many of the results you are
currently looking at are PSA 10"*. Price, city, and text filters are not counted. The
approximation errs high on purpose: over-counting is a visibly imperfect number, while
under-counting would shorten the pager and hide listings a seller has paid to show.

Counter updates are atomic `ADD`s, and a failure is logged and swallowed — a sidebar showing
41 instead of 42 must never be the reason a seller's listing fails to publish.

### The migration path

Every limitation above disappears behind an OpenSearch implementation of
`IListingSearchIndex`, with no change to any handler or endpoint. That is precisely why it
is a separate interface from `IListingRepository` rather than more methods on it.

---

## Concurrency

Every aggregate carries a `Version`. Every write is conditioned on the value that was read;
a failed condition surfaces as `ConcurrencyConflictException`, which the API translates into
a retry and only then into a 503.

`Listing` gained its version because `IListingRepository.UpdateAsync` documented a
concurrency conflict with nothing to condition on. Conditioning on `UpdatedAt` instead would
be a timestamp doing a concurrency token's job — it fails only when two writes land in the
same tick, which is exactly the case it exists to catch.

The expected version is passed **explicitly** rather than inferred from the aggregate: a
caller that applied two changes has already incremented the version twice, and the
repository cannot tell how far to count back.

View and watcher counts sit outside this entirely. They are unconditional atomic `ADD`s, so
a page view never fails a seller's concurrent edit and a lost view is never worth a retry.

### Put versus update, which is not a style choice

**A listing update is an `UpdateItem` with a `SET`.** It has to be. A `PutItem` replaces the
whole item, and the counters above are absent from the mapped item precisely because
something else maintains them — so a put would delete them, and every seller edit would
silently reset the listing's view count to zero. Writing them from the aggregate instead is
no better: the copy in hand was read when the seller opened the form, so saving it rolls
back every view counted while they typed. Assigning only the attributes this layer owns is
the only version that is right in both directions.

**Auction and browse rows use `PutItem`, also deliberately.** They have no externally
maintained attributes, and both depend on a put's replace semantics to *drop* sparse index
keys. That is exactly how a closed auction leaves `gsi_auction_end` and how a listing that
stops being a live auction leaves `gsi_browse_ending` — converting either to an update
would leave the old keys behind and the sweep would read a closed auction forever.

**Every conditional write also asserts the row exists.** `attribute_exists(pk) AND #v = :expected`,
not the version alone: a condition that only compares a version is satisfied by an item that
has no version because it has never been written, which turns an update into an upsert that
bypasses every check the create path makes.

---

## Offer and order repositories

`IOfferRepository` and `IOrderRepository` are defined but **deliberately unregistered**.
Their features are scaffolded, not built.

A stub that throws at run time looks wired until a buyer hits it. With no registration at
all, `ValidateOnBuild` fails the *deployment* the moment someone adds a handler that needs
one. Do not "fix" this by adding a throwing stub.
