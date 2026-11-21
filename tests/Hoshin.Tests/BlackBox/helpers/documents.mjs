// Text builders for Layout 2 JSONC documents of a change.
import { CHANGE, INTENT } from './workspace.mjs';

function fence(obj) {
  return '```jsonc\n' + JSON.stringify(obj, null, 2) + '\n```';
}

export function intent(status, extra = {}) {
  const base = { title: 'Greeting', system: 'demo', author: 't', date: '2026-11-01', status, scope: 'demo' };
  if (typeof extra === 'string' && extra.trim()) {
    for (const line of extra.trim().split('\n')) {
      const m = line.match(/^([^:]+):\s*(.+)$/);
      if (m) {
        const k = m[1].trim();
        const raw = m[2].trim();
        if (raw.startsWith('[') && raw.endsWith(']')) {
          const inner = raw.slice(1, -1).trim();
          base[k] = inner ? inner.split(',').map((s) => s.trim().replace(/^["']|["']$/g, '')) : [];
        } else {
          base[k] = raw.replace(/^["']|["']$/g, '');
        }
      }
    }
  } else if (extra && typeof extra === 'object') {
    Object.assign(base, extra);
  }
  return `${fence(base)}\n\n# Intent: Greeting\n`;
}

export function spec(status, pin = 'pending acceptance', scenarios = 2, change = CHANGE) {
  const body = Array.from(
    { length: scenarios },
    (_, i) => `\n    @R1.${i + 1}\n    Scenario: Greeting ${i + 1}\n      Given a visitor\n      When the page opens\n      Then the greeting ${i + 1} shows\n`
  ).join('');
  return (
    `${fence({ layout: 2, source_intent: `${change}/intent.md`, source_intent_commit: pin, system: 'demo', date: '2026-11-01', status })}\n\n` +
    `# Spec: Greeting\n\n## Requirements\n\n\`\`\`gherkin\nFeature: Greeting\n  The greeting shows.\n\n  @R1 @from:outcome-1\n  Rule: The greeting shows\n${body}\`\`\`\n\n## Design\n\nA string.\n`
  );
}

export function plan(status, pin = 'pending acceptance', tasks = [['T1', 'app'], ['T2', 'app']], change = CHANGE) {
  const task = ([id, repo, dependsOn = []]) =>
    `\n### ${id}: Do ${id}\n\n${fence({ id, repo, depends_on: dependsOn, uses: [], files: [`src/${id}.ts`], covers: ['R1.1'], supports: [] })}\n\n#### Change\n\nDo it.\n\n#### Tests\n\nTest it.\n\n#### Done when\n\nIt passes.\n`;
  return (
    `${fence({ layout: 2, source_spec: `${change}/spec.md`, source_spec_commit: pin, system: 'demo', date: '2026-11-01', author: 't', status })}\n\n` +
    `# Plan: Greeting\n\n## Approach\n\nDo it.\n\n## Scenario coverage\n\n| Scenario | Test | Task |\n|---|---|---|\n| R1.1 | it shows | T1 |\n\n## Tasks\n` +
    tasks.map(task).join('')
  );
}

export const review = (n) =>
  `${fence({ layout: 2 })}\n\n# Review findings\n` +
  Array.from(
    { length: n },
    (_, i) => `\n## Review ${i + 1}\n\n${fence({ review: i + 1, reviewed_at: `2026-11-0${i + 1}`, commits: { app: `abc123${i}` } })}\n`
  ).join('');

export function fixIntent(status, change = CHANGE) {
  return (
    `${fence({
      title: 'Fix the greeting typo',
      system: 'demo',
      author: 'Test, engineer',
      date: '2026-10-01',
      status,
      scope: 'greeting',
      track: 'fix',
      kind: 'bug',
      context: ['app@abc1234'],
    })}\n\n` +
    `# Intent: Fix the greeting typo\n\n## Problem\n\nThe greeting reads "Helo".\n\n## Proposed outcome\n\nThe greeting reads "Hello".\n\n## Open questions\n\nNone.\n`
  );
}

export function fixSpec(status, pin = 'pending acceptance', change = CHANGE) {
  return (
    `${fence({
      layout: 2,
      source_intent: `${change}/intent.md`,
      source_intent_commit: pin,
      system: 'demo',
      date: '2026-10-01',
      status,
      policies_applied: [],
      policies_not_checked: ['security'],
    })}\n\n` +
    `# Spec: Fix the greeting typo\n\n## Requirements\n\n\`\`\`gherkin\nFeature: Fix the greeting typo\n  The greeting reads "Hello".\n\n  @R1 @from:problem\n  Rule: The greeting is spelled correctly\n\n    @R1.1\n    Scenario: The greeting is shown\n      Given a visitor\n      When the page opens\n      Then the greeting reads "Hello"\n\`\`\`\n\n## Design\n\nThe string in src/greeting.ts.\n`
  );
}

export function fixPlan(status, pin = 'pending acceptance', change = CHANGE) {
  return (
    `${fence({
      layout: 2,
      source_spec: `${change}/spec.md`,
      source_spec_commit: pin,
      system: 'demo',
      date: '2026-10-01',
      author: 'Test',
      status,
    })}\n\n` +
    `# Plan: Fix the greeting typo\n\n## Approach\n\nCorrect the string.\n\n## Scenario coverage\n\n| Scenario | Test | Task |\n|---|---|---|\n| R1.1 | greeting reads Hello | T1 |\n\n## Tasks\n\n### T1: Fix the greeting\n\n${fence({
      id: 'T1',
      repo: 'app',
      depends_on: [],
      uses: [],
      files: ['src/greeting.ts'],
      covers: ['R1.1'],
      supports: [],
    })}\n\n#### Change\n\nFix it.\n\n#### Tests\n\nTest it.\n\n#### Done when\n\nIt passes.\n`
  );
}

export const FIX_BUILD_LOG =
  `${fence({ layout: 2 })}\n\n# Build log: app\n\n### T1\n\n${fence({ task: 'T1', commits: ['abc1234'], departure: null })}\n`;
