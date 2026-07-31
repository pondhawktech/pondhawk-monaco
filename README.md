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

## Diff

`DiffEditor` shows two documents side by side, or interleaved in one pane:

```razor
<DiffEditor Original="@before" Modified="@after" Language="yaml" />
```

It is **read-only by default** — a diff is usually shown, not edited. To make the right-hand side
editable, bind it:

```razor
<DiffEditor Original="@before" @bind-Modified="after" Language="yaml" ReadOnly="false" />
```

| Parameter | Default | |
|---|---|---|
| `SideBySide` | `true` | Two panes, or one interleaved pane when false |
| `IgnoreTrimWhitespace` | `false` | Ignore leading/trailing whitespace differences |
| `OriginalEditable` | `false` | Allow editing the left side too (its edits are not reported) |
| `OverviewRuler` | `true` | The change ruler down the right edge |

Methods: `GoToNextDiffAsync()`, `GoToPreviousDiffAsync()`, `RevealFirstDiffAsync()`, `LayoutAsync()`,
and `GetValueAsync(DiffSide)`.

The change count arrives through the `OnDiffComputed` callback rather than a property, because Monaco
computes the diff asynchronously — reading it straight after setting the models always reports zero.

Toggling `SideBySide` or `IgnoreTrimWhitespace` goes through Monaco's `updateOptions`, so the scroll
position and undo stack survive the change.

**No extra download.** The diff algorithm and view are part of the Monaco core that `code-editor.js`
already bundles, and the computation runs in the `editor.worker.js` the editor loads anyway.

## Layout

```
Pondhawk.CodeEditor.slnx          all four projects
.github/workflows/                CI and release
.github/scripts/next-version.sh   the version bump, shared by both workflows
src/Pondhawk.Blazor.CodeEditor/   RCL, NuGet-packable
  README.md                       the package readme — usage only, shipped to nuget.org
  js/                             esbuild sources (Monaco + monaco-yaml + workers)
  wwwroot/dist/                   bundled output — build artifact, gitignored
tests/…Tests/                     bUnit tests over the interop boundary
demo/Pondhawk.CodeEditor.Demo/    Blazor WASM harness
build/                            Cake Frosting build
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
./build.sh --target Clean   # Empty bin/, obj/, wwwroot/dist/ and artifacts/
```

`Pack` depends on `Test`, not merely `Build`: the package embeds the bundled JavaScript, so shipping one
that failed its tests would put a broken editor into every consuming app with no local signal.

Node and npm are needed **only in this repo**, and only to produce `wwwroot/dist`.

### The solution is the project list

`Pondhawk.CodeEditor.slnx` holds all four projects, and the build reads them from it rather than keeping
its own list — `Restore`, `Build` and `Test` run against the solution, and `Clean` parses it for the
directories to empty. A project added to the solution is picked up by the build without `build/Program.cs`
being touched, and cannot quietly fall out of CI by being forgotten in a second list.

Two places still name a project directly, both deliberately:

- **`Pack`** targets the library alone. The demo and the build project are ordinary non-packable
  projects; packing the solution would emit nupkgs for them too.
- **`Clean`** skips `build/`. Cake is executing out of `build/bin` while the target runs, and deleting a
  loaded assembly is legal on Linux but fails outright on Windows.

Because the solution exists, the usual root-level commands work directly:

```bash
dotnet build Pondhawk.CodeEditor.slnx
dotnet test  Pondhawk.CodeEditor.slnx
```

These skip Cake but not the JavaScript: the library's `BundleJs` target runs `BeforeBuild` either way, so
a fresh clone still produces `wwwroot/dist`. Pass `-p:SkipJsBundle=true` where node is unavailable.

## Publishing

Two workflows, both driving `./build.sh` rather than re-implementing the build in YAML.

| | Trigger | Feed | Version |
|---|---|---|---|
| `ci.yml` | push to `main`, and every PR | GitHub Packages | `1.4.3-ci.<run>` |
| `release.yml` | manual, pick the bump | nuget.org (and GitHub Packages) | `1.4.3` |

### Versions come from tags

`.github/scripts/next-version.sh` reads the newest `vMAJOR.MINOR.PATCH` tag and applies the requested
bump. Nothing in the repo records the version, so nothing can fall out of sync with what was published —
the csproj keeps its `1.0.0` default, and CI always passes `--packageVersion` explicitly.

```
newest tag v1.4.2  →  patch 1.4.3   minor 1.5.0   major 2.0.0
```

Prerelease tags and non-version tags are skipped, so a `v1.5.0-rc1` never becomes the base to count from.
With no tags at all the base is `0.0.0`, which makes **`major` the first release: 1.0.0**.

CI publishes the *next patch* as a prerelease — `1.4.3-ci.87`. NuGet orders that below the eventual
`1.4.3`, so a CI build can never occupy or shadow a real release, and consumers only see one if they opt
into prereleases.

### Releasing

Run **Release to NuGet.org** from the Actions tab, choose `patch`/`minor`/`major`, and optionally tick
**dry run** to build and pack without publishing anything. On a real run the order is: test → pack →
push to nuget.org → mirror to GitHub Packages → tag the built commit and open a GitHub release.

The push comes *before* the tag deliberately. A published package missing its tag is a one-command fix; a
tag whose version was never published blocks retrying that version.

Releases must run from `main`, and the workflow refuses a version whose tag already exists.

### One-time setup

- **`NUGET_API_KEY`** — a nuget.org API key, added as a repository secret. Nothing else is needed:
  GitHub Packages authenticates with the built-in `GITHUB_TOKEN`.
- `release.yml` references a `nuget.org` **environment**, created automatically on first run. Adding a
  required reviewer to it makes every publish need approval — worth doing, since a version pushed to
  nuget.org can be unlisted but never replaced or deleted.
- Package metadata (`PackageLicenseExpression`, `RepositoryUrl`, readme) lives in the library csproj.
  `RepositoryUrl` is not optional: GitHub Packages resolves package ownership from it and rejects the
  push without it.

## Status

Working, and proven in the demo: Monaco renders in Blazor WASM, typing round-trips through .NET without
the caret jumping, schema-driven completion fires from a supplied JSON Schema, and host diagnostics
render alongside the language service's own.

`DiffEditor` is proven in the demo too — side-by-side and inline, live re-diff while editing the right
pane, next/previous navigation, and the whitespace toggle collapsing a whitespace-only diff to zero
changes.

Publishing is wired up but unexercised: no version has been tagged or pushed to either feed yet, so the
first run of either workflow is also its first real test.

Not done: tested across hosting models other than WASM.
