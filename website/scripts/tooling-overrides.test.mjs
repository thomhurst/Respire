import assert from 'node:assert/strict';
import {mkdtemp, readFile, rm, utimes, writeFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {test} from 'node:test';
import {runInNewContext} from 'node:vm';

// package.json pins these fixes only below @docusaurus/bundler 3.10.2:
// https://github.com/advisories/GHSA-rj75-hqrm-r3gf
// https://github.com/advisories/GHSA-5c6j-r48x-rmvq
// https://github.com/advisories/GHSA-qj8w-gfj5-8c6v
// serialize-javascript 7 needs Node 20+ (this site requires 24); selector-parser
// 7 changes insertion during iteration. Keep these consumer tests when updating
// Docusaurus; remove the overrides when its ranges resolve to patched versions.
// Resolve from the actual consumers so a hoisted, patched copy cannot hide an
// old nested dependency used by Docusaurus.
const require = createRequire(import.meta.url);
const bundler = createRequire(require.resolve('@docusaurus/bundler/package.json'));
const cssMinimizer = createRequire(bundler.resolve('css-minimizer-webpack-plugin'));
const copyPlugin = createRequire(bundler.resolve('copy-webpack-plugin'));
const CssMinimizerPlugin = bundler('css-minimizer-webpack-plugin');
const CopyPlugin = bundler('copy-webpack-plugin');
const webpack = bundler('webpack');

test('build consumers serialize RegExp and Date values without executing injected code', () => {
  for (const consumer of [cssMinimizer, copyPlugin]) {
    const serialize = consumer('serialize-javascript');
    const date = new Date('2026-01-01T00:00:00Z');
    date.toISOString = () => '"+(globalThis.injected=true)+"';
    const regex = /docs/gi;
    Object.defineProperty(regex, 'flags', {value: '"+(globalThis.injected=true)+"'});
    for (const value of [date, regex]) {
      const context = {injected: false};
      try {
        runInNewContext(`(${serialize(value)})`, context);
      } catch (error) {
        // Dates are rejected during serialization; sanitized but duplicated
        // flags can be rejected by RegExp when evaluating the result.
        if (value === date) {
          assert.equal(error.name, 'TypeError');
          assert.equal(error.message, 'Invalid Date ISO string');
        } else {
          assert.equal(error.name, 'SyntaxError');
        }
      }
      assert.equal(context.injected, false);
    }
    const restored = runInNewContext(`(${serialize({
      pattern: /docs/gi,
      date: new Date('2026-01-01T00:00:00Z'),
      transform: (value) => value.toUpperCase(),
    })})`);
    assert.equal(restored.pattern.source, 'docs');
    assert.equal(restored.pattern.flags, 'gi');
    assert.equal(restored.date.toISOString(), '2026-01-01T00:00:00.000Z');
    assert.equal(restored.transform('respire'), 'RESPIRE');
  }
});

test('serializers treat array-like objects as objects rather than iterating their length', () => {
  for (const consumer of [cssMinimizer, copyPlugin]) {
    const serialize = consumer('serialize-javascript');
    // A small length reproduces the incorrect classification without a CPU
    // exhaustion payload or a timing-sensitive assertion.
    const value = Object.assign(Object.create(Array.prototype), {0: 'docs', length: 3});
    const restored = runInNewContext(`(${serialize(value)})`);
    assert.equal(Array.isArray(restored), false);
    assert.equal(restored[0], 'docs');
    assert.equal(restored.length, 3);
  }
});

const css = `
  /* Exercise selector deduplication, pseudo traversal, attributes and calc. */
  .card, .card { color: rgb(255, 0, 0); width: calc(10px + 10px); }
  .card::before { content: "Respire"; }
  [data-kind="docs"] > *.link:nth-child(2n + 1) { margin: 0px 0px; }
  :is(.card, .card, .link):not([hidden]) { padding: 0px; }
  @media (min-width: 800px) { .card { display: block; } }
`;

async function compile(compiler) {
  const stats = await new Promise((resolve, reject) => {
    compiler.run((error, result) => error ? reject(error) : resolve(result));
  });
  assert.equal(stats.hasErrors(), false, stats.toString({all: false, errors: true}));
}

for (const advanced of [false, true]) {
  test(`Docusaurus ${advanced ? 'advanced' : 'default'} CSS and cached copy transforms work in webpack`, async () => {
    const directory = await mkdtemp(path.join(tmpdir(), 'respire-tooling-'));
    let compiler;
    try {
      await writeFile(path.join(directory, 'input.css'), css);
      await writeFile(path.join(directory, 'input.txt'), 'Respire documentation');
      // Webpack allows up to two seconds of filesystem timestamp uncertainty.
      // Backdate these unchanged fixtures before the first snapshot so cache
      // reuse does not depend on how quickly the two compilations run.
      const timestamp = new Date(Date.now() - 10_000);
      for (const filename of ['input.css', 'input.txt']) {
        await utimes(path.join(directory, filename), timestamp, timestamp);
      }
      let transforms = 0;
      const preset = advanced ? bundler.resolve('@docusaurus/cssnano-preset') : 'default';
      compiler = webpack({
        mode: 'production',
        context: directory,
        entry: {},
        cache: {type: 'memory'},
        output: {path: path.join(directory, 'output')},
        plugins: [new CopyPlugin({
          patterns: [
            {from: 'input.css', to: 'site.css'},
            {
              from: 'input.txt',
              to: 'copied.txt',
              transform: {
                transformer(content) {
                  transforms++;
                  return content.toString().toUpperCase();
                },
                cache: {
                  keys: {pattern: /docs/gi, date: new Date('2026-01-01T00:00:00Z')},
                },
              },
            },
          ],
        })],
        optimization: {
          minimize: true,
          minimizer: [new CssMinimizerPlugin({
            // Exercise serialization to the real worker with bounded concurrency.
            parallel: 1,
            minimizerOptions: {preset},
          })],
        },
      });
      await compile(compiler);
      const first = await readFile(path.join(directory, 'output/site.css'), 'utf8');
      assert.equal(first, '.card{color:red;width:20px}.card:before{content:"Respire"}[data-kind=docs]>.link:nth-child(odd){margin:0}:is(.card,.card,.link):not([hidden]){padding:0}@media (min-width:800px){.card{display:block}}');
      assert.equal(await readFile(path.join(directory, 'output/copied.txt'), 'utf8'), 'RESPIRE DOCUMENTATION');
      assert.equal(transforms, 1, 'the first compilation must run the transform');
      await compile(compiler);
      assert.equal(await readFile(path.join(directory, 'output/site.css'), 'utf8'), first);
      assert.equal(await readFile(path.join(directory, 'output/copied.txt'), 'utf8'), 'RESPIRE DOCUMENTATION');
      assert.equal(transforms, 1, 'the second compilation must reuse the transform cache');
    } finally {
      if (compiler) {
        await new Promise((resolve, reject) => compiler.close(error => error ? reject(error) : resolve()));
      }
      await rm(directory, {recursive: true, force: true});
    }
  });
}
