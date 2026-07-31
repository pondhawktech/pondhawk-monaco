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

/** id -> { editor, model, dotNet, revision, changeTimer, subscriptions } */
const editors = new Map();

/** id -> { editor, original, modified, dotNet, revision, changeTimer, subscriptions } */
const diffs = new Map();

let workersReady = false;
let yamlConfigured = null;

/**
 * Point Monaco's worker loader at our own assets. The base path is supplied by .NET rather than guessed:
 * it differs between hosting models and base-href configurations, and a wrong guess fails only at the
 * moment the language service is first needed — long after startup, where it is hard to diagnose.
 */
function configureWorkers(baseUrl) {
  if (workersReady) return;

  const url = name => `${baseUrl.replace(/\/$/, '')}/${name}.worker.js`;

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
export function configureSchema(schemaJson, fileMatch) {
  const schema = typeof schemaJson === 'string' ? JSON.parse(schemaJson) : schemaJson;
  const match = fileMatch?.length ? fileMatch : ['*'];
  const uri = 'https://pondhawk.local/schema.json';

  monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
    validate: true,
    enableSchemaRequest: false,
    schemas: [{ uri, fileMatch: match, schema }],
  });

  yamlConfigured?.dispose();
  yamlConfigured = configureMonacoYaml(monaco, {
    enableSchemaRequest: false,
    validate: true,
    format: true,
    hover: true,
    completion: true,
    schemas: [{ uri, fileMatch: match, schema }],
  });
}

/**
 * Inject Monaco's stylesheet ourselves. esbuild extracts it from the ESM imports into a separate file,
 * so without this every consumer would have to remember a <link> in index.html — and the failure mode
 * (an unstyled, unusable editor) gives no hint as to why.
 */
function ensureStyles(baseUrl) {
  const href = `${baseUrl.replace(/\/$/, '')}/code-editor.css`;
  if (document.querySelector(`link[data-pondhawk-editor]`)) return;

  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = href;
  link.setAttribute('data-pondhawk-editor', '');
  document.head.appendChild(link);
}

export function create(id, host, options) {
  ensureStyles(options.baseUrl);
  configureWorkers(options.baseUrl);
  dispose(id); // defensive: a re-render that recreated the host must not leak the previous editor

  checkLanguage(options.language);
  const model = monaco.editor.createModel(options.value ?? '', options.language ?? 'plaintext');

  const editor = monaco.editor.create(host, {
    // Defaults chosen to be sensible for source editing generally, then overridden by whatever the
    // caller passes. `editorOptions` is a raw Monaco IStandaloneEditorConstructionOptions bag so this
    // component never becomes the bottleneck on Monaco's option surface.
    minimap: { enabled: options.minimap ?? false },
    scrollBeyondLastLine: false,
    tabSize: options.tabSize ?? 2,
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
  });

  const entry = { editor, model, dotNet: null, revision: 0, changeTimer: 0, subscriptions: [] };
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
  entry.editor.dispose();
  entry.model.dispose();
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
  ensureStyles(options.baseUrl);
  configureWorkers(options.baseUrl);
  disposeDiff(id); // defensive, matching create()

  checkLanguage(options.language);
  const language = options.language ?? 'plaintext';
  const original = monaco.editor.createModel(options.original ?? '', language);
  const modified = monaco.editor.createModel(options.modified ?? '', language);

  const editor = monaco.editor.createDiffEditor(host, {
    minimap: { enabled: options.minimap ?? false },
    scrollBeyondLastLine: false,
    tabSize: options.tabSize ?? 2,
    renderWhitespace: 'selection',
    fontSize: options.fontSize ?? 12.5,
    fixedOverflowWidgets: true,
    theme: options.theme ?? 'vs',

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
  });

  editor.setModel({ original, modified });
  host.dataset.pondhawkEditor = id;   // see create()

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

/** Change view options — side-by-side vs inline, whitespace handling — without rebuilding the editor. */
export function setDiffOptions(id, options) {
  const entry = diffs.get(id);
  if (!entry) return;

  entry.editor.updateOptions({
    renderSideBySide: options.sideBySide,
    ignoreTrimWhitespace: options.ignoreTrimWhitespace,
    readOnly: options.readOnly,
    originalEditable: options.originalEditable,
  });
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
