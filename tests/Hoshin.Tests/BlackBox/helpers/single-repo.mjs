// Builds a real single-repo layout in a scratch folder.
import { realpathSync } from 'node:fs';
import { join } from 'node:path';
import { git, repo } from './workspace.mjs';
import { tempDir } from './temp.mjs';

export const REPO = 'mono';

export function singleRepo({ apps = { api: 'apps/api', web: 'apps/web' }, marker = {} } = {}) {
  const root = realpathSync.native(tempDir('hoshin-single-'));
  const workspace = join(root, 'ws');
  const path = join(workspace, REPO);
  const files = {};
  for (const folder of Object.values(apps)) if (folder !== '.') files[`${folder}/README.md`] = `# ${folder}\n`;
  files['README.md'] = `# ${REPO}\n`;
  files['.hoshin.json'] = JSON.stringify({ version: 1, layout: 'single-repo', systems: { demo: { apps } }, ...marker }, null, 2) + '\n';
  files['intent/demo/policies.md'] = '# Policies\n';
  repo(path, files);
  return { root, workspace, repo: path, remote: `${path}.remote.git`, git: (...args) => git(path, ...args) };
}
