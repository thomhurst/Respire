import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { test } from 'node:test';

const props = readFileSync(new URL('../../Directory.Build.props', import.meta.url), 'utf8');
const packageList = props.match(/<RespirePackageProjects>([\s\S]*?)<\/RespirePackageProjects>/)?.[1];
assert.ok(packageList, 'Directory.Build.props must declare the shipping package list');
const packages = packageList.split(';').map(name => name.trim()).filter(Boolean);
assert.ok(packages.length > 0, 'The shipping package list must not be empty');
const directoryUrl = new URL('../docs/packages.md', import.meta.url);
const directory = readFileSync(directoryUrl, 'utf8');

function assertPackageGuides(markdown) {
  // Only linked package IDs in the table's first column count, not incidental prose mentions.
  const entries = new Map();
  for (const row of markdown.split(/\r?\n/).filter(line => line.startsWith('|'))) {
    const firstColumn = row.split('|')[1];
    for (const [, name, link] of firstColumn.matchAll(/\[`([^`]+)`\]\(([^)]+)\)/g)) {
      assert.ok(!entries.has(name), `Duplicate package entry: ${name}`);
      entries.set(name, link);
    }
  }
  for (const name of packages) {
    assert.ok(entries.has(name), `Missing shipping package guide: ${name}`);
    const link = entries.get(name);
    assert.match(link, /^(?:[\w-]+\/)*[\w-]+\.md(?:#[\w-]+)?$/, `${name} needs a website guide`);
    assert.ok(existsSync(new URL(link.split('#')[0], directoryUrl)), `${name} guide does not exist: ${link}`);
  }
}

test('every shipping package has a linked website guide in the package directory', () => {
  assertPackageGuides(directory);
});

test('a missing package entry fails even if the package is mentioned elsewhere', () => {
  for (const name of packages) {
    const withoutEntry = directory.replaceAll('`' + name + '`', name);
    assert.throws(() => assertPackageGuides(withoutEntry), {
      message: `Missing shipping package guide: ${name}`,
    });
  }
});
