// Architectural and packaging invariants for Hoshin.Net.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { bareEnv, dotnet, program, repoRoot } from './helpers/workspace.mjs';
import { tempDir } from './helpers/temp.mjs';

const MINIMAL_SPEC =
  '```jsonc\n{\n  "layout": 2,\n  "system": "demo"\n}\n```\n\n```gherkin\nFeature: Demo\n  @R1 @from:D1\n  Rule: One\n    @R1.1\n    Scenario: First\n      Given a thing\n```\n';

test('Hoshin.Workflow references only Markdig and Gherkin', () => {
  const csproj = readFileSync(join(repoRoot, 'src', 'Hoshin.Workflow', 'Hoshin.Workflow.csproj'), 'utf8');
  const pkgs = [...csproj.matchAll(/<PackageReference\s+Include="([^"]+)"/g)].map((m) => m[1]).sort();
  assert.deepEqual(pkgs, ['Gherkin', 'Markdig']);
});

test('dogfooding safety: no .agents/hooks.json or .claude-plugin in repo root', () => {
  assert.equal(existsSync(join(repoRoot, '.agents', 'hooks.json')), false);
  assert.equal(existsSync(join(repoRoot, '.claude-plugin')), false);
});

test('hoshin.dll runs standalone with minimal environment and zero warnings', () => {
  const dir = tempDir('hoshin-layout-');
  const spec = join(dir, 'spec.md');
  writeFileSync(spec, MINIMAL_SPEC);
  const r = spawnSync(dotnet, [program, 'check', spec], { cwd: dir, encoding: 'utf8', env: bareEnv });
  assert.equal(r.status, 0, r.stderr);
  assert.equal(r.stdout, '');
  assert.equal(r.stderr, '');
});
