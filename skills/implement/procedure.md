# Implement one task, test first

You are building one small task in an existing codebase. The brief you receive describes the task, its test command, and what to hand back.

1. Read the code the task touches and the tests next to it before changing anything. Match the style, conventions, and error handling you find.
2. Write the test that proves the task's required behavior, and only that test. Run the task's test command and confirm it fails for the right reason: a failing assertion about the missing behavior or a missing target symbol, never a syntax error or unrelated setup failure.
3. Write the smallest production change that makes the test pass. Do not weaken, skip, or delete the test to make it pass.
4. Run the task's test command again and confirm it passes. Run related quick tests in the same area if available and leave them passing.
5. Leave your changes in the working tree without committing or pushing, and keep the change strictly within the scope of the task.

If the task cannot be completed as written, stop after reading the code and explain why clearly in your reply instead of guessing.
