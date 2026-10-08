// The `stacktrace` language: highlighting for exception stack traces, which Monaco has no grammar for.
//
// It reads the two shapes applications actually log: .NET's Exception.ToString() — the exception, its
// inner exceptions, and frames with their file and line — and Node's error.stack. Also the composite
// Pondhawk.Logging sends when context is attached to an error: a "--- Context ---" banner, that context
// as JSON (highlighted as JSON), then an "--- Exception ---" banner and the trace.
//
// DESIGN: narrow on purpose. A payload marked as text may be a trace or may be anything, so only lines
// with a trace's shape are coloured — an exception name ending in Exception or Error, a frame starting
// with "at", the runtime's own markers. Everything else stays plain, so prose renders as plain text.
//
// Every capture group must take part in the match, if only as an empty string: Monarch reads an optional
// group that did not match as a crash, not as an empty token — hence ((?:x)?), never (x)?.
//
// The tokens are ones Monaco's built-in themes (vs, vs-dark) already colour, so a trace reads well with
// no custom theme. Framework and library frames are dimmed so the application's own frames stand out;
// the only grey both built-in themes share is operator.sql's, hence that otherwise odd token name.
//
// Kept free of a Monaco import so the grammar can be tested against Monaco's real Monarch tokenizer.

export const id = 'stacktrace';

const dim = 'operator.sql';

export const language = {
  tokenPostfix: '.stacktrace',
  defaultToken: '',

  // A type name ending in Exception or Error: System.InvalidOperationException, TypeError, Error.
  exception: /(?:[A-Za-z_$][\w$`+.]*)?(?:Exception|Error)/,

  tokenizer: {
    root: [
      // Pondhawk.Logging's error-with-context payload: the context between the banners is JSON.
      [/^--- Context -+\s*$/, { token: 'comment', next: '@context', nextEmbedded: 'json' }],
      [/^--- Exception -+\s*$/, 'comment'],

      // The runtime's markers: "--- End of inner exception stack trace ---", "--- End of stack trace from
      // previous location ---", and the "<---" closing an AggregateException's inner exception.
      [/^\s*--- End of .*$/, 'comment'],
      [/^\s*<---\s*$/, 'comment'],

      // An inner exception: " ---> System.Exception: message", or "---> (Inner Exception #0) ..." in an
      // AggregateException.
      [/^(\s*)(--->)(\s*)((?:\(Inner Exception #\d+\)\s*)?)(@exception)(:)(.*)$/,
        ['', 'keyword.flow', '', 'comment', 'type', 'delimiter', 'strong']],
      [/^(\s*)(--->)(\s*)((?:\(Inner Exception #\d+\)\s*)?)(@exception)(\s*)$/,
        ['', 'keyword.flow', '', 'comment', 'type', '']],

      // The exception: "System.InvalidOperationException: message", "TypeError: message".
      [/^(\s*)(@exception)(:)(.*)$/, ['', 'type', 'delimiter', 'strong']],
      [/^(\s*)(@exception)(\s*)$/, ['', 'type', '']],

      // Framework and library frames, dimmed whole.
      [/^\s+at\s+(?:System|Microsoft|Windows)\..*$/, dim],
      [/^\s+at\s+.*(?:node:internal|node_modules[\\/]).*$/, dim],

      // .NET: "at Namespace.Type.Method(Int32 id) in /src/File.cs:line 42", the location optional.
      [/^(\s+)(at)(\s+)(.*?)([^.\s(]+)(\(.*\))(\s+in\s+)(.+?)(:line\s+)(\d+)(\s*)$/,
        ['', 'keyword', '', '', 'strong', '', 'keyword', 'string', 'keyword', 'number', '']],
      [/^(\s+)(at)(\s+)(.*?)([^.\s(]+)(\(.*\))(\s*)$/,
        ['', 'keyword', '', '', 'strong', '', '']],

      // Node: "at fn (/app/file.js:10:5)", "at async fn (...)", "at /app/file.js:10:5".
      [/^(\s+)(at)(\s+)((?:async\s+)?)(.+?)(\s+\()(.+?)(:\d+(?::\d+)?)(\)\s*)$/,
        ['', 'keyword', '', 'keyword', 'strong', '', 'string', 'number', '']],
      [/^(\s+)(at)(\s+)((?:async\s+)?)(.+?)(:\d+(?::\d+)?)(\s*)$/,
        ['', 'keyword', '', 'keyword', 'string', 'number', '']],
      [/^(\s+)(at)(\s.*)$/, ['', 'keyword', '']],

      // Anything else is plain.
      [/.+/, ''],
    ],

    // Inside the context: JSON's own tokenizer runs until the exception banner.
    context: [
      [/^--- Exception -+\s*$/, { token: '@rematch', next: '@pop', nextEmbedded: '@pop' }],
    ],
  },
};

/** Registers the language with a Monaco instance. */
export function register(monaco) {
  monaco.languages.register({ id, aliases: ['Stack trace'] });
  monaco.languages.setMonarchTokensProvider(id, language);

  // JSON's highlighting loads only when a JSON document is opened; embedding it does not count, so a trace's
  // context would stay uncoloured on a page with no JSON editor. Opening a trace opens a JSON document too —
  // after a turn, never inside this callback: a model created while the trace's own is being created stalls
  // the trace's tokenization, leaving the whole document uncoloured. Once JSON's highlighting arrives, Monaco
  // re-colours the context by itself.
  monaco.languages.onLanguage(id, () => setTimeout(() => monaco.editor.createModel('', 'json').dispose()));
}
