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
    private string _lastValueFromEditor = string.Empty;
    private string? _appliedLanguage;
    private string? _appliedSchema;
    private string? _appliedTheme;

    /// <summary>The document text. Supports <c>@bind-Value</c>.</summary>
    [Parameter] public string Value { get; set; } = string.Empty;

    /// <summary>Raised after the debounce elapses, carrying the editor's current text.</summary>
    [Parameter] public EventCallback<string> ValueChanged { get; set; }

    /// <summary>Monaco language id — <c>csharp</c>, <c>yaml</c>, <c>json</c>, <c>sql</c>, <c>markdown</c>…</summary>
    [Parameter] public string Language { get; set; } = "plaintext";

    /// <summary>Monaco theme id. Built-ins are <c>vs</c>, <c>vs-dark</c> and <c>hc-black</c>.</summary>
    [Parameter] public string Theme { get; set; } = "vs";

    /// <summary>
    /// A JSON Schema (as JSON text) driving completion, hover and validation. Applies to YAML and JSON;
    /// ignored for languages without a schema-aware service.
    /// </summary>
    [Parameter] public string? Schema { get; set; }

    /// <summary>Which documents the schema applies to. Defaults to all of them.</summary>
    [Parameter] public IReadOnlyList<string>? SchemaFileMatch { get; set; }

    /// <summary>Host-supplied diagnostics, shown alongside the language service's own.</summary>
    [Parameter] public IReadOnlyList<EditorMarker>? Diagnostics { get; set; }

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

            await _interop.CreateAsync(_id, _host, new EditorOptions
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

            await _interop.AttachAsync(_id, _self);
            _created = true;

            // Schema and markers may have been supplied before the editor existed.
            await ApplySchemaAsync();
            await ApplyMarkersAsync();
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

        await ApplySchemaAsync();
        await ApplyMarkersAsync();
    }

    private async Task ApplySchemaAsync()
    {
        if (_interop is null || Schema is null || Schema == _appliedSchema) return;

        _appliedSchema = Schema;
        await _interop.ConfigureSchemaAsync(Schema, SchemaFileMatch);
    }

    private async Task ApplyMarkersAsync()
    {
        if (_interop is null || Diagnostics is null) return;

        await _interop.SetMarkersAsync(_id, Diagnostics);
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
        _ = revision; // reserved: lets a future implementation discard out-of-order edits
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

    /// <summary>Disposes the Monaco editor, the JS module reference and the .NET callback handle.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_created && _interop is not null)
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
