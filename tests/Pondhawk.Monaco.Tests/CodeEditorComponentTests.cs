using System.Text.Json;
using Bunit;
using BunitContext = Bunit.TestContext;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Pondhawk.Monaco.Tests;

/// <summary>
/// Component behaviour, with the JS module mocked.
///
/// <para>What matters here is not that Monaco works — the demo proves that in a browser — but that the
/// component issues the right calls in the right order, and crucially that it does NOT issue the one call
/// that would break typing.</para>
/// </summary>
[TestFixture]
public class CodeEditorComponentTests
{
    // ABSOLUTE, because the component composes the path from NavigationManager.BaseUri rather than
    // using a relative one — bUnit's fake base is http://localhost/. That this must be absolute is the
    // point: a relative path would resolve against the document URL and break under a non-root base href.
    private const string ModulePath =
        "http://localhost/_content/Pondhawk.Monaco/dist/code-editor.js";

    // Matches Blazor's JS interop serializer configuration.
    private static readonly JsonSerializerOptions InteropJson = new(JsonSerializerDefaults.Web);

    private static (BunitContext Ctx, BunitJSModuleInterop Module) Arrange()
    {
        var ctx = new BunitContext();
        ctx.Services.AddLogging();

        // The component builds its module path from NavigationManager.BaseUri; bUnit's fake base is "/".
        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.Mode = JSRuntimeMode.Loose;   // every export resolves; we assert on specific invocations
        return (ctx, module);
    }

    [Test]
    public void Renders_a_host_element_for_monaco_to_own()
    {
        var (ctx, _) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<CodeEditor>(p => p.Add(c => c.Class, "tall"));

        // Monaco renders INTO this element and owns everything inside it. Blazor must contribute an
        // empty host and nothing more, or the two renderers fight over the same DOM.
        var host = cut.Find("div.pondhawk-code-editor");
        host.ClassList.ShouldContain("tall");
        host.ChildElementCount.ShouldBe(0);
    }

    [Test]
    public void Creates_and_attaches_on_first_render()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        ctx.Render<CodeEditor>(p => p
            .Add(c => c.Value, "hello")
            .Add(c => c.Language, "sql"));

        module.VerifyInvoke("create");
        // attach must follow create: JS holds the .NET reference against an editor that already exists.
        module.VerifyInvoke("attach");
    }

    [Test]
    public void Passes_an_absolute_worker_base_url()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        ctx.Render<CodeEditor>();

        // A relative path would resolve against the document URL rather than <base href>, and would only
        // fail when a language service is first needed — long after startup.
        var options = module.Invocations["create"].Single().Arguments[2]!;
        var baseUrl = options.GetType().GetProperty("BaseUrl")!.GetValue(options) as string;

        baseUrl.ShouldNotBeNull();
        Uri.IsWellFormedUriString(baseUrl, UriKind.Absolute).ShouldBeTrue();
        baseUrl.ShouldEndWith("_content/Pondhawk.Monaco/dist");
    }

    /// <summary>
    /// THE ECHO GUARD. When JS reports an edit and the parent hands that same text straight back as
    /// <c>Value</c>, the component must not push it into the editor — doing so resets the model mid-typing
    /// and throws the caret to the end of the document.
    /// </summary>
    [Test]
    public async Task Does_not_push_back_the_value_the_editor_just_reported()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var bound = "start";
        var cut = ctx.Render<CodeEditor>(p => p
            .Add(c => c.Value, bound)
            .Add(c => c.ValueChanged, v => bound = v));

        await cut.InvokeAsync(() => cut.Instance.OnDocumentChanged("typed by user", 1));
        cut.Render(p => p.Add(c => c.Value, bound));   // parent re-renders with the echo

        bound.ShouldBe("typed by user");
        module.Invocations.Identifiers.ShouldNotContain("setValue",
            "the value came FROM the editor, so pushing it back would reset the model and move the caret");
    }

    [Test]
    public void Pushes_a_value_that_genuinely_came_from_outside()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<CodeEditor>(p => p.Add(c => c.Value, "start"));

        // A load, an undo from the host, a format action — anything the editor did not originate.
        cut.Render(p => p.Add(c => c.Value, "replaced externally"));

        module.VerifyInvoke("setValue");
    }

    [Test]
    public void Applies_language_and_theme_only_when_they_change()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<CodeEditor>(p => p.Add(c => c.Language, "yaml"));

        // Re-rendering with identical parameters must not re-apply: setModelLanguage resets the language
        // service, which would drop diagnostics and completions on every parent render.
        cut.Render(p => p.Add(c => c.Language, "yaml"));
        module.Invocations.Identifiers.ShouldNotContain("setLanguage");

        cut.Render(p => p.Add(c => c.Language, "json"));
        module.VerifyInvoke("setLanguage");
    }

    [Test]
    public void Applies_a_schema_once_rather_than_on_every_render()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        const string schema = """{"type":"object"}""";
        var cut = ctx.Render<CodeEditor>(p => p.Add(c => c.Schema, schema));

        cut.Render(p => p.Add(c => c.Schema, schema));
        cut.Render(p => p.Add(c => c.Schema, schema));

        // configureSchema tears down and rebuilds the YAML language service; doing it per render would
        // make completion flicker and drop in-flight requests.
        module.Invocations["configureSchema"].Count.ShouldBe(1);
    }

    /// <summary>
    /// These options were once passed to create() and never revisited, so a parameter that changed after
    /// the first render silently did nothing while looking perfectly live. The push must happen — and
    /// must NOT happen when nothing moved, since updateOptions on every parent render is interop churn.
    /// </summary>
    [Test]
    public void Applies_option_changes_to_the_live_editor()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<CodeEditor>(p => p.Add(c => c.ReadOnly, false));

        cut.Render(p => p.Add(c => c.ReadOnly, false));
        module.Invocations.Identifiers.ShouldNotContain("updateOptions",
            "nothing changed, so re-applying would be pure interop churn");

        cut.Render(p => p.Add(c => c.ReadOnly, true));
        module.VerifyInvoke("updateOptions");
    }

    [TestCase("readOnly")]
    [TestCase("minimap")]
    [TestCase("tabSize")]
    [TestCase("fontSize")]
    [TestCase("editorOptions")]
    public void Emits_the_live_option_names_the_js_module_reads(string property)
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<CodeEditor>(p => p.Add(c => c.FontSize, 12.5));
        cut.Render(p => p.Add(c => c.FontSize, 18.0));

        var options = module.Invocations["updateOptions"].Single().Arguments[1]!;
        var json = JsonSerializer.SerializeToElement(options, InteropJson);

        json.TryGetProperty(property, out _).ShouldBeTrue();
    }

    [Test]
    public async Task Forwards_cursor_selection_and_focus_to_the_editor()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<CodeEditor>();

        await cut.Instance.SetPositionAsync(3, 5);
        await cut.Instance.SetSelectionAsync(new EditorSelection
        {
            StartLine = 1, StartColumn = 1, EndLine = 2, EndColumn = 4,
        });
        await cut.Instance.FocusAsync();

        module.VerifyInvoke("setPosition");
        module.VerifyInvoke("setSelection");
        module.VerifyInvoke("focus");
    }

    [Test]
    public void Pushes_decorations_only_when_the_set_changes()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        EditorDecoration[] one = [new() { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 5 }];
        // A DIFFERENT list instance holding an EQUAL decoration: records compare by value, so this must
        // not count as a change. Rebuilding the list each render is the normal Blazor pattern.
        EditorDecoration[] same = [new() { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 5 }];
        EditorDecoration[] other = [new() { StartLine = 9, StartColumn = 1, EndLine = 9, EndColumn = 5 }];

        var cut = ctx.Render<CodeEditor>(p => p.Add(c => c.Decorations, one));
        module.Invocations["setDecorations"].Count.ShouldBe(1);

        cut.Render(p => p.Add(c => c.Decorations, same));
        module.Invocations["setDecorations"].Count.ShouldBe(1, "an equal set is not a change");

        cut.Render(p => p.Add(c => c.Decorations, other));
        module.Invocations["setDecorations"].Count.ShouldBe(2);
    }

    /// <summary>
    /// Markers had no change guard at all: every parent render re-sent the whole list, rebuilding the
    /// squiggles and the overview ruler each time.
    /// </summary>
    [Test]
    public void Pushes_markers_only_when_the_set_changes()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        EditorMarker[] one =
            [new() { StartLine = 3, StartColumn = 1, EndLine = 3, EndColumn = 9, Message = "boom" }];
        EditorMarker[] same =
            [new() { StartLine = 3, StartColumn = 1, EndLine = 3, EndColumn = 9, Message = "boom" }];

        var cut = ctx.Render<CodeEditor>(p => p.Add(c => c.Diagnostics, one));
        cut.Render(p => p.Add(c => c.Diagnostics, same));

        module.Invocations["setMarkers"].Count.ShouldBe(1);
    }

    [TestCase("startLine")]
    [TestCase("endColumn")]
    [TestCase("className")]
    [TestCase("glyphMarginClassName")]
    [TestCase("wholeLine")]
    [TestCase("hoverMessage")]
    [TestCase("overviewRulerColor")]
    public void Emits_the_decoration_names_the_js_module_reads(string property)
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        ctx.Render<CodeEditor>(p => p.Add(c => c.Decorations,
            [new EditorDecoration { StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2 }]));

        var sent = module.Invocations["setDecorations"].Single().Arguments[1]!;
        var json = JsonSerializer.SerializeToElement(sent, InteropJson);

        json[0].TryGetProperty(property, out _).ShouldBeTrue();
    }

    [TestCase("name")]
    [TestCase("base")]
    [TestCase("inherit")]
    [TestCase("rules")]
    [TestCase("colors")]
    public async Task Emits_the_theme_names_the_js_module_reads(string property)
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<CodeEditor>();
        await cut.Instance.DefineThemeAsync(new EditorTheme
        {
            Name = "pondhawk-dark",
            Base = "vs-dark",
            Rules = [new EditorTokenRule { Token = "comment", Foreground = "#6a9955" }],
            Colors = new Dictionary<string, string> { ["editor.background"] = "#1e1e1e" },
        });

        var sent = module.Invocations["defineTheme"].Single().Arguments[0]!;
        var json = JsonSerializer.SerializeToElement(sent, InteropJson);

        json.TryGetProperty(property, out _).ShouldBeTrue();
    }

    [Test]
    public async Task Runs_a_named_monaco_action()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<CodeEditor>();

        await cut.Instance.RunActionAsync("editor.action.formatDocument");

        // The action id is the whole payload — a typo is only visible because the JS side reports
        // whether Monaco recognised it, so the id must arrive intact.
        var invocation = module.Invocations["runAction"].Single();
        invocation.Arguments[1].ShouldBe("editor.action.formatDocument");
    }

    /// <summary>
    /// Narrowing or widening which documents a schema covers is a change to the schema configuration.
    /// Comparing only the schema TEXT meant SchemaFileMatch was read once at first render and ignored
    /// thereafter — live-looking and inert, the same trap as the construction-only options.
    /// </summary>
    [Test]
    public void Reapplies_the_schema_when_only_its_file_match_changes()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        const string schema = """{"type":"object"}""";
        var cut = ctx.Render<CodeEditor>(p => p
            .Add(c => c.Schema, schema)
            .Add(c => c.SchemaFileMatch, ["*.yaml"]));

        // A different list instance holding the same values is not a change.
        cut.Render(p => p.Add(c => c.Schema, schema).Add(c => c.SchemaFileMatch, ["*.yaml"]));
        module.Invocations["configureSchema"].Count.ShouldBe(1);

        cut.Render(p => p.Add(c => c.Schema, schema).Add(c => c.SchemaFileMatch, ["*.yaml", "*.yml"]));
        module.Invocations["configureSchema"].Count.ShouldBe(2, "the schema now covers different documents");
    }

    [Test]
    public async Task Disposes_the_editor_before_releasing_the_dotnet_reference()
    {
        var (ctx, module) = Arrange();
        var cut = ctx.Render<CodeEditor>();

        await cut.Instance.DisposeAsync();

        // Without this the Monaco instance and its model outlive the component — a leak on every
        // navigation, which in a long-lived SPA accumulates until the tab slows to a crawl.
        module.VerifyInvoke("dispose");
        ctx.Dispose();
    }
}
