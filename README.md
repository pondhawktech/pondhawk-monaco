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

No Blazor component library ships a code editor, so any Blazor app needing to edit source, config,
queries or scripts has to own the Monaco integration itself — the ESM bundle, the worker plumbing, the
disposal discipline, the binding semantics. This is that integration, built once and packaged.

## Why not BlazorMonaco

[BlazorMonaco](https://www.nuget.org/packages/BlazorMonaco) (3.5.0) is a mature, actively maintained
wrapper and its design is the reference for this one — the editor-registry-by-id pattern, the
`setModelMarkers` shape, disposal discipline, and `_content/` path resolution are all borrowed from it.

It is not used directly because it loads Monaco through the **AMD loader**
(`require.paths.vs = _content/BlazorMonaco/lib/monaco-editor/min/vs`), while `monaco-yaml` v5 is
**ESM-only and bundler-dependent** — the package ships no `esm/` build. The two cannot be combined
cleanly, and schema-driven YAML is the entire point here.

BlazorMonaco does ship Monaco's built-in JSON worker, so it gives schema intelligence in *JSON* mode
only. That was rejected as a workaround — telling a consumer "your schema works, but only if you author
in JSON" is not a general-purpose editor.

Pinning an older `monaco-yaml` with an AMD build was also rejected. `monaco-yaml` v5 dropping AMD is the
ecosystem signalling its direction, and building on the path being deprecated buys a shortcut now for a
migration later.

## Schema-driven editing

Supply a JSON Schema and YAML and JSON gain completion, hover documentation and validation:

```razor
<CodeEditor @bind-Value="manifest" Language="yaml" Schema="@schemaJson" />
```

This is the capability that motivated the project. Monaco's built-in JSON service handles `json`;
`monaco-yaml` handles `yaml`. Both are driven from the same schema text, so a document can be edited in
either format against one contract.

### Gotcha: the `$schema` modeline wins

A YAML document whose first line carries a language-server modeline —

```yaml
# yaml-language-server: $schema=../../schema/my-schema.json
```

— has its schema association taken from **that line**, overriding whatever is passed to `Schema`. If the
URL is relative and does not resolve against the serving origin, the language service quietly falls back
to word-based suggestions: no error, no diagnostic, just completion that lists words already in the
document instead of schema properties.

This is easy to miss because the fallback looks like working completion. The tell is the icon — schema
properties carry a property glyph, word suggestions carry `abc`.

Either strip the modeline and rely on `Schema`, or point it at a URL the app actually serves.

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

## Building

```bash
./build.sh                  # Build + Test (default)
./build.sh --target Bundle  # Re-bundle Monaco into wwwroot/dist
./build.sh --target Pack    # NuGet package into artifacts/
./build.sh --target Demo    # Run the demo on http://localhost:5200
```

`Pack` depends on `Test`, not merely `Build`: the package embeds the bundled JavaScript, so shipping one
that failed its tests would put a broken editor into every consuming app with no local signal.

Node and npm are needed **only in this repo**, and only to produce `wwwroot/dist`.

## Status

Working, and proven in the demo: Monaco renders in Blazor WASM, typing round-trips through .NET without
the caret jumping, schema-driven completion fires from a supplied JSON Schema, and host diagnostics
render alongside the language service's own.

Not done: published to a feed, tested across hosting models other than WASM.
