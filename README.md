# Pondhawk Code Editor

A general-purpose Blazor code editor wrapping **Monaco** — the editor behind VS Code — packaged so that
consuming apps need **no JavaScript toolchain at all**.

```razor
<CodeEditor @bind-Value="source" Language="csharp" />
```

Every language Monaco ships is available, with full language-service support (completion, diagnostics,
hover) for the six that have one: **TypeScript/JavaScript, JSON, CSS/SCSS/LESS, HTML, and YAML**.
Syntax highlighting covers the rest of Monaco's ~80 languages.

**Optional JSON-Schema intelligence** for YAML and JSON, when a schema is supplied:

```razor
<CodeEditor @bind-Value="config" Language="yaml" Schema="@schemaJson" />
```

## Why this exists

No Blazor component library ships a code editor — Telerik's `TextArea` is a plain input and its `Editor`
is rich-text/HTML — so any Blazor app needing to edit source, config, queries or scripts has to own this
integration. This is that integration, built once.

Schema-driven YAML was the motivating case (a gateway config editor, where completion is driven by a
JSON Schema generated from the app's own contracts), but the component is not specific to it.

## Why not BlazorMonaco

[BlazorMonaco](https://www.nuget.org/packages/BlazorMonaco) (3.5.0) is a mature, actively maintained
wrapper and its design is the reference for this one — the editor-registry-by-id pattern, the
`setModelMarkers` shape, disposal discipline, and `_content/` path resolution are all borrowed from it.

It is not used directly because it loads Monaco through the **AMD loader**
(`require.paths.vs = _content/BlazorMonaco/lib/monaco-editor/min/vs`), while `monaco-yaml` v5 is
**ESM-only and bundler-dependent** — the package ships no `esm/` build. The two cannot be combined
cleanly, and schema-driven YAML is the entire point here.

BlazorMonaco does ship Monaco's built-in JSON worker, so it gives schema intelligence in *JSON* mode.
That was rejected as a workaround: the gateway's configs are authored in YAML.

Pinning an older `monaco-yaml` with an AMD build was also rejected — `monaco-yaml` v5 dropping AMD is the
ecosystem signalling its direction, and it would mean running a different version than the existing
Angular editor does.

## Layout

```
src/Pondhawk.Blazor.CodeEditor/   RCL, NuGet-packable
  js/                             esbuild sources (Monaco + monaco-yaml + workers)
  wwwroot/dist/                   bundled output — build artifact, gitignored
demo/Pondhawk.CodeEditor.Demo/    Blazor WASM harness
docs/                             design notes
```

The npm/esbuild step exists **only in this repo**. Consumers get a NuGet package containing pre-bundled
assets under `_content/Pondhawk.Blazor.CodeEditor/`.

## On size

The full bundle is large — Monaco is a large editor, and `ts.worker.js` alone is 5.7 MB because it
contains the TypeScript compiler. This is mostly not a first-load cost:

**Language workers load lazily.** Monaco fetches a language's worker only when a document of that
language is first opened. An app that only edits YAML never downloads the TypeScript, CSS or HTML
workers. What always loads is `code-editor.js` (3.7 MB) plus its CSS, and only when the component is
first rendered — so put it behind a lazily-loaded page and it costs nothing until used.

The main module carries Monaco's full language set deliberately, because this component serves many
applications and cannot know which languages a consumer needs.

## Design constraints

Four things this component must get right, or it becomes something to fight rather than use:

1. **Worker URLs** resolve under `_content/Pondhawk.Blazor.CodeEditor/` — the base path is passed from
   .NET rather than guessed, since it differs between hosting models.
2. **Disposal** — `IAsyncDisposable` disposes the Monaco editor *and* the `IJSObjectReference`. Without
   it, editors leak on every navigation.
3. **Layout is explicit** — `automaticLayout: false` plus deliberate `layout()` calls. Monaco's built-in
   ResizeObserver has a history of feedback loops inside `overflow: hidden` containers.
4. **No binding echo** — changes originating in JS must not be pushed back into the model, or the caret
   jumps mid-typing. Guarded with a revision counter.

## Status

Scaffolded. Not yet implemented.
