#!/usr/bin/env python3
"""LIVORA governance CLI — registry validation, PR classification, audits.

Subcommands
  validate-registry  registry self-validation (schema + lifecycle + conflicts)
  classify           governance classification of a PR from evidence files
  inspect-pr         classify + full manifest (JSON) for CI artifacts
  explain            human-readable explanation of the last classification
  audit              workspace/branch drift audit (lanes vs git state)
  benchmark          synthetic scaling benchmark (1..N lanes)

Exit codes (stable contract for CI):
  classify/inspect-pr: 0 GREEN, 1 YELLOW, 2 RED
  validate-registry:   0 valid, 2 invalid
  audit/benchmark:     0 on success (findings are data, not exit codes)

Security: git is invoked with argument arrays only; registry/diff inputs are
untrusted; every failure path exits 2 (fail-closed). No shell interpolation.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from governance import git_evidence as gite                      # noqa: E402
from governance.engine import (GovernanceInput, classify,           # noqa: E402
                               parse_pr_contract)
from governance.model import Reason, Severity                       # noqa: E402
from governance.paths import PathError, normalize_repo_path         # noqa: E402
from governance.registry import (RegistryError, canonical_task_hash,  # noqa: E402
                                 load_registry)
from governance.report import build_manifest, dump_json, render_human  # noqa: E402

LEGACY_CONTRACT_FIELDS = ("task_id", "agent_id", "wave", "base_commit", "tests_run")


def _red(reason: Reason, msg: str) -> int:
    print("RED   | " + reason.value + ": " + msg)
    print("\nVERDICT: RED")
    return 2


# ---------------------------------------------------------------- registry cmd
def cmd_validate_registry(args) -> int:
    try:
        reg = load_registry(args.registry)
    except RegistryError as e:
        for c in e.constraints:
            print(f"{c.severity.value:11s} | {c.reason.value} | {c.message}")
        print("\nREGISTRY: INVALID")
        return 2
    except (OSError, PathError) as e:
        print(f"REGISTRY_CORRUPTION | {e}")
        return 2
    info = {
        "version": reg.version, "sha256": reg.sha256,
        "lanes": len(reg.lanes), "claims": len(reg.claims),
        "frozen": len(reg.frozen), "architecture": len(reg.architecture),
        "integration": len(reg.integration_owned),
        "overlap_groups": len(reg.overlap_groups),
    }
    if args.json:
        print(json.dumps(info, sort_keys=True))
    else:
        for k, v in info.items():
            print(f"{k:16s} = {v}")
    print("\nREGISTRY: VALID (schema + lifecycle + exclusive-conflict scan passed)")
    return 0


# --------------------------------------------------------------- classify cmd
def _load_legacy_inputs(root: str) -> tuple[list, dict, dict, str]:
    """changed.txt / meta.json / additions.diff — the workflow-produced inputs."""
    changed_p = os.path.join(root, "changed.txt")
    meta_p = os.path.join(root, "meta.json")
    diff_p = os.path.join(root, "additions.diff")
    if not os.path.isfile(meta_p):
        raise FileNotFoundError(meta_p)
    # changed.txt/changed.json are optional ONLY because _changeset_from_lines
    # can derive richer evidence from local git + meta SHAs; an empty result
    # still fails closed later (EMPTY_CHANGESET / MISSING_EVIDENCE).
    lines = ([l.strip() for l in open(changed_p, encoding="utf-8") if l.strip()]
             if os.path.isfile(changed_p) else [])
    meta = json.load(open(meta_p, encoding="utf-8"))
    diff_text = open(diff_p, encoding="utf-8", errors="replace").read() \
        if os.path.isfile(diff_p) else ""
    return lines, meta, diff_text


def _changeset_from_lines(lines: list[str], input_dir: str, meta: dict) -> list:
    """Build the changeset from the strongest available evidence:
    1. changed.json in input_dir (workflow-derived name-status with ops + renames)
    2. local git diff base...head (when both SHAs exist in this checkout)
    3. legacy changed.txt flattened list -> operation UNKNOWN (engine fails closed)
    """
    rich_p = os.path.join(input_dir, "changed.json")
    if os.path.isfile(rich_p):
        try:
            data = json.load(open(rich_p, encoding="utf-8"))
            out = []
            for e in data:
                out.append(gite.ChangedFile(
                    path=e.get("path", ""), old_path=e.get("old_path"),
                    operation=_op(e.get("operation", "unknown")),
                    binary=bool(e.get("binary", False))))
            if out:
                return out
        except (json.JSONDecodeError, OSError):
            pass  # corrupt rich input: fall back to weaker evidence
    base, head = meta.get("base_sha"), meta.get("head_sha")
    mb = meta.get("merge_base")
    if base and head:
        try:
            chk = subprocess.run(["git", "rev-parse", "--git-dir"], capture_output=True, text=True)
            if chk.returncode == 0:
                cs = gite.read_name_status_diff(".", mb or base, head)
                if cs:
                    return cs
        except (gite.GitError, OSError, FileNotFoundError):
            pass
    seen = set()
    out = []
    for l in lines:
        if l in seen:
            continue
        seen.add(l)
        # flattened name-only evidence cannot prove the operation: UNKNOWN ops
        # fail closed in the engine (UNKNOWN_OPERATION) — by design.
        out.append(gite.ChangedFile(path=l, operation=_op("unknown"), old_path=None))
    return out


def _op(name: str):
    from governance.model import Operation
    try:
        return Operation(name)
    except ValueError:
        return Operation.UNKNOWN


def _branch_evidence(reg_repo_root: str, contract: dict, reg, lane) -> gite.BranchEvidence | None:
    """Derive branch topology from a local git checkout if available. When the
    checkout cannot answer, return evidence WITH gaps (fail-closed), not None,
    unless there is no git at all — then None (engine records MISSING_EVIDENCE)."""
    try:
        chk = subprocess.run(["git", "-C", reg_repo_root, "rev-parse", "--git-dir"],
                             capture_output=True, text=True)
        if chk.returncode != 0:
            return None
    except OSError:
        return None
    branch = contract.get("lane_branch") or (lane.branch if lane else None)
    base = (reg.policy or {}).get("base_branch", "master")
    declared_base = contract.get("base_commit")
    frozen_prefixes = tuple(sorted({m.literal_prefix[0] for m in reg.frozen
                                    if m.literal_prefix}))
    try:
        return gite.branch_evidence(reg_repo_root, branch or "", base,
                                    declared_base, frozen_prefixes)
    except gite.GitError as e:
        ev = gite.BranchEvidence()
        ev.gaps.append(f"GIT_ERROR: {e}")
        return ev


def _branch_evidence_from_meta(meta: dict) -> gite.BranchEvidence:
    """Branch evidence derived from GitHub-computed meta (workflow proves the
    head SHA exists by fetching it; merge_base is git-topology from CI)."""
    ev = gite.BranchEvidence()
    ev.head_sha = meta.get("head_sha") or None
    ev.base_sha = meta.get("base_sha") or None
    ev.merge_base = meta.get("merge_base") or None
    ev.branch_exists = bool(ev.head_sha)
    ev.commits_behind = int(meta.get("commits_behind", -1))
    ev.commits_ahead = int(meta.get("commits_ahead", -1))
    if not ev.merge_base:
        ev.gaps.append("NO_MERGE_BASE_IN_META")
    if ev.commits_behind < 0:
        ev.gaps.append("COMMITS_BEHIND_UNKNOWN")
    arch = bool(meta.get("architecture_changed_after_fork"))
    ev.architecture_changed_after_fork = arch
    return ev


def run_classify(args) -> tuple[int, dict]:
    reg_path = args.registry
    try:
        reg = load_registry(reg_path)
    except RegistryError as e:
        for c in e.constraints:
            print(f"{c.severity.value:11s} | {c.reason.value} | {c.message}")
        print("\nVERDICT: RED")
        return 2, {"final_decision": "RED", "reason_codes":
                   sorted({c.reason.value for c in e.constraints})}
    except OSError as e:
        print(f"REGISTRY_CORRUPTION | {e}")
        return 2, {"final_decision": "RED"}
    except Exception as e:  # any parser escape is RED, never a crash-through
        print(f"PARSER_FAILURE | {type(e).__name__}: {e}")
        return 2, {"final_decision": "RED"}

    try:
        lines, meta, diff_text = _load_legacy_inputs(args.input_dir or ".")
    except FileNotFoundError as e:
        print(f"MISSING_EVIDENCE | required input file not found: {e.filename}")
        print("\nVERDICT: RED")
        return 2, {"final_decision": "RED"}
    except (json.JSONDecodeError, OSError) as e:
        print(f"EVIDENCE_CORRUPTION | {e}")
        print("\nVERDICT: RED")
        return 2, {"final_decision": "RED"}

    contract = parse_pr_contract(meta.get("body") or "")
    if not contract.get("task_id"):
        m = LEGACY_CONTRACT_FIELDS and _legacy_task_id(meta)
        if m:
            contract["task_id"] = m
    changeset = _changeset_from_lines(lines, args.input_dir or ".", meta)
    lane = reg.lanes.get(contract.get("task_id") or "")
    if args.no_git:
        be = _branch_evidence_from_meta(meta)
    else:
        be = _branch_evidence(os.getcwd(), contract, reg, lane)
        if be is None:
            be = _branch_evidence_from_meta(meta)
        else:
            # merge GitHub-meta values where local git could not answer
            meta_be = _branch_evidence_from_meta(meta)
            if not be.merge_base:
                be.merge_base = meta_be.merge_base
                be.gaps = [g for g in be.gaps if not g.startswith("NO_MERGE_BASE")]
            if be.commits_behind < 0 and meta_be.commits_behind >= 0:
                be.commits_behind = meta_be.commits_behind
                be.gaps = [g for g in be.gaps if not g.startswith("COMMITS_BEHIND")]
            if be.commits_ahead < 0 and meta_be.commits_ahead >= 0:
                be.commits_ahead = meta_be.commits_ahead
            if not be.architecture_changed_after_fork:
                be.architecture_changed_after_fork = bool(
                    meta.get("architecture_changed_after_fork"))
    added = diff_text.count("\n+")
    deleted = diff_text.count("\n-")
    gi = GovernanceInput(
        registry=reg, changeset=changeset, contract=contract, meta=meta,
        branch_evidence=be, diff_text=diff_text, added=added, deleted=deleted,
        timestamp=args.as_of)
    res = classify(gi)
    manifest = build_manifest(
        gi, res, repo=os.environ.get("GH_REPO", os.path.basename(os.getcwd())),
        base_sha=meta.get("base_sha", ""), head_sha=meta.get("head_sha", ""),
        merge_base=meta.get("merge_base"), pr=meta.get("pr"))
    code = {"GREEN": 0, "YELLOW": 1, "RED": 2}[res.decision.value]
    return code, manifest


def _legacy_task_id(meta: dict) -> str | None:
    """Accept the v1 PR-body convention when Task-Id is a raw lane slug."""
    body = meta.get("body") or ""
    for lane_hint in ("Task-Id",):
        pass
    import re
    m = re.search(r"Task-Id:\s*(\S+)", body)
    return m.group(1) if m else None


def cmd_classify(args) -> int:
    code, manifest = run_classify(args)
    if args.json:
        print(dump_json(manifest))
    else:
        print(render_human(manifest))
    if args.manifest_out and manifest:
        with open(args.manifest_out, "w", encoding="utf-8") as f:
            f.write(dump_json(manifest))
    verdict = manifest.get("final_decision", "RED")
    if not args.json:
        note = {"GREEN": "— eligible for automatic merge (build/tests are separate gates)",
                "YELLOW": "— held for coordinator review",
                "RED": "— blocked"}[verdict]
        print(f"\nVERDICT: {verdict} {note}")
    return code


def cmd_inspect_pr(args) -> int:
    return cmd_classify(args)


def cmd_explain(args) -> int:
    try:
        manifest = json.load(open(args.manifest, encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as e:
        print(f"MISSING_EVIDENCE | {e}")
        return 2
    print(f"decision={manifest['final_decision']} lane={manifest.get('lane_id')} "
          f"task={manifest.get('task_id')}@rev{manifest.get('task_revision')} "
          f"registry@{str(manifest.get('registry_sha256'))[:12]}")
    by_file: dict[str, list] = {}
    for c in manifest.get("hard_constraints", []):
        by_file.setdefault(c.get("path") or "-", []).append(c)
    for fd in manifest.get("per_file_decisions", []):
        hc = by_file.get(fd["path"], [])
        print(f"\n[{fd['decision']:6s}] {fd['operation']:6s} {fd['path']}")
        print(f"   class={fd['policy_class']} owners={fd['owners']} "
              f"rules={fd['matched_rules']} relevance=L{fd['relevance_level']}")
        for c in fd.get("hard_constraints", []):
            print(f"   -> {c['severity']}: {c['reason']}")
            print(f"      {c['message']}")
    for path, cons in sorted(by_file.items()):
        if path == "-":
            for c in cons:
                print(f"\n[PR-level] {c['reason']}: {c['message']}")
    failed = sorted(k for k, v in manifest.get("green_predicates", {}).items() if not v)
    if failed:
        print("\nGREEN predicates NOT proven: " + ", ".join(failed))
    return 0


def cmd_audit(args) -> int:
    """Drift audit: registry lanes vs real git branches (local checkout)."""
    try:
        reg = load_registry(args.registry)
    except RegistryError as e:
        for c in e.constraints:
            print(f"{c.severity.value:11s} | {c.reason.value} | {c.message}")
        print("\nAUDIT: registry invalid — cannot audit against git")
        return 2
    root = args.repo or os.getcwd()
    try:
        out = subprocess.run(["git", "-C", root, "for-each-ref", "--format=%(refname)",
                              "refs/heads", "refs/remotes/origin"],
                             capture_output=True, text=True, check=True).stdout
    except (subprocess.SubprocessError, OSError) as e:
        print(f"MISSING_EVIDENCE | git not available: {e}")
        return 2
    refs = {l.replace("refs/heads/", "").replace("refs/remotes/origin/", "").strip()
            for l in out.splitlines() if l.strip()}
    report = {"registry_sha256": reg.sha256, "lanes": {}, "unreferenced_branches": []}
    lane_branch_stems = set()
    for lane in reg.lanes.values():
        if not lane.branch:
            continue
        stem = lane.branch.rstrip("*").rstrip("/")
        lane_branch_stems.add(stem)
    for lane in reg.lanes.values():
        pat = lane.branch or ""
        if not pat:
            continue
        if "*" in pat:
            exists = sorted(r for r in refs if r.startswith(pat.rstrip("*")))
        else:
            exists = [r for r in refs if r == pat]
        report["lanes"][lane.id] = {
            "branch_pattern": pat, "status": lane.status.value,
            "matching_branches": exists,
            "task_hash": lane.task_hash,
        }
    for r in sorted(refs):
        if r in ("master", "main", "HEAD"):
            continue
        if not any(r == p or r.startswith(p.rstrip("*").rstrip("/") + "/") or
                   p.rstrip("*").rstrip("/") == r for p in
                   (l.branch or "" for l in reg.lanes.values()) if p):
            report["unreferenced_branches"].append(r)
    print(json.dumps(report, indent=1, sort_keys=True, ensure_ascii=False))
    return 0


def cmd_benchmark(args) -> int:
    """Scaling benchmark: build synthetic registries of N lanes, classify an
    F-file changeset, report timing. Deterministic (fixed seed-free construction)."""
    import time
    from governance.registry import Claim, Lane
    from governance.model import Lifecycle
    from governance.paths import Matcher
    rows = []
    for n_lanes in (1, 10, 50, 100, 500):
        reg = _synthetic_registry(n_lanes)
        changeset = _synthetic_changeset(reg, args.files)
        gi = GovernanceInput(registry=reg, changeset=changeset,
                             contract=_synthetic_contract(reg),
                             meta={"mergeable": "clean", "author": "bench"},
                             branch_evidence=None, diff_text="",
                             added=args.files * 40, deleted=args.files * 10)
        t0 = time.perf_counter()
        res = classify(gi)
        dt = time.perf_counter() - t0
        rows.append({"lanes": n_lanes, "rules": len(reg.claims), "files": len(changeset),
                     "seconds": round(dt, 4), "decision": res.decision.value})
    if args.json:
        print(json.dumps({"benchmark": rows, "engine": "livora-governance/1"}, indent=1))
    else:
        print(f"{'lanes':>6} {'rules':>7} {'files':>6} {'seconds':>9}  decision")
        for r in rows:
            print(f"{r['lanes']:>6} {r['rules']:>7} {r['files']:>6} {r['seconds']:>9.4f}  {r['decision']}")
    return 0


def _synthetic_registry(n: int):
    from governance.model import Lifecycle
    from governance.paths import Matcher
    from governance.registry import Claim, Lane, Registry
    lanes = {}
    claims = []
    for i in range(n):
        lid = f"bench-lane{i:03d}"
        pat = f"bench/src/mod{i:03d}/**"
        m = Matcher(pat)
        lane = Lane(id=lid, agent=f"agent-{i}", wave="bench",
                    branch=f"bench/{lid}",
                    task=f"synthetic module {i}", task_id=lid, task_revision=1,
                    task_hash=canonical_task_hash(f"synthetic module {i}"),
                    owns=[m], status=Lifecycle.ACTIVE, exclusive=True, raw_index=i)
        lanes[lid] = lane
        claims.append(Claim(subject=lid, pattern=pat,
                            operations={"create", "modify", "delete", "rename", "copy"},
                            kind="lane", priority=700, exclusive=True,
                            authority=lane.agent, lifecycle="active", expiry=None,
                            reason=lane.task, matcher=m, rule_id=f"lane:{lid}:{pat}"))
    pol = Matcher("bench/policy/**")
    claims.append(Claim(subject="frozen", pattern="bench/policy/**",
                        operations={"create", "modify", "delete", "rename", "copy"},
                        kind="frozen", priority=900, exclusive=True, authority="lead",
                        lifecycle=None, expiry=None, reason="frozen family",
                        matcher=pol, rule_id="frozen:bench/policy/**"))
    return Registry(version=2, policy={"auto_merge_max_changed_files": 10_000},
                    integration_owned=[Matcher("bench/shared/root.cs")],
                    frozen=[pol], architecture=[Matcher("bench/.ci/**")],
                    generated=[Matcher("bench/**/obj/**")], lanes=lanes,
                    claims=claims, overlap_groups=[], sha256="0" * 64,
                    validation_findings=[])


def _synthetic_changeset(reg: Registry, files: int):
    out = []
    lids = sorted(reg.lanes)
    for i in range(files):
        lane = lids[i % len(lids)]
        mod = lane.replace("bench-lane", "mod")
        out.append(gite.ChangedFile(path=f"bench/src/{mod}/File{i:05d}.cs",
                                    operation=gite.Operation.CREATE))
    return out


def _synthetic_contract(reg):
    first = sorted(reg.lanes)[0]
    lane = reg.lanes[first]
    return {"task_id": first, "agent_id": lane.agent, "wave": "bench",
            "base_commit": "0" * 40, "tests_run": "engine-benchmark",
            "task_hash": lane.task_hash, "task_revision": str(lane.task_revision),
            "lane_branch": lane.branch,
            "owned_scope": ",".join(m.pattern for m in lane.owns),
            "drift_ack": None}


# ---------------------------------------------------------------------- main
def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(prog="livora_gates.py", description=__doc__)
    ap.add_argument("--registry", default=".github/OWNERSHIP.yaml",
                    help="path to OWNERSHIP.yaml (policy)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("validate-registry", help="self-validate the governance registry")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_validate_registry)

    for name, help_ in (("classify", "classify a PR from evidence files"),
                        ("inspect-pr", "classify + emit full JSON manifest")):
        p = sub.add_parser(name, help=help_)
        p.add_argument("--input-dir", default=".",
                       help="directory holding changed.txt / meta.json / additions.diff")
        p.add_argument("--json", action="store_true")
        p.add_argument("--manifest-out", default=None)
        p.add_argument("--no-git", action="store_true",
                       help="skip local git branch evidence (CI passes this; the "
                            "workflow supplies meta-derived evidence instead)")
        p.add_argument("--as-of", default="FROZEN",
                       help="explicit timestamp for the manifest (never read the clock)")
        p.set_defaults(fn=cmd_classify)

    p = sub.add_parser("explain", help="explain a governance-manifest.json")
    p.add_argument("manifest", nargs="?", default="governance-manifest.json")
    p.set_defaults(fn=cmd_explain)

    p = sub.add_parser("audit", help="registry-vs-git drift audit")
    p.add_argument("--repo", default=None)
    p.set_defaults(fn=cmd_audit)

    p = sub.add_parser("benchmark", help="scaling benchmark over synthetic registries")
    p.add_argument("--files", type=int, default=200)
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_benchmark)
    return ap


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.fn(args)
    except RegistryError as e:
        for c in e.constraints:
            print(f"{c.severity.value:11s} | {c.reason.value} | {c.message}")
        return 2
    except PathError as e:
        return _red(Reason.MALFORMED_PATH, str(e))
    except Exception as e:  # fail-closed: any unexpected crash is RED
        import traceback
        traceback.print_exc()
        return _red(Reason.PARSER_FAILURE, f"unexpected {type(e).__name__}: {e}")


if __name__ == "__main__":
    sys.exit(main())
