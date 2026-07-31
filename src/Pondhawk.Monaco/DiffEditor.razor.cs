using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Pondhawk.Monaco;

/// <summary>
/// A Monaco-backed side-by-side (or inline) diff.
///
/// <code>
/// &lt;DiffEditor Original="@before" @bind-Modified="after" Language="yaml" /&gt;
/// </code>
///
/// <para>Read-only is the common case — showing what changed — and is what <see cref="ReadOnly"/>
/// defaults to. When the right-hand side is editable, edits arrive debounced through
/// <see cref="ModifiedChanged"/> under the same echo guard as <see cref="CodeEditor"/>.</para>
/// </summary>
public sealed partial class DiffEditor : ComponentBase, IAsyncDisposable
{
    private const string AssetPath = "_content/Pondhawk.Monaco/dist";

    [Inject] private IJSRuntime Js { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private readonly string _id = $"pde-{Guid.NewGuid():N}";
    private ElementReference _host;
    private CodeEditorInterop? _interop;
    private DotNetObjectReference<DiffEditor>? _self;

    private bool _created;
    private string _lastModifiedFromEditor = string.Empty;
    private string? _appliedOriginal;
    private string? _appliedLanguage;
    private string? _appliedTheme;
    private DiffViewOptions? _appliedView;

    /// <summary>The left-hand document — what is being compared against.</summary>
    [Parameter] public string Original { get; set; } = string.Empty;

    /// <summary>The right-hand document. Supports <c>@bind-Modified</c> when <see cref="ReadOnly"/> is false.</summary>
    [Parameter] public string Modified { get; set; } = string.Empty;

    /// <summary>Raised after the debounce elapses, carrying the right-hand side's current text.
    /// Never raised while <see cref="ReadOnly"/> is true and <see cref="OriginalEditable"/> is false.</summary>
    [Parameter] public EventCallback<string> ModifiedChanged { get; set; }

    /// <summary>Raised whenever Monaco finishes recomputing the diff, with the number of changed regions.
    /// The count is only available through this callback — the computation is asynchronous.</summary>
    [Parameter] public EventCallback<int> OnDiffComputed { get; set; }

    /// <summary>Monaco language id, applied to both sides.</summary>
    [Parameter] public string Language { get; set; } = "plaintext";

    /// <summary>Monaco theme id. Built-ins are <c>vs</c>, <c>vs-dark</c> and <c>hc-black</c>.</summary>
    [Parameter] public string Theme { get; set; } = "vs";

    /// <summary>Lock the right-hand side. Defaults to true: a diff is usually shown, not edited.</summary>
    [Parameter] public bool ReadOnly { get; set; } = true;

    /// <summary>Allow editing the left-hand side too. Off by default — the original is normally a fixed
    /// reference point, and its edits are not reported back.</summary>
    [Parameter] public bool OriginalEditable { get; set; }

    /// <summary>Two panes when true (the default), one interleaved pane when false.</summary>
    [Parameter] public bool SideBySide { get; set; } = true;

    /// <summary>Treat lines differing only in leading or trailing whitespace as unchanged. Useful when
    /// comparing re-serialized documents, where indentation churn would otherwise dominate.</summary>
    [Parameter] public bool IgnoreTrimWhitespace { get; set; }

    /// <summary>Show the change ruler down the right edge.</summary>
    [Parameter] public bool OverviewRuler { get; set; } = true;

    /// <summary>Show the minimap overview. Off by default — a diff is already two panes wide.</summary>
    [Parameter] public bool Minimap { get; set; }
    /// <summary>Spaces per indent level.</summary>
    [Parameter] public int TabSize { get; set; } = 2;
    /// <summary>Editor font size in pixels.</summary>
    [Parameter] public double FontSize { get; set; } = 12.5;

    /// <summary>How long typing must pause before <see cref="ModifiedChanged"/> fires.</summary>
    [Parameter] public int DebounceMs { get; set; } = 300;

    /// <summary>Escape hatch — raw Monaco diff-editor options, merged over this component's defaults.</summary>
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
            var baseUrl = new Uri(new Uri(Navigation.BaseUri), AssetPath).ToString().TrimEnd('/');

            _interop = new CodeEditorInterop(Js, baseUrl);
            _self = DotNetObjectReference.Create(this);

            _lastModifiedFromEditor = Modified;
            _appliedOriginal = Original;
            _appliedLanguage = Language;
            _appliedTheme = Theme;
            _appliedView = CurrentView();

            await _interop.CreateDiffAsync(_id, _host, new DiffOptions
            {
                BaseUrl = baseUrl,
                Original = Original,
                Modified = Modified,
                Language = Language,
                Theme = Theme,
                ReadOnly = ReadOnly,
                OriginalEditable = OriginalEditable,
                SideBySide = SideBySide,
                IgnoreTrimWhitespace = IgnoreTrimWhitespace,
                OverviewRuler = OverviewRuler,
                Minimap = Minimap,
                TabSize = TabSize,
                FontSize = FontSize,
                DebounceMs = DebounceMs,
                RawOptions = EditorOptions,
            });

            await _interop.AttachDiffAsync(_id, _self);
            _created = true;
            return;
        }

        if (!_created || _interop is null) return;

        // Push only what changed — re-applying a side would reset its model and lose the scroll position.
        if (Original != _appliedOriginal)
        {
            _appliedOriginal = Original;
            await _interop.SetDiffValueAsync(_id, "original", Original);
        }

        if (Modified != _lastModifiedFromEditor)
        {
            _lastModifiedFromEditor = Modified;
            await _interop.SetDiffValueAsync(_id, "modified", Modified);
        }

        if (Language != _appliedLanguage)
        {
            _appliedLanguage = Language;
            await _interop.SetDiffLanguageAsync(_id, Language);
        }

        if (Theme != _appliedTheme)
        {
            _appliedTheme = Theme;
            await _interop.SetThemeAsync(Theme);
        }

        var view = CurrentView();
        if (view != _appliedView)
        {
            _appliedView = view;
            await _interop.SetDiffOptionsAsync(_id, view);
        }
    }

    private DiffViewOptions CurrentView() => new()
    {
        SideBySide = SideBySide,
        IgnoreTrimWhitespace = IgnoreTrimWhitespace,
        ReadOnly = ReadOnly,
        OriginalEditable = OriginalEditable,
    };

    /// <summary>
    /// Called from JavaScript after the debounce elapses. Recording the text before raising
    /// <see cref="ModifiedChanged"/> is the binding-echo guard — see <see cref="CodeEditor"/>.
    /// </summary>
    [JSInvokable]
    public async Task OnModifiedChanged(string value, int revision)
    {
        _ = revision;
        _lastModifiedFromEditor = value;
        Modified = value;

        if (ModifiedChanged.HasDelegate)
            await ModifiedChanged.InvokeAsync(value);
    }

    /// <summary>Called from JavaScript each time Monaco finishes recomputing the diff.</summary>
    [JSInvokable]
    public async Task OnDiffUpdated(int changeCount)
    {
        if (OnDiffComputed.HasDelegate)
            await OnDiffComputed.InvokeAsync(changeCount);
    }

    /// <summary>Jump to the next change.</summary>
    public async Task GoToNextDiffAsync()
    {
        if (_created && _interop is not null) await _interop.GoToDiffAsync(_id, "next");
    }

    /// <summary>Jump to the previous change.</summary>
    public async Task GoToPreviousDiffAsync()
    {
        if (_created && _interop is not null) await _interop.GoToDiffAsync(_id, "previous");
    }

    /// <summary>Scroll to the first change, waiting for the diff computation if it is still running.</summary>
    public async Task RevealFirstDiffAsync()
    {
        if (_created && _interop is not null) await _interop.RevealFirstDiffAsync(_id);
    }

    /// <summary>Re-measure after the container resizes. Required — automatic layout is off by design.</summary>
    public async Task LayoutAsync()
    {
        if (_created && _interop is not null) await _interop.DiffLayoutAsync(_id);
    }

    /// <summary>Read a side's current text directly, bypassing the debounce.</summary>
    public async Task<string> GetValueAsync(DiffSide side = DiffSide.Modified)
    {
        if (!_created || _interop is null)
            return side == DiffSide.Original ? Original : Modified;

        return await _interop.GetDiffValueAsync(_id, side == DiffSide.Original ? "original" : "modified");
    }

    /// <summary>Disposes the Monaco diff editor, both models, the JS module reference and the callback handle.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_created && _interop is not null)
                await _interop.DisposeDiffAsync(_id);
        }
        catch (JSDisconnectedException)
        {
            // Normal on navigation away or a dropped circuit — the JS side is already gone.
        }

        if (_interop is not null) await _interop.DisposeAsync();

        _self?.Dispose();
    }
}

/// <summary>Which pane of a <see cref="DiffEditor"/> to read.</summary>
public enum DiffSide
{
    /// <summary>The left-hand document — what is being compared against.</summary>
    Original,
    /// <summary>The right-hand document.</summary>
    Modified,
}
