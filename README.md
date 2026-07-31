# Pondhawk.Monaco

A general-purpose Blazor code editor wrapping **Monaco** — the editor behind VS Code — packaged so that
consuming apps need **no JavaScript toolchain at all**.

```razor
<CodeEditor @bind-Value="source" Language="csharp" />
```

Eight languages are bundled. **YAML, JSON, HTML and CSS** carry a full language service — completion,
diagnostics, hover — and **XML, Markdown, SQL and C#** carry syntax highlighting, which is all Monaco
offers for those in any case. Anything else falls back to plain text with a console warning.

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
Pondhawk.Monaco.slnx              all four projects
Directory.Build.props             the released version — single source of truth
.github/workflows/                CI and release
.github/scripts/version.sh        reads and bumps that version
src/Pondhawk.Monaco/              RCL, NuGet-packable
  README.md                       the package readme — usage only, shipped to nuget.org
  js/                             esbuild sources (Monaco + monaco-yaml + workers)
  js/test/                        Node tests — code-editor.js against a stubbed Monaco
  wwwroot/dist/                   bundled output — build artifact, gitignored
tests/Pondhawk.Monaco.Tests/      bUnit tests — the component against a stubbed code-editor.js
demo/Pondhawk.Monaco.Demo/        Blazor WASM harness
build/                            Cake Frosting build
docs/                             design notes
```

The npm/esbuild step exists **only in this repo**. Consumers get a NuGet package containing pre-bundled
assets under `_content/Pondhawk.Monaco/`.

## On size

Monaco is a large editor. The package is 1.9 MB, and most of what it contains is not a first-load cost:

**Language workers load lazily.** Monaco fetches a language's worker only when a document of that
language is first opened. An app that only edits YAML never downloads the JSON, CSS or HTML workers.
What always loads is `code-editor.js` (3.3 MB) plus its CSS, and only when the component is first
rendered — so put it behind a lazily-loaded page and it costs nothing until used.

### Why the language set is curated

`code-editor.js` imports Monaco one contribution at a time rather than through the `monaco-editor`
barrel, which would pull in ~80 highlighting grammars and every language service. Measured:

| | nupkg |
|---|---:|
| Full Monaco language set | 3,436,349 B |
| The eight bundled languages | **1,957,925 B** |

Almost all of that is `ts.worker.js`, 5.7 MB uncompressed because it contains the TypeScript compiler,
for a service most consumers of a config editor never open. The main module barely moves — 95% of it is
the editor core, and all eight languages together are ~190 KB.

**The contributions are not symmetric, and this is the trap when adding a language.** JSON's service
contribution calls `languages.register()` itself and stands alone. CSS's and HTML's do not — they only
attach via `languages.onLanguage(id, …)`, and the `register()` call for those ids lives in
`basic-languages`. Import the service without the grammar and the id is never registered at all, so the
hook never fires: the language does not lose completion, it ceases to exist, and documents render as
unhighlighted plain text with no error. Both halves are imported for `css` and `html`.

An unbundled language id is caught at runtime by `checkLanguage()`, which reads Monaco's own registry —
so it cannot drift from the bundle — and warns once per unknown id.

## Design constraints

Four things this component must get right, or it becomes something to fight rather than use:

1. **Worker URLs** resolve under `_content/Pondhawk.Monaco/` — the base path is passed from
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

### Both halves of the interop boundary are tested

The boundary is untyped in both directions, so each side is tested against a stub of the other:

| | Real | Mocked | Pins |
|---|---|---|---|
| `tests/Pondhawk.Monaco.Tests` (bUnit) | the component | `code-editor.js` | what .NET **sends**, and what it does *not* re-send |
| `js/test` (`node --test`) | `code-editor.js` | Monaco | what the module **does** with what arrives |

The .NET payload tests say outright that they can only assert what C# emits. The JS tests close that:
marker severities, decoration field names, worker routing, theme colour normalisation. Nearly every
defect found in this component has been on the JavaScript side of that line.

`./build.sh --target TestJs` runs the JavaScript tests alone; `Test` runs both, so `Pack` gates on both.

Node and npm are needed **only in this repo**, and only to produce `wwwroot/dist`.

### The solution is the project list

`Pondhawk.Monaco.slnx` holds all four projects, and the build reads them from it rather than keeping
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
dotnet build Pondhawk.Monaco.slnx
dotnet test  Pondhawk.Monaco.slnx
```

These skip Cake but not the JavaScript: the library's `BundleJs` target runs `BeforeBuild` either way, so
a fresh clone still produces `wwwroot/dist`. Pass `-p:SkipJsBundle=true` where node is unavailable.

## Publishing

Two workflows, both driving `./build.sh` rather than re-implementing the build in YAML.

| | Trigger | Feed | Version |
|---|---|---|---|
| `ci.yml` | push to `main`, and every PR | GitHub Packages | `1.0.0-ci.<run>` |
| `release.yml` | manual, pick the bump | nuget.org (and GitHub Packages) | `1.0.0` |

### The version lives in a file

`Directory.Build.props` holds it, and it is the single source of truth:

```xml
<VersionPrefix>1.0.0</VersionPrefix>
```

MSBuild imports that for every project automatically, so `./build.sh --target Pack` with no arguments
produces the real version — what the file says is what packs, locally and in CI alike. Read the file and
you know what the next release will be.

**The file is the version to publish next**, not the last one published. So the release workflow packs
exactly what it reads, and the bump is applied *before* publishing:

```
file 1.0.0  →  none 1.0.0   patch 1.0.1   minor 1.1.0   major 2.0.0
```

`none` publishes the file untouched — that is the first release, and the retry path if a publish fails
part-way. Every other choice rewrites the file first. After a successful release the file therefore
records what was last published, and the workflow commits it back to `main` with `[skip ci]`.

The commit happens **after** the package is pushed, never before: a committed bump and a tag for a
release that never published would block retrying that version.

CI suffixes the same file — `1.0.0-ci.87`. NuGet orders that below the eventual `1.0.0`, so a CI build
can never occupy or shadow the release it precedes, and consumers only see one if they opt into
prereleases.

Tags are still created (`v1.0.0`, plus a GitHub release with the nupkg attached) — as release markers,
and as the check that stops a version being published twice.

### Releasing

Run **Release to NuGet.org** from the Actions tab and choose `none`/`patch`/`minor`/`major`. Tick
**dry run** to build and pack without publishing or committing anything.

**The very first release is `none`** — the file already reads `1.0.0`, and bumping would skip past it.

On a real run the order is: resolve the version → test → pack → verify the nupkg carries the expected
version → compare the bundle against CI's → push to nuget.org → mirror to GitHub Packages → commit the
bumped file → tag and open a GitHub release.

### Why the release rebuilds, and what checks that

The release packs from source rather than promoting the package CI already published, because a NuGet
version is baked into the `.nuspec` and the filename — there is no retag. `1.0.0-ci.3` cannot become
`1.0.0` without repacking, which is rebuilding.

That is the right trade for NuGet, but it means the bytes being published are not literally the bytes CI
tested. `compare-bundles.sh` closes the gap: the release downloads CI's package for the same commit and
compares `staticwebassets/` — the npm and esbuild output — file by file. Everything else in the package
legitimately differs between two versions (the nuspec, the assembly, the relationship parts), and
diffing those would be noise that trains you to ignore the check.

It is **best effort**: no CI run for the commit, or an artifact past its 14-day retention, logs a notice
and continues, because neither should block a legitimate release. A bundle that is present *and*
different fails the release — same commit and the same locked toolchain should produce the same bundle,
so a mismatch means the build has become non-deterministic.

Run it by hand on any two packages:

```bash
.github/scripts/compare-bundles.sh a.nupkg b.nupkg
```

Releases must run from `main`, and the workflow refuses a version whose tag already exists — which is
also what stops `none` from silently republishing.

### One-time setup

- **`NUGET_ORG_API_KEY`** — the nuget.org API key. It is an **organisation** secret on `pondhawktech`,
  shared across repositories rather than copied into each one, so nothing needs adding here as long as
  its visibility includes this repository. The release workflow checks it is non-empty before pushing,
  because `dotnet nuget push` with an empty key returns a 403 that reads like a permissions problem
  rather than a missing secret. GitHub Packages needs nothing — it uses the built-in `GITHUB_TOKEN`.
- `release.yml` references a `nuget.org` **environment**, created automatically on first run. Adding a
  required reviewer to it makes every publish need approval — worth doing, since a version pushed to
  nuget.org can be unlisted but never replaced or deleted.
- Package metadata (`PackageLicenseExpression`, `RepositoryUrl`, readme) lives in the library csproj.
  `RepositoryUrl` is not optional: GitHub Packages resolves package ownership from it and rejects the
  push without it.
- The release workflow pushes the version bump to `main`. If you add branch protection, `github-actions`
  needs permission to push — otherwise the publish succeeds and only the bump commit fails, leaving the
  file behind what was released.

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
