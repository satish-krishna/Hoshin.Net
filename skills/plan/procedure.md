# Write the implementation plan from the accepted specification

You are translating an accepted specification into small, ordered, test-first engineering tasks with clear dependencies, shared decisions, and scenario coverage.

## Gathering direction and confirming the outline

1. Read the accepted specification, the intent, the relevant context files, and each target repository's written conventions and build rules before drafting.
2. Ask the engineer for any approach direction (task ordering, architecture choices, code to reuse, or pitfalls to avoid) if none was provided.
   - Apply plan-level direction that fits within the accepted spec.
   - If a requested direction contradicts a spec requirement or scenario outcome, stop without writing the plan, identify the exact contradiction, and recommend reopening the spec.
   - Clarify ambiguous direction before drafting.
3. Present a concise 5-to-8-line outline showing repository split, task order, main design choices, and the riskiest step, and confirm with the engineer before writing the full plan.

## Drafting rules

- **Scenario coverage:** Assign every scenario in the specification to one covering task, and list contributing tasks as supporting. Leave zero scenarios uncovered and cite only IDs present in the specification.
- **Shared decisions:** Identify every question that multiple tasks must answer identically (ownership of files, canonical data shapes, ordering rules, boundary contracts). Give each shared decision one owning task that defines a single named function or type, and require dependent tasks to declare that dependency and reuse it rather than re-implementing it.
- **Decision tables:** For any task whose behavior depends on three or more inputs or stateful transitions, include an explicit decision table covering every valid combination and marking impossible ones—or cite a specification Scenario Outline's Examples table directly without duplicating it.
- **Scoped tests:** Give every testable task a focused test command that executes only that task's new test(s) without a shell wrapper, never a watch mode. Keep whole-repository lint, build, or full-suite checks at build finish rather than inside every individual task.
- **Task order and dependencies:** Order tasks so dependencies always point strictly upward. Avoid unnecessary cross-repository dependencies so independent repositories can build cleanly.
- **Partly built plans:** If replanning after some tasks are already logged, never reuse a logged task ID for different work; keep unchanged logged tasks verbatim and number new tasks above the highest existing ID.

When the draft is written, summarize the task count, ordering, and key shared decisions in two sentences and let the engineer know it is ready for review.
