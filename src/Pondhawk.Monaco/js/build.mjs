// Bundles Monaco + monaco-yaml into ../wwwroot/dist.
//
// This step exists ONLY in this repo. Consumers get the emitted assets inside the NuGet package under
// _content/Pondhawk.Monaco/dist/ and never need node, npm or a bundler.
//
// monaco-yaml v5 is ESM-only and its language server runs in a worker, so a bundler is not optional —
// which is precisely why BlazorMonaco (AMD loader) cannot host it.

import * as esbuild from 'esbuild';
import { rmSync, mkdirSync } from 'node:fs';

const outdir = '../wwwroot/dist';
const watch = process.argv.includes('--watch');

// Clean so stale worker chunks from a previous Monaco version can't linger and get served.
rmSync(outdir, { recursive: true, force: true });
mkdirSync(outdir, { recursive: true });

/** @type {import('esbuild').BuildOptions} */
const shared = {
  bundle: true,
  minify: !watch,
  sourcemap: watch,
  outdir,
  legalComments: 'none',
  logLevel: 'info',
  // Monaco ships the codicon icon font; emit it as a file rather than inlining ~70KB of base64.
  loader: { '.ttf': 'file' },
};

// The component module. ESM so Blazor can import() it as a JS module.
const main = {
  ...shared,
  entryPoints: { 'code-editor': 'code-editor.js' },
  format: 'esm',
  // Monaco's CSS is imported by its ESM entry points; esbuild emits it as code-editor.css.
};

// Workers are bundled separately and loaded as CLASSIC workers (not type: "module").
// Classic + IIFE is the widest-compatibility combination and avoids module-worker support questions
// in older browsers; the trade-off is that each worker carries its own copy of shared Monaco code.
//
// One worker per language service. This list and the contribution imports in code-editor.js must agree:
// a service imported without its worker fails at the moment that language is first opened, and a worker
// built for a service that was never imported is dead weight in the package.
//
// TypeScript is deliberately absent. ts.worker.js is 5.7 MB — it contains the TypeScript compiler — and
// dropping it with the rest of the unused language set takes the package from 3.3 MB to 2.0 MB. TS and
// JS are not in the supported set, so nothing routes to it.
const workers = {
  ...shared,
  entryPoints: {
    'editor.worker': 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js',
    'json.worker': 'node_modules/monaco-editor/esm/vs/language/json/json.worker.js',
    'css.worker': 'node_modules/monaco-editor/esm/vs/language/css/css.worker.js',
    'html.worker': 'node_modules/monaco-editor/esm/vs/language/html/html.worker.js',
    'yaml.worker': 'node_modules/monaco-yaml/yaml.worker.js',
  },
  format: 'iife',
};

if (watch) {
  const contexts = await Promise.all([esbuild.context(main), esbuild.context(workers)]);
  await Promise.all(contexts.map(c => c.watch()));
  console.log('watching…');
} else {
  await Promise.all([esbuild.build(main), esbuild.build(workers)]);
  console.log(`bundled -> ${outdir}`);
}
