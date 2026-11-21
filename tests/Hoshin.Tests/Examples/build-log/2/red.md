```jsonc
{ "layout": 2 }
```

# Build log: app

### T1

```jsonc
{ "task": "T1",
  "commits": ["abc1234", "def5678"],
  "red": "observed" }
```

### T2

```jsonc
{ "task": "T2",
  "commits": ["1234567"],
  "departure": "rule 1: the runner found no test in the new file",
  "red": "not observed: no test ran" }
```

### T3

```jsonc
{ "task": "T3",
  "commits": ["89abcde"],
  "notes": "the task has no test command, so the entry has no red" }
```
