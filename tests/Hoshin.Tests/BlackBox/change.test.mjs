import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { bareEnv, dotnet, program, testsRoot } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const examples = join(testsRoot, 'Examples');

function check(cwd, files) {
  const r = spawnSync(dotnet, [program, 'check', ...files], {
    cwd,
    env: bareEnv,
    encoding: 'utf8',
  });
  assert.equal(r.error, undefined);
  return { status: r.status, lines: r.stdout.replace(/\r\n/g, '\n').split('\n').filter((l) => l !== ''), stderr: r.stderr };
}

function spec2(ids) {
  const rules = [...new Set(ids.map((id) => id.split('.')[0]))];
  const body = rules
    .map((rule) => {
      const scenarios = ids
        .filter((id) => id.startsWith(`${rule}.`))
        .map((id) => `    @${id}\n    Scenario: Scenario ${id}\n      Given a thing\n      Then it is so\n`)
        .join('\n');
      return `  @${rule} @from:outcome-1\n  Rule: Rule ${rule}\n${scenarios}`;
    })
    .join('\n');
  return '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n# Spec\n\n```gherkin\nFeature: Demo\n' + body + '```\n';
}

function plan2(tasks, decisions = [], frontMatterExtra = {}) {
  const shared =
    decisions.length === 0
      ? '```jsonc\n[]\n```'
      : '```jsonc\n' + JSON.stringify(decisions.map((d) => ({ question: d.question, owner: d.owner, function: 'f()' })), null, 2) + '\n```';
  const body = tasks
    .map((t) => {
      const obj = {
        id: t.id,
        repo: t.repo ?? 'app',
        depends_on: t.depends ?? [],
        ...(t.uses ? { uses: t.uses } : {}),
        ...(t.covers ? { covers: t.covers } : {}),
        ...(t.supports ? { supports: t.supports } : {}),
      };
      return `### ${t.id}: Task ${t.id}\n\n\`\`\`jsonc\n${JSON.stringify(obj, null, 2)}\n\`\`\`\n\n#### Change\n\nDo it.\n\n#### Tests\n\nTest it.\n`;
    })
    .join('\n');
  const fm = { layout: 2, system: 'demo', ...frontMatterExtra };
  return '```jsonc\n' + JSON.stringify(fm, null, 2) + '\n```\n\n# Plan\n\n## Shared decisions\n\n' + shared + '\n\n## Tasks\n\n' + body;
}

function log2(ids) {
  return (
    '```jsonc\n{ "layout": 2 }\n```\n\n# Build log\n\n' +
    ids.map((id) => `### ${id}\n\n\`\`\`jsonc\n{\n  "task": "${id}",\n  "commits": ["abc1234"]\n}\n\`\`\`\n`).join('\n')
  );
}

function change(files) {
  const dir = tempDir('hoshin-change-');
  for (const [rel, text] of Object.entries(files)) {
    mkdirSync(dirname(join(dir, rel)), { recursive: true });
    writeFileSync(join(dir, rel), text);
  }
  return dir;
}

function lineAfter(text, anchor, re) {
  const lines = text.split('\n');
  const from = lines.findIndex((l) => anchor.test(l));
  assert.ok(from >= 0, `no line matches ${anchor}`);
  const at = lines.findIndex((l, i) => i >= from && re.test(l));
  assert.ok(at >= 0, `no line matches ${re}`);
  return at + 1;
}

const lineOf = (text, re) => lineAfter(text, re, re);

test('every cross-reference row is reported', () => {
  const T = (id, extra = {}) => ({ id, ...extra });
  const base = ['R1.1', 'R4.3'];
  const rows = [
    {
      situation: 'task T5 depends on T9, which does not exist',
      plan: plan2([T('T1', { covers: ['R1.1', 'R4.3'] }), T('T5', { depends: ['T9'] })]),
      expect: (p) => `plan.md:${lineAfter(p, /"id": "T5"/, /"depends_on"/)}: T5 depends on T9, which is not in the plan`,
    },
    {
      situation: 'task T5 depends on T6, which comes after it',
      plan: plan2([T('T1', { covers: ['R1.1', 'R4.3'] }), T('T5', { depends: ['T6'] }), T('T6')]),
      expect: (p) => `plan.md:${lineAfter(p, /"id": "T5"/, /"depends_on"/)}: T5 depends on T6, which comes after it`,
    },
    {
      situation: 'two tasks are both called T5',
      plan: plan2([T('T5', { covers: ['R1.1', 'R4.3'] }), T('T5')]),
      expect: (p) =>
        `plan.md:${p
          .split('\n')
          .map((l, i) => (l.startsWith('### T5:') ? i + 1 : 0))
          .filter(Boolean)[1]}: task ID T5 is used twice`,
    },
    {
      situation: 'task T5 covers R9.2 and the spec has no R9',
      plan: plan2([T('T1', { covers: ['R1.1', 'R4.3'] }), T('T5', { covers: ['R9.2'] })]),
      expect: (p) => `plan.md:${lineAfter(p, /"id": "T5"/, /"covers"/)}: T5 covers R9.2, which is not in the spec`,
    },
    {
      situation: 'scenario R4.3 in the spec is covered by no task',
      plan: plan2([T('T1', { covers: ['R1.1'] })]),
      expect: (_p, s) => `spec.md:${lineOf(s, /^\s*Scenario: Scenario R4\.3/)}: scenario R4.3 is covered by no task`,
    },
    {
      situation: 'task T5 uses a shared decision the plan does not list',
      plan: plan2([T('T1', { covers: ['R1.1', 'R4.3'] }), T('T5', { uses: ['base branch'] })]),
      expect: (p) => `plan.md:${lineAfter(p, /"id": "T5"/, /"uses"/)}: T5 uses "base branch", which no shared decision owns`,
    },
    {
      situation: 'task T5 uses a shared decision without depending on its owner T2',
      plan: plan2(
        [T('T1', { covers: ['R1.1', 'R4.3'] }), T('T2'), T('T5', { uses: ['base branch'], depends: ['T1'] })],
        [{ question: 'base branch', owner: 'T2' }],
      ),
      expect: (p) => `plan.md:${lineAfter(p, /"id": "T5"/, /"uses"/)}: T5 uses "base branch" but does not depend on T2`,
    },
    {
      situation: 'the build log has an entry for T12, which the plan lacks',
      plan: plan2([T('T1', { covers: ['R1.1', 'R4.3'] })]),
      log: log2(['T1', 'T12']),
      expect: (_p, _s, l) => `build/app.md:${lineOf(l, /^### T12/)}: build log entry T12 is not a task in the plan`,
    },
    {
      situation: 'the build log has an entry for T3, a task in another repo',
      plan: plan2([T('T1', { covers: ['R1.1', 'R4.3'] }), T('T3', { repo: 'hoshin' })]),
      log: log2(['T1', 'T3']),
      expect: (_p, _s, l) => `build/app.md:${lineOf(l, /^### T3/)}: build log entry T3 belongs to repository hoshin`,
    },
  ];
  for (const row of rows) {
    const spec = spec2(base);
    const files = { 'spec.md': spec, 'plan.md': row.plan };
    if (row.log) files['build/app.md'] = row.log;
    const dir = change(files);
    const result = check(dir, Object.keys(files));
    assert.deepEqual(result.lines, [row.expect(row.plan, spec, row.log)], row.situation);
    assert.equal(result.status, 1, row.situation);
  }
});

test('a clean change passes', () => {
  const dir = change({
    'spec.md': spec2(['R1.1', 'R4.3']),
    'plan.md': plan2(
      [
        { id: 'T1', covers: ['R1.1'] },
        { id: 'T2', depends: ['T1'], covers: ['R4.3'], uses: ['base branch'] },
      ],
      [{ question: 'base branch', owner: 'T1' }],
    ),
    'build/app.md': log2(['T1']),
  });
  const result = check(dir, ['spec.md', 'plan.md', 'build/app.md']);
  assert.deepEqual(result.lines, []);
  assert.equal(result.status, 0);
});

test('a superseded plan is not checked against the spec, so a reopened spec with new scenarios can be accepted', () => {
  const tasks = [{ id: 'T1', covers: ['R1.1', 'R9.9'] }, { id: 'T2', depends: ['T1'], covers: ['R1.1'] }];
  const superseded = plan2(tasks, [], { status: 'superseded' });
  const dir = change({ 'spec.md': spec2(['R1.1', 'R2.1']), 'plan.md': superseded, 'build/app.md': log2(['T1', 'T7']) });
  const result = check(dir, ['spec.md', 'plan.md']);
  assert.deepEqual(result.lines, []);
  assert.equal(result.status, 0);
  const own = change({ 'spec.md': spec2(['R1.1']), 'plan.md': plan2([{ id: 'T1', depends: ['T2'], covers: ['R1.1'] }, { id: 'T2' }], [], { status: 'superseded' }) });
  assert.match(check(own, ['spec.md', 'plan.md']).lines.join('\n'), /T1 depends on T2, which comes after it/);
  const logged = check(dir, ['spec.md', 'plan.md', 'build/app.md']);
  assert.match(logged.lines.join('\n'), /build log entry T7 is not a task in the plan/);
  const draft = change({ 'spec.md': spec2(['R1.1', 'R2.1']), 'plan.md': plan2(tasks, [], { status: 'draft' }) });
  const live = check(draft, ['spec.md', 'plan.md']).lines.join('\n');
  assert.match(live, /scenario R2\.1 is covered by no task/);
  assert.match(live, /T1 covers R9\.9, which is not in the spec/);
});

test('a layout-1 plan or spec is refused with exit 2', () => {
  const dir = change({ 'spec.md': '```jsonc\n{ "layout": 1, "system": "demo" }\n```\n' });
  const result = check(dir, ['spec.md']);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /layout 1/);
});

test('check by hand on an edited plan', () => {
  const dir = tempDir('hoshin-hand-');
  cpSync(join(examples, 'change', '2', 'valid'), dir, { recursive: true });
  const original = readFileSync(join(dir, 'plan.md'), 'utf8');
  assert.deepEqual(check(dir, ['spec.md', 'plan.md']).lines, []);

  const edited = original.replace('"depends_on": ["T1"]', '"depends_on": ["T9"]');
  assert.notEqual(edited, original);
  writeFileSync(join(dir, 'plan.md'), edited);
  const result = check(dir, ['spec.md', 'plan.md']);
  assert.equal(result.status, 1);
  assert.deepEqual(result.lines, [
    `plan.md:${lineAfter(edited, /"id": "T2"/, /"depends_on"/)}: T2 depends on T9, which is not in the plan`,
  ]);

  writeFileSync(join(dir, 'plan.md'), original);
  const reverted = check(dir, ['spec.md', 'plan.md']);
  assert.equal(reverted.status, 0);
  assert.deepEqual(reverted.lines, []);
});

test('a task that covers a rule covers every scenario under it', () => {
  const spec = spec2(['R1.1', 'R1.2', 'R2.1']);
  const plan = plan2([
    { id: 'T1', covers: ['R1'] },
    { id: 'T2', depends: ['T1'], covers: ['R2.1'], supports: ['R1'] },
  ]);
  const dir = change({ 'spec.md': spec, 'plan.md': plan });
  const result = check(dir, ['spec.md', 'plan.md']);
  assert.deepEqual(result.lines, []);
  assert.equal(result.status, 0);
});

test('a depends_on field written with a space before the colon is reported at its own line', () => {
  const plan = plan2([{ id: 'T1', covers: ['R1.1'] }, { id: 'T2', depends: ['T9'] }]).replaceAll('"depends_on":', '"depends_on" :');
  const dir = change({ 'spec.md': spec2(['R1.1']), 'plan.md': plan });
  const result = check(dir, ['spec.md', 'plan.md']);
  assert.deepEqual(result.lines, [`plan.md:${lineAfter(plan, /"id": "T2"/, /"depends_on" :/)}: T2 depends on T9, which is not in the plan`]);
  assert.equal(result.status, 1);
});
