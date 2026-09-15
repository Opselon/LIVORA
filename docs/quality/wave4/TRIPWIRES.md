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

## R3 widenings (14 Sep, against the merged P1 tree at `1c7bd50`) — exactly what changed and what is demanded instead

Three false-positive rules were refined BY THIS LANE (per the R3 brief: refining the scanner's
false-positive rules is the quality lane's own call, documented here and pinned by proof-of-fire
tests — the only sanctioned alternative was allowlisting honest code until the lists ate the rules).

1. **Tripwire 1 (.Result)** — was: any `.Result` text in server/src code. Now: `.Result` is flagged
   only when the RECEIVER reads as a Task (a `Task`/`ValueTask` symbol declared in the file, an
   unawaited `…Async(...)` local, a `Task.<factory>(…)` call, or a `*Task`-shaped name).
   `.Wait()` and `GetAwaiter().GetResult()` are UNCHANGED — unconditional, no receiver evidence.
   Why: the verification lane ships `record RuleOutcome(string RuleKey, string Result, …)` DTOs;
   `o.Result` on them is a property read, and 11 honest lines were crying wolf.
   Residual false negative, disclosed in `AnalyzerRefinements.cs`: a Task-typed member reached via
   `.Result` that no same-file evidence names (cross-file Task property, no `Async` suffix). The
   blocking idioms without receiver requirements stay fully covered, and the new non-vacuity assert
   (`declined >= 1`) fails if the tree ever stops containing property-read `.Result`s — the
   narrowing then needs re-examination, not trust.
2. **Tripwire 4 (Problems.Of code argument)** — was: any bare identifier argument fires. Now: a bare
   identifier is excused ONLY if every same-file assignment to it has a right-hand side built
   exclusively from `ProblemCodes.X` / `nameof(...)` atoms (ternary value positions recursed; the
   condition may read state). Never-assigned or DB/request-assigned names still fire (fail-closed).
   Why: P1-B's replay branch passes `replayCode`, a local whose only possible values ARE frozen
   constants; the law is about the code VALUE reaching the client, not the spelling.
3. **T2 word-level (claim words)** — `StateMachineSymbol` additionally accepts an in-file enum
   DECLARATION named `*State|Status|Grade|Tier|Phase` or a switch over such a type. Why: the rule
   fired on `Engines/Pipeline/Verification.cs` — the file whose entire purpose is refusing a
   collapsed "verified" (it DECLARES `enum VerificationStatus { Unattested, Corroborated, … }` +
   `EvidenceGrade`) — while hunting files with no state machine at all. A planted-violation proof
   (zero-enum `T2_ClaimWords_Bad.cs.fix`) keeps firing, pinning what the rule still catches.

**Honest limits** (each in the scanner header with its consequence): raw-string holes are dropped
(not scanned as code); a `:` inside a hole keeps flowing as code (false-positive bias chosen
deliberately — silent misses are the forbidden direction); the heuristics are textual, not a
compiler — proof-of-fire tests exist so a broken rule is RED, not green.
T4 note (R3): the shipped `wave4-keys/lane-p1d.*.xml` carry `----` inside XML comments — INVALID
per XML 1.0 §2.5 (strict parse throws). `TripwireScanner.ReadKeys` parses strictly first and only
then through a comment-interior-only normalisation (data bytes never mutated, any other XML error
still throws), so the bilingual law is CERTIFIED on the real manifests (100 EN + 100 FA keys, sets
identical, no placeholder drift, no empty values, FA in Persian script — all verified 14 Sep), and
the malformation itself is printed by `T4 MANIFEST HEALTH: MALFORMED…` and filed as request line
R-R3-1 in `requests/r3.md` for the owning lane to fix.
