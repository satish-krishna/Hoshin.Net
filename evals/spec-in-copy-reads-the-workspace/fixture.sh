#!/usr/bin/env bash
# The session starts inside a change's own copy of the master repository, as the tool driving the build starts it
# (the original case's deliberate setting). The case folder is the copy: a linked worktree of master/, on the change's
# branch, with the accepted intent. master/ (the engineer's checkout, on main) and app/ sit inside it as nested
# repositories that the master's .git/info/exclude hides, so the copy is clean and the master's git directory stays at
# master/.git, where every case's graders read the hook trace and the reflogs.
#
# app has one commit after the context file's source_commit (home.sh, the home page), so the context file is stale by
# one commit and the spec can only find home.sh by reading the product repository, which is next to the copy.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../shared/workspace.sh"

change=2026-11-01-new-arrivals

(
  cd app
  cat > home.sh <<'SH'
#!/usr/bin/env bash
# The home page: what a visitor sees when the site opens. home.sh <returning|new> [first name]
echo "<h1>Our shop</h1>"
bash greet.sh "${1:-new}" "${2:-}"
SH
  git_in add . && git_in commit -q -m "feat: add the home page"
)

change_start "$change" feat demo
copy="$(copy_of "$change")"
folder="intent/demo/$change"
cat > "$copy/$folder/intent.md" <<'MD'
```jsonc
{ "title": "Show new arrivals to returning visitors", "system": "demo", "author": "Pat Owner, Product Owner", "date": "2026-11-01", "status": "draft", "scope": "demo" }
```

# Intent: Show new arrivals to returning visitors

## Problem

Returning visitors open the site and see the same page as a stranger, so they do not find what is new since their last visit. Support hears that regulars keep asking what arrived.

## Proposed outcome

1. A returning visitor sees a short "New arrivals" note on the home page, below the greeting.
2. A first-time visitor does not see the note.

**Success looks like:** returning visitors see the note on every visit, and first-time visitors never do.

## Affected users and systems

- **Users:** returning and first-time visitors.
- **Systems:** the app.

## Constraints

- The note is shown on the home page only.

## Decisions

| # | Situation | Decision | What should happen |
|---|---|---|---|
| D1 | A returning visitor never gave a first name | handle it | Show the note without a name. |
| D2 | Translating the note | out of scope | - |

## Open questions

- None.
MD
commit_doc "$change" intent-draft "$folder/intent.md"
sed 's/"status": "draft"/"status": "accepted"/' "$copy/$folder/intent.md" > "$copy/$folder/intent.md.new"
mv "$copy/$folder/intent.md.new" "$copy/$folder/intent.md"
commit_doc "$change" accept-intent "$folder/intent.md"

# Move the copy into the case folder: git will not add a worktree in a folder that is not empty, and the case folder
# already holds master/ and app/. The copy's files are moved in, then git is told where the worktree went.
printf '/master/\n/app/\n' >> master/.git/info/exclude
shopt -s dotglob
mv "$copy"/* "$workspace_root"/
rmdir "$copy" "$workspace_root/.hoshin/copies" "$workspace_root/.hoshin"
git -C master worktree repair "$workspace_root"
