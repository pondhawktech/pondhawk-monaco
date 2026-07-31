using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Pondhawk.Monaco;

/// <summary>
/// Typed wrapper over the <c>code-editor.js</c> module. Owns the module reference and disposes it.
///
/// <para>Separated from the component so the interop surface can be exercised without a renderer, and so
/// the component's logic is about state rather than string-typed JS calls.</para>
/// </summary>
internal sealed class CodeEditorInterop(IJSRuntime js, string baseUrl) : IAsyncDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _module = new(() =>
        js.InvokeAsync<IJSObjectReference>("import", $"{baseUrl}/code-editor.js").AsTask());

    private Task<IJSObjectReference> Module => _module.Value;

    public string BaseUrl { get; } = baseUrl;

    public async Task CreateAsync(string id, ElementReference host, EditorOptions options) =>
        await (await Module).InvokeVoidAsync("create", id, host, options);

    public async Task AttachAsync<T>(string id, DotNetObjectReference<T> reference) where T : class =>
        await (await Module).InvokeVoidAsync("attach", id, reference);

    public async Task<string> GetValueAsync(string id) =>
        await (await Module).InvokeAsync<string>("getValue", id);

    public async Task SetValueAsync(string id, string value) =>
        await (await Module).InvokeVoidAsync("setValue", id, value);

    public async Task SetLanguageAsync(string id, string language) =>
        await (await Module).InvokeVoidAsync("setLanguage", id, language);

    public async Task UpdateOptionsAsync(string id, LiveEditorOptions options) =>
        await (await Module).InvokeVoidAsync("updateOptions", id, options);

    public async Task SetMarkersAsync(string id, IReadOnlyList<EditorMarker> markers) =>
        await (await Module).InvokeVoidAsync("setMarkers", id, markers);

    public async Task<EditorPosition?> GetPositionAsync(string id) =>
        await (await Module).InvokeAsync<EditorPosition?>("getPosition", id);

    public async Task SetPositionAsync(string id, int line, int column) =>
        await (await Module).InvokeVoidAsync("setPosition", id, line, column);

    public async Task<EditorSelection?> GetSelectionAsync(string id) =>
        await (await Module).InvokeAsync<EditorSelection?>("getSelection", id);

    public async Task SetSelectionAsync(string id, EditorSelection selection) =>
        await (await Module).InvokeVoidAsync("setSelection", id, selection);

    public async Task FocusAsync(string id) =>
        await (await Module).InvokeVoidAsync("focus", id);

    public async Task<bool> HasFocusAsync(string id) =>
        await (await Module).InvokeAsync<bool>("hasTextFocus", id);

    public async Task<bool> RunActionAsync(string id, string actionId) =>
        await (await Module).InvokeAsync<bool>("runAction", id, actionId);

    public async Task ConfigureSchemaAsync(string schemaJson, IReadOnlyList<string>? fileMatch) =>
        await (await Module).InvokeVoidAsync("configureSchema", schemaJson, fileMatch);

    public async Task RevealLineAsync(string id, int line, int column) =>
        await (await Module).InvokeVoidAsync("revealLine", id, line, column);

    public async Task LayoutAsync(string id) =>
        await (await Module).InvokeVoidAsync("layout", id);

    public async Task SetThemeAsync(string theme) =>
        await (await Module).InvokeVoidAsync("setTheme", theme);

    public async Task DisposeEditorAsync(string id) =>
        await (await Module).InvokeVoidAsync("dispose", id);

    // --- Diff editor -------------------------------------------------------------------------------

    public async Task CreateDiffAsync(string id, ElementReference host, DiffOptions options) =>
        await (await Module).InvokeVoidAsync("createDiff", id, host, options);

    public async Task AttachDiffAsync<T>(string id, DotNetObjectReference<T> reference) where T : class =>
        await (await Module).InvokeVoidAsync("attachDiff", id, reference);

    public async Task<string> GetDiffValueAsync(string id, string side) =>
        await (await Module).InvokeAsync<string>("getDiffValue", id, side);

    public async Task SetDiffValueAsync(string id, string side, string value) =>
        await (await Module).InvokeVoidAsync("setDiffValue", id, side, value);

    public async Task SetDiffLanguageAsync(string id, string language) =>
        await (await Module).InvokeVoidAsync("setDiffLanguage", id, language);

    public async Task SetDiffOptionsAsync(string id, DiffViewOptions options) =>
        await (await Module).InvokeVoidAsync("setDiffOptions", id, options);

    public async Task GoToDiffAsync(string id, string target) =>
        await (await Module).InvokeVoidAsync("goToDiff", id, target);

    public async Task RevealFirstDiffAsync(string id) =>
        await (await Module).InvokeVoidAsync("revealFirstDiff", id);

    public async Task DiffLayoutAsync(string id) =>
        await (await Module).InvokeVoidAsync("diffLayout", id);

    public async Task DisposeDiffAsync(string id) =>
        await (await Module).InvokeVoidAsync("disposeDiff", id);

    public async ValueTask DisposeAsync()
    {
        if (!_module.IsValueCreated) return;

        try
        {
            (await Module).DisposeAsync().AsTask().Ignore();
        }
        catch (JSDisconnectedException)
        {
            // The circuit or page is already gone — there is nothing left to dispose on the other side.
            // Swallowing this is correct rather than defensive: it is the normal path on navigation away.
        }
    }
}

file static class TaskExtensions
{
    /// <summary>Fire-and-forget that still observes faults, so a failed disposal cannot become an
    /// unobserved TaskException that takes down the process.</summary>
    public static void Ignore(this Task task) =>
        task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
}

/// <summary>Construction options handed to Monaco. Serialized camelCase by Blazor's JS interop.</summary>
internal sealed record EditorOptions
{
    public required string BaseUrl { get; init; }
    public required string Value { get; init; }
    public required string Language { get; init; }
    public string Theme { get; init; } = "vs";
    public bool ReadOnly { get; init; }
    public bool Minimap { get; init; }
    public int TabSize { get; init; } = 2;
    public double FontSize { get; init; } = 12.5;
    public int DebounceMs { get; init; } = 300;

    /// <summary>Raw Monaco options, merged over the defaults above by the JS module.</summary>
    [JsonPropertyName("editorOptions")]
    public IReadOnlyDictionary<string, object>? RawOptions { get; init; }
}

/// <summary>Construction options for the diff editor. Serialized camelCase by Blazor's JS interop.</summary>
internal sealed record DiffOptions
{
    public required string BaseUrl { get; init; }
    public required string Original { get; init; }
    public required string Modified { get; init; }
    public required string Language { get; init; }
    public string Theme { get; init; } = "vs";

    /// <summary>Locks the MODIFIED (right) side. The original is governed by <see cref="OriginalEditable"/>.</summary>
    public bool ReadOnly { get; init; }
    public bool OriginalEditable { get; init; }

    /// <summary>Two panes when true, one interleaved pane when false.</summary>
    public bool SideBySide { get; init; } = true;

    /// <summary>Treat lines differing only in leading/trailing whitespace as unchanged.</summary>
    public bool IgnoreTrimWhitespace { get; init; }

    /// <summary>Show the change ruler down the right edge.</summary>
    public bool OverviewRuler { get; init; } = true;

    public bool Minimap { get; init; }
    public int TabSize { get; init; } = 2;
    public double FontSize { get; init; } = 12.5;
    public int DebounceMs { get; init; } = 300;

    [JsonPropertyName("editorOptions")]
    public IReadOnlyDictionary<string, object>? RawOptions { get; init; }
}

/// <summary>
/// The options that can be changed on a live editor, sent whenever one of them differs from what was
/// last applied.
///
/// <para><see cref="RawOptions"/> is compared by REFERENCE, as records do for dictionaries. A caller that
/// rebuilds the dictionary on every render therefore pushes an update on every render — hold it in a
/// field rather than constructing it inline in markup.</para>
/// </summary>
internal sealed record LiveEditorOptions
{
    public required bool ReadOnly { get; init; }
    public required bool Minimap { get; init; }
    public required int TabSize { get; init; }
    public required double FontSize { get; init; }

    [JsonPropertyName("editorOptions")]
    public IReadOnlyDictionary<string, object>? RawOptions { get; init; }
}

/// <summary>The subset of diff options that can be changed without rebuilding the editor.</summary>
internal sealed record DiffViewOptions
{
    public required bool SideBySide { get; init; }
    public required bool IgnoreTrimWhitespace { get; init; }
    public required bool OverviewRuler { get; init; }
    public required bool ReadOnly { get; init; }
    public required bool OriginalEditable { get; init; }
    public required bool Minimap { get; init; }
    public required int TabSize { get; init; }
    public required double FontSize { get; init; }

    [JsonPropertyName("editorOptions")]
    public IReadOnlyDictionary<string, object>? RawOptions { get; init; }
}
