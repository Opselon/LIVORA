<!-- Fill EVERY field. The governance controller parses these lines; a missing
required field = RED = blocked. Agent claims below are NEVER proof — only the
gate's own executed tests are. Task-Hash must be the sha256 (hex, any prefix
>=8 chars) of the canonicalized task text registered for your lane id in
.github/OWNERSHIP.yaml ('task' field, whitespace-collapsed, case-folded). -->

Task-Id: <lane id from .github/OWNERSHIP.yaml lanes[], e.g. w4-p1b-platform>
Agent-Id: <the agent your lane registers: agent-a | agent-b | agent-c | coordinator>
Wave: <3b | 3c | 4 | ...>
Base-Commit: <full SHA of the master commit this work was branched from>
Lane-Branch: <the branch this PR is delivered on; must match the lane's registered branch glob>
Owned-Scope: <comma-separated globs from YOUR lane's owns[] — '**' is rejected (RED)>
Task-Hash: <sha256 of your lane's canonical task text — proves the task definition you worked from>
Task-Revision: <lane's task_revision in the registry, integer>
Tests-Run: <what you ran locally, e.g. dotnet test 439/439 — CLAIM, will be re-executed>
Build-Run: <dotnet build -f net10.0-windows ... 0W/0E, or not-run>

## What
<one paragraph: feature/fix delivered>

## Honesty notes
<which parts are real vs mock/dev-flagged; any UI claim that could mislead>

## Dependencies
<contracts consumed, other lanes/PRs this assumes>

## Task drift (only if Task-Hash differs from the registry — otherwise delete)
Task-Drift-Ack: <what changed in the task definition and whether it touches ownership /
security / architecture responsibilities. Ownership/security/architecture changes
require explicit lead re-authorization BEFORE this PR is openable.>

## Checklist (controller-checked, do not tick for others)
- [ ] No file outside Owned-Scope changed
- [ ] No Application/Abstractions/**, Domain/Enums/**, .github/**, scripts/** edits
- [ ] New strings added via wave-keys XML files, not by editing AppResources.resx
- [ ] DI/Settings changes delivered as APPEND blocks in the PR description
- [ ] governance_local.py ran clean on this branch (or CI gate reviewed)
