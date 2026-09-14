"""LIVORA governance engine (wave 5).

Policy + State + Evidence -> Decision, fail-closed, deterministic.
Package layout:
  paths      - formal path normalization + glob matching (no substring matching)
  model      - registry/lane/claim/change dataclasses, reason-code enums
  registry   - v1-compatible loader + registry self-validation
  resolver   - ownership resolution, precedence, conflict counting
  git_evidence - safe local-git evidence gathering (merge-base, renames, staleness)
  engine     - per-file decisions, hard constraints, GREEN predicates, manifest
  report     - human + stable JSON output
"""
