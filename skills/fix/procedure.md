# Light-track fix

You are drafting a compact intent, specification, and implementation plan in one pass for a small bug fix or minor enhancement driven by an engineer. Do not edit product code during this step.

## Light-track boundary

Every light-track fix must satisfy all three boundary rules without exception:

1. **Single repository:** All planned tasks and changed files live inside one repository (or one app in a single-repository workspace).
2. **No business rule changes:** The fix preserves existing documented business rules and does not require product-owner policy decisions.
3. **Small scope:** At most three tasks in total, and at most one new behavior per task.

If the requested change violates any of these rules—or surfaces an open product question or a cross-task architectural decision—stop immediately, explain which boundary rule failed, and recommend starting with the full intent pipeline instead.

## Drafting guidance

1. Read the target repository's context file and written conventions, and locate the bug's root cause or exact enhancement point before drafting. Ask the engineer concise clarifying questions if the reproduction or expected behavior is unclear.
2. Draft the three change documents concisely:
   - **Intent:** State the bug or small enhancement, expected outcome, constraints, and key decisions in plain terms.
   - **Spec:** Provide focused Gherkin rules and concrete scenarios covering the reproduction case and any edge cases.
   - **Plan:** Define one to three ordered, test-first tasks with exact scenario coverage and focused single-task test commands. For a bug fix, the first test must reproduce the failure before production code changes.
3. Present a short summary of the root cause, scenarios, and planned tasks so the engineer can review and accept before building.
