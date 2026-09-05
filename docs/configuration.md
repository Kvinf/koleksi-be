# Configuration

Every setting the backend needs, where it comes from, and what happens when it is missing.

The short version: **nothing is optional and nothing has a silent default.**
`KoleksiOptions.Validate()` runs before the host is built and throws on anything absent or
malformed, so a misconfigured deployment fails to start rather than starting in a weakened
state. A blank Cognito authority discovered lazily on the first request means an API that
accepted traffic while unable to validate a single token.

Validation reports *every* problem at once, not the first — reporting them one at a time
turns a five-minute fix into five cold starts.

---

## Backend

Bound from the `Koleksi` section. In a deployed stage these arrive as environment
variables set by the CDK app; `__` is the .NET configuration provider's section separator,
so `KOLEKSI__COGNITO__CLIENTID` binds straight onto `KoleksiOptions.Cognito.ClientId` with
no glue code.

| Key | Environment variable | Example | Notes |
|---|---|---|---|
| `Region` | `KOLEKSI__REGION` | `ap-southeast-3` | Default region for every SDK client. |
| `Cognito:UserPoolId` | `KOLEKSI__COGNITO__USERPOOLID` | `ap-southeast-3_ab12cd34e` | Must contain `_`. |
| `Cognito:ClientId` | `KOLEKSI__COGNITO__CLIENTID` | `4f3k…` | Checked against every token's `client_id`. |
| `Cognito:Region` | `KOLEKSI__COGNITO__REGION` | — | Optional; falls back to `Region`. |
| `Cognito:HostedUiDomain` | `KOLEKSI__COGNITO__HOSTEDUIDOMAIN` | `https://koleksi-dev.auth.ap-southeast-3.amazoncognito.com` | Optional. Google sign-in's token exchange. Matches the CDK stack's `HostedUiDomain` output. Unset until Google is configured — see below. |
| `Dynamo:TableName` | `KOLEKSI__DYNAMO__TABLENAME` | `koleksi-prod` | |
| `Dynamo:ServiceUrl` | `KOLEKSI__DYNAMO__SERVICEURL` | `http://localhost:8000` | **Development only.** DynamoDB Local. |
| `Media:PublicBucket` | `KOLEKSI__MEDIA__PUBLICBUCKET` | `koleksi-prod-media-…` | |
| `Media:PrivateBucket` | `KOLEKSI__MEDIA__PRIVATEBUCKET` | `koleksi-prod-identity-…` | Must differ from the public bucket. |
| `Media:CdnBaseUrl` | `KOLEKSI__MEDIA__CDNBASEURL` | `https://koleksi.id/media` | Must be absolute **https**. |
| `Media:UploadGrantLifetime` | `KOLEKSI__MEDIA__UPLOADGRANTLIFETIME` | `00:15:00` | Between 1 second and 1 hour. |
| `Payment:BaseUrl` | `KOLEKSI__PAYMENT__BASEURL` | `https://api.doku.com` | Must be absolute **https**. |
| `Payment:ClientId` | `KOLEKSI__PAYMENT__CLIENTID` | `BRN-…` | Merchant identifier. Not a secret. |
| `Payment:SecretArn` | `KOLEKSI__PAYMENT__SECRETARN` | `arn:aws:secretsmanager:…` | **Must be an ARN.** See below. |
| `Payment:ReturnUrl` | `KOLEKSI__PAYMENT__RETURNURL` | `https://koleksi.id/checkout/selesai` | Must be absolute **https**. |
| `Cors:AllowedOrigins` | `KOLEKSI__CORS__ALLOWEDORIGINS__0` | `https://koleksi.id` | Array. See below. |

### Rules the validator enforces, and why

**`Media:PrivateBucket` must differ from `Media:PublicBucket`.** A KTP scan and a photo of a
Gunpla box must not be one policy change away from sharing a CDN distribution. If someone
ever points both at the same bucket, startup fails here rather than in a breach report.

**`Payment:SecretArn` must start with `arn:aws:secretsmanager:`.** The webhook signing key
is the one true secret in the system; this stops it from being "temporarily" inlined into
configuration where it would end up in a template, a build artefact, and a log line.

**`Cors:AllowedOrigins` rejects `*`** — the policy allows credentials, and a wildcard paired
with credentials lets any site on the internet make authenticated calls on a signed-in
user's behalf. Entries must be **bare origins**: scheme, host, and optional port, with no
path and no query. A trailing slash or path silently never matches, which presents as "CORS
is broken" long after the deployment that caused it.

**Every URL must be https.** Plaintext would mean fetching signing keys, or redirecting a
paying buyer, over a channel any intermediary can rewrite.

### Google sign-in

Not configured out of the box. `infra/lib/auth-stack.ts` accepts `googleClientId` and
`googleClientSecretArn` as CDK context (`-c googleClientId=… -c googleClientSecretArn=…`);
until both are supplied, the stack deploys with no Google identity provider at all — an
absence visible in `cdk diff`, not a provider that is present but silently broken — and
`Cognito:HostedUiDomain` should stay unset, which is exactly what makes
`ExchangeAuthorizationCodeAsync` report every Google sign-in attempt as a coarse failure
instead of throwing.

Once a Google Cloud Console OAuth client exists:

1. `cdk deploy -c stage=dev -c googleClientId=<id> -c googleClientSecretArn=<Secrets Manager ARN>`.
2. Read the `GoogleRedirectUri` stack output (`https://{domain}/oauth2/idpresponse`) and add
   it as an **Authorized redirect URI** on the Google OAuth client.
3. Set `Cognito:HostedUiDomain` (the `HostedUiDomain` output) on the API, and
   `KOLEKSI_COGNITO_DOMAIN`/`KOLEKSI_COGNITO_CLIENT_ID` on the frontend (see below).

The client secret is never CDK context — it is a Secrets Manager ARN, resolved the same way
`Payment:SecretArn` is, so it never lands in a synthesized template.

### What is deliberately *not* configuration

Anything security-relevant that should not vary per environment is a constant in code:
token algorithms, clock skew, the image content-type allowlist, upload size caps, rate-limit
budgets, the escrow auto-release window, and the auction increment ladder. Making these
configurable would mean a console edit could weaken them without a code review.

The Cognito **authority is derived**, not configured — `https://cognito-idp.{region}.amazonaws.com/{poolId}`
— so it cannot drift out of step with the pool id, and nobody can point the API at an issuer
we do not control by editing one setting.

---

## Frontend

| Variable | Required | Notes |
|---|---|---|
| `KOLEKSI_API_URL` | **yes** | Server-side only. Throws at startup if absent, rather than producing fetches to `undefined/api/...`. |
| `SESSION_SECRET` | **yes** | Signs the session cookie. At least 32 characters, or startup fails. Comma-separated to rotate: the first value signs, the rest still verify. Generate with `openssl rand -base64 48`. |
| `KOLEKSI_API_TIMEOUT_MS` | no | Defaults to 8000. Deliberately below the Lambda timeout so a hang surfaces as a handled error page rather than a killed invocation. |
| `VITE_MEDIA_BASE_URL` | no | CDN base for listing photo keys. `VITE_`-prefixed variables **are embedded in the client bundle** — never put a secret behind that prefix. |
| `KOLEKSI_COGNITO_DOMAIN` | no | The pool's hosted-UI base URL, e.g. `https://koleksi-dev.auth.ap-southeast-3.amazoncognito.com`. Needed to build the Cognito authorize URL. Not a secret — it is a public endpoint, the same value as the backend's `Cognito:HostedUiDomain`. Absent means "Continue with Google" redirects back to sign-in with a message instead of to Cognito. |
| `KOLEKSI_COGNITO_CLIENT_ID` | no | The pool's web app client id. Not a secret — Google's own guidance says a public client's id is safe to embed; PKCE, not this value, is what protects the exchange. |

### The session cookie

`__Host-koleksi_session`, holding the access token, the refresh token and the handle.

The `__Host-` prefix is enforced by the browser rather than by us: it refuses the cookie
unless it is `Secure`, carries no `Domain`, and is scoped to `/`. That makes it impossible
for a subdomain — including one an attacker manages to stand up — to write a session
cookie this server would then read. `HttpOnly` keeps it out of script's reach, so an XSS
hole cannot be escalated into a stolen session, and `SameSite=Lax` rather than `Strict` is
deliberate: the session has to survive the top-level navigation back from the payment
gateway, and `Strict` would sign a buyer out at exactly that moment.

Neither token is ever serialised into a page. The root loader publishes only the handle,
which is public anyway — it is the shop name.

The API client lives in `app/lib/api.server.ts`. The `.server` suffix is a build-time
guarantee: React Router refuses to include it in a client bundle and the build fails if a
component imports it. That matters because every call from it can carry an access token.

---

## Local development

Four terminals' worth of commands, in order. Each step is needed by the one after it.

```bash
# 1. Storage. In-memory, so it starts clean every time.
docker run -d --name koleksi-dynamodb-local -p 8000:8000 \
  amazon/dynamodb-local:latest -jar DynamoDBLocal.jar -inMemory -sharedDb

# 2. The table. Its shape is read from the synthesized CDK template, so a local table is
#    provably the shape that gets deployed — run cdk synth first if cdk.out is absent.
cd infra && npx cdk synth -c stage=dev >/dev/null && cd ..
dotnet run scripts/dev-table.cs

# 3. The API. The launch profile sets ASPNETCORE_ENVIRONMENT=Development, which is what
#    loads appsettings.Development.json and permits the local DynamoDB endpoint.
dotnet run --project backend/src/Koleksi.Api

# 4. The frontend. KOLEKSI_API_URL is required and has no default.
cd frontend
KOLEKSI_API_URL=http://localhost:5033 npm run dev
```

On Windows PowerShell, step 4 is two statements rather than a prefix:

```powershell
$env:KOLEKSI_API_URL = "http://localhost:5033"
npm run dev
```

The site is then on `http://127.0.0.1:5173` and the API on `http://localhost:5033`. Browse
will be empty until something is published — the catalogue endpoints answer `200` with no
items, which is correct rather than broken.

```jsonc
// backend/src/Koleksi.Api/appsettings.Development.json — committed on purpose.
// Everything in it is an identifier; the payment key is still an ARN resolved through
// the developer's own AWS credentials.
```

**`Dynamo:ServiceUrl` is refused outside development.** Startup validation takes the host
environment into account, so nobody can point a deployed stage at an endpoint of their
choosing — and read or write the marketplace's data through it — by editing one environment
variable. It must also be an absolute `http`/`https` URL: `localhost:8000` parses as an
absolute URI whose *scheme* is `localhost`, so a bare host and port is rejected explicitly
rather than failing later with a connection error naming nothing useful.

**No AWS credentials are needed to run locally.** When `Dynamo:ServiceUrl` is set the SDK
client is built with static placeholders, because DynamoDB Local has nothing to
authenticate to but the SDK still has to sign the request. That branch is unreachable in a
deployed stage, since validation refuses the setting that selects it.

Cognito has no local emulator, so a real development user pool is needed — sign-in is one
of the few things that cannot be faked locally without weakening the token validation that
exists to be strict. Everything anonymous (browse, search, categories, item pages) works
without one.

Anything genuinely secret in local development belongs in .NET user-secrets, never in a
tracked file:

```bash
dotnet user-secrets set "Koleksi:Payment:ClientId" "…" --project backend/src/Koleksi.Api
```

---

## Build and deploy order

The CDK app declares `backend/publish/api` and `frontend/build/lambda` as Lambda assets, so
both must exist before synth produces anything deployable. Each directory holds a `.gitkeep`
explaining what belongs there, so `cdk synth` runs on a clean checkout — but deploying
without building first produces a function with no entry point.

```bash
dotnet publish backend/src/Koleksi.Api -c Release -r linux-arm64 --self-contained \
  -p:PublishReadyToRun=true -o backend/publish/api

cd frontend && npm run build && cd ..

cd infra && npx cdk deploy -c stage=dev --all
```

`stage` accepts `dev`, `staging`, or `prod` and defaults to **`dev`**. That default is
deliberate: the stage decides deletion protection, log retention, and whether buckets are
destroyed with their stack, so a forgotten flag must not deploy to production.

### How the web Lambda artefact is produced

`npm run build` runs two steps. The first is the React Router build, which emits
`build/server/index.js` and the client assets. The second, `build:lambda`, uses esbuild to
bundle `server/lambda.ts` together with that server build into a single
`build/lambda/lambda.js`.

`--packages=bundle` inlines `react-dom/server` and everything else, so the deployed artefact
carries no `node_modules` and resolves nothing at cold start — which is most of the
difference between a fast and a slow first request. The AWS SDK is left external because the
Node 22 runtime already provides it.
