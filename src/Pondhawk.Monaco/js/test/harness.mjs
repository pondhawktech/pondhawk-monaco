// Builds code-editor.js with Monaco swapped for the fake, and gives each test a fresh instance of it.
//
// Bundling rather than a Node loader hook: esbuild is already a dependency, and it means the tests run
// against the same transformation that ships rather than against the sources.

import * as esbuild from 'esbuild';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const outfile = path.join(here, '.tmp', 'bundle.mjs');

// Every monaco-editor specifier — the namespace import and all the side-effect contribution imports —
// collapses onto one fake. monaco-yaml gets its own.
const stubMonaco = {
  name: 'stub-monaco',
  setup(build) {
    build.onResolve({ filter: /^monaco-editor/ }, () => ({ path: path.join(here, 'fake-monaco.js') }));
    build.onResolve({ filter: /^monaco-yaml$/ }, () => ({ path: path.join(here, 'fake-monaco-yaml.js') }));
  },
};

mkdirSync(path.dirname(outfile), { recursive: true });

await esbuild.build({
  // A wrapper entry so the tests can reach both the module under test and the fake it was built against.
  stdin: {
    contents: `export * as mod from '../code-editor.js';\nexport * as monaco from './fake-monaco.js';\n`,
    resolveDir: here,
    sourcefile: 'entry.mjs',
    loader: 'js',
  },
  bundle: true,
  format: 'esm',
  outfile,
  plugins: [stubMonaco],
  logLevel: 'silent',
});

/**
 * The module keeps editors in a module-level Map, so tests must not share one instance or a leaked
 * editor from one test becomes another test's mystery. A unique query gives Node a fresh module graph.
 */
let generation = 0;
export async function load() {
  const url = `${pathToFileURL(outfile).href}?g=${generation++}`;
  const { mod, monaco } = await import(url);
  monaco.reset();
  return { mod, monaco };
}

/** Minimal DOM. The module touches querySelector, createElement, head.appendChild and Worker. */
export function installDom() {
  const head = { children: [], appendChild(node) { head.children.push(node); } };

  globalThis.document = {
    __head: head,
    head,
    querySelector: (selector) =>
      head.children.find(n => n.__selector === selector || selector.includes(n.__marker)) ?? null,
    createElement: () => {
      const node = { rel: '', href: '', __marker: null, setAttribute(k) { node.__marker = k; } };
      return node;
    },
  };

  globalThis.self = globalThis;
  globalThis.Worker = class { constructor(url) { this.url = url; } };

  const warnings = [];
  globalThis.console = { ...console, warn: (...a) => warnings.push(a.join(' ')) };
  return warnings;
}

/** A host element stand-in — the module only sets dataset on it. */
export const makeHost = () => ({ dataset: {} });

/** Every recorded call of a given name. */
export const calls = (monaco, name) => monaco.log.filter(c => c.name === name);

/** The single recorded call of a given name; fails the test if there is not exactly one. */
export function onlyCall(monaco, name) {
  const found = calls(monaco, name);
  if (found.length !== 1) {
    throw new Error(`expected exactly one '${name}' call, saw ${found.length}`);
  }
  return found[0];
}
