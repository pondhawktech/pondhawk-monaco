# Pondhawk.Monaco

A Blazor code editor wrapping **Monaco** — the editor behind VS Code — with JSON-Schema-driven
completion for YAML and JSON.

**Your app needs no JavaScript toolchain.** No npm, no bundler, no `<script>` or `<link>` tags to add.
The component loads its own module and injects its own stylesheet.

```razor
<CodeEditor @bind-Value="source" Language="csharp" Style="height:400px" />
```

## Install

```bash
dotnet add package Pondhawk.Monaco
```

Add the namespace to `_Imports.razor`:

```razor
@using Pondhawk.Monaco
```

That is the whole setup. Requires **.NET 10**.

## Give it a height

The host element has **no intrinsic height**. Without one you get a zero-pixel editor, which looks like
the component failed to load:

```razor
<CodeEditor @bind-Value="source" Style="height:400px" />      @* or *@
<CodeEditor @bind-Value="source" Class="editor" />            @* .editor { height: 100% } *@
```

## Languages

Every language Monaco ships is available for syntax highlighting — around 80 of them, including `csharp`,
`sql`, `python`, `markdown`, `xml` and `dockerfile`.

Six have a full language service, with completion, diagnostics and hover:

| | |
|---|---|
| `typescript`, `javascript` | |
| `json` | schema-aware — see below |
| `yaml` | schema-aware — see below |
| `css`, `scss`, `less` | |
| `html` | |

Pass the Monaco language id to `Language`. It can change at runtime; the document survives the switch.

## Schema-driven editing

Supply a JSON Schema as text, and `yaml` and `json` documents gain completion, hover documentation and
validation against it:

```razor
<CodeEditor @bind-Value="manifest" Language="yaml" Schema="@schemaJson" Style="height:100%" />

@code {
    private string? schemaJson;
    private string manifest = "";

    protected override async Task OnInitializedAsync() =>
        schemaJson = await Http.GetStringAsync("my-schema.json");
}
```

Both formats are driven from the same schema text, so one contract covers a document authored either way.

Use `SchemaFileMatch` to narrow which documents it applies to; the default is all of them.

### Gotcha: a `$schema` modeline overrides this

A YAML document whose first line carries a language-server modeline —

```yaml
# yaml-language-server: $schema=../../schema/my-schema.json
```

— takes its schema association from **that line**, ignoring whatever you passed to `Schema`. If the URL
is relative and does not resolve against the serving origin, the language service silently falls back to
word-based suggestions: no error, no diagnostic, just completion listing words already in the document.

The fallback looks like working completion, which is what makes it hard to spot. The tell is the icon —
schema properties carry a property glyph, word suggestions carry `abc`.

Either strip the modeline and rely on `Schema`, or point it at a URL your app actually serves.

## Your own diagnostics

Show validation errors from your application alongside the language service's own. They are namespaced
separately, so the two never overwrite each other:

```razor
<CodeEditor @bind-Value="source" Diagnostics="@markers" />

@code {
    private IReadOnlyList<EditorMarker> markers = [];

    private void Validate() => markers = [
        new EditorMarker
        {
            StartLine = 3, StartColumn = 5,
            EndLine = 3,   EndColumn = 12,
            Message = "'replicas' must be at least 1.",
            Severity = MarkerSeverity.Error,   // or Warning, Info
            Source = "manifest-rules",         // shown in the hover
        },
    ];
}
```

Positions are **1-based** and end positions are **exclusive** — to underline the single character at
column 5, use `StartColumn = 5, EndColumn = 6`.

## `CodeEditor` parameters

| Parameter | Type | Default | |
|---|---|---|---|
| `Value` | `string` | `""` | The document. Supports `@bind-Value` |
| `Language` | `string` | `plaintext` | Monaco language id |
| `Theme` | `string` | `vs` | `vs`, `vs-dark`, `hc-black` |
| `Schema` | `string?` | `null` | JSON Schema as text |
| `SchemaFileMatch` | `IReadOnlyList<string>?` | all | Which documents the schema covers |
| `Diagnostics` | `IReadOnlyList<EditorMarker>?` | `null` | Your own squiggles |
| `ReadOnly` | `bool` | `false` | Selection and copy still work |
| `Minimap` | `bool` | `false` | The overview strip; it costs width |
| `TabSize` | `int` | `2` | |
| `FontSize` | `double` | `12.5` | |
| `DebounceMs` | `int` | `300` | Typing pause before `ValueChanged` fires |
| `EditorOptions` | `IReadOnlyDictionary<string, object>?` | `null` | Raw Monaco options, merged over the above |
| `Class` / `Style` | `string?` | `null` | On the host element |

Methods: `GetValueAsync()` reads the text immediately, bypassing the debounce.
`RevealLineAsync(line, column)` scrolls a line into view and puts the caret on it — for "jump to this
diagnostic". `LayoutAsync()` re-measures after the container resizes.

### Keep `DebounceMs` non-zero

It is what stops every keystroke crossing the JavaScript/.NET boundary. Under Blazor Server that boundary
is a network hop, so a zero debounce means a round trip per character.

### Call `LayoutAsync()` when the container resizes

Monaco's own automatic layout is deliberately disabled — its `ResizeObserver` can feed back inside
`overflow: hidden` containers. Splitters, tab switches, and collapsing panels need an explicit call:

```razor
<CodeEditor @ref="_editor" ... />

@code {
    private CodeEditor? _editor;
    private async Task OnPanelResized() => await (_editor?.LayoutAsync() ?? Task.CompletedTask);
}
```

## Diff

`DiffEditor` shows two documents side by side, or interleaved in one pane:

```razor
<DiffEditor Original="@before" Modified="@after" Language="yaml" Style="height:400px" />
```

It is **read-only by default** — a diff is usually shown, not edited. To make the right-hand side
editable, bind it:

```razor
<DiffEditor Original="@before" @bind-Modified="after" ReadOnly="false" />
```

| Parameter | Type | Default | |
|---|---|---|---|
| `Original` | `string` | `""` | The left-hand document |
| `Modified` | `string` | `""` | The right-hand document. Supports `@bind-Modified` |
| `ReadOnly` | `bool` | `true` | Locks the right-hand side |
| `OriginalEditable` | `bool` | `false` | Left side editable too; its edits are not reported |
| `SideBySide` | `bool` | `true` | Two panes, or one interleaved pane when false |
| `IgnoreTrimWhitespace` | `bool` | `false` | Ignore leading/trailing whitespace differences |
| `OverviewRuler` | `bool` | `true` | The change ruler down the right edge |

`Language`, `Theme`, `Minimap`, `TabSize`, `FontSize`, `DebounceMs`, `EditorOptions`, `Class` and `Style`
work as they do on `CodeEditor`.

Methods: `GoToNextDiffAsync()`, `GoToPreviousDiffAsync()`, `RevealFirstDiffAsync()`, `LayoutAsync()`, and
`GetValueAsync(DiffSide.Original | DiffSide.Modified)`.

### The change count arrives by callback

Monaco computes the diff asynchronously, so reading a count straight after setting the documents always
reports zero. Use `OnDiffComputed`, which fires on every recomputation:

```razor
<DiffEditor Original="@before" @bind-Modified="after" OnDiffComputed="c => _changes = c" />
```

Toggling `SideBySide` or `IgnoreTrimWhitespace` goes through Monaco's `updateOptions`, so scroll position
and undo history survive the change.

## Load size

Monaco is a large editor, and this package carries its full language set — it cannot know which languages
you need. Most of that is not a first-load cost:

- **Language workers load lazily.** Monaco fetches a language's worker only when a document of that
  language is first opened. An app that only edits YAML never downloads the TypeScript, CSS or HTML
  workers. (`ts.worker.js` is the big one at 5.7 MB — it contains the TypeScript compiler.)
- **The main module loads on first render**, not at startup — `code-editor.js` at 3.7 MB plus its CSS.
  Put the editor behind a lazily-loaded page and it costs nothing until someone opens it.

## Hosting models

Proven in **Blazor WebAssembly**. The component is written for Blazor generally — disposal handles
`JSDisconnectedException`, and the debounce exists because of Blazor Server's per-keystroke network hop —
but Server, MAUI and hybrid models have not been exercised yet. Reports welcome.

## Links

Source, design notes and issues: <https://github.com/pondhawktech/pondhawk-monaco>

MIT licensed.
