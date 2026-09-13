#!/usr/bin/env bash
# Agent-side: create your lane branch from CURRENT master and open a contract PR.
# Usage: scripts/integration/new-lane-pr.sh <wave> <lane-slug> [--draft] [--push]
# Default is DRY RUN (prints the exact commands). --push actually pushes + opens PR.
# Never touches any branch but your own agent/<wave>/<lane-slug>.
set -euo pipefail
WAVE="${1:?wave e.g. 3b}"; SLUG="${2:?lane slug e.g. lane02-health-connect}"; shift 2 || true
DRAFT="--draft"; [ "${1:-}" = "--draft" ] && shift
PUSH=""
BR="agent/${WAVE}/${SLUG}"
git fetch origin master
SHA=$(git rev-parse origin/master)
echo "# base master = $SHA"
echo "git checkout -b $BR origin/master 2>/dev/null || git checkout $BR"
echo "# ... work inside your OWNED paths only (see .github/OWNERSHIP.yaml) ..."
echo "git add -A && git commit -m 'feat(${WAVE}): ${SLUG} ...'"
if [ "${1:-}" = "--push" ]; then
  git push -u origin "$BR"
  BASE_SHA="$SHA" gh pr create $DRAFT --base master --head "$BR" \
    --title "[${WAVE}/${SLUG}] <one-line>" \
    --body "$(sed -e "s/<lane-or-issue id>/${SLUG}/" -e "s/<full SHA of the master commit this work was branched from>/${BASE_SHA}/" .github/pull_request_template.md)"
else
  echo "# dry run — re-run with --push when READY_FOR_REVIEW"
fi
