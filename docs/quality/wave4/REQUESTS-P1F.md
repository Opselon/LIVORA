# Wave 4 P1-F — requests to the lead (P1-F lane, Agent 16)

Format: `R-P1F-<n> | target | what you need | why | blocking?`
(append-only from here down; the lead mirrors accepted lines into
`docs/architecture/wave4/INTEGRATION_REQUESTS-P1.md` under the P1-F heading — that shared file is
outside this lane's write scope, so nothing here edits it.)

- **R-P1F-1 | .github/workflows/pr-gates.yml | apply the `server-gates` job in
  `docs/quality/wave4/CI-SERVER-JOB.md` as-is (or a reviewed variant) | the backend has no CI gate
  at all today: no workflow builds server/src or runs server/tests; twelve Phase-2 lanes would land
  unguarded | blocking for Wave 4 P2 start: yes**
- **R-P1F-2 | Tests/LIVORA.Tests.csproj or CI step shape | prefer the CI two-step split (already in
  the R-P1F-1 job) over a file edit; if the lead wants ONE client command instead, add an xunit
  collection pin for `Wave3c-Perf` — a file P1-F may not touch | StoragePerformanceBudgetTests fails
  ~19% of one-command full-suite runs with zero code change; evidence in FLAKE-STORAGE-BUDGET.md |
  blocking: no (the split in R-P1F-1 solves it)**
- **R-P1F-3 | Wave4Gate (this lane) — lead merge note | remove `AllowKnownPerfRetry` from the gate
  harness once R-P1F-1's split ships; the retry is a stopgap and keeping it after the split would
  hide a real regression in that class behind a coin flip | documented in Wave4Gate.cs header |
  blocking: no**
- **R-P1F-4 | docs/architecture/wave4/INTEGRATION_REQUESTS-P1.md | mirror this lane's four lines
  under the "P1-F (QA / release gate)" heading (the file is append-only but not in P1-F's write
  scope) | single ledger completeness for merge review | blocking: no**
- **R-P1F-5 | integration | when P1-B lands the sync module, re-run the gate harness and flip
  `Wave4TruthMatrix.md` rows 2 + the sync budget: `Wave4PerformanceBudgetTests.Sync_batch_…`
  self-upgrades from NOT_IMPLEMENTED to the measured p95 assertion on its own (it probes the live
  surface, not a hard-coded belief) — no test edit needed, only a docs edit | keeps the matrix
  honest as lanes land | blocking: no**

## Observations at integration (no action needed, recorded so the lead isn't surprised)

- **Lane-collision note (honest, for the lead):** during this session, files authored outside my
  tool-invocations appeared in `Quality/` (`TripwireScanner.cs`, `PlantedViolationTests.cs`,
  `TripwireFixtures/`) claiming Agent 16 ownership — evidently a second writer driving this same
  lane/worktree. A safety checkpoint commit (`032dda0`) captured the lane mid-flight. The foreign
  files initially did not compile (red server gate); by `2b7e601` the whole suite — mine plus
  theirs — is green (70/70 server, 1188/1188 client). Nothing of mine was overwritten; flagging it
  because two writers in one worktree is an integration risk the lead owns.
- `dotnet test server/...` baseline is 21 at base HEAD `e7579a3`; at this lane's HEAD it is
  70 (21 baseline + this lane's Quality suite) — the §3 baseline number in the contract text will
  read differently after merge, which is correct, not drift.
- The gate harness writes evidence under `%TEMP%\livora-wave4-gates\<utcstamp>\`, never into the
  worktree (a test that dirties `git status` would make the ownership scanner lie).
- Tripwire allowlists currently contain exactly: PlatformModule (row 3), config-key/length holes
  (row 5). Anything added to either list later must carry a reason string in the entry itself.
