# Koleksi.id — Backend

.NET 10 · Clean Architecture · ASP.NET Core Minimal APIs on Lambda.

Part of the Koleksi.id marketplace, split out of the original monorepo into its own repo.
The companion repos, expected checked out as siblings (same parent directory):

- `../koleksi-frontend` — React Router 7 SSR frontend
- `../koleksi-infra` — AWS CDK, deploys this API and the frontend together

> **Continuing this work?** Read [`CONTINUATION.md`](./CONTINUATION.md) first. It records
> what exists, what is deliberate, and the decisions that must not be quietly reversed.

Dependency direction is **Domain ← Application ← Infrastructure ← Api**, enforced by
project references. `Koleksi.Domain` references nothing at all.

## Requirements

- .NET SDK 10
- Docker, for local DynamoDB and MinIO stand-ins

## Getting started

```bash
# Builds with warnings as errors, then runs all tests.
dotnet build Koleksi.slnx
dotnet test  Koleksi.slnx

# The storage tests need a local DynamoDB. Without it they skip and everything else
# still runs, so this is optional rather than a prerequisite.
docker compose up -d

# Create the local table. Its shape is read from koleksi-infra's synthesized CDK
# template, so it is provably the shape that gets deployed.
#   cd ../koleksi-infra && npx cdk synth -c stage=dev   # once, if cdk.out isn't there yet
dotnet run scripts/dev-table.cs

# The API, locally. The same Program that runs in Lambda. No AWS credentials needed:
# a configured local DynamoDB endpoint selects static placeholders.
dotnet run --project src/Koleksi.Api        # http://localhost:5033
```

`scripts/dev-table.cs` looks for `koleksi-infra`'s synthesized template at
`../koleksi-infra/cdk.out/Koleksi-Data-dev.template.json`. If your checkout isn't laid out
as siblings, set `KOLEKSI_INFRA_TEMPLATE` to the template's absolute path instead.

**Signing in locally needs no AWS account.** While `appsettings.Development.json` still
carries placeholder Cognito ids, a Debug build serves sign-up and sign-in from an
in-process user pool — accounts in memory, tokens signed by the process and verified by the
real bearer handler. The API logs a warning at startup when it is active. Put a real
development pool's ids in that file and the Cognito adapter takes over with no other
change; `docs/security.md` covers what gates it and what it deliberately does not replace.

## Deploying

Self-contained arm64 publish for the `provided.al2023` custom runtime that
`../koleksi-infra` deploys as a Lambda asset:

```bash
dotnet publish src/Koleksi.Api -c Release -r linux-arm64 --self-contained \
  -p:PublishReadyToRun=true -o publish/api
```

Then run the deploy from `../koleksi-infra` — see that repo's README. Order matters: this
publish and the frontend's build must both exist before `cdk deploy` runs.

## Design decisions worth knowing before you change anything

| Decision | Why |
|---|---|
| DynamoDB single table | The user's explicit choice. Its real limits — approximate facet counts, substring-only text search, paging that walks — are documented in [`CONTINUATION.md`](./CONTINUATION.md) §7, not hidden. |
| Cognito for identity | The API holds no password material and no signing key, so compromising it cannot mint a token for anyone. |
| No MediatR, Serilog, Swashbuckle, FluentAssertions | Each was considered and rejected — licence changes or in-box equivalents. See `CONTINUATION.md` §2. |
| `InvariantGlobalization` on | Faster cold starts, at the cost that **no ICU-dependent API may be used**. This has already caused one production-shaped bug; see `CONTINUATION.md` §11. |

## Security

Security is the standing constraint on this codebase, not a checklist item.
[`docs/security.md`](./docs/security.md) records the controls and, more usefully, the
reasoning — what each one stops, and what it would cost to remove.

## Status

Builds clean with warnings as errors and passes 278 tests — 86 domain, 62 infrastructure,
58 against a real DynamoDB, and 72 over the real HTTP pipeline. Catalogue, browse, and the
item and auction pages are complete end-to-end. Checkout, escrow, dashboard, offers,
reviews, messaging, and payouts are scaffolded with typed contracts and `TODO` markers.
`CONTINUATION.md` §3 has the current table.
