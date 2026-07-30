using Bunit;
using BunitContext = Bunit.TestContext;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Pondhawk.Blazor.CodeEditor.Tests;

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
        "http://localhost/_content/Pondhawk.Blazor.CodeEditor/dist/code-editor.js";

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
        baseUrl.ShouldEndWith("_content/Pondhawk.Blazor.CodeEditor/dist");
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
