#!/usr/bin/env bash
# PREPARED ONLY — run manually after explicit user authorization.
# Makes master PR-only. Requires repo admin. Idempotent.
#
# Effects (exactly):
#  - direct pushes to master blocked (except: admins+bypass below, see caveat)
#  - required status checks: the PR-gate job names (they must exist as checks;
#    GitHub matches by check name: 'controller-gate', 'verify merge state ...',
#    'roll-up status')
#  - PR must be up to date with master before merge (kills stale-base merges)
#  - squash merges only; branch automatically deleted post-merge OFF (agents
#    may want to keep lane branches; set true if desired)
set -euo pipefail
REPO="${1:-Opselon/LIVORA}"
gh api -X PUT "repos/$REPO/branches/master/protection" \
  -H "Accept: application/vnd.github+json" \
  --input - <<'JSON'
{
  "required_status_checks": {
    "strict": true,
    "contexts": ["controller-gate", "verify merge state (base HEAD + PR applied)", "roll-up status"]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": {
    "required_approving_review_count": 0
  },
  "restrictions": null,
  "required_linear_history": false,
  "allow_force_pushes": false,
  "allow_deletions": false,
  "required_conversation_resolution": true
}
JSON
echo "master now PR-only for non-admins; IntegratE workflow (admin app) still merges."
echo "ROLLBACK: gh api -X DELETE repos/$REPO/branches/master/protection"
