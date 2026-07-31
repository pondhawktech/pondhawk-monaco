namespace Pondhawk.Monaco;

/// <summary>
/// A caret location in the document.
///
/// <para>Positions are <b>1-based</b>, matching Monaco and <see cref="EditorMarker"/>: the first
/// character of the first line is <c>Line 1, Column 1</c>.</para>
/// </summary>
public sealed record EditorPosition
{
    /// <summary>1-based line number.</summary>
    public required int Line { get; init; }

    /// <summary>1-based column. Column 1 is before the first character.</summary>
    public required int Column { get; init; }
}

/// <summary>
/// A selected range of the document.
///
/// <para>1-based, and the end is <b>exclusive</b> — the same convention as <see cref="EditorMarker"/>.
/// A selection of the single character at column 5 is <c>StartColumn = 5, EndColumn = 6</c>. An empty
/// range (start equal to end) is a caret with nothing selected.</para>
/// </summary>
public sealed record EditorSelection
{
    /// <summary>1-based line the selection starts on.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based column the selection starts at.</summary>
    public required int StartColumn { get; init; }

    /// <summary>1-based line the selection ends on.</summary>
    public required int EndLine { get; init; }

    /// <summary>1-based column the selection ends at, exclusive.</summary>
    public required int EndColumn { get; init; }
}
