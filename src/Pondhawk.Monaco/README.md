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
<CodeEditor @bind-Value="source" Style="height:400px" />
```

or give it a class and size that from your own CSS:

```razor
<CodeEditor @bind-Value="source" Class="editor" />
```
```css
.editor { height: 100%; }
```

## Languages

Eight languages are bundled. Four carry a full language service — completion, diagnostics and hover —
and four are syntax highlighting, which is all Monaco offers for them in any case:

| `Language` | | |
|---|---|---|
| `yaml` | service | **schema-aware** — see below |
| `json` | service | **schema-aware** — see below |
| `html` | service | tag and attribute completion |
| `css` | service | property completion, colour decorators |
| `xml` | highlighting | |
| `markdown` | highlighting | |
| `sql` | highlighting | |
| `csharp` | highlighting | |

Pass the Monaco language id to `Language`. It can change at runtime; the document survives the switch.

The set is deliberately narrow. Monaco ships ~80 highlighting grammars and a TypeScript service whose
worker alone is 5.7 MB, and carrying all of it made the package 3.3 MB for capability most applications
never touch. Trimming to these eight took it to 1.9 MB.

**An unbundled language falls back to plain text** and logs a console warning naming what *is* bundled —
Monaco's own behaviour is to fall back in silence, which reads as a broken editor rather than an absent
language. If you need one that is not here, open an issue; adding a highlighting grammar costs ~10 KB.

Note that `scss`, `less`, `handlebars` and `razor` are **not** included, even though their services would
otherwise ride along with `css` and `html` — the grammars that register those ids are not bundled.

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

### Each editor gets its own schema

Two editors on one page can carry different schemas. Monaco's schema configuration is page-global — one
call replaces the lot — so the component keeps every live editor's schema and re-applies the whole set
together, scoping each to its own document.

`SchemaFileMatch` overrides that scope. Pass `["*"]` when several editors should share one contract:

```razor
<CodeEditor @bind-Value="a" Language="yaml" Schema="@shared" SchemaFileMatch='["*"]' />
<CodeEditor @bind-Value="b" Language="json" Schema="@shared" SchemaFileMatch='["*"]' />
```

A schema is retracted when its editor is disposed, so a closed tab stops validating anything.

Note that `DiffEditor` documents are not covered by a `CodeEditor`'s schema — it has no `Schema`
parameter, and schemas now scope to the editor that supplied them.

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

## Decorations

Styling, where diagnostics are a claim that something is wrong — search hits, merge-conflict regions,
coverage gutters, blame lines. Replace the list to change the set; an empty list clears it.

```razor
<CodeEditor @bind-Value="src" Decorations="@_decorations" />

@code {
    private IReadOnlyList<EditorDecoration> _decorations = [];

    private void HighlightBlock() => _decorations = [
        new EditorDecoration
        {
            StartLine = 6, StartColumn = 1, EndLine = 9, EndColumn = 1,
            WholeLine = true,
            ClassName = "my-highlight",              // background across the line
            GlyphMarginClassName = "my-glyph",       // icon left of the line numbers
            OverviewRulerColor = "#f59e0b",          // mark on the right-hand ruler
            HoverMessage = "Explained on hover, as **markdown**.",
        },
    ];
}
```

The class names are **yours** — nothing is scoped by this component, so prefix them, and note that a
class which does not exist draws nothing at all with no error. The CSS must be global: Monaco renders
its own DOM, so a scoped `.razor.css` rule never reaches it.

Supplying a `GlyphMarginClassName` turns Monaco's glyph margin on, since a glyph drawn into a margin
that is not there is invisible. It is never turned back off.

## Custom themes

```csharp
await editor.DefineThemeAsync(new EditorTheme
{
    Name = "my-dusk",
    Base = "vs-dark",
    Rules = [ new EditorTokenRule { Token = "comment", Foreground = "#7f9f7f", FontStyle = "italic" } ],
    Colors = new Dictionary<string, string> { ["editor.background"] = "#1b1d23" },
});
```

Then set `Theme="my-dusk"`.

**Themes are global — including the `Theme` parameter itself.** Monaco keeps one registry and one active
theme per document, so `Theme` is the one parameter here that is not per-editor: setting it restyles
every editor on the page, and the last to render wins. Monaco exposes no per-editor theme, so unlike
`Schema` this cannot be scoped around. Drive it from one place in your application.

Write colours as `#rrggbb` throughout. Monaco itself is inconsistent — rule colours must omit the `#`
while `Colors` requires it, and the wrong form throws rather than being ignored — so both are normalised
for you.

## `CodeEditor` parameters

| Parameter | Type | Default | |
|---|---|---|---|
| `Value` | `string` | `""` | The document. Supports `@bind-Value` |
| `Language` | `string` | `plaintext` | Monaco language id |
| `Theme` | `string` | `vs` | `vs`, `vs-dark`, `hc-black` — **page-global**, see below |
| `Schema` | `string?` | `null` | JSON Schema as text |
| `SchemaFileMatch` | `IReadOnlyList<string>?` | all | Which documents the schema covers |
| `Diagnostics` | `IReadOnlyList<EditorMarker>?` | `null` | Your own squiggles |
| `Decorations` | `IReadOnlyList<EditorDecoration>?` | `null` | Styled regions — see above |
| `ReadOnly` | `bool` | `false` | Selection and copy still work |
| `Minimap` | `bool` | `false` | The overview strip; it costs width |
| `TabSize` | `int` | `2` | |
| `FontSize` | `double` | `12.5` | |
| `DebounceMs` | `int` | `300` | Typing pause before `ValueChanged` fires |
| `EditorOptions` | `IReadOnlyDictionary<string, object>?` | `null` | Raw Monaco options, merged over the above |
| `Class` / `Style` | `string?` | `null` | On the host element |

Every parameter is live: changing `ReadOnly`, `TabSize`, `FontSize`, `Minimap` or `EditorOptions` after
first render applies to the running editor without losing the document, scroll position or undo history.

> `EditorOptions` is compared by **reference**. Hold the dictionary in a field — building it inline in
> markup creates a new one each render, which pushes an update every render.

## Methods

| | |
|---|---|
| `GetValueAsync()` | The text right now, bypassing the debounce |
| `GetPositionAsync()` / `SetPositionAsync(line, column)` | The caret, 1-based |
| `GetSelectionAsync()` / `SetSelectionAsync(selection)` | The selected range, 1-based and end-exclusive |
| `FocusAsync()` / `HasFocusAsync()` | Keyboard focus |
| `RevealLineAsync(line, column)` | Scroll a line into view and put the caret on it |
| `LayoutAsync()` | Re-measure after the container resizes |
| `RunActionAsync(actionId)` | Run any built-in Monaco action — see below |
| `GetScrollTopAsync()` / `SetScrollTopAsync(px)` | Vertical scroll offset |
| `DefineThemeAsync(theme)` | Register a custom theme — see above |

### `RunActionAsync` is the whole command surface

Monaco's built-in actions are addressable by id, so one method covers formatting, find, comment toggling
and the rest without a wrapper per feature:

```csharp
await editor.RunActionAsync("editor.action.formatDocument");
await editor.RunActionAsync("actions.find");
await editor.RunActionAsync("editor.action.commentLine");
```

It returns whether Monaco recognised the id as a registered action, so a typo is visible rather than a
silent no-op. A few built-ins (`undo`, `redo`) are commands rather than actions: they still run, but
report `false`.

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

Monaco is a large editor. Most of what ships here is not a first-load cost:

- **Language workers load lazily.** Monaco fetches a language's worker only when a document of that
  language is first opened. An app that only edits YAML never downloads the JSON, CSS or HTML workers.
- **The main module loads on first render**, not at startup — `code-editor.js` at 3.3 MB plus its CSS.
  Put the editor behind a lazily-loaded page and it costs nothing until someone opens it.

Roughly 95% of that main module is Monaco's editor core — rendering, find, folding, the suggest widget.
The eight bundled languages account for about 190 KB of it between them.

## Hosting models

Proven in **Blazor WebAssembly**. The component is written for Blazor generally — disposal handles
`JSDisconnectedException`, and the debounce exists because of Blazor Server's per-keystroke network hop —
but Server, MAUI and hybrid models have not been exercised yet. Reports welcome.

## Links

Source, design notes and issues: <https://github.com/pondhawktech/pondhawk-monaco>

MIT licensed.
