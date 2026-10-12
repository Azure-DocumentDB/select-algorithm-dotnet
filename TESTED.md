# Validation record — 2026-10-12

## Verified locally on the comparison rewrite

- macOS arm64, patched .NET SDK 10.0.401 / target net10.0.
- Locked no-HTTP-cache restore and Release build: zero warnings/errors.
- 126 offline .NET cases pass, zero skipped. Eight audit-helper tests pass.
- Current NuGet.org feed audit of both app and test graphs, direct + transitive:
  no vulnerabilities reported on this date. The fail-closed helper checks feed errors.
- Fixture validation against the pinned shared 50 × 1536 dataset: first-record
  query/K=5 and a different record query/K=2 produce different canonical query hashes
  and expected top IDs for COS/L2/IP. No database/model connection occurred.
- CLI unknown/removed configuration and missing configuration with EOF exit nonzero.
- Explicit live test selection without flags/environment: **18 failures, zero skips**,
  before network/database writes. This is a negative guard test, not a live pass.
- Offline tests cover failure-first/middle/last continuation, nine-case summaries,
  variable query/K, query/score oracle, malformed responses/vectors/writes, full
  index catalog conflicts (including hidden/unique/sparse/partial/TTL), preservation,
  exact-host-before-token construction, TLS, SCRAM and real driver OIDC callback
  contract/audience/expiry, and credential-free sanitized error output.

## Not yet validated for this repository/revision

**No managed-Azure service run has been performed for this comparison candidate.**
All 18 auth × algorithm × metric release cases remain a parent/operator-owned gate.
Prior public vector-sample evidence is attribution/design input, not live proof of
this rewrite. Local fakes do not prove Azure authentication, index support or service
query results. Azure resources, cleanup, push/PR/merge and visibility are outside
this validation record.

The required gate is documented in README: real SCRAM and real Entra OIDC on an
approved tier, all nine cases each, catalog/preservation/rerun checks, independent
JSON rank/score assertions, plus a changed query/TopK run. Retain sanitized evidence
against the exact commit before changing this status.

Model inference was deliberately removed; no Azure OpenAI deployment, S0 capability,
semantic quality, large-scale recall/performance or production suitability is claimed.
