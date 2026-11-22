// Writes active run.json and step.json files under <git-common-dir>/hoshin/ for guard black-box tests.
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { git } from './workspace.mjs';

export function writeActiveRun(repoDir, { sessionId = 's1', harness = 'claude-code', changeFolder = null, kind = 'NeedsApproval', task = null, attempt = 0 } = {}) {
  const commonDir = git(repoDir, 'rev-parse', '--path-format=absolute', '--git-common-dir');
  const hoshinDir = join(commonDir, 'hoshin');
  mkdirSync(hoshinDir, { recursive: true });
  const now = new Date().toISOString();
  const resolvedChange = changeFolder ? resolve(repoDir, changeFolder) : join(repoDir, '.hoshin');

  writeFileSync(
    join(hoshinDir, 'run.json'),
    JSON.stringify({ started: now, sessionId, harness, changeFolder: resolvedChange }, null, 2) + '\n',
  );
  writeFileSync(
    join(hoshinDir, 'step.json'),
    JSON.stringify({ kind, task, attempt, startHead: null, at: now, sessionId, changeFolder: resolvedChange }, null, 2) + '\n',
  );
  return hoshinDir;
}
