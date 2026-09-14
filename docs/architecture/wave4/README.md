# Wave 4 documentation set (Phase 1) — index

Written by **Agent 01 / lane `w4-p1a-architecture`** at baseline `e7579a3`. These files are the audit
truth and the binding architecture/contract surface for all 16 Wave-4 agents. Read in this order:

| # | File | What it settles | Read if you are… |
|---|---|---|---|
| 0 | `CONTRACT-P1.md` | per-lane rules of engagement (amended at e7579a3) | ANY lane — mandatory first |
| 1 | `docs/audit/wave4/AUDIT-P1-BASELINE.md` | what is actually REAL/PARTIAL/UNCONFIGURED/MISSING/REFUTED today + executed gate evidence + the state-claim law | anyone about to make a status claim |
| 2 | `ARCHITECTURE-P1.md` | component map, module+table ownership, sync/identity protocol, truthfulness spine, Phase-2 boundaries, risks | every lane (design authority) |
| 3 | `docs/contracts/wave4/CAPABILITY-MAP.md` | the 13 capability keys × owner × today's state × what Phase 2 may not re-decide | server lanes, client connector UI |
| 4 | `docs/contracts/wave4/SERVER-CONTRACT-P1.md` | module obligations, per-module provides/consumes/invariants, merge-time cross checks | P1-B / P1-C / P1-E |
| 5 | `docs/contracts/wave4/CLIENT-CONTRACT-P1.md` | seam layout, token custody, ProblemCode→l10n map, offline bridge, capability screen vocabulary | P1-D, Phase-2 client lanes |
| 6 | `docs/decisions/wave4/ADR-INDEX.md` | ADR-0001…0008 (monolith, contributions, auth, provider posture, state-claim, sync, codes, governance) | anyone tempted to deviate |
| 7 | `docs/integration/wave4/INTEGRATION-MATRIX-P1.md` | merge order, rendezvous points, collision pre-judgement, Phase-1 exit criteria | the lead + lanes planning merges |
| 8 | `INTEGRATION_REQUESTS-P1.md` / `requests/<lane>.md` | the only sanctioned change channel for frozen files | every lane, continuously |

Rules that never leave these files: truth over appearance (§0 of the contract), the state-claim law
(audit §D), frozen-path discipline + request ledger (contract §2, ADR-0008), no lane migrations
(ADR-0002), no push (contract §8).
