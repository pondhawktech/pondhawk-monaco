namespace Pondhawk.Monaco;

/// <summary>
/// A styled region of the document — a highlighted line, a glyph in the margin, an inline background.
///
/// <para>Decorations are styling; <see cref="EditorMarker"/> is diagnostics. Use a marker for something
/// that is wrong, and a decoration for search hits, merge-conflict regions, coverage gutters, blame
/// lines, or anything else that colours the document without claiming a problem.</para>
///
/// <para>Positions are <b>1-based</b> and end positions are <b>exclusive</b>, matching
/// <see cref="EditorMarker"/> and <see cref="EditorSelection"/>.</para>
///
/// <para>The styling fields name CSS classes, which your application supplies. Nothing is scoped by this
/// component, so prefix them to avoid colliding with Monaco's own — a class that does not exist renders
/// nothing at all, with no error.</para>
/// </summary>
public sealed record EditorDecoration
{
    /// <summary>1-based line the decoration starts on.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based column the decoration starts at.</summary>
    public required int StartColumn { get; init; }

    /// <summary>1-based line the decoration ends on.</summary>
    public required int EndLine { get; init; }

    /// <summary>1-based column the decoration ends at, exclusive.</summary>
    public required int EndColumn { get; init; }

    /// <summary>CSS class applied to the whole decorated range — the usual choice for a background.</summary>
    public string? ClassName { get; init; }

    /// <summary>CSS class applied to the text itself, for colour or weight rather than a background.</summary>
    public string? InlineClassName { get; init; }

    /// <summary>CSS class for an icon in the glyph margin, left of the line numbers. The glyph margin
    /// must be enabled — it is on by default, but <c>EditorOptions</c> can turn it off.</summary>
    public string? GlyphMarginClassName { get; init; }

    /// <summary>CSS class for the narrow strip between the line numbers and the text.</summary>
    public string? LineNumberClassName { get; init; }

    /// <summary>Extend the decoration across the full line regardless of the columns given.</summary>
    public bool WholeLine { get; init; }

    /// <summary>Markdown shown when the pointer rests on the range.</summary>
    public string? HoverMessage { get; init; }

    /// <summary>Colour of the mark in the overview ruler down the right edge — <c>#rrggbb</c>, or any
    /// Monaco theme colour id. Omit for no mark.</summary>
    public string? OverviewRulerColor { get; init; }
}
