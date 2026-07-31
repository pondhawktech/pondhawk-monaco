import { test, describe, before } from 'node:test';
import assert from 'node:assert/strict';
import { load, installDom, makeHost, calls, onlyCall } from './harness.mjs';

let warnings;
before(() => { warnings = installDom(); });

/** Creates an editor with sane defaults and returns everything a test needs. */
async function editorFixture(options = {}) {
  const { mod, monaco } = await load();
  warnings.length = 0;
  const host = makeHost();
  mod.create('e1', host, {
    baseUrl: 'https://app.example/_content/Pondhawk.Monaco/dist',
    value: 'hello', language: 'yaml', tabSize: 2, fontSize: 12.5,
    minimap: false, readOnly: false, debounceMs: 300, ...options,
  });
  return { mod, monaco, host };
}

describe('worker routing', () => {
  // A label routed to the wrong worker does not throw — it silently yields no completions for that
  // language. This is the failure the code comments call out, and the only test that can catch it.
  test('every bundled language service reaches its own worker', async () => {
    const { monaco } = await editorFixture();
    const url = label => globalThis.self.MonacoEnvironment.getWorker('', label).url;
    const base = 'https://app.example/_content/Pondhawk.Monaco/dist';

    assert.equal(url('json'), `${base}/json.worker.js`);
    assert.equal(url('yaml'), `${base}/yaml.worker.js`);
    assert.equal(url('css'), `${base}/css.worker.js`);
    assert.equal(url('scss'), `${base}/css.worker.js`, 'scss shares the css worker');
    assert.equal(url('less'), `${base}/css.worker.js`);
    assert.equal(url('html'), `${base}/html.worker.js`);
    assert.equal(url('razor'), `${base}/html.worker.js`, 'razor shares the html worker');
    assert.ok(monaco);
  });

  test('an unrouted label falls back to the plain editor worker', async () => {
    await editorFixture();
    const url = label => globalThis.self.MonacoEnvironment.getWorker('', label).url;

    // typescript is deliberately unbundled: it must NOT ask for a ts.worker.js that does not ship.
    assert.match(url('typescript'), /editor\.worker\.js$/);
    assert.match(url('anything-else'), /editor\.worker\.js$/);
  });
});

describe('construction', () => {
  // Both halves of a bug that made TabSize inert: it is a MODEL option, and Monaco's indentation
  // detection would overwrite it from the document anyway.
  test('tabSize is set on the model, not left to the editor options', async () => {
    const { monaco } = await editorFixture({ tabSize: 8 });
    assert.deepEqual(onlyCall(monaco, 'model.updateOptions').args[0], { tabSize: 8 });
  });

  test('indentation detection is off, or tabSize is only advisory', async () => {
    const { monaco } = await editorFixture({ tabSize: 8 });
    assert.equal(onlyCall(monaco, 'editor.create').args[0].detectIndentation, false);
  });

  test('the model is ours and automatic layout stays off', async () => {
    const { monaco } = await editorFixture();
    const options = onlyCall(monaco, 'editor.create').args[0];

    assert.equal(options.automaticLayout, false, 'Monaco ResizeObserver feeds back in overflow:hidden');
    assert.equal(options.model.__kind, 'model');
  });

  test('raw editorOptions override the defaults but not the model or layout', async () => {
    const { monaco } = await editorFixture({
      editorOptions: { fontSize: 99, automaticLayout: true, wordWrap: 'on' },
    });
    const options = onlyCall(monaco, 'editor.create').args[0];

    assert.equal(options.fontSize, 99, 'caller wins over the component default');
    assert.equal(options.wordWrap, 'on');
    assert.equal(options.automaticLayout, false, 'not overridable — it is applied after the spread');
  });

  test('the registry key is stamped on the host for console inspection', async () => {
    const { host } = await editorFixture();
    assert.equal(host.dataset.pondhawkEditor, 'e1');
  });
});

describe('live options', () => {
  test('updateOptions maps to Monaco shapes and keeps tabSize on the model', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.updateOptions('e1', { readOnly: true, minimap: true, tabSize: 4, fontSize: 20 });

    const editorOptions = onlyCall(monaco, 'editor.updateOptions').args[0];
    assert.deepEqual(editorOptions.minimap, { enabled: true }, 'minimap is an object, not a bool');
    assert.equal(editorOptions.readOnly, true);
    assert.equal(editorOptions.fontSize, 20);
    assert.equal(editorOptions.detectIndentation, false);
    assert.deepEqual(onlyCall(monaco, 'model.updateOptions').args[0], { tabSize: 4 });
  });

  test('updating an unknown editor is a no-op rather than a throw', async () => {
    const { mod } = await editorFixture();
    assert.doesNotThrow(() => mod.updateOptions('does-not-exist', { tabSize: 4 }));
  });
});

describe('setValue echo guard', () => {
  // The caret-jump guard, on the JS side. The .NET side has its own; this is the belt to that braces.
  test('an unchanged value is not written back into the model', async () => {
    const { mod, monaco } = await editorFixture({ value: 'hello' });
    monaco.reset();

    mod.setValue('e1', 'hello');

    assert.equal(calls(monaco, 'model.pushEditOperations').length, 0);
  });

  test('a genuinely different value is pushed, preserving undo history', async () => {
    const { mod, monaco } = await editorFixture({ value: 'hello' });
    monaco.reset();

    mod.setValue('e1', 'goodbye');

    const edits = onlyCall(monaco, 'model.pushEditOperations').args[0];
    assert.equal(edits[0].text, 'goodbye');
  });
});

describe('markers', () => {
  // The half of the payload contract the .NET tests say in as many words they cannot check.
  test('severity strings map to Monaco severities, unknown counting as error', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setMarkers('e1', [
      { startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, message: 'a', severity: 'error' },
      { startLine: 2, startColumn: 1, endLine: 2, endColumn: 2, message: 'b', severity: 'warning' },
      { startLine: 3, startColumn: 1, endLine: 3, endColumn: 2, message: 'c', severity: 'info' },
      { startLine: 4, startColumn: 1, endLine: 4, endColumn: 2, message: 'd', severity: 'nonsense' },
    ]);

    const [owner, markers] = onlyCall(monaco, 'editor.setModelMarkers').args;
    assert.equal(owner, 'pondhawk', 'namespaced so the language service keeps its own');
    assert.deepEqual(markers.map(m => m.severity), [8, 4, 2, 8]);
  });

  test('1-based positions are renamed, not renumbered', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setMarkers('e1', [
      { startLine: 3, startColumn: 5, endLine: 3, endColumn: 12, message: 'boom', source: 'rules' },
    ]);

    const marker = onlyCall(monaco, 'editor.setModelMarkers').args[1][0];
    assert.equal(marker.startLineNumber, 3);
    assert.equal(marker.startColumn, 5);
    assert.equal(marker.endLineNumber, 3);
    assert.equal(marker.endColumn, 12);
    assert.equal(marker.source, 'rules');
  });

  test('a null marker list clears rather than throwing', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setMarkers('e1', null);

    assert.deepEqual(onlyCall(monaco, 'editor.setModelMarkers').args[1], []);
  });
});

describe('decorations', () => {
  test('fields are mapped to Monaco range and options', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setDecorations('e1', [{
      startLine: 6, startColumn: 1, endLine: 9, endColumn: 1,
      wholeLine: true, className: 'hl', inlineClassName: 'inl',
      lineNumberClassName: 'ln', hoverMessage: 'why', overviewRulerColor: '#f59e0b',
    }]);

    const [decoration] = onlyCall(monaco, 'decorations.set').args[0];
    assert.deepEqual(decoration.range,
      { startLineNumber: 6, startColumn: 1, endLineNumber: 9, endColumn: 1 });
    assert.equal(decoration.options.className, 'hl');
    assert.equal(decoration.options.inlineClassName, 'inl');
    assert.equal(decoration.options.linesDecorationsClassName, 'ln', 'Monaco spells this one plural');
    assert.equal(decoration.options.isWholeLine, true);
    assert.deepEqual(decoration.options.hoverMessage, { value: 'why' });
    assert.equal(decoration.options.overviewRuler.color, '#f59e0b');
  });

  test('a glyph decoration turns the glyph margin on, since it is off by default', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setDecorations('e1', [
      { startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, glyphMarginClassName: 'g' },
    ]);

    const enabling = calls(monaco, 'editor.updateOptions').filter(c => c.args[0].glyphMargin === true);
    assert.equal(enabling.length, 1, 'a glyph drawn into an absent margin is invisible and silent');
  });

  test('decorations without a glyph leave the margin alone', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setDecorations('e1', [{ startLine: 1, startColumn: 1, endLine: 1, endColumn: 2, className: 'x' }]);

    assert.equal(calls(monaco, 'editor.updateOptions').length, 0);
  });

  test('the collection is created once and reused', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setDecorations('e1', []);
    mod.setDecorations('e1', []);

    assert.equal(calls(monaco, 'editor.createDecorationsCollection').length, 1);
    assert.equal(calls(monaco, 'decorations.set').length, 2);
  });
});

describe('themes', () => {
  // Monaco wants rule colours WITHOUT a '#' and colors entries WITH one, and throws on the wrong form.
  test('colour formats are normalised in both directions', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.defineTheme({
      name: 'dusk', base: 'vs-dark', inherit: true,
      rules: [{ token: 'comment', foreground: '#7f9f7f', background: 'ff0000', fontStyle: 'italic' }],
      colors: { 'editor.background': '1b1d23', 'editor.foreground': '#ffffff' },
    });

    const [name, theme] = onlyCall(monaco, 'editor.defineTheme').args;
    assert.equal(name, 'dusk');
    assert.equal(theme.rules[0].foreground, '7f9f7f', 'rule colours drop the hash');
    assert.equal(theme.rules[0].background, 'ff0000', 'already bare, left alone');
    assert.equal(theme.colors['editor.background'], '#1b1d23', 'theme colours gain one');
    assert.equal(theme.colors['editor.foreground'], '#ffffff', 'already hashed, left alone');
  });

  test('a theme with no rules or colours still defines', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.defineTheme({ name: 'bare' });

    const [, theme] = onlyCall(monaco, 'editor.defineTheme').args;
    assert.deepEqual(theme.rules, []);
    assert.deepEqual(theme.colors, {});
    assert.equal(theme.base, 'vs');
    assert.equal(theme.inherit, true);
  });
});

describe('unbundled languages', () => {
  test('an unknown language warns once and names what is bundled', async () => {
    const { mod } = await editorFixture({ language: 'python' });

    assert.equal(warnings.length, 1);
    assert.match(warnings[0], /python/);
    assert.match(warnings[0], /csharp, css, html, json/, 'lists what IS available');

    mod.setLanguage('e1', 'python');
    assert.equal(warnings.length, 1, 'warned once per id, not once per call');
  });

  test('a bundled language is silent', async () => {
    await editorFixture({ language: 'yaml' });
    assert.equal(warnings.length, 0);
  });
});

describe('actions', () => {
  test('a registered action runs and reports true', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    const ran = await mod.runAction('e1', 'editor.action.formatDocument');

    assert.equal(ran, true);
    assert.equal(onlyCall(monaco, 'action.run').args[0], 'editor.action.formatDocument');
  });

  test('an unknown id reports false and does not escape as an exception', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    // Monaco throws out of trigger() for an unknown id; a mistyped action must not take out the caller.
    const ran = await mod.runAction('e1', 'editor.action.nonsense');

    assert.equal(ran, false);
    assert.equal(calls(monaco, 'editor.trigger').length, 1, 'still attempted, for commands like undo');
    assert.match(warnings.at(-1) ?? '', /nonsense/);
  });
});

describe('cursor and selection', () => {
  test('positions translate between Monaco names and ours', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setPosition('e1', 7, 3);
    assert.deepEqual(onlyCall(monaco, 'editor.setPosition').args[0], { lineNumber: 7, column: 3 });
    assert.deepEqual(mod.getPosition('e1'), { line: 7, column: 3 });
  });

  test('a missing column defaults to the start of the line', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setPosition('e1', 7, undefined);
    assert.equal(onlyCall(monaco, 'editor.setPosition').args[0].column, 1);
  });

  test('selections translate in both directions', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.setSelection('e1', { startLine: 1, startColumn: 2, endLine: 3, endColumn: 4 });

    assert.deepEqual(onlyCall(monaco, 'editor.setSelection').args[0],
      { startLineNumber: 1, startColumn: 2, endLineNumber: 3, endColumn: 4 });
    assert.deepEqual(mod.getSelection('e1'),
      { startLine: 1, startColumn: 2, endLine: 3, endColumn: 4 });
  });

  test('reading from an unknown editor gives null rather than throwing', async () => {
    const { mod } = await editorFixture();
    assert.equal(mod.getPosition('nope'), null);
    assert.equal(mod.getSelection('nope'), null);
    assert.equal(mod.getValue('nope'), '');
  });
});

describe('disposal', () => {
  // Without this the Monaco instance and its model outlive the component — a leak per navigation.
  test('the editor is disposed before its model', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.dispose('e1');

    const order = monaco.log.map(c => c.name);
    const editorAt = order.indexOf('editor.dispose');
    const modelAt = order.indexOf('model.dispose');

    assert.ok(editorAt >= 0 && modelAt >= 0, 'both disposed');
    assert.ok(editorAt < modelAt, 'disposing a model still attached leaves the editor reading a dead one');
  });

  test('content subscriptions and decorations are released too', async () => {
    const { mod, monaco } = await editorFixture();
    mod.setDecorations('e1', []);
    monaco.reset();

    mod.dispose('e1');

    assert.equal(calls(monaco, 'model.onDidChangeContent.dispose').length, 1);
    assert.equal(calls(monaco, 'decorations.clear').length, 1);
  });

  test('disposing twice, or an unknown id, is harmless', async () => {
    const { mod } = await editorFixture();
    mod.dispose('e1');
    assert.doesNotThrow(() => mod.dispose('e1'));
    assert.doesNotThrow(() => mod.dispose('never-existed'));
  });

  test('creating over an existing id disposes the previous editor first', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.create('e1', makeHost(), { baseUrl: 'https://app.example/d', value: '', language: 'yaml' });

    const order = monaco.log.map(c => c.name);
    assert.ok(order.indexOf('editor.dispose') < order.indexOf('editor.create'),
      'a re-render that recreated the host must not leak the previous editor');
  });
});

describe('schema', () => {
  test('one schema drives both the JSON service and monaco-yaml', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.configureSchema('e1', '{"type":"object"}', null);

    const json = onlyCall(monaco, 'json.setDiagnosticsOptions').args[0];
    const yaml = onlyCall(monaco, 'configureMonacoYaml').args[0];

    assert.deepEqual(json.schemas[0].schema, { type: 'object' });
    assert.deepEqual(yaml.schemas[0].schema, { type: 'object' });
    assert.equal(json.schemas[0].uri, yaml.schemas[0].uri, 'the same contract in both formats');
    assert.equal(json.enableSchemaRequest, false, 'no network fetches for schemas');
  });

  test('a schema defaults to its own editor, not to every document', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.configureSchema('e1', '{"type":"object"}', null);

    const [entry] = onlyCall(monaco, 'json.setDiagnosticsOptions').args[0].schemas;
    assert.deepEqual(entry.fileMatch, ['inmemory://pondhawk/e1.yaml'],
      "the old default of ['*'] made every schema claim every document");
  });

  // The defect: Monaco's schema configuration is page-global, so configuring one editor used to
  // replace the other's. Both must survive, each scoped to its own document.
  test('two editors keep their own schemas', async () => {
    const { mod, monaco } = await editorFixture();
    mod.create('e2', makeHost(), {
      baseUrl: 'https://app.example/d', value: '', language: 'json', tabSize: 2,
    });
    monaco.reset();

    mod.configureSchema('e1', '{"title":"first"}', null);
    mod.configureSchema('e2', '{"title":"second"}', null);

    const { schemas } = calls(monaco, 'json.setDiagnosticsOptions').at(-1).args[0];
    assert.equal(schemas.length, 2, 'the second schema must not replace the first');

    const byTitle = Object.fromEntries(schemas.map(s => [s.schema.title, s]));
    assert.deepEqual(byTitle.first.fileMatch, ['inmemory://pondhawk/e1.yaml']);
    assert.deepEqual(byTitle.second.fileMatch, ['inmemory://pondhawk/e2.json']);
    assert.notEqual(byTitle.first.uri, byTitle.second.uri, 'distinct keys, or they collapse into one');
  });

  test('an explicit fileMatch still widens the scope', async () => {
    const { mod, monaco } = await editorFixture();
    monaco.reset();

    mod.configureSchema('e1', '{"type":"object"}', ['*']);

    const [entry] = onlyCall(monaco, 'json.setDiagnosticsOptions').args[0].schemas;
    assert.deepEqual(entry.fileMatch, ['*'], 'a caller sharing one contract across editors');
  });

  test('disposing an editor retracts its schema', async () => {
    const { mod, monaco } = await editorFixture();
    mod.configureSchema('e1', '{"type":"object"}', null);
    monaco.reset();

    mod.dispose('e1');

    const { schemas } = calls(monaco, 'json.setDiagnosticsOptions').at(-1).args[0];
    assert.equal(schemas.length, 0, 'a dead editor must stop claiming documents');
  });

  test('reconfiguring disposes the previous yaml registration', async () => {
    const { mod, monaco } = await editorFixture();
    mod.configureSchema('e1', '{"type":"object"}', ['*.yaml']);
    monaco.reset();

    mod.configureSchema('e1', '{"type":"array"}', ['*.yaml']);

    assert.equal(calls(monaco, 'yaml.dispose').length, 1, 'or the old service is left registered');
  });
});

describe('diff editor', () => {
  async function diffFixture(options = {}) {
    const { mod, monaco } = await load();
    warnings.length = 0;
    mod.createDiff('d1', makeHost(), {
      baseUrl: 'https://app.example/d',
      original: 'before', modified: 'after', language: 'yaml',
      tabSize: 2, fontSize: 12.5, sideBySide: true, readOnly: true, ...options,
    });
    return { mod, monaco };
  }

  test('two models are created and handed to the editor together', async () => {
    const { monaco } = await diffFixture();

    const created = calls(monaco, 'editor.createModel');
    assert.equal(created.length, 2, 'one per side');
    assert.deepEqual(created.map(c => c.args[0]), ['before', 'after']);

    const { original, modified } = onlyCall(monaco, 'diff.setModel').args[0];
    assert.equal(original.__value, 'before');
    assert.equal(modified.__value, 'after');
  });

  test('tabSize lands on BOTH models, and indentation detection is off', async () => {
    const { monaco } = await diffFixture({ tabSize: 8 });

    const updates = calls(monaco, 'model.updateOptions');
    assert.equal(updates.length, 2, 'a diff has two models and both need it');
    assert.ok(updates.every(u => u.args[0].tabSize === 8));
    assert.equal(onlyCall(monaco, 'editor.createDiffEditor').args[0].detectIndentation, false);
  });

  test('readOnly governs the modified side and the original is locked separately', async () => {
    const { monaco } = await diffFixture({ readOnly: true, originalEditable: false });
    const options = onlyCall(monaco, 'editor.createDiffEditor').args[0];

    assert.equal(options.readOnly, true);
    assert.equal(options.originalEditable, false);
    assert.equal(options.renderSideBySide, true);
  });

  test('a side is addressed by name in both directions', async () => {
    const { mod, monaco } = await diffFixture();
    monaco.reset();

    assert.equal(mod.getDiffValue('d1', 'original'), 'before');
    assert.equal(mod.getDiffValue('d1', 'modified'), 'after');

    mod.setDiffValue('d1', 'original', 'changed');
    assert.equal(mod.getDiffValue('d1', 'original'), 'changed');
    assert.equal(mod.getDiffValue('d1', 'modified'), 'after', 'the other side is untouched');
  });

  test('an unchanged side is not written back — the caret guard, doubled', async () => {
    const { mod, monaco } = await diffFixture();
    monaco.reset();

    mod.setDiffValue('d1', 'modified', 'after');

    assert.equal(calls(monaco, 'model.pushEditOperations').length, 0);
  });

  test('view options update in place rather than rebuilding the editor', async () => {
    const { mod, monaco } = await diffFixture();
    monaco.reset();

    mod.setDiffOptions('d1', {
      sideBySide: false, ignoreTrimWhitespace: true, overviewRuler: false,
      readOnly: false, originalEditable: true, minimap: true, tabSize: 4, fontSize: 16,
    });

    const options = onlyCall(monaco, 'diff.updateOptions').args[0];
    assert.equal(options.renderSideBySide, false, 'Monaco spells this renderSideBySide');
    assert.equal(options.ignoreTrimWhitespace, true);
    assert.equal(options.renderOverviewRuler, false);
    assert.deepEqual(options.minimap, { enabled: true });
    assert.equal(calls(monaco, 'editor.createDiffEditor').length, 0, 'rebuilding would lose both models');
    assert.equal(calls(monaco, 'model.updateOptions').filter(u => u.args[0].tabSize === 4).length, 2);
  });

  test('language changes apply to both sides', async () => {
    const { mod, monaco } = await diffFixture();
    monaco.reset();

    mod.setDiffLanguage('d1', 'json');

    assert.equal(calls(monaco, 'editor.setModelLanguage').length, 2);
  });

  test('navigation moves the diff and focuses where the user will type', async () => {
    const { mod, monaco } = await diffFixture();
    monaco.reset();

    mod.goToDiff('d1', 'next');
    mod.goToDiff('d1', 'previous');
    mod.goToDiff('d1', 'nonsense');
    mod.revealFirstDiff('d1');

    assert.deepEqual(calls(monaco, 'diff.goToDiff').map(c => c.args[0]),
      ['next', 'previous', 'next'], 'anything not previous means next');
    assert.equal(calls(monaco, 'editor.focus').length, 3, 'focus follows the jump');
    assert.equal(calls(monaco, 'diff.revealFirstDiff').length, 1);
  });

  test('disposal releases the editor before either model', async () => {
    const { mod, monaco } = await diffFixture();
    monaco.reset();

    mod.disposeDiff('d1');

    const order = monaco.log.map(c => c.name);
    const models = order.map((n, i) => (n === 'model.dispose' ? i : -1)).filter(i => i >= 0);

    assert.equal(models.length, 2, 'both sides disposed');
    assert.ok(order.indexOf('diff.dispose') < models[0],
      'a model disposed while still attached leaves the editor reading a dead one');
    assert.equal(calls(monaco, 'diff.onDidUpdateDiff.dispose').length, 1);
  });

  test('unknown ids are no-ops across the whole diff surface', async () => {
    const { mod } = await diffFixture();

    assert.equal(mod.getDiffValue('nope', 'original'), '');
    assert.doesNotThrow(() => mod.setDiffValue('nope', 'original', 'x'));
    assert.doesNotThrow(() => mod.setDiffLanguage('nope', 'json'));
    assert.doesNotThrow(() => mod.setDiffOptions('nope', { sideBySide: true }));
    assert.doesNotThrow(() => mod.goToDiff('nope', 'next'));
    assert.doesNotThrow(() => mod.revealFirstDiff('nope'));
    assert.doesNotThrow(() => mod.diffLayout('nope'));
    assert.doesNotThrow(() => mod.disposeDiff('nope'));
  });
});

describe('stylesheet injection', () => {
  test('the stylesheet is added once however many editors exist', async () => {
    const { mod } = await editorFixture();
    const linksAfterFirst = globalThis.document.__head.children.length;

    mod.create('e2', makeHost(), { baseUrl: 'https://app.example/d', value: '', language: 'yaml' });

    assert.equal(globalThis.document.__head.children.length, linksAfterFirst);
    assert.equal(linksAfterFirst, 1);
  });

  test('the marker attribute does not collide with the host stamp', async () => {
    await editorFixture();
    const [link] = globalThis.document.__head.children;

    // These shared a name once. The link lives in <head> so it sorted first, and looking the editor up
    // by its stamp from the console returned the stylesheet with an empty value instead.
    assert.equal(link.__marker, 'data-pondhawk-monaco-styles');
    assert.notEqual(link.__marker, 'data-pondhawk-editor');
  });
});
