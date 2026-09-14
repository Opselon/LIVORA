# Wave 4 Phase 1 — cross-lane integration requests (APPEND-ONLY, shared dir)

Rules: append under YOUR heading only. Never edit or reorder another lane's section.
Format per request: `R-<lane>-<n> | target file | what you need | why | blocking? yes/no`
The lead (orchestrator) resolves these at merge time and records the verdict in each lane's section.

## P1-A (architecture / audit)
R-P1A-1 | .github/OWNERSHIP.yaml (lane row w4-p1a-architecture) | add `- docs/integration/wave4/**` to my `owns:` | I deliver the integration matrix per the Phase-1 brief, but the lane row lacks the path — gate would flag UNREGISTERED SCOPE (RED) on my own PR | blocking? no (lead merges this branch manually; fix before Wave-4 PR automation is used for p1a)
R-P1A-2 | .github/OWNERSHIP.yaml (architecture_owned) | `docs/architecture/**` is architecture_owned (lead-only ⇒ YELLOW/RED), yet it contains the lane-owned `docs/architecture/wave4/**` rows; add an exception pattern `docs/architecture/wave4/requests/**` stays per-lane and confirm classification order treats `w4-p1a-architecture` edits under `docs/architecture/wave4/**` as lane-scoped | my contract/architecture deliverables sit inside an architecture_owned glob (`scripts/integration/livora_gates.py:302-325`) | blocking? no (same as R-1)
R-P1A-3 | docs/architecture/wave4/CONTRACT-P1.md | amended: base SHA a2b6b97→e7579a3, server baseline 17→21 (pre-wire commit f17ae9f), + pointer list to binding P1 docs | the contract's stated baseline went stale under its own commits; lanes must fork e7579a3 | blocking? no (already applied in this lane by lead instruction "docs are yours")
R-P1A-4 | (informational, no file) | migration-ready | (none — this lane owns no entities; the doc set defines proposed table names for P1-B/C/E in ARCHITECTURE-P1 §3) | audit §B records the single Wave4P1Schema migration duty at integration step 7 | blocking? no

## P1-B (platform / persistence / sync)
(none yet)

## P1-C (identity)
(none yet)

## P1-D (client↔cloud seam)
(none yet)

## P1-E (engines / verification)
(none yet)

## P1-F (QA / release gate)
(none yet)

## LEAD VERDICTS (lead writes last)
(none yet)
