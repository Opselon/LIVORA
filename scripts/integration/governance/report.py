"""Machine-readable governance manifest + human report.

JSON output is stable for CI consumption: sorted keys, no timestamps unless
explicitly supplied, enums as machine strings only (no prose in enum fields).
"""
from __future__ import annotations

import json

from .engine import GovernanceInput, GovernanceResult


def build_manifest(gi: GovernanceInput, res: GovernanceResult,
                   repo: str, base_sha: str, head_sha: str,
                   merge_base: str | None, pr: int | None) -> dict:
    reg = gi.registry
    be = gi.branch_evidence
    return {
        "manifest_version": 1,
        "repository": repo,
        "pr": pr,
        "base_sha": base_sha,
        "head_sha": head_sha,
        "merge_base": merge_base,
        "lane_id": res.lane_id,
        "agent_id": gi.contract.get("agent_id"),
        "task_id": gi.contract.get("task_id"),
        "task_revision": (reg.lanes[res.lane_id].task_revision
                          if res.lane_id in reg.lanes else None),
        "task_hash": (reg.lanes[res.lane_id].task_hash
                      if res.lane_id in reg.lanes else None),
        "declared_task_hash": gi.contract.get("task_hash"),
        "registry_version": reg.version,
        "registry_sha256": reg.sha256,
        "engine": "livora-governance/1",
        "timestamp": gi.timestamp,
        "branch_evidence": None if be is None else {
            "head_sha": be.head_sha, "base_sha": be.base_sha,
            "merge_base": be.merge_base, "commits_behind": be.commits_behind,
            "commits_ahead": be.commits_ahead, "branch_exists": be.branch_exists,
            "is_base_branch": be.is_base_branch,
            "force_push_suspect": be.force_push_suspect, "gaps": sorted(be.gaps),
            "architecture_changed_after_fork": be.architecture_changed_after_fork,
        },
        "changed_files": len(res.file_decisions),
        "change_magnitude": res.change_magnitude,
        "per_file_decisions": [f.to_dict() for f in
                               sorted(res.file_decisions, key=lambda x: x.path)],
        "conflicts": sorted({c.reason.value for c in res.hard_constraints
                             if c.reason.value.startswith("OWNERSHIP")}),
        "hard_constraints": [c.to_dict() for c in
                             sorted(res.hard_constraints,
                                    key=lambda c: (c.reason.value, c.path or "", c.lane_id or ""))],
        "green_predicates": {k: bool(v) for k, v in sorted(res.green_predicates.items())},
        "risk_factors": res.risk.to_dict(),
        "task_drift": res.task_drift,
        "reason_codes": list(res.reason_codes),
        "final_decision": res.decision.value,
    }


def render_human(manifest: dict) -> str:
    out = []
    out.append(f"GOVERNANCE VERDICT: {manifest.get('final_decision', 'RED')}")
    out.append(f"lane={manifest.get('lane_id')} task={manifest.get('task_id')} "
               f"rev={manifest.get('task_revision')} files={manifest.get('changed_files', 0)} "
               f"magnitude={manifest.get('change_magnitude')}")
    for c in manifest.get("hard_constraints", []):
        out.append(f"{c['severity']:11s} | {c['reason']:34s} | {c['message'][:180]}")
    failed = sorted(k for k, v in manifest.get("green_predicates", {}).items() if not v)
    if failed:
        out.append("GREEN predicates failed: " + ", ".join(failed))
    out.append("reason codes: " + (", ".join(manifest.get("reason_codes", [])) or "-"))
    return "\n".join(out)


def dump_json(manifest: dict) -> str:
    return json.dumps(manifest, indent=1, sort_keys=True, ensure_ascii=False)
