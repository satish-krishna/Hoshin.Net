# Extract business context from code

Read a system's code and write down what it currently does in plain business language so product owners never have to read source code.

Code shows what a system **does**, not necessarily what the business **intends**. Bugs, workarounds, and historical leftovers look identical to requirements in source code, so tag every rule with honest provenance and never upgrade certainty by guessing.

## What to capture per repository

- **Purpose:** One concise paragraph on what the repository exists for and who relies on it.
- **Domain terms:** Plain-language definitions of the business vocabulary used in models, enums, and messages.
- **Capabilities:** What each kind of user can do today, derived from actual entry points rather than directory layout.
- **Business rules:** Validations, limits, approvals, state transitions, permissions, schedules, and financial logic stated in plain business prose, numbered sequentially (`C1`, `C2`, ...).
- **Integrations:** External and sibling systems it communicates with, named by business purpose.
- **Known oddities:** Apparent workarounds, dead feature flags, or customer-specific exceptions.

Avoid class names, file paths, and framework names in the main prose (brief references in the Note column are acceptable for engineers).

## Provenance tags

Tag every rule and oddity accurately:
- `seen in code`: Clear, unambiguous single code path with no contradiction.
- `confirmed by engineer`: Explicitly asked and confirmed by the engineer as deliberate and current.
- `intent unknown`: Behavior exists in code, but nobody present can confirm whether it is still intended.

## Refreshing and interviewing

- When refreshing an existing context file, preserve earlier engineer answers and existing `C` numbers. Never reuse a retired `C` number; assign newly discovered rules the next unused number.
- Compare integration seams across repositories so disagreements on limits, statuses, or permissions surface as interview questions.
- Interview the engineer only where reading the code leaves real doubt (contradicting paths, unexplained magic numbers, dead flags, apparent workarounds, or cross-repo disagreements). Ask one clear question per turn including what the code currently does.
- Keep each context document concise enough for a product owner to read in ten minutes.
