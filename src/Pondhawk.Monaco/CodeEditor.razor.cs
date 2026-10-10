using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Pondhawk.Monaco;

/// <summary>
/// A Monaco-backed code editor.
///
/// <code>
/// &lt;CodeEditor @bind-Value="source" Language="csharp" /&gt;
/// </code>
///
/// <para>JavaScript owns the document; edits arrive here debounced. Setting <see cref="Value"/> from .NET
/// pushes text back into the editor, but only when it genuinely differs from what the editor already
/// holds — see the revision guard in <see cref="OnDocumentChanged"/>.</para>
/// </summary>
public sealed partial class CodeEditor : ComponentBase, IAsyncDisposable
{
    private const string AssetPath = "_content/Pondhawk.Monaco/dist";

    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly string _id = $"pce-{Guid.NewGuid():N}";
    private ElementReference _host;
    private CodeEditorInterop? _interop;
    private DotNetObjectReference<CodeEditor>? _self;

    private bool _created;

    /// <summary>
    /// Set the moment creation is ISSUED, where <see cref="_created"/> is set once it has completed.
    /// Disposal keys off this one: between the create call and the attach call that follows it there is
    /// an await — a network round trip under Blazor Server — and a component disposed inside that window
    /// would otherwise never tell JavaScript to tear the editor down, leaking the Monaco instance, its
    /// model and its DOM for the life of the page.
    /// </summary>
    private bool _createIssued;

    private string _lastValueFromEditor = string.Empty;
    private string? _appliedLanguage;
    private string? _appliedSchema;
    private IReadOnlyList<string>? _appliedSchemaFileMatch;

    /// <summary>Highest edit revision accepted from JavaScript — see <see cref="OnDocumentChanged"/>.</summary>
    private int _lastRevision;
    private string? _appliedTheme;
    private LiveEditorOptions? _appliedOptions;
    private IReadOnlyList<EditorMarker>? _appliedDiagnostics;
    private IReadOnlyList<EditorDecoration>? _appliedDecorations;

    /// <summary>The document text. Supports <c>@bind-Value</c>.</summary>
    [Parameter] public string Value { get; set; } = string.Empty;

    /// <summary>Raised after the debounce elapses, carrying the editor's current text.</summary>
    [Parameter] public EventCallback<string> ValueChanged { get; set; }

    /// <summary>Monaco language id — <c>csharp</c>, <c>yaml</c>, <c>json</c>, <c>sql</c>, <c>markdown</c>, <c>stacktrace</c>…</summary>
    [Parameter] public string Language { get; set; } = "plaintext";

    /// <summary>
    /// Monaco theme id. Built-ins are <c>vs</c>, <c>vs-dark</c> and <c>hc-black</c>.
    ///
    /// <para><b>Page-global, unlike every other parameter here.</b> Monaco keeps one active theme for the
    /// document, so setting this restyles every editor on the page and the last one to render wins. That
    /// is Monaco's design — there is no per-editor theme to expose — and unlike the schema it cannot be
    /// scoped around. Drive it from one place in the host application rather than per editor.</para>
    /// </summary>
    [Parameter] public string Theme { get; set; } = "vs";

    /// <summary>
    /// A JSON Schema (as JSON text) driving completion, hover and validation. Applies to YAML and JSON;
    /// ignored for languages without a schema-aware service.
    /// </summary>
    [Parameter] public string? Schema { get; set; }

    /// <summary>
    /// Which documents the schema applies to. Defaults to <b>this editor's own document</b>, so two
    /// editors on one page can carry different schemas without colliding.
    ///
    /// <para>Supply a value to widen that — <c>["*"]</c> applies the schema to every document on the
    /// page, which is what you want when several editors share one contract.</para>
    /// </summary>
    [Parameter] public IReadOnlyList<string>? SchemaFileMatch { get; set; }

    /// <summary>Host-supplied diagnostics, shown alongside the language service's own.</summary>
    [Parameter] public IReadOnlyList<EditorMarker>? Diagnostics { get; set; }

    /// <summary>
    /// Styled regions — line highlights, glyph-margin icons, inline colouring. Replace the list to change
    /// the set; an empty list clears it.
    ///
    /// <para>Decorations are styling, where <see cref="Diagnostics"/> is a claim that something is wrong.
    /// Search hits, merge-conflict regions and coverage gutters belong here.</para>
    /// </summary>
    [Parameter] public IReadOnlyList<EditorDecoration>? Decorations { get; set; }

    /// <summary>Render the document read-only. Selection and copy still work.</summary>
    [Parameter] public bool ReadOnly { get; set; }
    /// <summary>Show the minimap overview on the right. Off by default — it costs width.</summary>
    [Parameter] public bool Minimap { get; set; }
    /// <summary>Spaces per indent level.</summary>
    [Parameter] public int TabSize { get; set; } = 2;
    /// <summary>Editor font size in pixels.</summary>
    [Parameter] public double FontSize { get; set; } = 12.5;

    /// <summary>How long typing must pause before <see cref="ValueChanged"/> fires. Keep this non-zero:
    /// it is what stops every keystroke crossing the interop boundary.</summary>
    [Parameter] public int DebounceMs { get; set; } = 300;

    /// <summary>Escape hatch — raw Monaco construction options, merged over this component's defaults.
    /// Present so the component never becomes the bottleneck on Monaco's option surface.</summary>
    [Parameter] public IReadOnlyDictionary<string, object>? EditorOptions { get; set; }

    /// <summary>Extra CSS classes on the host element.</summary>
    [Parameter] public string? Class { get; set; }
    /// <summary>Inline styles on the host element. The host has no intrinsic height — give it one.</summary>
    [Parameter] public string? Style { get; set; }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Absolute, and built from BaseUri so a non-root <base href> still resolves. Worker URLs are
            // the classic silent failure here: a wrong path only breaks when a language service is first
            // needed, long after startup.
            var baseUrl = new Uri(new Uri(Navigation.BaseUri), AssetPath).ToString().TrimEnd('/');

            _interop = new CodeEditorInterop(Js, baseUrl);
            _self = DotNetObjectReference.Create(this);

            _lastValueFromEditor = Value;
            _appliedLanguage = Language;
            _appliedTheme = Theme;
            _appliedOptions = CurrentOptions();

            _createIssued = true;
            var created = await _interop.CreateAsync(_id, _host, new EditorOptions
            {
                BaseUrl = baseUrl,
                Value = Value,
                Language = Language,
                Theme = Theme,
                ReadOnly = ReadOnly,
                Minimap = Minimap,
                TabSize = TabSize,
                FontSize = FontSize,
                DebounceMs = DebounceMs,
                RawOptions = EditorOptions,
            });

            // Its host had already left the page: the component is on its way out, and stays uncreated —
            // every later call waits on _created, and disposal ignores an editor that was never made.
            if (!created) return;

            await _interop.AttachAsync(_id, _self);
            _created = true;

            // Schema and markers may have been supplied before the editor existed.
            await ApplySchemaAsync();
            await ApplyMarkersAsync();
            await ApplyDecorationsAsync();
            return;
        }

        if (!_created || _interop is null) return;

        // Push only what actually changed. Re-applying unchanged state would reset the model, the
        // language service, or the markers on every parent render.
        if (Value != _lastValueFromEditor)
        {
            _lastValueFromEditor = Value;
            await _interop.SetValueAsync(_id, Value);
        }

        if (Language != _appliedLanguage)
        {
            _appliedLanguage = Language;
            await _interop.SetLanguageAsync(_id, Language);
        }

        if (Theme != _appliedTheme)
        {
            _appliedTheme = Theme;
            await _interop.SetThemeAsync(Theme);
        }

        // Appearance and behaviour options. These used to be construction-only, which made ReadOnly,
        // Minimap, TabSize and FontSize look like live parameters while doing nothing after first render.
        var options = CurrentOptions();
        if (options != _appliedOptions)
        {
            _appliedOptions = options;
            await _interop.UpdateOptionsAsync(_id, options);
        }

        await ApplySchemaAsync();
        await ApplyMarkersAsync();
        await ApplyDecorationsAsync();
    }

    private LiveEditorOptions CurrentOptions() => new()
    {
        ReadOnly = ReadOnly,
        Minimap = Minimap,
        TabSize = TabSize,
        FontSize = FontSize,
        RawOptions = EditorOptions,
    };

    /// <summary>
    /// Re-applies when EITHER the schema text or the documents it applies to change. Comparing only the
    /// text meant a caller could narrow or widen <see cref="SchemaFileMatch"/> and have nothing happen —
    /// a parameter that looks live but is read once.
    /// </summary>
    private async Task ApplySchemaAsync()
    {
        if (_interop is null || Schema is null) return;

        var fileMatchChanged = !(_appliedSchemaFileMatch is null
            ? SchemaFileMatch is null
            : SchemaFileMatch is not null && _appliedSchemaFileMatch.SequenceEqual(SchemaFileMatch));

        if (Schema == _appliedSchema && !fileMatchChanged) return;

        _appliedSchema = Schema;
        _appliedSchemaFileMatch = SchemaFileMatch is null ? null : [.. SchemaFileMatch];
        await _interop.ConfigureSchemaAsync(_id, Schema, SchemaFileMatch);
    }

    /// <summary>
    /// Pushes markers only when the set actually differs. <see cref="EditorMarker"/> is a record, so
    /// SequenceEqual compares by value — without this, every parent render re-sent the whole list and
    /// setModelMarkers rebuilt the squiggles and the overview ruler each time.
    /// </summary>
    private async Task ApplyMarkersAsync()
    {
        if (_interop is null || Diagnostics is null) return;
        if (_appliedDiagnostics is not null && _appliedDiagnostics.SequenceEqual(Diagnostics)) return;

        _appliedDiagnostics = [.. Diagnostics];
        await _interop.SetMarkersAsync(_id, Diagnostics);
    }

    /// <summary>Same value-comparison as markers — see <see cref="ApplyMarkersAsync"/>.</summary>
    private async Task ApplyDecorationsAsync()
    {
        if (_interop is null || Decorations is null) return;
        if (_appliedDecorations is not null && _appliedDecorations.SequenceEqual(Decorations)) return;

        _appliedDecorations = [.. Decorations];
        await _interop.SetDecorationsAsync(_id, Decorations);
    }

    /// <summary>
    /// Called from JavaScript after the debounce elapses.
    ///
    /// <para>Recording the text as <see cref="_lastValueFromEditor"/> BEFORE raising
    /// <see cref="ValueChanged"/> is the whole binding-echo guard: the parent's re-render hands the same
    /// string back as <see cref="Value"/>, and the equality check in <see cref="OnAfterRenderAsync"/>
    /// then skips pushing it into the editor. Without this the caret jumps to the end mid-typing.</para>
    /// </summary>
    [JSInvokable]
    public async Task OnDocumentChanged(string value, int revision)
    {
        // The revision counts edits on the JS side, so it only ever grows. An older one arriving after a
        // newer one means two debounced callbacks crossed on the way here — a real possibility under
        // Blazor Server, where each is a message over a circuit — and applying the older would put stale
        // text back into a document the user has since moved on from.
        if (revision < _lastRevision) return;
        _lastRevision = revision;

        _lastValueFromEditor = value;
        Value = value;

        if (ValueChanged.HasDelegate)
            await ValueChanged.InvokeAsync(value);
    }

    /// <summary>Scroll a line into view and put the caret on it — for "jump to this diagnostic".</summary>
    public async Task RevealLineAsync(int line, int column = 1)
    {
        if (_created && _interop is not null)
            await _interop.RevealLineAsync(_id, line, column);
    }

    /// <summary>Re-measure after the container resizes. Required: Monaco's own ResizeObserver is
    /// deliberately disabled because it can feed back inside <c>overflow: hidden</c> containers.</summary>
    public async Task LayoutAsync()
    {
        if (_created && _interop is not null)
            await _interop.LayoutAsync(_id);
    }

    /// <summary>Read the editor's current text directly, bypassing the debounce.</summary>
    public async Task<string> GetValueAsync() =>
        _created && _interop is not null ? await _interop.GetValueAsync(_id) : Value;

    /// <summary>Where the caret is. Null before the editor exists, or when it has never held a cursor.</summary>
    public async Task<EditorPosition?> GetPositionAsync() =>
        _created && _interop is not null ? await _interop.GetPositionAsync(_id) : null;

    /// <summary>Move the caret. Does not scroll — use <see cref="RevealLineAsync"/> for that.</summary>
    public async Task SetPositionAsync(int line, int column = 1)
    {
        if (_created && _interop is not null)
            await _interop.SetPositionAsync(_id, line, column);
    }

    /// <summary>The selected range, or an empty range when nothing is selected. Null before creation.</summary>
    public async Task<EditorSelection?> GetSelectionAsync() =>
        _created && _interop is not null ? await _interop.GetSelectionAsync(_id) : null;

    /// <summary>Select a range. Positions are 1-based and the end is exclusive.</summary>
    public async Task SetSelectionAsync(EditorSelection selection)
    {
        if (_created && _interop is not null)
            await _interop.SetSelectionAsync(_id, selection);
    }

    /// <summary>Vertical scroll offset in pixels. Pair with <see cref="GetPositionAsync"/> to save and
    /// restore view state across a tab switch.</summary>
    public async Task<double> GetScrollTopAsync() =>
        _created && _interop is not null ? await _interop.GetScrollTopAsync(_id) : 0;

    /// <summary>Restore a scroll offset previously read from <see cref="GetScrollTopAsync"/>.</summary>
    public async Task SetScrollTopAsync(double scrollTop)
    {
        if (_created && _interop is not null)
            await _interop.SetScrollTopAsync(_id, scrollTop);
    }

    /// <summary>
    /// Register a custom theme, then set <see cref="Theme"/> to its <see cref="EditorTheme.Name"/>.
    ///
    /// <para><b>Themes are global.</b> Monaco keeps one registry and one active theme per page, so this
    /// affects every editor on it — Monaco's design, not this component's. Registering an existing name
    /// replaces it.</para>
    /// </summary>
    public async Task DefineThemeAsync(EditorTheme theme)
    {
        if (_created && _interop is not null)
            await _interop.DefineThemeAsync(theme);
    }

    /// <summary>Put keyboard focus in the editor — after opening a panel, or restoring a tab.</summary>
    public async Task FocusAsync()
    {
        if (_created && _interop is not null)
            await _interop.FocusAsync(_id);
    }

    /// <summary>Whether the editor currently holds keyboard focus.</summary>
    public async Task<bool> HasFocusAsync() =>
        _created && _interop is not null && await _interop.HasFocusAsync(_id);

    /// <summary>
    /// Run a built-in Monaco action by id — the whole of Monaco's command surface through one method.
    ///
    /// <code>
    /// await editor.RunActionAsync("editor.action.formatDocument");
    /// await editor.RunActionAsync("actions.find");
    /// await editor.RunActionAsync("editor.action.commentLine");
    /// </code>
    ///
    /// <para>Returns whether Monaco knew the id as a registered action. A few built-ins (<c>undo</c>,
    /// <c>redo</c>) are commands rather than actions and are dispatched anyway but report <c>false</c> —
    /// so a <c>false</c> means "not a known action", which is what makes a mistyped id visible instead of
    /// a silent no-op.</para>
    /// </summary>
    public async Task<bool> RunActionAsync(string actionId) =>
        _created && _interop is not null && await _interop.RunActionAsync(_id, actionId);

    /// <summary>Disposes the Monaco editor, the JS module reference and the .NET callback handle.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            // _createIssued, not _created: see the field. dispose(id) no-ops on an id JavaScript never
            // registered, so calling it when creation had not finished is safe and calling it when
            // creation HAD finished is the whole point.
            if (_createIssued && _interop is not null)
                await _interop.DisposeEditorAsync(_id);
        }
        catch (JSDisconnectedException)
        {
            // Normal on navigation away or a dropped circuit — the JS side is already gone.
        }

        if (_interop is not null) await _interop.DisposeAsync();

        // Last: the JS side must not be able to call back into a disposed reference.
        _self?.Dispose();
    }
}
