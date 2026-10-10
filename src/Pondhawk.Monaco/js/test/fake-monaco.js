// A stand-in for Monaco, recording what code-editor.js asks of it.
//
// The mirror image of the bUnit tests: there .NET is real and this module is mocked, so they pin what
// .NET SENDS. Here the module is real and Monaco is mocked, pinning what it DOES with what it receives.
// Between them the interop boundary is covered from both sides — the payload tests on the .NET side say
// in as many words that they cannot check the consuming half.
//
// Only the surface code-editor.js actually touches is implemented. Anything it starts using will fail
// loudly here as "not a function" rather than silently doing nothing, which is the point.

export const log = [];

const record = (name, ...args) => log.push({ name, args });

export function reset() {
  log.length = 0;
  editor.__failCreate = null;
  languages.__registered = ['plaintext', 'yaml', 'json', 'css', 'html', 'xml', 'markdown', 'sql', 'csharp'];
}

const disposable = (name) => ({ dispose: () => record(`${name}.dispose`) });

function makeModel(value, language, uri) {
  const model = {
    __kind: 'model',
    uri: uri ?? { toString: () => 'inmemory://model/1' },
    __value: value ?? '',
    __language: language,
    __options: {},
    getValue: () => model.__value,
    getFullModelRange: () => ({ startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 }),
    pushEditOperations(_before, edits, _after) {
      record('model.pushEditOperations', edits);
      model.__value = edits[0].text;
      return null;
    },
    updateOptions(options) {
      record('model.updateOptions', options);
      Object.assign(model.__options, options);
    },
    onDidChangeContent(handler) {
      model.__onChange = handler;
      return disposable('model.onDidChangeContent');
    },
    dispose: () => record('model.dispose', model.__language),
  };
  return model;
}

function makeEditor(host, options) {
  const editor = {
    __kind: 'editor',
    __host: host,
    __options: { ...options },
    __position: { lineNumber: 1, column: 1 },
    __selection: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 1 },
    __focused: false,
    __scrollTop: 0,
    __decorations: null,
    layout: () => record('editor.layout'),
    updateOptions(o) {
      record('editor.updateOptions', o);
      Object.assign(editor.__options, o);
    },
    getPosition: () => editor.__position,
    setPosition(p) { record('editor.setPosition', p); editor.__position = p; },
    getSelection: () => editor.__selection,
    setSelection(s) { record('editor.setSelection', s); editor.__selection = s; },
    focus() { record('editor.focus'); editor.__focused = true; },
    hasTextFocus: () => editor.__focused,
    getScrollTop: () => editor.__scrollTop,
    setScrollTop(n) { record('editor.setScrollTop', n); editor.__scrollTop = n; },
    revealLineInCenter: (n) => record('editor.revealLineInCenter', n),
    // Only ids in __actions are "registered"; everything else falls through to trigger(), as in Monaco.
    __actions: new Set(['editor.action.formatDocument', 'actions.find']),
    getAction: (id) => (editor.__actions.has(id)
      ? { id, run: async () => record('action.run', id) }
      : null),
    trigger(source, id) {
      record('editor.trigger', source, id);
      // Monaco routes an unknown id to its unexpected-error handler, which throws here.
      throw new Error(`command '${id}' not found`);
    },
    createDecorationsCollection() {
      record('editor.createDecorationsCollection');
      const collection = {
        __items: [],
        set(items) { record('decorations.set', items); collection.__items = items; },
        clear() { record('decorations.clear'); collection.__items = []; },
      };
      editor.__decorations = collection;
      return collection;
    },
    dispose: () => record('editor.dispose'),
  };
  return editor;
}

function makeDiffEditor(host, options) {
  const modified = makeEditor(host, options);
  const diff = {
    __kind: 'diffEditor',
    __options: { ...options },
    __models: null,
    setModel(models) { record('diff.setModel', models); diff.__models = models; },
    updateOptions(o) { record('diff.updateOptions', o); Object.assign(diff.__options, o); },
    onDidUpdateDiff(handler) { diff.__onUpdate = handler; return disposable('diff.onDidUpdateDiff'); },
    getLineChanges: () => diff.__lineChanges ?? [],
    goToDiff: (target) => record('diff.goToDiff', target),
    getModifiedEditor: () => modified,
    revealFirstDiff: () => record('diff.revealFirstDiff'),
    layout: () => record('diff.layout'),
    dispose: () => record('diff.dispose'),
  };
  return diff;
}

// Top level, NOT under `editor` — Monaco exports MarkerSeverity alongside Uri and Range, while
// OverviewRulerLane sits inside the editor namespace. Putting either in the wrong place would make the
// tests agree with a Monaco that does not exist.
export const MarkerSeverity = { Hint: 1, Info: 2, Warning: 4, Error: 8 };

// Top level in Monaco, alongside MarkerSeverity. Only parse/toString are used.
export const Uri = { parse: (value) => ({ toString: () => value, __uri: value }) };

export const editor = {
  OverviewRulerLane: { Left: 1, Center: 2, Right: 4, Full: 7 },

  createModel(value, language, uri) {
    record('editor.createModel', value, language, uri?.toString());
    return makeModel(value, language, uri);
  },
  /** When set, the next create / createDiffEditor throws it — Monaco failing for a reason of its own. */
  __failCreate: null,
  create(host, options) {
    record('editor.create', options);
    if (editor.__failCreate) throw editor.__failCreate;
    return makeEditor(host, options);
  },
  createDiffEditor(host, options) {
    record('editor.createDiffEditor', options);
    if (editor.__failCreate) throw editor.__failCreate;
    return makeDiffEditor(host, options);
  },
  setModelLanguage: (model, language) => record('editor.setModelLanguage', language),
  setModelMarkers: (model, owner, markers) => record('editor.setModelMarkers', owner, markers),
  setTheme: (theme) => record('editor.setTheme', theme),
  defineTheme: (name, theme) => record('editor.defineTheme', name, theme),
};

export const languages = {
  __registered: [],
  // What the module registers itself, at load — before a test's reset(), so kept apart from __registered.
  __own: [],
  __grammars: {},
  register: ({ id }) => languages.__own.push(id),
  setMonarchTokensProvider: (id, grammar) => { languages.__grammars[id] = grammar; },
  __onLanguage: {},
  onLanguage: (id, callback) => { languages.__onLanguage[id] = callback; },
  getLanguages: () => [...languages.__registered, ...languages.__own].map(id => ({ id })),
  json: {
    jsonDefaults: {
      setDiagnosticsOptions: (o) => record('json.setDiagnosticsOptions', o),
    },
  },
};

reset();
