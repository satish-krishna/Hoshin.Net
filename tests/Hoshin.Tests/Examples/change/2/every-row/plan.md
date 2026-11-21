```jsonc
{ "layout": 2,
  "system": "demo" }
```

# Plan: Demo

## Shared decisions

```jsonc
[{ "question": "log format",
  "owner": "T1",
  "function": "logFormat()" }]
```

## Tasks

### T1: First

```jsonc
{ "id": "T1",
  "repo": "app",
  "depends_on": [],
  "covers": ["R1.1"] }
```

#### Change

One.

#### Tests

One.

### T2: Second

```jsonc
{ "id": "T2",
  "repo": "app",
  "depends_on": ["T9"] }
```

#### Change

Two.

#### Tests

Two.

### T3: Third

```jsonc
{ "id": "T3",
  "repo": "app",
  "depends_on": ["T4"],
  "uses": ["base branch"] }
```

#### Change

Three.

#### Tests

Three.

### T4: Fourth

```jsonc
{ "id": "T4",
  "repo": "app",
  "depends_on": [],
  "uses": ["log format"],
  "covers": ["R9.2"] }
```

#### Change

Four.

#### Tests

Four.

### T4: Fourth again

```jsonc
{ "id": "T4",
  "repo": "app",
  "depends_on": [] }
```

#### Change

Four again.

#### Tests

Four again.
