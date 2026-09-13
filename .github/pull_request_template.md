<!-- Fill EVERY field. The integration controller parses these lines; a missing
field = RED = blocked. Agent claims below are NEVER proof — only gate runs are. -->

Task-Id: <lane-or-issue id, e.g. w3b-lane02 / #3>
Agent-Id: <agent-a | agent-b | agent-c | coordinator>
Wave: <3b | 3c | ...>
Base-Commit: <full SHA of the master commit this work was branched from>
Owned-Scope: <comma-separated globs this PR is allowed to touch, from OWNERSHIP.yaml lane owns>
Tests-Run: <what you ran locally, e.g. dotnet test 439/439 — CLAIM, will be re-executed>
Build-Run: <dotnet build -f net10.0-windows ... 0W/0E, or not-run>

## What
<one paragraph: feature/fix delivered>

## Honesty notes
<which parts are real vs mock/dev-flagged; any UI claim that could mislead>

## Dependencies
<contracts consumed, other lanes/PRs this assumes>

## Checklist (controller-checked, do not tick for others)
- [ ] No file outside Owned-Scope changed
- [ ] No Application/Abstractions/**, Domain/Enums/**, .github/**, scripts/** edits
- [ ] New strings added via wave-keys XML files, not by editing AppResources.resx
- [ ] DI/Settings changes delivered as APPEND blocks in the PR description
