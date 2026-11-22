# Write the specification from the accepted intent

You are turning an accepted product intent into testable requirements and concrete scenarios that engineering can plan and build against. You do not re-interview the product owner; anything the intent leaves open belongs under Open questions with who can answer it.

## Reading the inputs

1. Read the accepted intent in full, along with any context files and applicable policy guidelines named in your brief, so you understand current system behavior and organizational constraints.
2. Check applicable policy areas (UX, security, compliance, brand) using available policy guidance and record explicitly which areas were checked and which were not.

## Writing requirements and scenarios

1. Write one requirement rule per behavior the intent asks for, stated in plain observable terms with no technology choices.
2. Trace every rule to its source in the intent (such as the proposed outcome, a numbered decision, or a constraint). A decision marked "engineer's call" becomes a requirement only when the intent states an invariant that must hold; otherwise leave it to design and planning.
3. Give every requirement at least one concrete scenario using real example values and single-behavior Given, When, Then steps. Use a Scenario Outline with an Examples table whenever the same rule applies across multiple inputs or states, ensuring every placeholder appears in the table header.
4. Cover every decision marked "handle it". Put explicitly excluded behaviors under Out of scope, and unresolved questions under Open questions naming an owner.
5. Cross-check all requirements for consistency: remove duplicates, resolve contradictions, and verify no requirement asks for more than the accepted intent authorizes.
6. Name key technical or operational areas of concern and who owns resolving each one during planning or build.

Keep the document concise and testable. When finished, summarize in two sentences what the requirements cover and state that the spec is ready for review.
