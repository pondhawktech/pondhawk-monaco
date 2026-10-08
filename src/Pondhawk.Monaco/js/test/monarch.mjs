// Tokenizes text with a grammar through Monaco's REAL Monarch tokenizer — no DOM, no editor — so a grammar's
// tests check what Monaco will actually colour rather than what its regexes were meant to match.
//
// Bundled like harness.mjs: monaco-editor's ESM is not loadable by Node as-is. The compiler and lexer are
// Monaco's own; only the services the lexer asks for are stood in for.

import * as esbuild from 'esbuild';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const outfile = path.join(here, '.tmp', 'monarch.mjs');

mkdirSync(path.dirname(outfile), { recursive: true });

await esbuild.build({
  stdin: {
    contents: [
      `export { compile } from 'monaco-editor/esm/vs/editor/standalone/common/monarch/monarchCompile.js';`,
      `export { MonarchTokenizer } from 'monaco-editor/esm/vs/editor/standalone/common/monarch/monarchLexer.js';`,
    ].join('\n'),
    resolveDir: path.join(here, '..'),
    sourcefile: 'monarch-entry.mjs',
    loader: 'js',
  },
  bundle: true,
  format: 'esm',
  outfile,
  logLevel: 'silent',
});

const { compile, MonarchTokenizer } = await import(pathToFileURL(outfile).href);

// An embedded language (the stack trace's JSON context) has no tokenizer here, so its text comes back as
// one untyped token: enough to see where embedding starts and stops.
const languageService = {
  languageIdCodec: { encodeLanguageId: () => 1 },
  getLanguageIdByLanguageName: name => name,
  getLanguageIdByMimeType: () => null,
  isRegisteredLanguageId: () => true,
  requestBasicLanguageFeatures() {},
};
const configuration = { getValue: () => 20000, onDidChangeConfiguration: () => ({ dispose() {} }) };

/**
 * Each line's tokens as [text, type] pairs, with the grammar's postfix dropped from the types:
 * tokenize(grammar, 'a\nb') → [[['a', 'type']], [['b', '']]].
 */
export function tokenize(languageId, grammar, text) {
  const tokenizer = new MonarchTokenizer(languageService, {}, languageId, compile(languageId, grammar), configuration);
  const postfix = grammar.tokenPostfix ?? `.${languageId}`;
  let state = tokenizer.getInitialState();

  return text.split('\n').map(line => {
    const { tokens, endState } = tokenizer.tokenize(line, true, state);
    state = endState;
    return tokens.map((token, i) => [
      line.slice(token.offset, tokens[i + 1]?.offset ?? line.length),
      token.type.endsWith(postfix) ? token.type.slice(0, -postfix.length) : token.type,
    ]);
  });
}
