# Pondhawk Code Editor

A Blazor code-editor component wrapping **Monaco** with **JSON-Schema-driven completion and validation**
for YAML and JSON, packaged so that consuming apps need **no JavaScript toolchain at all**.

```razor
<CodeEditor @bind-Value="document"
            Language="yaml"
            Schema="@schemaJson"
            Diagnostics="@diagnostics" />
```

## Why this exists

The gateway's config Editor is, at its core, a schema-aware YAML editor. Its most valuable authoring
affordance is completion and validation driven by the JSON Schema generated from the gateway's contracts.
No Blazor component library ships a code editor, so this is the piece that has to be owned.

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
