// @signalwire/docusaurus-plugin-llms-txt 1.2.2 prepends the full site URL to
// in-page links that already carry the baseUrl, so the generated Markdown links
// to /Respire/Respire/... Collapse the duplicated baseUrl after each build.
import {readdir, readFile, writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import config from '../docusaurus.config.js';

const buildDir = fileURLToPath(new URL('../build/', import.meta.url));
const siteUrl = config.url.replace(/\/$/, '');
const baseUrl = config.baseUrl;
const duplicated = `${siteUrl}${baseUrl}${baseUrl.slice(1)}`;
const fixed = `${siteUrl}${baseUrl}`;

if (baseUrl !== '/') {
  let count = 0;
  for (const entry of await readdir(buildDir, {recursive: true, withFileTypes: true})) {
    if (!entry.isFile() || !/\.(md|txt)$/.test(entry.name)) continue;
    const path = join(entry.parentPath, entry.name);
    const text = await readFile(path, 'utf8');
    if (!text.includes(duplicated)) continue;
    await writeFile(path, text.replaceAll(duplicated, fixed));
    count++;
  }
  console.log(`[fix-llms-links] Collapsed duplicated baseUrl in ${count} files.`);
}
