#!/usr/bin/env python3
"""LIVORA integration controller — gate evaluator.

Deterministic, fail-safe. Classifies a PR diff against the ownership registry:
  RED    -> block merge, print actionable diagnostics (exit 2)
  YELLOW -> block auto-merge, request coordinator review (exit 1)
  GREEN  -> eligible for automatic merge (exit 0)

Input files (prepared by the workflow, not by this script):
  changed.txt     one path per line (PR vs base, rename/add/delete already flattened)
  additions.diff  unified diff of the PR (context may be absent; only + lines matter)
  meta.json       {"pr": N, "title": str, "body": str, "base_sha": str, "head_sha": str,
                   "merge_sha": str, "mergeable": "clean|dirty|unknown",
                   "commits": N, "author": str}

The script NEVER merges and NEVER trusts agent claims: it re-derives everything
from files GitHub produced. On any error or missing input it exits 2 (RED) —
uncertainty blocks, by design.
"""
import fnmatch
import json
import os
import re
import sys

ROOT = os.getcwd()
REG_PATH = os.path.join(ROOT, ".github", "OWNERSHIP.yaml")


def fail_hard(msg: str) -> None:
    print("::error::" + msg)
    print("\nVERDICT: RED (controller error — fail-safe stop)")
    sys.exit(2)


# ---------------------------------------------------------------- registry load
def load_registry(path: str) -> dict:
    """Parse the subset of OWNERSHIP.yaml we enforce. PyYAML if present, else a
    small indentation parser that understands this file's shapes only."""
    try:
        text = open(path, encoding="utf-8").read()
    except OSError:
        fail_hard(f"registry missing: {path}")
    try:
        import yaml  # type: ignore
        return yaml.safe_load(text)
    except ImportError:
        return _mini_yaml(text)


def _mini_yaml(text: str) -> dict:
    """Enough YAML for OWNERSHIP.yaml: maps, scalar lists, list-of-maps, comments."""
    root: dict = {}
    # stack of (indent, container)
    stack: list[tuple[int, object]] = [(-1, root)]

    def close_to(indent: int):
        while stack and stack[-1][0] >= indent:
            stack.pop()

    lines = [l.rstrip("\n") for l in text.splitlines()]
    i = 0
    while i < len(lines):
        raw = lines[i]
        i += 1
        if not raw.strip() or raw.strip().startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        line = raw.strip()
        close_to(indent)
        parent = stack[-1][1]
        if line.startswith("- "):
            item = line[2:].strip()
            if not isinstance(parent, list):
                fail_hard(f"registry parse: list item outside list at: {raw}")
            if ":" in item and not item.startswith('"'):
                # list-of-maps entry
                d: dict = {}
                parent.append(d)
                stack.append((indent, d))
                _kv(d, item)
            else:
                parent.append(_scalar(item))
            continue
        if isinstance(parent, dict):
            m = re.match(r"^([\w./-]+):\s*(.*)$", line)
            if not m:
                fail_hard(f"registry parse: bad line: {raw}")
                return root
            key, val = m.group(1), m.group(2).strip()
            if val == "":
                # container: peek next non-comment line to pick list vs map
                nxt = next((l for l in lines[i:] if l.strip() and not l.strip().startswith("#")), "")
                nind = len(nxt) - len(nxt.lstrip(" "))
                if nind > indent and nxt.strip().startswith("- "):
                    child: list = []
                    parent[key] = child
                    stack.append((indent, child))
                elif nind > indent:
                    child_d: dict = {}
                    parent[key] = child_d
                    stack.append((indent, child_d))
                else:
                    parent[key] = {}
            else:
                parent[key] = _scalar(val)
        else:
            # list of scalars under a key
            m = re.match(r"^([\w./-]+):\s*(.*)$", line)
            if m:
                key, val = m.group(1), m.group(2).strip()
                holder = parent  # the dict that owns this list
                if val == "":
                    nxt = next((l for l in lines[i:] if l.strip() and not l.strip().startswith("#")), "")
                    nind = len(nxt) - len(nxt.lstrip(" "))
                    if nind > indent and nxt.strip().startswith("- "):
                        child = []
                        holder[key] = child
                        stack.append((indent, child))
                    else:
                        holder[key] = {}
                else:
                    holder[key] = _scalar(val)
    return root


def _kv(d: dict, item: str) -> None:
    m = re.match(r"^([\w-]+):\s*(.*)$", item)
    if not m:
        return
    k, v = m.group(1), m.group(2).strip()
    if v.startswith("[") and v.endswith("]"):
        inner = v[1:-1].strip()
        d[k] = [ _scalar(x.strip()) for x in inner.split(",") ] if inner else []
    elif v:
        d[k] = _scalar(v)
    else:
        d[k] = None


def _scalar(v: str):
    v = v.strip()
    if v.startswith("["):                      # inline list: [a, b] / []
        inner = v.strip("[]").strip()
        return [_scalar(x) for x in inner.split(",")] if inner else []
    v = re.split(r"\s+#", v, 1)[0].strip()    # strip trailing inline comment
    v = v.strip('"').strip("'")
    if v.isdigit():
        return int(v)
    if v.lower() in ("true", "false"):
        return v.lower() == "true"
    return v


# ---------------------------------------------------------------- PR contract
REQUIRED_META = [
    ("task_id", r"Task-Id:\s*(\S+)"),
    ("agent_id", r"Agent-Id:\s*(\S+)"),
    ("wave", r"Wave:\s*(\S+)"),
    ("base_commit", r"Base-Commit:\s*([0-9a-f]{7,40})"),
    ("owned_scope", r"Owned-Scope:\s*(.+)"),
    ("tests_run", r"Tests-Run:\s*(.+)"),
]
FAKE_CLAIM = re.compile(r"Tests-Run:\s*\d+\s*(?:passed|green|/)", re.I)


def parse_pr_contract(body: str) -> dict:
    out = {}
    for key, pat in REQUIRED_META:
        m = re.search(pat, body)
        out[key] = m.group(1).strip() if m else None
    return out


# ---------------------------------------------------------------- matching
def norm(p: str) -> str:
    p = p.replace("\\", "/")
    while p.startswith("./"):
        p = p[2:]
    return p.lstrip("/")


def matches_any(path: str, patterns: list[str]) -> bool:
    for pat in patterns:
        pat = norm(pat)
        if pat == "**":
            return True
        if pat.endswith("/**"):
            base = pat[:-3]
            if path == base or path.startswith(base + "/"):
                return True
        elif fnmatch.fnmatch(path, pat):
            return True
        elif pat == path:
            return True
    return False


def lane_for_path(path: str, reg: dict):
    """Return (lane_id, owns) for the registered lane whose owns cover path, else (None, ...)."""
    for lane in reg.get("lanes") or []:
        if not isinstance(lane, dict):
            continue
        owns = [norm(o) for o in (lane.get("owns") or [])]
        if lane.get("id") == "lead":
            continue
        if matches_any(path, owns):
            return lane.get("id"), owns
    return None, []


# ---------------------------------------------------------------- scanners
SECRET_PATTERNS = [
    re.compile(r"(?i)\bsk-[A-Za-z0-9][A-Za-z0-9._-]{16,}"),   # full API keys, not stubs like sk-1cd...7367
    re.compile(r"(?i)api[_-]?key\s*[:=]\s*['\"][^'\"]{16,}['\"]"),
    re.compile(r"(?i)bearer\s+[A-Za-z0-9._-]{32,}"),
    re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    re.compile(r"(?i)(password|passwd|secret)\s*[:=]\s*['\"][^'\"]{8,}['\"]"),
]
BAD_XAML_COMMENT = re.compile(r"\{" + chr(47) + r"\*")
DI_HOLLOW = re.compile(r"services\.Add(Singleton|Scoped|Transient)<(\w+)>\(\);")
BRUSH_ON_COLOR = re.compile(r"(TextColor|BackgroundColor|FieldBackgroundColor)\s*=\s*\"\{StaticResource\s+\w*Brush\}\"\s*")


def scan_added_lines(additions_path: str):
    hits = {"secret": [], "brace_xaml": [], "hollow_di": [], "brush": []}
    cur = ""
    try:
        with open(additions_path, encoding="utf-8", errors="replace") as f:
            for ln in f:
                if ln.startswith("diff --git ") or ln.startswith("--- "):
                    continue
                if ln.startswith("+++ b/"):
                    cur = norm(ln[6:].strip())
                    continue
                if not ln.startswith("+") or ln.startswith("+++"):
                    continue
                code = ln[1:]
                loc = f"{cur}: {ln!r}"[:160]
                for pat in SECRET_PATTERNS:
                    if pat.search(code):
                        hits["secret"].append(loc)
                        break
                if cur.endswith(".xaml") and BAD_XAML_COMMENT.search(code):
                    hits["brace_xaml"].append(loc)
                if cur.endswith(".cs") and DI_HOLLOW.search(code):
                    hits["hollow_di"].append(loc)
                if cur.endswith(".xaml") and BRUSH_ON_COLOR.search(code):
                    hits["brush"].append(loc)
    except OSError:
        fail_hard("additions.diff not readable")
    return hits


def main() -> None:
    reg = load_registry(REG_PATH)
    policy = reg.get("policy") or {}

    try:
        changed = [norm(l.strip()) for l in open("changed.txt") if l.strip()]
    except OSError:
        fail_hard("changed.txt not readable (diff step did not run?)")
    try:
        meta = json.load(open("meta.json", encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        fail_hard("meta.json not readable")

    red: list[str] = []
    yellow: list[str] = []

    frozen = [norm(p) for p in ((reg.get("frozen") or {}).get("paths") or []) if isinstance(p, str)]
    arch = [norm(p) for p in ((reg.get("architecture_owned") or {}).get("paths") or []) if isinstance(p, str)]
    integ = [norm(p) for p in (reg.get("integration_owned") or []) if isinstance(p, str)]

    # 1. mergeability
    mg = meta.get("mergeable", "unknown")
    if mg == "dirty":
        red.append(f"CONFLICT: GitHub reports mergeable=dirty against current master ({meta.get('base_sha','?')[:8]}). Rebase branch onto master HEAD and re-open.")
    elif mg == "unknown":
        yellow.append("MERGEABILITY UNKNOWN (GitHub still computing) — controller will not guess; re-run gate.")

    # 2. PR contract metadata
    contract = parse_pr_contract(meta.get("body") or "")
    for k in ("task_id", "agent_id", "wave", "base_commit", "tests_run"):
        if not contract.get(k):
            red.append(f"INVALID METADATA: PR body missing '{k}' line (use .github/pull_request_template.md).")
    if contract.get("tests_run") and FAKE_CLAIM.search(meta.get("body") or ""):
        print("INFO  | AGENT CLAIM: Tests-Run numbers are claims, not proof. Only the gate job's executed tests count.")

    # 3. declared base vs actual fork point (merge-base computed from git, not claims)
    mb = meta.get("merge_base")
    if mb and contract.get("base_commit"):
        if not mb.startswith(contract["base_commit"]):
            yellow.append(f"BASE METADATA MISMATCH: PR declares Base-Commit {contract['base_commit'][:8]} but its actual merge-base with master is {mb[:8]} (branch history rewritten or wrong declaration).")
        elif mb != meta.get("base_sha"):
            print(f"INFO  | behind master: forked at {mb[:8]}, master is at {meta.get('base_sha','')[:8]} — merge-state build/test still validates the combination.")

    # 4. per-path classification (per-rule findings aggregated so a 45-file PR
    #    reports one drift line, not 45)
    declared = [norm(x.strip()) for x in re.split(r"[,;]", contract.get("owned_scope") or "") if x.strip()]
    # lead-amendment exception: a PR declaring the registry lead's Agent-Id may touch
    # frozen/architecture/integration-owned paths, but only as YELLOW (never auto-GREEN).
    lead_agent = next((l.get("agent") for l in (reg.get("lanes") or [])
                       if isinstance(l, dict) and l.get("id") == "lead"), None)
    acting_as_lead = bool(lead_agent) and contract.get("agent_id") == lead_agent
    lead_yellow: list[str] = []
    shared_hits: list[str] = []
    drift_hits: list[str] = []
    red_path_hits = 0
    for path in changed:
        if matches_any(path, frozen) or matches_any(path, arch) or matches_any(path, integ):
            if acting_as_lead:
                lead_yellow.append(path)
            elif matches_any(path, frozen):
                red.append(f"FROZEN CONTRACT EDIT: {path} — Application/Abstractions/** + Domain/Enums/** are lead-only; ask the lead to amend via a separate PR.")
            else:
                red.append(f"ARCHITECTURE-OWNED EDIT: {path} — workflows/registry/scripts/integration-owned files are lead-only.")
            continue
        lane_id, _owns = lane_for_path(path, reg)
        if lane_id is None:
            if red_path_hits < 6:
                red.append(f"UNREGISTERED SCOPE: {path} matches no lane 'owns' in .github/OWNERSHIP.yaml — register the lane before opening the PR.")
            red_path_hits += 1
        elif declared and not matches_any(path, declared):
            drift_hits.append(path)
    if lead_yellow:
        yellow.append(f"LEAD AMENDMENT ({len(lead_yellow)} files): frozen/architecture/integration-owned paths touched by the lead lane itself ({lead_yellow[:6]}...) — human/coordinator review required, never auto-GREEN.")
    if shared_hits:
        yellow.append(f"SHARED FILE ({len(shared_hits)}): {shared_hits[:6]}{'...' if len(shared_hits) > 6 else ''} "
                      f"— integration-owned; deliver APPEND blocks instead of editing.")
    if drift_hits:
        yellow.append(f"SCOPE DRIFT ({len(drift_hits)} files inside the lane but outside declared Owned-Scope): {drift_hits[:6]}")
    if red_path_hits > 6:
        red.append(f"(+{red_path_hits - 6} more frozen/architecture/unregistered path violations)")

    # 5. duplicate implementation: two NEW type definitions of the same name
    iface_counter: dict[str, list[str]] = {}
    try:
        with open("additions.diff", encoding="utf-8", errors="replace") as f:
            cur = ""
            for ln in f:
                if ln.startswith("+++ b/"):
                    cur = norm(ln[6:].strip())
                    continue
                if not ln.startswith("+"):
                    continue
                m = re.match(r"^\+\s*(?:public|internal|sealed|abstract|static|\s)*(?:interface|class|record|enum)\s+(\w+)", ln)
                if m and cur.endswith(".cs"):
                    iface_counter.setdefault(m.group(1), []).append(cur)
    except OSError:
        fail_hard("additions.diff not readable")
    for name, files in iface_counter.items():
        if len(set(os.path.basename(f) for f in files)) > 1 or len(set(files)) > 1:
            yellow.append(f"POSSIBLE DUPLICATE TYPE: '{name}' defined in {sorted(set(files))}")

    # 6. overlap groups: PR touching paths owned by more than one registered lane
    touched_lanes = set()
    for path in changed:
        lid, _ = lane_for_path(path, reg)
        if lid:
            touched_lanes.add(lid)
    for grp in reg.get("overlap_groups") or []:
        if not isinstance(grp, dict):
            continue
        members = set(grp.get("lanes") or [])
        if len(touched_lanes & members) > 1:
            yellow.append(f"OVERLAP GROUP '{grp.get('name')}': change set spans lanes {sorted(touched_lanes & members)} — one lane must win; lead arbitration.")

    # 7. size gate
    max_files = int(policy.get("auto_merge_max_changed_files") or 40)
    if len(changed) > max_files:
        yellow.append(f"OVERSIZED CHANGE: {len(changed)} files > {max_files} auto-merge cap.")

    # 8. content scanners
    hits = scan_added_lines("additions.diff")
    if hits["secret"]:
        red.append(f"SECRET IN DIFF: {len(hits['secret'])} hit(s) — purge from history before any merge. samples: {hits['secret'][:3]}")
    if hits["brace_xaml"]:
        red.append(f"XAML BRACE-COMMENT (MAUIX2002 tripwire): {hits['brace_xaml'][:2]} — use <!-- --> anchors.")
    if hits["hollow_di"]:
        yellow.append(f"SUSPECT HOLLOW DI: interface registered with no instance ({hits['hollow_di'][:2]}).")
    if hits["brush"]:
        yellow.append(f"BRUSH-ON-COLOR PATTERN ({hits['brush'][:2]}) — known MAUI runtime trap; verify at app build.")

    # ---- verdict (fail-safe: any doubt stops the merge) -----------------------
    print(f"CHANGED FILES: {len(changed)}")
    for r in red:
        print("RED   | " + r)
    for y in yellow:
        print("YELLOW| " + y)
    if red:
        print("\nVERDICT: RED")
        sys.exit(2)
    if yellow:
        print("\nVERDICT: YELLOW — held for coordinator review")
        sys.exit(1)
    print("\nVERDICT: GREEN — eligible for automatic merge (build/tests still must pass)")
    sys.exit(0)


if __name__ == "__main__":
    main()
