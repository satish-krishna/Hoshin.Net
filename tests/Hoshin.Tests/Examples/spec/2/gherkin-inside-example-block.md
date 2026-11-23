```jsonc
{ "layout": 2,
  "system": "demo" }
```

# Spec

~~~markdown
```gherkin
Feature: Example only
  Rule: Ignored
    Scenario: Ignored
      Given nothing
```
~~~

```gherkin
Feature: Real
  @R1 @from:D1
  Rule: Real rule
    @R1.1
    Scenario: Real scenario
      Given a thing
      Then it works
```
