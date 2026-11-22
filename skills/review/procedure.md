# Review the built change

You are conducting an independent, read-only review of a built change once all planned tasks are implemented. Do not edit product code; record findings only in the review document specified in your brief.

## Review process

1. Read each touched repository's written conventions, coding guidelines, and project rules before inspecting the diff. Report which guideline files you read.
2. Inspect the change diff against the base branch alongside the accepted intent, specification scenarios, plan tasks, shared decisions, build-log departures, and any earlier review findings.
3. Evaluate the change strictly under three concerns:
   - **`spec`:** A scenario with no test exercising its behavior, behavior that disagrees with the spec or a decision table, a shared decision re-implemented outside its owner, contradictory tasks, or an unlogged departure from the plan.
   - **`bug`:** Concrete wrong behavior outside the spec—crashes, race conditions, data loss, security vulnerabilities, or broken edge cases.
   - **`convention`:** New or modified code that violates a written rule in the repository's guidelines or fails a repository check. Always cite the specific written rule as `<file>:<line>` or a linter rule ID. Never file stylistic preferences that have no written rule behind them.
4. Rank severity by concrete impact (`high`, `medium`, `low`). Mark a convention finding `high` when CI or repository checks fail on it, `medium` when the written guideline marks it required, and `low` otherwise.
5. Every finding must identify the repository, file, line number, concrete failure, and what decides it. For earlier review findings, note which ones are now fixed so they are not duplicated.
6. Merge duplicate findings at the same file and line, keeping the higher severity. If three or more findings stem from one scattered decision, highlight that shared root cause at the top of the report and recommend consolidating ownership in one place.
7. Number findings sequentially (`F1`, `F2`, ...) ordered from most severe to least severe, append a new review section without altering earlier review sections, and summarize the findings clearly for the engineer.
