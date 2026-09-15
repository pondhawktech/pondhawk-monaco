using System.Text.Json.Serialization;

namespace Pondhawk.Monaco;

/// <summary>Severity of a squiggle in the editor gutter and overview ruler.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MarkerSeverity>))]
public enum MarkerSeverity
{
    /// <summary>Red squiggle. Blocks, in the host application's judgement.</summary>
    [JsonStringEnumMemberName("error")] Error,
    /// <summary>Yellow squiggle. Advisory.</summary>
    [JsonStringEnumMemberName("warning")] Warning,
    /// <summary>Informational hint.</summary>
    [JsonStringEnumMemberName("info")] Info,
}

/// <summary>
/// A diagnostic the host application wants shown in the editor — a validation error, a compiler message,
/// a lint finding.
///
/// <para>These are namespaced separately from the language service's own diagnostics, so schema errors
/// from monaco-yaml and rule violations from your app coexist rather than overwriting each other.</para>
///
/// <para>Positions are <b>1-based</b>, matching Monaco (and most compilers). End positions are exclusive:
/// to underline a single character at column 5, use <c>StartColumn = 5, EndColumn = 6</c>.</para>
/// </summary>
public sealed record EditorMarker
{
    /// <summary>1-based line the underline starts on.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based column the underline starts at.</summary>
    public required int StartColumn { get; init; }

    /// <summary>1-based line the underline ends on.</summary>
    public required int EndLine { get; init; }

    /// <summary>1-based column the underline ends at, exclusive.</summary>
    public required int EndColumn { get; init; }

    /// <summary>What is wrong, shown when the pointer rests on the underline.</summary>
    public required string Message { get; init; }

    /// <summary>The squiggle's colour and weight. Defaults to <see cref="MarkerSeverity.Error"/>.</summary>
    public MarkerSeverity Severity { get; init; } = MarkerSeverity.Error;

    /// <summary>Shown in the hover next to the message — typically a rule id or tool name.</summary>
    public string? Source { get; init; }
}
