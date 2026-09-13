#!/usr/bin/env python3
"""Append/update an entry in docs/agents/ledger.json on master via the GitHub API.

Usage:
  livora_ledger.py merge   --pr N --sha SHA            # state=MERGED
  livora_ledger.py verify  --sha SHA                   # last entry -> REVERIFIED, verified_master_sha=SHA
  livora_ledger.py fail    --sha SHA                   # last entry -> RECOVERY_REQUIRED, verified_master_sha stays

Requires: GH_TOKEN + GH_REPO env. The ledger is append-only in spirit: entries
are never removed, only state-transitioned. Ephemeral run info (git sha of the
ledger writer) is not recorded — PR/head SHAs are the durable identifiers.
"""
import argparse
import base64
import json
import os
import subprocess
import sys

PATH = "docs/agents/ledger.json"


def gh(args):
    return subprocess.run(["gh", "api"] + args, capture_output=True, text=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("action", choices=["merge", "verify", "fail"])
    ap.add_argument("--pr", type=int)
    ap.add_argument("--sha", default="")
    ap.add_argument("--override", action="store_true",
                    help="coordinator-authorized YELLOW merge (recorded in the entry)")
    a = ap.parse_args()
    repo = os.environ.get("GH_REPO") or sys.exit("GH_REPO unset")
    if not os.environ.get("GH_TOKEN"):
        sys.exit("GH_TOKEN unset")

    r = gh([f"repos/{repo}/contents/{PATH}", "--jq", ".content"])
    if r.returncode == 0 and r.stdout.strip():
        cur = json.loads(base64.b64decode(r.stdout.strip()).decode())
        old = gh([f"repos/{repo}/contents/{PATH}", "--jq", ".sha"]).stdout.strip()
    else:
        cur = {"entries": []}
        old = ""
    cur.setdefault("entries", [])

    if a.action == "merge":
        if not a.pr or not a.sha:
            sys.exit("merge needs --pr and --sha")
        entry = {"pr": a.pr, "head_sha": a.sha, "state": "MERGED",
                 "run": os.environ.get("GITHUB_RUN_ID", "")}
        if a.override:
            entry["override"] = "coordinator YELLOW override"
        cur["entries"].append(entry)
    elif a.action == "verify":
        if not cur["entries"]:
            sys.exit("nothing to verify")
        cur["entries"][-1]["state"] = "REVERIFIED"
        cur["entries"][-1]["verified_master_sha"] = a.sha
        cur["verified_master_sha"] = a.sha
    else:  # fail
        if cur["entries"]:
            cur["entries"][-1]["state"] = "RECOVERY_REQUIRED"
        cur["last_failure_run"] = os.environ.get("GITHUB_RUN_ID", "")

    b = base64.b64encode(json.dumps(cur, indent=1).encode()).decode()
    msg = f"ledger: {a.action} {a.sha or a.pr or ''} [skip ci]".strip()
    args = [f"repos/{repo}/contents/{PATH}", "-X", "PUT",
            "-f", f"message={msg}", "-f", f"content={b}", "-f", "branch=master"]
    if old:
        args += ["-f", f"sha={old}"]
    w = gh(args)
    if w.returncode != 0:
        print(w.stderr, file=sys.stderr)
        sys.exit(1)
    print(f"ledger {a.action} recorded")


if __name__ == "__main__":
    main()
