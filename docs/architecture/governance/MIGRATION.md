# Registry migration v1 → v2

The engine (wave 5) loads both schema versions; **v1 files are never silently
reinterpreted** (§41 of the wave-5 brief). This document states exactly what
`version: 2` changes, how compatibility works, and the recipe LIVORA already
applied on `wave5/governance`.

## 1. Compatibility rules (implemented in `governance/registry.py`)

| Aspect | v1 behavior | v2 behavior |
|---|---|---|
| Root key | `version: 1` (or absent) | `version: 2` |
| Lane `exclusive` | defaults **false** — legacy `owns:` patterns are non-exclusive, so the historical `wave3b-keys/**` triple-claim does not retroactively explode | defaults **true** — two ACTIVE lanes with intersecting patterns = `OWNERSHIP_CONFLICT` RED at load |
| Branch co-assignment | WARNING (`REGISTRY_INVALID`, HARD_YELLOW in `validation_warnings`) — master's v1 registry lists `wave3c/integration` five times and still loads | RED unless the lane declares `shared_branch: true` |
| Dangling `overlap_groups` lane refs | WARNING | RED |
| Unknown lifecycle status | RED (both) | RED |
| `generated:` key | absent → built-in floor patterns apply (`engine_defaults.DEFAULT_GENERATED_PATTERNS`: obj/, bin/, *.Designer.cs, …) | optional; when present, its `paths:` replace the floor |
| `policy.base_branch` | defaults `master` | explicit (staleness/ancestry computed against it) |
| `policy.stale_commits_threshold` | default 25 | explicit |
| `policy.agent_bindings` | not enforced | maps GitHub actor login → lane id; divergence = `PR_AUTHOR_IDENTITY_MISMATCH` RED |
| Lane `task_revision`, `expiry` | optional, tolerated | optional, honored (`task_revision` mismatch = `TASK_REVISION_UNKNOWN`) |

Loading a v1 file therefore yields the **same ownership semantics as before**
plus a warnings list the CLI prints; nothing is upgraded silently.

## 2. New PR-body contract fields (both versions)

`Task-Hash` (sha256 hex, ≥8 chars, of the canonical task text),
`Task-Revision`, `Lane-Branch`, `Owned-Scope` (globs; `**` rejected for
non-lead lanes). See `.github/pull_request_template.md`. The old fields stay
required — the contract only grows.

## 3. Migration recipe (executed in this PR)

1. Fix dangling references: removed `w3b-lane08`/`w3b-lane09` from
   `overlap_groups` (they were never registered); groups re-declared over
   lanes that actually exist (`planning-domain`, `security-keys`).
2. Historical shared-branch lanes (`w3c-lane05/06/07/01b`, `integration-w3c`)
   keep `branch: wave3c/integration` + `shared_branch: true` — preserves the
   audit fact that they delivered there, satisfies one-branch-one-lane.
3. Closed waves moved to truthful lifecycles: `w3c-lane01..04` → `superseded`,
   `w3c-lane05..07/01b` → `merged`, `integration-w3c` → `released`,
   `w3b-lane02..05` → `stalled` (their branches still exist; reviving them is
   a lead lifecycle transition, which the state machine logs).
4. `agent_bindings: {opselon: lead}` declared for the real GitHub account of
   the coordinator. Add one entry per real actor as accounts are fixed —
   unbound actors fall back to the Agent-Id==lane.agent check only.
5. `generated: paths:` made explicit (same list as the built-in floor).
6. `version: 2` flips the strict defaults. `validate-registry` must be green
   before this bump; it is (`REGISTRY: VALID`, 15 lanes, 0 warnings).

## 4. Rollback story

Set `version: 1` and remove v2-only keys — the engine's v1 path restores
permissive defaults (no exclusive-conflict RED, hygiene as warnings). Lane
`status:` values survive both versions. Because classification evidence
(manifests) is additive, no historical artifact needs conversion. Rollback of
the *gate* itself is moving `.github/workflows/pr-gates.yml` back out of
`disabled/` (see `workflow-integration-notes.md`).

## 5. Verified example v2 registry

The example below was validated with
`livora_gates.py --registry <file> validate-registry` → `REGISTRY: VALID`
(2 lanes, 11 claims, 0 warnings) on the wave5/governance engine:

```yaml
version: 2
policy:
  master_push_forbidden: true
  rebase_policy: safe_only
  auto_merge_max_changed_files: 40
  squash_merge: true
  base_branch: master
  stale_commits_threshold: 25
  agent_bindings:
    opselon: lead
integration_owned:
  - MauiProgram.cs
  - Resources/Styles/**
frozen:
  owner: lead
  paths:
    - Application/Abstractions/**
    - Domain/Enums/**
architecture_owned:
  owner: lead
  paths:
    - .github/**
    - scripts/**
generated:
  paths:
    - obj/**
    - bin/**
lanes:
  - id: w5-lane01
    agent: agent-d
    wave: 5
    branch: agent/w5/lane01-*
    task: payment ledger module
    task_revision: 1
    owns:
      - Application/Ledger/**
      - Tests/Tests/Ledger/**
    status: active
    expiry: "2026-12-31"
  - id: lead
    agent: coordinator
    wave: all
    branch: integration/**
    task: contracts, APPEND application, merges, docs, ledger
    owns:
      - "**"
    status: active
overlap_groups: []
```

`w5-lane01`'s PR body must then declare
`Task-Hash: f8f8f4c4…` (sha256 of `"payment ledger module"` canonicalized)
— reproducible via `governance.registry.canonical_task_hash`.
