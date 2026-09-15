# P1-A (w4-p1a-architecture) — lane request ledger

Format: `R-<lane>-<n> | target file | what you need | why | blocking? yes/no`
(mirrors `docs/architecture/wave4/INTEGRATION_REQUESTS-P1.md`; this file is the per-lane copy the
contract's §2 asks each lane to keep. Lead resolves at merge.)

R-P1A-1 | .github/OWNERSHIP.yaml (lane row w4-p1a-architecture) | add `- docs/integration/wave4/**` to `owns:` | the Phase-1 brief assigns me the integration matrix, but the registry row omits the path; the scope gate would report UNREGISTERED SCOPE on my PR | blocking? no
R-P1A-2 | .github/OWNERSHIP.yaml (architecture_owned vs lane rows) | confirm classification: my deliverables live under `docs/architecture/wave4/**`, which is ALSO inside `architecture_owned: docs/architecture/**` (`scripts/integration/livora_gates.py:302-325`: frozen/arch/integration matches first ⇒ RED for a lane, YELLOW for lead). Either exempt `docs/architecture/wave4/**` for the p1a row or accept every p1a PR as a lead-review YELLOW | contract §2 tells me to write these docs; the registry must not call them violations | blocking? no (documentation lane, merged manually by the lead)
R-P1A-3 | docs/architecture/wave4/CONTRACT-P1.md | DONE-IN-LANE amendment (recorded): base a2b6b97→e7579a3, server baseline 17→21, binding-doc pointer list added | the contract went stale under commits f17ae9f/3710385/e7579a3; lanes must fork the current SHA | blocking? no
R-P1A-4 | informational | migration-ready | none — P1-A owns no entities and no code; proposed table names for P1-B/C/E are fixed in ARCHITECTURE-P1 §3 so the lead's single `Wave4P1Schema` generation is predictable | blocking? no
