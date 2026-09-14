# Wave 4 P1-F — Honesty Tripwires (contract §7, executable)

**Where:** `server/tests/Livora.Server.Tests/Quality/SourceHonestyTests.cs`, run as part of the
normal server gate (`dotnet test server/tests/...`) — no special CI surface needed. Scanner:
`SourceTokenizer.cs` (pinned by `SourceTokenizerSelfTests.cs`, 18 cases). Root discovery:
`RepoPaths.cs` — walks up to `LIVORA.slnx` and REFUSES to scan an unknown tree.

**Design law (from the brief):** no silent skips, no fail-open. Concretely: a scan whose scope
directory is missing THROWS; a scan that found zero candidate files/tests FAILS (`Scope_sanity`,
`okSites >= 1`, `paths.Count >= 3`); every rule carries a proof-of-fire test on a synthetic
offender, because a green regex that matches nothing is indistinguishable from a dead one.

| # | Rule | What is scanned | Allowlist (the complete list) |
|---|---|---|---|
| 1 | no `.Result` / `.Wait()` / `GetAwaiter().GetResult()` in `server/src` | code text only (comments + string content removed; **interpolation holes kept as code**) | none |
| 2 | no secret-shaped string literals in `server/src` (`sk-…`, bearer+32, private-key block, `password= "…"`, long base64) | literal VALUES (≥16 chars) | none (empty config values are the honest absence) |
| 3 | a module `Report()` returning `DependencyState.Ok` must show probe evidence (`Probe`/`CanConnect`/`GetConfig`/`Http`/config reads) in the SAME class | `server/src/**/Modules/**` | `PlatformModule` — reports Ok for its own always-up host surface; its database state is probed live per request (`PlatformModule.cs:33`) |
| 4 | ProblemCodes discipline: no Modules/** string literal equal to a code value; `Problems.Of` code argument must be a `ProblemCodes.`/`nameof()` constant | Modules/** code + literals | none — ambiguous shapes are enumerated as violations, never skipped |
| 5 | privacy: log call sites (template holes AND structured arguments) may not carry password/token/secret/refresh VALUE identifiers | `server/src` log calls | constant NAMES (`ConfigKeySection`, `UidClaim`, `SessionClaim`, `Default{Issuer,Audience}`) and `X.Length` measurements — LivoraAuth.cs:92/105 are the living examples |
| 6 | API versioning: every OpenAPI path starts `/api/v1` or is `/healthz`/`/openapi*` | LIVE document from the fixture host | utilities enumerated explicitly (no wildcard) |

**How to add an allowlist entry:** in `SourceHonestyTests.cs`, with the reason IN the entry tuple,
plus a request line in `REQUESTS-P1F.md` the lead can see. Editing the rule instead of the entry
is the failure mode this documents itself against.

**Honest limits** (each in the scanner header with its consequence): raw-string holes are dropped
(not scanned as code); a `:` inside a hole keeps flowing as code (false-positive bias chosen
deliberately — silent misses are the forbidden direction); the heuristics are textual, not a
compiler — proof-of-fire tests exist so a broken rule is RED, not green.
