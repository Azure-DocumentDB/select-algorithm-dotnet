# select-algorithm-dotnet

Compare **IVF, HNSW and DiskANN × COS, L2 and IP**: nine isolated collections, nine explicit result rows, and a nonzero exit if **any** case fails. The default path uses 50 pinned hotel records with 1536-dimensional precomputed vectors and a user-selected query-vector file. **No Azure OpenAI deployment or model call is required or implemented.**

This is a small correctness/compatibility sample, **not a performance benchmark or production service**. [TESTED.md](TESTED.md) separates local evidence from pending managed-Azure validation. [UPSTREAM.md](UPSTREAM.md) records the original extraction and attributed reuse of the public vector-search sample. Microsoft MIT notice remains in [LICENSE.md](LICENSE.md).

## Requirements and boundaries

- Patched **.NET SDK 10.0.401** (pinned in `global.json`), Python 3 for data/audit helpers.
- An **explicitly approved disposable Azure DocumentDB** environment supporting all three algorithms. Tier/region/permissions/network access must be verified separately. An S0 Azure OpenAI deployment is irrelevant: this sample contains no model workflow.
- Native SCRAM credentials, or Azure Identity credentials for a registered/authorized database Entra principal. Azure login alone does not grant database access.
- Authorization to create nine collections and indexes and insert absent fixture records. No deployment or resource provisioning occurs here.

Database names must start with `vectorsearch_disposable_`, but the prefix is an intent guard, **not proof the database is disposable**. Obtain approval for the exact namespace. The program never drops, deletes, replaces, updates, enumerates databases, or automatically removes anything, even after errors. Resources remain for inspection; an operator handles separately authorized cleanup.

## 1. Build and run local tests (no cloud credentials)

From the repository root:

```sh
dotnet restore tests/SelectAlgorithm.Tests.csproj --locked-mode --no-http-cache
dotnet build tests/SelectAlgorithm.Tests.csproj --no-restore --configuration Release
dotnet test tests/SelectAlgorithm.Tests.csproj --no-restore --no-build --configuration Release --filter 'Category!=Live'
python3 -m unittest discover -s tools -p 'test_*.py'
python3 tools/check-vulnerabilities.py
```

Dependencies and transitives are locked for the application and test project. Restore audits all dependency levels. The additional current-feed audit fails on advisories or incomplete/feed-error responses. Advisory cleanliness is time-specific, not a permanent security guarantee.

CI uses SHA-pinned GitHub-owned actions, `contents: read`, and no persisted checkout credential. It performs only build, offline tests and dependency audit: **no Azure login, secrets, deployment or live gate**.

## 2. Prepare the pinned fixture and your query

Explicitly download the fixture and extract its first vector:

```sh
python3 tools/prepare-data.py
```

This refuses existing files, verifies the pinned SHA-256, and writes `data/Hotels_Vector.json` and `data/query-vector.json`. It does not contact Azure or invoke a model. [data/README.md](data/README.md) contains source, checksum and licensing details. Nothing downloads automatically on clone/build/run.

`QUERY_VECTOR_FILE` can instead point to **any valid finite nonzero JSON array of 1536 numbers**, in the same embedding space as the pinned dataset. It is not silently replaced with the first record. Both shared and original .NET fixture hashes are accepted; document IDs become `dotnet-hotel-<HotelId>`. Custom/re-embedded document datasets are intentionally outside this sample's pinned oracle.

```sh
export VECTOR_DATA_FILE=data/Hotels_Vector.json
export QUERY_VECTOR_FILE=data/query-vector.json
export VectorSearch__TopK=5  # supported range 1..50; actually passed to the query

dotnet run --no-build --configuration Release -- validate-fixture
```

The local-only command reports record count, dimensions, configured K, canonical query SHA-256, and expected first ID per metric. It never opens a database connection. To independently check changed-query behavior, create a separate JSON file from another fixture record (do not overwrite your current query), change `QUERY_VECTOR_FILE`, and set `VectorSearch__TopK=2`. The digest/oracle and resulting top-K must change accordingly.

## 3. Explicit process configuration

Configuration comes **only from process environment**. `.env.example` is placeholder-only and safe-by-default; `.NET` does **not** auto-load `.env`. `appsettings.json` is empty and no longer read. Set values using your shell or secret manager. Do not put passwords in a URI, command history, report or checked-in file.

| Variable | Meaning |
| --- | --- |
| `MONGO_URI` | Credential-free `mongodb+srv://<exact-host>/?tls=true&retrywrites=false` |
| `MONGO_ALLOWED_HOST` | Exact host in URI; must end in `.mongocluster.cosmos.azure.com` |
| `MONGO_DATABASE` | Explicit approved `vectorsearch_disposable_...` name, at most 63 chars |
| `MONGO_COLLECTION_PREFIX` | Explicit lowercase prefix, 1..48 chars, letters/digits/underscore, first char a letter |
| `MONGO_AUTH_MODE` | `scram` or `oidc` |
| `MONGO_USERNAME`, `MONGO_PASSWORD` | Required for SCRAM; **both must be unset/empty for OIDC** |
| `ALLOW_DATABASE_WRITES` | Must be exactly `true` for comparison/live writes |
| `VECTOR_DATA_FILE` | Default `data/Hotels_Vector.json` |
| `QUERY_VECTOR_FILE` | Default `data/query-vector.json`; selected query is genuinely used |
| `VectorSearch__TopK` | Default `5`, valid `1..50` |
| `OPERATION_TIMEOUT_SECONDS` | Per-case deadline `30..600`, default `300` |

### Migration from the original configuration

The documented **`VectorSearch__TopK` is retained and honored**. The undocumented `TOP_K` alias is removed rather than allowed to override it invisibly.

**Text queries/model inference are removed**. `VectorSearch__Query` and `QUERY_TEXT` do not describe a precomputed vector and cannot be honestly honored without model inference. They are rejected if present, even empty; explicitly unset them and supply `QUERY_VECTOR_FILE`. The program never silently ignores a configured text query.

Also unset the removed `VectorSearch__Similarity` (all nine metrics always run), `DocumentDB__ClusterName`, `DocumentDB__DatabaseName`, `DocumentDB__LoadBatchSize`, `DataFiles__WithVectors`, `Embedding__EmbeddedField`, `Embedding__Dimensions`, `AzureOpenAI__Endpoint` and `AzureOpenAI__EmbeddingModel`. All these old keys fail fast if present. Use the table above; dimensions/field are fixed to the pinned fixture. There are no competing configuration layers or legacy implicit database defaults.

### Native SCRAM example (bash/zsh)

```sh
export MONGO_URI='mongodb+srv://<approved-cluster-host>/?tls=true&retrywrites=false'
export MONGO_ALLOWED_HOST='<approved-cluster-host>'
export MONGO_DATABASE='vectorsearch_disposable_example'
export MONGO_COLLECTION_PREFIX='comparison'
export MONGO_AUTH_MODE=scram
# Inject MONGO_USERNAME and MONGO_PASSWORD securely into this process environment.
export ALLOW_DATABASE_WRITES=true

dotnet run --no-build --configuration Release -- compare-all
```

PowerShell equivalent uses `$env:NAME = 'value'`, including `$env:VectorSearch__TopK = '5'`.

### Real Entra OIDC

```sh
unset MONGO_USERNAME MONGO_PASSWORD
export MONGO_AUTH_MODE=oidc
# Sign in to the intended tenant/principal using an approved Azure Identity mechanism.
dotnet run --no-build --configuration Release -- compare-all
```

The exact credential-free destination is validated **before credential factories and client construction**. Only TLS-verified Azure SRV destinations are accepted; no insecure TLS, embedded credentials, arbitrary URI options, port/path overrides or unknown hosts. Allowed URI options are `tls=true`, `retrywrites=false`, `authSource=admin`; the OIDC credential explicitly uses `$external`.

MongoDB.Driver 3.12.0 uses its actual `IOidcCallback` contract. `DefaultAzureCredential` (interactive browser excluded) requests `https://ossrdbms-aad.database.windows.net/.default`, returns the token's real remaining lifetime, and rejects empty/expired/nearly expired tokens. Tokens, raw SDK errors and credential values are not printed.

## Execution, safety and interpretation

`compare-all` is the default, including when stdin is EOF; there is no input/menu loop. Unknown commands, missing configuration, rejected legacy keys and operational failures exit nonzero. `--help` and `validate-fixture` are local-only.

Collections are `<prefix>_<scram|oidc>_<ivf|hnsw|diskann>_<cos|l2|ip>`: separate per algorithm, metric **and auth mode**. Each case has an independent deadline, with bounded driver timeouts and at most twelve two-second readiness checks. A first/middle index, insertion, query or verification failure yields a FAIL row and does not stop later cases. A JSON summary always records nine attempted cases after valid global setup; setup failure has no misleading nine-case summary.

Existing fixture IDs are preserved byte-for-byte, including user edits. Only absent IDs are inserted, one at a time. A duplicate-ID race is tolerated only after verifying the exact ID exists; other unique-index/write errors fail. The complete fixture is validated before the first store call. Partial writes from an operational failure are retained and reported as failure, never counted as a successful batch.

The whole index catalog is inspected before insertion and again after population. A missing vector index is created even for a populated collection. Same-name/other-vector conflicts, wrong field/kind/metric/dimensions/parameters, unique/sparse/partial/TTL/hidden/collation semantics, malformed entries and unknown semantics fail closed. Ordinary unrelated indexes remain untouched. Known DiskANN server defaults for `maxDegree`/`lBuild` may appear in catalog responses. There is no reset/drop/automatic repair path: choose a separately approved new prefix or review the existing namespace.

Each PASS contains actual IDs/scores, K, inserted/preserved counts, index verification and the query digest. Results must have exactly K distinct known IDs and finite numeric scores. The local oracle checks ranking and scores with absolute tolerance 0.001: **COS cosine similarity (descending), L2 Euclidean distance (ascending), IP dot product (descending)**. It allows numerical ties, not unknown records or invalid scores. User vector edits or vector-bearing unrelated records can intentionally make this fixture oracle fail; they are never overwritten to obtain green results.

Self-query smoke results are not semantic relevance evidence. Arbitrary valid query vectors are supported, but approximate-index recall may fail the strict 50-record oracle; that is a truthful compatibility/correctness failure, not a reason to weaken validation. Timings are not benchmark-grade and scores across metrics are not directly comparable.

## Explicit managed-Azure release gate (paid resources)

Only run after the exact resource/database/write scope is approved. Normal local CI excludes it. Selecting `Category=Live` without guards **fails all selected tests; it never silently skips**.

Provide the table's required environment plus native credentials; authorize a real Entra principal separately. The gate internally isolates native credentials from OIDC and appends auth to collection names.

```sh
export RUN_LIVE_INTEGRATION=true
export LIVE_AUTH_MODES=scram,oidc
export ALLOW_DATABASE_WRITES=true
# MONGO_URI, MONGO_ALLOWED_HOST, MONGO_DATABASE, MONGO_COLLECTION_PREFIX,
# MONGO_USERNAME, MONGO_PASSWORD, VECTOR_DATA_FILE and QUERY_VECTOR_FILE must be explicit.
dotnet test tests/SelectAlgorithm.Tests.csproj --no-restore --no-build --configuration Release --filter 'Category=Live'
```

This selects **18 cases** (both auth modes × all nine), checks catalog, metric oracle, a second insert-only run, and preservation canaries. It inserts an edited fixture record and unrelated non-vector record **only if absent**; it never resets existing content. It leaves collections/data/indexes in place. Run CLI comparisons separately with both auth modes and a second query/K, and independently verify the resulting JSON IDs/scores before claiming the managed-service release gate. Current evidence and untested scope are in [TESTED.md](TESTED.md).

## Maintenance

Pinned source is independently maintained, not a transfer of upstream ownership. Review/port upstream fixes manually and retain notices. Infrastructure is deliberately not included: do not infer safety from upstream deployment defaults. No cloud-validation or model-inference claim follows from a successful local build.
