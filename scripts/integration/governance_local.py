#!/usr/bin/env python3
"""LIVORA governance gate — LOCAL runner (no GitHub API).

Usage:  python3 scripts/integration/governance_local.py <pr-ref-or-branch> [--base master]

Builds the evidence files CI derives from GitHub (changed.txt, changed.json,
additions.diff, meta.json) from a LOCAL checkout under ./gate-inputs/, then execs
the governance CLI's classify with --no-git. Engine exit code passes through:
0 GREEN, 1 YELLOW, 2 RED. Lanes self-check before pushing.

Safety: git via argument arrays only (never shell=True); missing ref/registry or
any git failure exits nonzero (fail-closed). Stdlib only.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CLI = os.path.join(HERE, "livora_gates.py")


def git(*args: str, binary: bool = False) -> str | bytes:
    """Run git in the current repo; any failure is fatal (fail-closed, exit 2)."""
    try:
        p = subprocess.run(("git",) + args, capture_output=True, check=True)
    except (subprocess.CalledProcessError, FileNotFoundError) as e:
        err = (getattr(e, "stderr", b"") or b"").decode(errors="replace").strip()
        sys.exit(f"RED: git {' '.join(args)} failed: {err or 'git unavailable'}")
    return p.stdout if binary else p.stdout.decode("utf-8", "surrogateescape")


def name_status(base: str, head: str) -> list[dict]:
    """git diff --name-status -M -C -z -> the engine's rich changeset records."""
    raw = git("diff", "--name-status", "-M", "-C", "-z", f"{base}...{head}", binary=True)
    fields = [f.decode("utf-8", "surrogateescape") for f in raw.split(b"\x00") if f]
    ops, out = {"A": "create", "M": "modify", "D": "delete", "T": "modify"}, []
    i = 0
    while i < len(fields):
        status, i = fields[i], i + 1
        code = status[0]
        if code in ("R", "C"):
            old, new, i = fields[i], fields[i + 1], i + 2
            out.append({"path": new, "operation": "rename" if code == "R" else "copy",
                        "old_path": old, "binary": False})
        else:
            out.append({"path": fields[i], "operation": ops.get(code, "unknown"),
                        "old_path": None, "binary": False})
            i += 1
    return out


def arch_patterns(registry: str) -> list[str]:
    """Frozen + architecture_owned 'paths' from the registry via a minimal
    indentation scan (stdlib only). The engine's PyYAML loader stays
    authoritative — this only feeds the architecture-drift hint in meta.json."""
    pats, section, in_paths = [], None, False
    for line in open(registry, encoding="utf-8"):
        s = line.rstrip("\r\n")
        stripped = s.strip()
        if not stripped or stripped.startswith("#"):
            continue
        indent = len(s) - len(s.lstrip(" "))
        if indent == 0 and stripped.endswith(":"):
            key = stripped[:-1]
            section = key if key in ("frozen", "architecture_owned") else None
            in_paths = False
            continue
        if section is None:
            continue
        if stripped == "paths:" and indent <= 2:
            in_paths = True
            continue
        if in_paths and indent <= 2 and not stripped.startswith("- "):
            in_paths = False
        if in_paths and stripped.startswith("- "):
            pats.append(stripped[2:].strip().strip('"').strip("'"))
    return pats


def touches(patterns: list[str], path: str) -> bool:
    for p in patterns:
        if p == "**" or (p.endswith("/**") and path.startswith(p[:-3] + "/")) or p == path:
            return True
    return False


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="governance_local.py")
    ap.add_argument("ref", help="PR head ref or branch to classify")
    ap.add_argument("--base", default="master")
    ap.add_argument("--registry", default=".github/OWNERSHIP.yaml")
    ap.add_argument("--input-dir", default="gate-inputs")
    args = ap.parse_args(argv)

    for what, path in (("governance CLI", CLI), ("registry", args.registry)):
        if not os.path.isfile(path):
            sys.exit(f"RED: {what} not found at {path}")

    head = git("rev-parse", "--verify", "-q", f"{args.ref}^{{commit}}").strip()
    base = git("rev-parse", "--verify", "-q", f"{args.base}^{{commit}}").strip()
    mb = git("merge-base", base, head).strip()

    os.makedirs(args.input_dir, exist_ok=True)

    def w(name: str, text: str) -> None:
        with open(os.path.join(args.input_dir, name), "w", encoding="utf-8") as f:
            f.write(text)

    cs = name_status(mb, head)
    names = [p for e in cs for p in (e["old_path"], e["path"]) if p]
    w("changed.json", json.dumps(cs))
    w("changed.txt", "\n".join(dict.fromkeys(names)) + ("\n" if names else ""))
    w("additions.diff", git("diff", f"{mb}..{head}"))
    drift = [l for l in git("diff", "--name-only", f"{mb}..{base}").splitlines() if l.strip()]
    w("meta.json", json.dumps({
        "pr": 0, "title": git("log", "-1", "--format=%s", head).strip(),
        "body": git("log", "-1", "--format=%B", head),
        "base_sha": base, "head_sha": head, "merge_base": mb,
        "mergeable": "clean" if git("status", "--porcelain") == "" else "unknown",
        "commits": int(git("rev-list", "--count", f"{mb}..{head}").strip() or 0),
        "author": git("log", "-1", "--format=%an", head).strip(),
        "commits_behind": int(git("rev-list", "--count", f"{mb}..{base}").strip() or 0),
        "commits_ahead": int(git("rev-list", "--count", f"{mb}..{head}").strip() or 0),
        "architecture_changed_after_fork": any(
            touches(arch_patterns(args.registry), f) for f in drift),
    }))

    print(f"local gate inputs -> {args.input_dir}/ "
          f"(base={base[:8]} head={head[:8]} merge_base={mb[:8]} files={len(cs)})")
    return subprocess.run((sys.executable, CLI, "--registry", args.registry, "classify",
                           "--input-dir", args.input_dir, "--no-git",
                           "--manifest-out", "governance-manifest.json",
                           "--as-of", "FROZEN")).returncode


if __name__ == "__main__":
    sys.exit(main())
