"""Governance mutation harness (wave 5). Run:
  python3 Tests/Governance/mutation_harness.py   (from repo root; suite must
kill every mutant — survivors indicate missing governance coverage.)

Governance mutation harness: flip decision-critical logic, the suite MUST kill it.

Equivalent mutants (changes that cannot alter any observable verdict) are
reported as EQUIVALENT, not failures. Line endings normalized in memory so
patterns match regardless of checkout settings.
"""
import os
import subprocess
import sys

PY = sys.executable
REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
E = REPO + "/scripts/integration/governance/engine.py"
P = REPO + "/scripts/integration/governance/paths.py"

MUTATIONS = [
    ("frozen RED -> YELLOW", E,
     "Reason.FROZEN_CONTRACT_TOUCHED,\n", "Reason.LEAD_AMENDMENT,\n"),
    ("fold ignores HARD_RED", E,
     "if any(c.severity == Severity.HARD_RED for c in constraints):\n        return Decision.RED",
     "if False:\n        return Decision.RED"),
    ("conflict C>1 disarmed", E, "if conflict_c > 1:", "if conflict_c > 99:"),
    ("agent-spoof removed", E,
     "con.append(_hc(Reason.PR_AUTHOR_IDENTITY_MISMATCH,\n                       f\"declared Agent-Id",
     "if True:\n            pass\n        _spoofed = (Reason.PR_AUTHOR_IDENTITY_MISMATCH,\n                       f\"declared Agent-Id"),
    ("OWNERSHIP_CONFLICT demoted to YELLOW", E,
     "    Reason.GENERATED_FILE_COMMITTED, Reason.LANE_STALE,",
     "    Reason.OWNERSHIP_CONFLICT,\n    Reason.GENERATED_FILE_COMMITTED, Reason.LANE_STALE,"),
    ("specificity metric flattened", P, "return 10 * p + 5 * d + e", "return 1"),
    ("traversal escape allowed", P,
     'raise PathError(f"path escapes repository root: {raw!r}")',
     "segments.append(seg); continue"),
    ("NFC normalization removed", P,
     's = unicodedata.normalize("NFC", raw).replace("\\\\", "/")',
     's = raw.replace("\\\\", "/")'),
    ("GREEN on empty changeset", E,
     "con.append(_hc(Reason.EMPTY_CHANGESET,", "con.append(_hc(Reason.MERGEABILITY_UNKNOWN,"),
    ("merged lane may deliver", E,
     "Lifecycle.MERGED):\n            con.append(_hc(Reason.LANE_NOT_DELIVERING,",
     "Lifecycle.RELEASED):\n            con.append(_hc(Reason.LANE_NOT_DELIVERING,"),
    ("secret scan disarmed", E,
     'for h in scans["secret"]:', "for h in []:"),
    ("case-collision detection removed", E,
     "if len(casefold_seen.get(cf, set())) > 1:", "if False:"),
    ("unknown-lane RED removed", E,
     "con.append(_hc(Reason.UNKNOWN_LANE,", "con.append(_hc(Reason.LEAD_AMENDMENT,"),
]


def main():
    srcs = {}
    for _, path, _, _ in MUTATIONS:
        if path not in srcs:
            with open(path, encoding="utf-8") as f:      # universal newlines
                srcs[path] = f.read()
    results = []
    for name, path, old, new in MUTATIONS:
        base = srcs[path]
        if old not in base:
            results.append((name, "PATCH-MISS"))
            continue
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(base.replace(old, new, 1))
        r = subprocess.run([PY, "-m", "pytest", "Tests/Governance", "-x", "-q", "--tb=no"],
                           cwd=REPO, capture_output=True, text=True)
        results.append((name, "KILLED" if r.returncode != 0 else "SURVIVED"))
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(base)
    for n, s in results:
        print(f"{n:38s} {s}")
    killed = sum(1 for _, s in results if s == "KILLED")
    miss = sum(1 for _, s in results if s == "PATCH-MISS")
    print(f"{killed}/{len(results)} killed ({miss} patch-miss)")
    return 0 if killed == len(results) - miss else 1


if __name__ == "__main__":
    sys.exit(main())
