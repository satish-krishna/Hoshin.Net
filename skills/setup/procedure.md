# Set up a workspace and system

Guide the engineer through initializing or updating a system home so product owners and engineers share consistent context, policies, and conventions.

## System setup decisions

1. **System name:** Propose a short, kebab-case business name (for example `claims` rather than a single service name) and confirm it with the engineer, avoiding collisions with existing systems.
2. **Repositories or apps:** Propose which checked-out repositories (or apps in a single-repository layout) belong to the system based on their names and READMEs, and confirm additions or removals with the engineer.
3. **Policy areas:** Ask the engineer one area at a time (security, compliance, brand, UX) whether an organizational policy skill applies and which role owns each area. Record only skills the engineer explicitly confirms; never guess or infer policy skills on their behalf.
4. **Commit scope:** If the repository configures commit-scope validation, confirm the lowercase kebab-case commit scope for the system, offering the system name as the default.
5. **Context freshness:** Identify repositories whose business context is missing or behind recent commits, confirm whether to build or refresh them, and summarize the resulting workspace configuration clearly.

Keep setup idempotent: preserve existing systems, naming patterns, and policies unless the engineer explicitly asks to change them.
