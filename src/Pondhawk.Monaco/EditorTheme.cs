namespace Pondhawk.Monaco;

/// <summary>
/// A custom Monaco theme, so an editor can match the application around it rather than being limited to
/// <c>vs</c>, <c>vs-dark</c> and <c>hc-black</c>.
///
/// <para>Register with <see cref="CodeEditor.DefineThemeAsync"/>, then set <c>Theme</c> to
/// <see cref="Name"/>.</para>
///
/// <para><b>Themes are global.</b> Monaco keeps one theme registry and one active theme for the page, so
/// defining or selecting a theme affects every editor on it. That is Monaco's design, not a limitation
/// of this component.</para>
/// </summary>
public sealed record EditorTheme
{
    /// <summary>The id to pass to <c>Theme</c>. Registering an existing name replaces it.</summary>
    public required string Name { get; init; }

    /// <summary>Built-in theme to start from: <c>vs</c>, <c>vs-dark</c> or <c>hc-black</c>.</summary>
    public string Base { get; init; } = "vs";

    /// <summary>Keep the base theme's rules and fill in from there. Turn this off only to style every
    /// token yourself — an un-inherited theme with a short rule list renders mostly uncoloured.</summary>
    public bool Inherit { get; init; } = true;

    /// <summary>Token colouring rules.</summary>
    public IReadOnlyList<EditorTokenRule>? Rules { get; init; }

    /// <summary>
    /// Editor chrome colours, keyed by Monaco colour id — <c>editor.background</c>,
    /// <c>editorLineNumber.foreground</c>, <c>editor.selectionBackground</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Colors { get; init; }
}

/// <summary>
/// One token-colouring rule within an <see cref="EditorTheme"/>.
///
/// <para>Colours may be written with or without a leading <c>#</c>. Monaco is inconsistent here — rule
/// colours must omit it while theme colours require it, and the wrong form throws rather than being
/// ignored — so both are normalised before they reach Monaco.</para>
/// </summary>
public sealed record EditorTokenRule
{
    /// <summary>Token type to colour: <c>comment</c>, <c>string</c>, <c>keyword</c>, <c>number</c>, or a
    /// language-scoped form such as <c>string.yaml</c>. An empty string is the default rule.</summary>
    public required string Token { get; init; }

    /// <summary>Text colour, <c>#rrggbb</c>.</summary>
    public string? Foreground { get; init; }

    /// <summary>Background colour, <c>#rrggbb</c>.</summary>
    public string? Background { get; init; }

    /// <summary>Any space-separated combination of <c>italic</c>, <c>bold</c> and <c>underline</c>.</summary>
    public string? FontStyle { get; init; }
}
