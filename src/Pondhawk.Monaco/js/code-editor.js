// The JS half of the CodeEditor component.
//
// DESIGN: JavaScript owns the document. Blazor is told about changes on a debounce, and pushes text back
// only when the change did not originate here. Round-tripping every keystroke across interop is the
// mistake that makes embedded editors feel laggy — in Blazor Server it is a network hop per character.

// Monaco is imported one contribution at a time rather than through the `monaco-editor` barrel. The
// barrel is editor.main.js, which is nothing but five side-effect imports — every language service and
// all 83 highlighting grammars — plus `export * from './edcore.main'`. Importing edcore.main directly
// keeps the whole editor (find, folding, suggest, quick access) and drops only what is not listed below.
//
// The set is deliberate, not incidental. Carrying Monaco's full language set cost 3.3 MB packaged, of
// which 5.7 MB uncompressed was the TypeScript compiler in ts.worker.js. Trimming to the languages this
// component actually claims to support takes the package to 2.0 MB.
//
// ADDING A LANGUAGE means an import here, plus its worker in build.mjs if it has a language service.
// Nothing else: checkLanguage() below reads Monaco's own registry, so it cannot fall out of step.
import * as monaco from 'monaco-editor/esm/vs/editor/edcore.main';

// Language SERVICES — completion, diagnostics, hover. Each runs in its own worker.
import 'monaco-editor/esm/vs/language/json/monaco.contribution';
import 'monaco-editor/esm/vs/language/css/monaco.contribution';
import 'monaco-editor/esm/vs/language/html/monaco.contribution';

// Highlighting, and — for css and html — the language REGISTRATION their services depend on.
//
// The three service contributions above are not symmetric, which is a trap worth spelling out. JSON's
// calls languages.register() itself, so it stands alone. CSS's and HTML's do not: they only attach via
// languages.onLanguage(id, …), and the register() call for those ids lives here in basic-languages. Import
// the service without this and the id is never registered, so the hook never fires — the language does not
// merely lose completion, it does not exist, and documents render as unhighlighted plain text.
import 'monaco-editor/esm/vs/basic-languages/css/css.contribution';
import 'monaco-editor/esm/vs/basic-languages/html/html.contribution';

// Highlighting only. Monaco ships no language service for these, in the full bundle either — XML, SQL,
// Markdown and C# were always colours and bracket matching, never completion.
import 'monaco-editor/esm/vs/basic-languages/yaml/yaml.contribution';
import 'monaco-editor/esm/vs/basic-languages/xml/xml.contribution';
import 'monaco-editor/esm/vs/basic-languages/markdown/markdown.contribution';
import 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution';
import 'monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution';

import { configureMonacoYaml } from 'monaco-yaml';

// Highlighting Monaco does not ship: our own grammars, registered when the module loads.
import { register as registerStackTrace } from './stacktrace.js';
registerStackTrace(monaco);

/** id -> { editor, model, dotNet, revision, changeTimer, subscriptions } */
const editors = new Map();

/** id -> { editor, original, modified, dotNet, revision, changeTimer, subscriptions } */
const diffs = new Map();

let workersReady = false;
let yamlConfigured = null;

/**
 * id -> { schema, fileMatch }. Monaco's JSON and YAML schema configuration is PAGE-GLOBAL — one call
 * replaces the lot — so a per-editor Schema parameter can only work if every editor's schema is held
 * here and the whole set is re-applied together. Configuring one editor used to overwrite the others,
 * leaving every document on the page validating against whichever rendered last.
 */
const schemas = new Map();

/**
 * The query that pins an asset URL to the release that shipped it. The paths never change between
 * releases, so without it a browser holding the previous release's stylesheet or workers keeps using them
 * after an upgrade. .NET versions the module import itself the same way.
 */
function versionQuery(assetVersion) {
  return assetVersion ? `?v=${encodeURIComponent(assetVersion)}` : '';
}

/**
 * Point Monaco's worker loader at our own assets. The base path is supplied by .NET rather than guessed:
 * it differs between hosting models and base-href configurations, and a wrong guess fails only at the
 * moment the language service is first needed — long after startup, where it is hard to diagnose.
 */
function configureWorkers(baseUrl, assetVersion) {
  if (workersReady) return;

  const url = name => `${baseUrl.replace(/\/$/, '')}/${name}.worker.js${versionQuery(assetVersion)}`;

  // Monaco dispatches by language LABEL, and several languages share one worker. A label routed to the
  // wrong worker does not throw — it silently produces no completions or diagnostics for that language,
  // so the mapping has to cover every service actually bundled.
  //
  // No typescript/javascript entry: that service is not bundled, so Monaco never asks for its worker.
  const byLabel = {
    json: 'json',
    css: 'css', scss: 'css', less: 'css',
    html: 'html', handlebars: 'html', razor: 'html',
    yaml: 'yaml',
  };

  self.MonacoEnvironment = {
    getWorker(_moduleId, label) {
      return new Worker(url(byLabel[label] ?? 'editor'));
    },
  };

  workersReady = true;
}

/**
 * Warn when a document asks for a language this build does not carry.
 *
 * Monaco's own behaviour here is to fall back to plaintext in silence: no error, no diagnostic, just an
 * unhighlighted document. Since this build ships a deliberately reduced language set, that silence would
 * read as "the editor is broken" rather than "that language was not bundled". One console warning per
 * unknown id turns it into something diagnosable — non-fatal, because a typo in a language id should not
 * take out the page.
 */
const warnedLanguages = new Set();

function checkLanguage(language) {
  if (!language || warnedLanguages.has(language)) return;

  // Monaco is the authority — it knows what the imports above actually registered, so this cannot drift
  // from the bundle the way a hand-maintained list would.
  if (monaco.languages.getLanguages().some(l => l.id === language)) return;

  warnedLanguages.add(language);
  // Deduplicated: an id can be registered twice — 'yaml' comes from both basic-languages and monaco-yaml.
  const supported = [...new Set(monaco.languages.getLanguages().map(l => l.id))].sort().join(', ');
  console.warn(
    `[pondhawk-monaco] Language '${language}' is not bundled in this build; ` +
    `the document will render as plain text. Bundled languages: ${supported}.`);
}

/**
 * Register a JSON Schema for both YAML (monaco-yaml) and JSON (Monaco's built-in service).
 * Called again whenever the schema changes; monaco-yaml's configure returns a disposable we replace.
 */
export function configureSchema(id, schemaJson, fileMatch) {
  const schema = typeof schemaJson === 'string' ? JSON.parse(schemaJson) : schemaJson;

  // Default scope is this editor's own document, addressed by its model URI. The old default of ['*']
  // meant every schema claimed every document, so the last one configured won the page.
  const own = editors.get(id)?.model.uri.toString();
  const match = fileMatch?.length ? fileMatch : (own ? [own] : ['*']);

  schemas.set(id, { schema, fileMatch: match });
  applySchemas();
}

/**
 * Re-applies every live editor's schema as one set. Each gets a distinct schema URI keyed by editor id,
 * because the services index by that URI — a shared key would collapse them back into one.
 */
function applySchemas() {
  const all = [...schemas.entries()].map(([id, entry]) => ({
    uri: `https://pondhawk.local/schema/${id}.json`,
    fileMatch: entry.fileMatch,
    schema: entry.schema,
  }));

  monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
    validate: true,
    enableSchemaRequest: false,
    // A document that breaks its schema is wrong, not questionable. Monaco's default is 'warning',
    // which drew a yellow squiggle under a string where the schema says integer -- beside the red
    // one a stray comma gets -- and read as advice a caller was free to ignore.
    schemaValidation: 'error',
    schemas: all,
  });

  yamlConfigured?.dispose();
  yamlConfigured = configureMonacoYaml(yamlMonaco, {
    enableSchemaRequest: false,
    validate: true,
    format: true,
    hover: true,
    completion: true,
    schemas: all,
  });
}

/**
 * Monaco as monaco-yaml sees it: the same in every respect except that the markers it sets are raised
 * from warning to error.
 *
 * The YAML language server reports a document that breaks its schema as a warning, and unlike Monaco's
 * JSON service (see schemaValidation above) monaco-yaml offers no setting for it. Its syntax errors are
 * already errors, and its warnings are its schema's, so raising them makes a schema problem the same red
 * squiggle in both formats. monaco-yaml sets markers through the Monaco it is handed, so the change is
 * confined to its own -- the global Monaco, and every other language's markers, are untouched.
 */
const yamlMonaco = {
  ...monaco,
  editor: {
    ...monaco.editor,
    setModelMarkers(model, owner, markers) {
      monaco.editor.setModelMarkers(model, owner, markers.map(m =>
        m.severity === monaco.MarkerSeverity.Warning ? { ...m, severity: monaco.MarkerSeverity.Error } : m));
    },
  },
};

/**
 * Inject Monaco's stylesheet ourselves. esbuild extracts it from the ESM imports into a separate file,
 * so without this every consumer would have to remember a <link> in index.html — and the failure mode
 * (an unstyled, unusable editor) gives no hint as to why.
 */
function ensureStyles(baseUrl, assetVersion) {
  const href = `${baseUrl.replace(/\/$/, '')}/code-editor.css${versionQuery(assetVersion)}`;

  // A DIFFERENT attribute from the data-pondhawk-editor stamp on the host element. They used to share
  // one name, and since this link lives in <head> it sorted first — so querySelector('[data-pondhawk-
  // editor]') from the devtools console returned the stylesheet with an empty value, defeating the one
  // thing that stamp exists for.
  if (document.querySelector('link[data-pondhawk-monaco-styles]')) return;

  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = href;
  link.setAttribute('data-pondhawk-monaco-styles', '');
  document.head.appendChild(link);
}

/**
 * Makes an editor over models already created, disposing those models if Monaco throws — they belong to no
 * editor then, and nothing else would release them. The error still propagates: a failure other than a
 * detached host is real and must be seen.
 */
function createOrRelease(make, models) {
  try {
    return make();
  } catch (error) {
    models.forEach(m => m.dispose());
    throw error;
  }
}

export function create(id, host, options) {
  // A host already taken off the page: the component was removed between rendering it and this call —
  // a list moved on quickly, a tab closed. Nothing to create, and not an error: the component is disposed
  // next, and dispose(id) ignores an id that was never created. Monaco would throw inside create(), and
  // the exception would surface as an unhandled render error for the whole page.
  if (!host?.isConnected) return false;

  ensureStyles(options.baseUrl, options.assetVersion);
  configureWorkers(options.baseUrl, options.assetVersion);
  dispose(id); // defensive: a re-render that recreated the host must not leak the previous editor

  checkLanguage(options.language);

  // An explicit URI, not Monaco's generated one: it is what a schema's fileMatch targets, so without it
  // a schema cannot be scoped to a single editor. Keyed by the registry id, so it is unique per editor.
  const language = options.language ?? 'plaintext';
  const model = monaco.editor.createModel(
    options.value ?? '', language, monaco.Uri.parse(`inmemory://pondhawk/${id}.${language}`));

  // tabSize is a MODEL option, not an editor option. Passing it to create() sets it on the model Monaco
  // would have created for itself — and we supply our own, so it is dropped silently.
  model.updateOptions({ tabSize: options.tabSize ?? 2 });

  const editor = createOrRelease(() => monaco.editor.create(host, {
    // Defaults chosen to be sensible for source editing generally, then overridden by whatever the
    // caller passes. `editorOptions` is a raw Monaco IStandaloneEditorConstructionOptions bag so this
    // component never becomes the bottleneck on Monaco's option surface.
    minimap: { enabled: options.minimap ?? false },
    scrollBeyondLastLine: false,
    tabSize: options.tabSize ?? 2,

    // Off, or TabSize is advisory at best. Monaco guesses indentation from the document on attach and
    // overwrites the model's tabSize with what it found — so an explicit TabSize=8 silently became 2 on
    // any 2-space-indented file. A component that exposes the setting has to mean it.
    detectIndentation: false,

    renderWhitespace: 'selection',
    fontSize: options.fontSize ?? 12.5,
    fixedOverflowWidgets: true,
    theme: options.theme ?? 'vs',
    readOnly: options.readOnly ?? false,
    ...(options.editorOptions ?? {}),

    // Not overridable. The model is ours to manage, and Monaco's built-in ResizeObserver feeds back
    // inside overflow:hidden containers — layout is driven deliberately from .NET instead.
    model,
    automaticLayout: false,
  }), [model]);

  const entry = {
    editor, model, dotNet: null, revision: 0, changeTimer: 0, subscriptions: [],
    // Created lazily by setDecorations. A collection owns its own ids, so replacing the set is one
    // call and nothing has to carry decoration ids across the interop boundary.
    decorations: null,
  };
  editors.set(id, entry);

  // Stamp the registry key onto the host. Nothing reads it at runtime — it exists so a live page can be
  // inspected from the devtools console, where the id is otherwise unreachable.
  host.dataset.pondhawkEditor = id;

  entry.subscriptions.push(model.onDidChangeContent(() => {
    entry.revision++;
    if (!entry.dotNet) return;

    clearTimeout(entry.changeTimer);
    entry.changeTimer = setTimeout(() => {
      // Send the revision so .NET can tell its own echo apart from a genuine user edit.
      entry.dotNet.invokeMethodAsync('OnDocumentChanged', model.getValue(), entry.revision);
    }, options.debounceMs ?? 300);
  }));

  editor.layout();
  return true;
}

export function attach(id, dotNetRef) {
  const entry = editors.get(id);
  if (entry) entry.dotNet = dotNetRef;
}

export function getValue(id) {
  return editors.get(id)?.model.getValue() ?? '';
}

/**
 * Push text in from .NET. No-op when the value already matches, which is what stops the caret from
 * jumping when .NET echoes back the value it was just handed.
 */
export function setValue(id, value) {
  const entry = editors.get(id);
  if (!entry || entry.model.getValue() === value) return;

  // pushEditOperations rather than setValue: preserves undo history and cursor position.
  entry.model.pushEditOperations(
    [],
    [{ range: entry.model.getFullModelRange(), text: value }],
    () => null);
}

export function setLanguage(id, language) {
  checkLanguage(language);
  const entry = editors.get(id);
  if (entry) monaco.editor.setModelLanguage(entry.model, language);
}

/**
 * Apply option changes to a live editor. Every option this component exposes goes through here after
 * construction, so a parameter that changes at runtime actually takes effect — previously they were
 * passed to create() and never revisited, which made ReadOnly and friends look live when they were not.
 *
 * updateOptions is Monaco's supported path for this: it diffs internally and preserves the model, the
 * scroll position and the undo stack, where recreating the editor would lose all three.
 */
export function updateOptions(id, options) {
  const entry = editors.get(id);
  if (!entry) return;

  entry.editor.updateOptions({
    minimap: { enabled: options.minimap ?? false },
    fontSize: options.fontSize ?? 12.5,
    readOnly: options.readOnly ?? false,
    detectIndentation: false,   // see create(): detection would overwrite tabSize below
    ...(options.editorOptions ?? {}),
  });

  // Model option, not an editor one — see create().
  entry.model.updateOptions({ tabSize: options.tabSize ?? 2 });
}

// --- Cursor, selection and focus ---------------------------------------------------------------------
//
// Read/write only. There is deliberately no cursor-moved EVENT: it fires on every arrow key, and under
// Blazor Server that is a network hop per keystroke — the same reasoning that put a debounce on content
// changes. A host that needs live cursor tracking should ask for it, so it can be debounced on purpose.

/** 1-based, matching Monaco and EditorMarker. Null when the editor has no cursor yet. */
export function getPosition(id) {
  const p = editors.get(id)?.editor.getPosition();
  return p ? { line: p.lineNumber, column: p.column } : null;
}

export function setPosition(id, line, column) {
  const entry = editors.get(id);
  if (!entry) return;

  entry.editor.setPosition({ lineNumber: line, column: column ?? 1 });
}

export function getSelection(id) {
  const s = editors.get(id)?.editor.getSelection();
  return s ? {
    startLine: s.startLineNumber, startColumn: s.startColumn,
    endLine: s.endLineNumber, endColumn: s.endColumn,
  } : null;
}

export function setSelection(id, selection) {
  const entry = editors.get(id);
  if (!entry) return;

  entry.editor.setSelection({
    startLineNumber: selection.startLine, startColumn: selection.startColumn,
    endLineNumber: selection.endLine, endColumn: selection.endColumn,
  });
}

export function focus(id) {
  editors.get(id)?.editor.focus();
}

export function hasTextFocus(id) {
  return editors.get(id)?.editor.hasTextFocus() ?? false;
}

/**
 * Run a built-in Monaco action by id — 'editor.action.formatDocument', 'actions.find',
 * 'editor.action.commentLine' — which is the whole of Monaco's command surface for one method.
 *
 * Returns whether the id named a REGISTERED action. Some built-ins (undo, redo) are commands rather than
 * actions and are only reachable through trigger(), which reports nothing back; those fall through and
 * return false. The return value therefore means "Monaco knew this as an action", which is what makes a
 * mistyped id visible instead of a silent no-op.
 */
export async function runAction(id, actionId) {
  const entry = editors.get(id);
  if (!entry) return false;

  const action = entry.editor.getAction(actionId);
  if (action) {
    await action.run();
    return true;
  }

  // Not a registered action. Some built-ins (undo, redo) are commands rather than actions and are only
  // reachable through trigger(). Monaco routes an unknown id to its unexpected-error handler, which
  // prints a stack trace — so catch it and say the useful thing instead.
  try {
    entry.editor.trigger('pondhawk', actionId, null);
  } catch {
    console.warn(`[pondhawk-monaco] '${actionId}' is not a Monaco action or command; nothing ran.`);
  }

  return false;
}

/**
 * Replace OUR diagnostics without touching the language service's own. The owner key namespaces them,
 * so schema errors from monaco-yaml and rule violations from the host app coexist.
 */
export function setMarkers(id, markers) {
  const entry = editors.get(id);
  if (!entry) return;

  monaco.editor.setModelMarkers(entry.model, 'pondhawk', (markers ?? []).map(m => ({
    startLineNumber: m.startLine, startColumn: m.startColumn,
    endLineNumber: m.endLine, endColumn: m.endColumn,
    message: m.message,
    severity: m.severity === 'warning'
      ? monaco.MarkerSeverity.Warning
      : m.severity === 'info' ? monaco.MarkerSeverity.Info : monaco.MarkerSeverity.Error,
    source: m.source,
  })));
}

/**
 * Replace the decoration set — line highlights, glyph-margin icons, inline styling.
 *
 * Decorations are what markers are not: styling rather than diagnostics. Search hits, merge conflict
 * regions, coverage gutters, blame lines. Monaco addresses them by generated id, but a decorations
 * COLLECTION owns its own ids, so .NET can stay declarative and hand over the whole set each time —
 * matching how Diagnostics already works, rather than making the host track ids across interop.
 */
export function setDecorations(id, decorations) {
  const entry = editors.get(id);
  if (!entry) return;

  entry.decorations ??= entry.editor.createDecorationsCollection();

  // Monaco's glyph margin is off by default, and a glyph decoration drawn into a margin that is not
  // there renders nothing at all, with no error. Asking for a glyph is asking for somewhere to put it.
  // Only ever turned ON: switching it back off would fight a caller who enabled it via EditorOptions.
  if ((decorations ?? []).some(d => d.glyphMarginClassName)) {
    entry.editor.updateOptions({ glyphMargin: true });
  }

  entry.decorations.set((decorations ?? []).map(d => ({
    range: {
      startLineNumber: d.startLine, startColumn: d.startColumn,
      endLineNumber: d.endLine, endColumn: d.endColumn,
    },
    options: {
      // Every field is optional; Monaco ignores the ones left undefined.
      className: d.className ?? undefined,
      inlineClassName: d.inlineClassName ?? undefined,
      glyphMarginClassName: d.glyphMarginClassName ?? undefined,
      linesDecorationsClassName: d.lineNumberClassName ?? undefined,
      isWholeLine: d.wholeLine ?? false,
      hoverMessage: d.hoverMessage ? { value: d.hoverMessage } : undefined,
      overviewRuler: d.overviewRulerColor
        ? { color: d.overviewRulerColor, position: monaco.editor.OverviewRulerLane.Right }
        : undefined,
    },
  })));
}

export function getScrollTop(id) {
  return editors.get(id)?.editor.getScrollTop() ?? 0;
}

export function setScrollTop(id, scrollTop) {
  editors.get(id)?.editor.setScrollTop(scrollTop);
}

/**
 * Register a custom theme. Monaco themes are GLOBAL — defining one affects every editor on the page,
 * and so does selecting it, which is Monaco's design rather than a choice made here.
 *
 * Colour formats are the trap. Rule colours must be bare hex with NO leading '#', while the `colors`
 * map requires one; Monaco throws on the wrong form rather than ignoring it. Both are normalised here
 * so a caller can write '#264f78' everywhere and have it work.
 */
export function defineTheme(theme) {
  const bare = c => (typeof c === 'string' ? c.replace(/^#/, '') : c);
  const hashed = c => (typeof c === 'string' && !c.startsWith('#') ? `#${c}` : c);

  monaco.editor.defineTheme(theme.name, {
    base: theme.base ?? 'vs',
    inherit: theme.inherit ?? true,
    rules: (theme.rules ?? []).map(r => ({
      token: r.token,
      foreground: r.foreground ? bare(r.foreground) : undefined,
      background: r.background ? bare(r.background) : undefined,
      fontStyle: r.fontStyle ?? undefined,
    })),
    colors: Object.fromEntries(
      Object.entries(theme.colors ?? {}).map(([k, v]) => [k, hashed(v)])),
  });
}

export function revealLine(id, lineNumber, column) {
  const entry = editors.get(id);
  if (!entry) return;
  entry.editor.revealLineInCenter(lineNumber);
  entry.editor.setPosition({ lineNumber, column: column ?? 1 });
  entry.editor.focus();
}

export function layout(id) {
  editors.get(id)?.editor.layout();
}

export function setTheme(theme) {
  monaco.editor.setTheme(theme);
}

export function dispose(id) {
  const entry = editors.get(id);
  if (!entry) return;

  clearTimeout(entry.changeTimer);
  entry.subscriptions.forEach(s => s.dispose());
  entry.decorations?.clear();
  entry.editor.dispose();
  entry.model.dispose();

  // Retract this editor's schema. Left behind it would keep claiming documents by a fileMatch pointing
  // at a model that no longer exists.
  if (schemas.delete(id)) applySchemas();
  // The DotNetObjectReference is disposed on the .NET side; dropping it here only releases our handle.
  editors.delete(id);
}

// ---------------------------------------------------------------------------------------------------
// Diff editor
//
// A separate registry rather than a flag on the entries above: a diff holds two models, its disposal
// order differs, and sharing one map would mean every function guarding against the other kind.
// ---------------------------------------------------------------------------------------------------

export function createDiff(id, host, options) {
  if (!host?.isConnected) return false;   // see create()

  ensureStyles(options.baseUrl, options.assetVersion);
  configureWorkers(options.baseUrl, options.assetVersion);
  disposeDiff(id); // defensive, matching create()

  checkLanguage(options.language);
  const language = options.language ?? 'plaintext';
  const original = monaco.editor.createModel(options.original ?? '', language);
  const modified = monaco.editor.createModel(options.modified ?? '', language);

  const editor = createOrRelease(() => monaco.editor.createDiffEditor(host, {
    minimap: { enabled: options.minimap ?? false },
    scrollBeyondLastLine: false,
    tabSize: options.tabSize ?? 2,
    renderWhitespace: 'selection',
    fontSize: options.fontSize ?? 12.5,
    fixedOverflowWidgets: true,
    theme: options.theme ?? 'vs',
    detectIndentation: false,   // see create(): detection would overwrite tabSize

    // Diff-specific. `readOnly` governs the MODIFIED side; the original is separately locked, because
    // the common case — reviewing what changed — wants the left side immutable even when the right is
    // being edited.
    readOnly: options.readOnly ?? false,
    originalEditable: options.originalEditable ?? false,
    renderSideBySide: options.sideBySide ?? true,
    ignoreTrimWhitespace: options.ignoreTrimWhitespace ?? false,
    renderOverviewRuler: options.overviewRuler ?? true,
    ...(options.editorOptions ?? {}),

    automaticLayout: false, // same reasoning as create(): layout is driven from .NET
  }), [original, modified]);

  editor.setModel({ original, modified });
  host.dataset.pondhawkEditor = id;   // see create()

  // Model option on BOTH sides — see create() for why passing it to the constructor is not enough.
  original.updateOptions({ tabSize: options.tabSize ?? 2 });
  modified.updateOptions({ tabSize: options.tabSize ?? 2 });

  const entry = { editor, original, modified, dotNet: null, revision: 0, changeTimer: 0, subscriptions: [] };
  diffs.set(id, entry);

  // Only the modified side is reported back. An editable original is supported, but it is a source
  // document being compared against, not the value the host is binding to.
  entry.subscriptions.push(modified.onDidChangeContent(() => {
    entry.revision++;
    if (!entry.dotNet) return;

    clearTimeout(entry.changeTimer);
    entry.changeTimer = setTimeout(() => {
      entry.dotNet.invokeMethodAsync('OnModifiedChanged', modified.getValue(), entry.revision);
    }, options.debounceMs ?? 300);
  }));

  // The diff is computed asynchronously, so the change count is only knowable via this event — reading
  // getLineChanges() straight after setModel returns null.
  entry.subscriptions.push(editor.onDidUpdateDiff(() => {
    entry.dotNet?.invokeMethodAsync('OnDiffUpdated', editor.getLineChanges()?.length ?? 0);
  }));

  editor.layout();
  return true;
}

export function attachDiff(id, dotNetRef) {
  const entry = diffs.get(id);
  if (entry) entry.dotNet = dotNetRef;
}

export function getDiffValue(id, side) {
  const entry = diffs.get(id);
  if (!entry) return '';
  return (side === 'original' ? entry.original : entry.modified).getValue();
}

/** Push text into one side. No-ops on an unchanged value — the same caret guard as setValue(). */
export function setDiffValue(id, side, value) {
  const entry = diffs.get(id);
  if (!entry) return;

  const model = side === 'original' ? entry.original : entry.modified;
  if (model.getValue() === value) return;

  model.pushEditOperations([], [{ range: model.getFullModelRange(), text: value }], () => null);
}

export function setDiffLanguage(id, language) {
  checkLanguage(language);
  const entry = diffs.get(id);
  if (!entry) return;

  monaco.editor.setModelLanguage(entry.original, language);
  monaco.editor.setModelLanguage(entry.modified, language);
}

/**
 * Change options on a live diff — side-by-side vs inline, whitespace handling, appearance — without
 * rebuilding the editor, which would lose both models, the scroll position and the undo stack.
 */
export function setDiffOptions(id, options) {
  const entry = diffs.get(id);
  if (!entry) return;

  entry.editor.updateOptions({
    renderSideBySide: options.sideBySide,
    ignoreTrimWhitespace: options.ignoreTrimWhitespace,
    renderOverviewRuler: options.overviewRuler,
    readOnly: options.readOnly,
    originalEditable: options.originalEditable,
    minimap: { enabled: options.minimap ?? false },
    fontSize: options.fontSize ?? 12.5,
    ...(options.editorOptions ?? {}),
  });

  entry.original.updateOptions({ tabSize: options.tabSize ?? 2 });
  entry.modified.updateOptions({ tabSize: options.tabSize ?? 2 });
}

/** Jump to the next or previous change. `target` is 'next' or 'previous'. */
export function goToDiff(id, target) {
  const entry = diffs.get(id);
  if (!entry) return;

  entry.editor.goToDiff(target === 'previous' ? 'previous' : 'next');
  entry.editor.getModifiedEditor().focus();
}

/** Scroll to the first change. Waits internally for the diff computation to finish. */
export function revealFirstDiff(id) {
  diffs.get(id)?.editor.revealFirstDiff();
}

export function diffLayout(id) {
  diffs.get(id)?.editor.layout();
}

export function disposeDiff(id) {
  const entry = diffs.get(id);
  if (!entry) return;

  clearTimeout(entry.changeTimer);
  entry.subscriptions.forEach(s => s.dispose());

  // The editor first: disposing a model still attached to a live editor leaves it reading a dead model.
  entry.editor.dispose();
  entry.original.dispose();
  entry.modified.dispose();
  diffs.delete(id);
}
