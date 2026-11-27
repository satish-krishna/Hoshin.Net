#!/usr/bin/env bash
# Fails when a commit between <base> and <head> changes a reviewer-owned gate path without a reviewer's Task trailer.
#
#   .github/scripts/gate-owner.sh <base-sha> <head-sha>
#
# Round 3 (docs/review/TASKS.md, R3-rules 1 and 2): the reviewer's session writes the gates, and the session that
# implements the other tasks never changes them. Both sessions push under the engineer's GitHub account, so CODEOWNERS
# can't tell them apart. This check makes a gate change by the implementer either fail or carry a false Task trailer,
# which is a deliberate act a reviewer can see in the history.
#
# A gate commit carries exactly one "Task: R21", "Task: B02" or "Task: B03" line. A gate the reviewer adds later for a
# late defect (R3-rule 6) extends R21, so it carries "Task: R21". The Task line is read from the whole message, not
# from git's trailer block, so it is found whether or not a blank line separates it from the attribution lines.
#
# Temporary: remove it, and .github/workflows/gate-owner.yml, once round 3 is merged.
set -euo pipefail

base="${1:?base sha}"
head="${2:?head sha}"
allowed='^(R21|B02|B03)$'
gated=(
  tests/Hoshin.Tests/Acceptance/
  evals/
  .github/CODEOWNERS
  .github/workflows/evals.yml
  .github/workflows/conventional.yml
  .github/workflows/gate-owner.yml
  .github/scripts/gate-owner.sh
)
exempt='^evals/results/' # run records are the one thing an implementer adds under evals/ (R3-rule 2)

summary="${GITHUB_STEP_SUMMARY:-/dev/null}"
from="$(git merge-base "$base" "$head")"
status=0
touched=0

{
  echo "### Commits that change a gate path"
  echo
  echo "| Commit | Task | Subject | Gate files |"
  echo "| :--- | :--- | :--- | :--- |"
} >> "$summary"

while IFS= read -r sha; do
  # Against the first parent, so a merge commit is judged by what it brings into the branch.
  mapfile -t files < <(git diff --name-only "$sha^1" "$sha" -- "${gated[@]}" | grep -Ev "$exempt" || true)
  [ "${#files[@]}" -eq 0 ] && continue
  touched=$((touched + 1))

  subject="$(git show -s --format=%s "$sha")"
  mapfile -t tasks < <(git show -s --format=%B "$sha" | sed -n 's/^Task:[[:space:]]*\([^[:space:]]*\)[[:space:]]*$/\1/p')
  task="${tasks[*]:-none}"
  short="${sha:0:7}"
  echo "| \`$short\` | $task | ${subject//|/\|} | ${#files[@]}: \`${files[0]}\`$([ "${#files[@]}" -gt 1 ] && echo ", …") |" >> "$summary"

  if [ "${#tasks[@]}" -ne 1 ] || ! [[ "${tasks[0]}" =~ $allowed ]]; then
    echo "::error::Commit $short ('$subject') changes reviewer-owned gate files, but its Task is '$task', not R21, B02 or B03. The implementer must not change the gates (TASKS.md, R3-rule 2); raise a gate dispute in the pull request instead. Files:"
    printf '  %s\n' "${files[@]}"
    status=1
  fi
done < <(git rev-list --reverse "$from..$head")

if [ "$touched" -eq 0 ]; then echo "| — | — | no commit changes a gate path | — |" >> "$summary"; fi
echo "gate owner: $touched commit(s) change a gate path; $([ "$status" -eq 0 ] && echo "all carry a reviewer task" || echo "some do not")"
exit "$status"
