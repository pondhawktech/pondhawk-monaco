using System.Text.Json;
using Bunit;
using BunitContext = Bunit.TestContext;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Pondhawk.Monaco.Tests;

/// <summary>
/// Diff-editor behaviour, with the JS module mocked.
///
/// <para>The diff holds TWO models, so the failure modes the single editor has are doubled: pushing a side
/// that did not change resets that model and loses its scroll position, and pushing back the text the
/// editor just reported moves the caret. These tests pin both, plus the disposal order.</para>
/// </summary>
[TestFixture]
public class DiffEditorComponentTests
{
    // Matches Blazor's JS interop serializer configuration.
    private static readonly JsonSerializerOptions InteropJson = new(JsonSerializerDefaults.Web);

    private const string ModulePath =
        "http://localhost/_content/Pondhawk.Monaco/dist/code-editor.js";

    private static (BunitContext Ctx, BunitJSModuleInterop Module) Arrange()
    {
        var ctx = new BunitContext();
        ctx.Services.AddLogging();

        var module = ctx.JSInterop.SetupModule(ModulePath);
        module.Mode = JSRuntimeMode.Loose;
        return (ctx, module);
    }

    [Test]
    public void Renders_a_host_element_for_monaco_to_own()
    {
        var (ctx, _) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<DiffEditor>(p => p.Add(c => c.Class, "tall"));

        var host = cut.Find("div.pondhawk-diff-editor");
        host.ClassList.ShouldContain("tall");
        host.ChildElementCount.ShouldBe(0);
    }

    [Test]
    public void Creates_and_attaches_on_first_render()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        ctx.Render<DiffEditor>(p => p
            .Add(c => c.Original, "before")
            .Add(c => c.Modified, "after")
            .Add(c => c.Language, "yaml"));

        module.VerifyInvoke("createDiff");
        module.VerifyInvoke("attachDiff");
    }

    [Test]
    public void Is_read_only_by_default()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        ctx.Render<DiffEditor>();

        // A diff is usually shown rather than edited. Defaulting to editable would let a review surface
        // silently accept changes that nothing is bound to collect.
        var options = module.Invocations["createDiff"].Single().Arguments[2]!;
        var readOnly = options.GetType().GetProperty("ReadOnly")!.GetValue(options);

        readOnly.ShouldBe(true);
    }

    [Test]
    public void Passes_an_absolute_worker_base_url()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        ctx.Render<DiffEditor>();

        var options = module.Invocations["createDiff"].Single().Arguments[2]!;
        var baseUrl = options.GetType().GetProperty("BaseUrl")!.GetValue(options) as string;

        baseUrl.ShouldNotBeNull();
        Uri.IsWellFormedUriString(baseUrl, UriKind.Absolute).ShouldBeTrue();
        baseUrl.ShouldEndWith("_content/Pondhawk.Monaco/dist");
    }

    /// <summary>The echo guard, on the modified side.</summary>
    [Test]
    public async Task Does_not_push_back_the_text_the_editor_just_reported()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var bound = "after";
        var cut = ctx.Render<DiffEditor>(p => p
            .Add(c => c.ReadOnly, false)
            .Add(c => c.Modified, bound)
            .Add(c => c.ModifiedChanged, v => bound = v));

        await cut.InvokeAsync(() => cut.Instance.OnModifiedChanged("typed by user", 1));
        cut.Render(p => p.Add(c => c.Modified, bound));

        bound.ShouldBe("typed by user");
        module.Invocations.Identifiers.ShouldNotContain("setDiffValue",
            "the text came FROM the editor, so pushing it back would reset the model and move the caret");
    }

    [Test]
    public void Pushes_each_side_only_when_that_side_changes()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<DiffEditor>(p => p
            .Add(c => c.Original, "before")
            .Add(c => c.Modified, "after"));

        cut.Render(p => p.Add(c => c.Original, "before").Add(c => c.Modified, "after"));
        module.Invocations.Identifiers.ShouldNotContain("setDiffValue");

        // Changing only the original must not also rewrite the modified model, which would throw away
        // the right-hand pane's scroll position and undo stack for no reason.
        cut.Render(p => p.Add(c => c.Original, "before, edited").Add(c => c.Modified, "after"));

        var sides = module.Invocations["setDiffValue"].Select(i => i.Arguments[1] as string).ToList();
        sides.ShouldBe(["original"]);
    }

    [Test]
    public void Updates_view_options_in_place_rather_than_rebuilding()
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<DiffEditor>(p => p.Add(c => c.SideBySide, true));

        cut.Render(p => p.Add(c => c.SideBySide, true));
        module.Invocations.Identifiers.ShouldNotContain("setDiffOptions");

        // Toggling inline/side-by-side must go through updateOptions. Recreating the editor would lose
        // both models, the scroll position, and the undo stack.
        cut.Render(p => p.Add(c => c.SideBySide, false));

        module.VerifyInvoke("setDiffOptions");
        module.Invocations.Identifiers.Count(i => i == "createDiff").ShouldBe(1);
    }

    [Test]
    public async Task Reports_the_change_count_from_the_async_diff_computation()
    {
        var (ctx, _) = Arrange();
        using var _ctx = ctx;

        var reported = -1;
        var cut = ctx.Render<DiffEditor>(p => p.Add(c => c.OnDiffComputed, n => reported = n));

        // getLineChanges() returns null until Monaco finishes computing, so the count can only arrive
        // through this callback — a caller reading it synchronously after setModel would always see zero.
        await cut.InvokeAsync(() => cut.Instance.OnDiffUpdated(7));

        reported.ShouldBe(7);
    }

    /// <summary>
    /// The property names <c>code-editor.js</c> destructures off the options bag. The boundary is untyped:
    /// a rename or a casing slip arrives as <c>undefined</c> and silently falls back to the JS-side default,
    /// so the symptom is an option that quietly does nothing rather than an error.
    /// </summary>
    [TestCase("baseUrl")]
    [TestCase("original")]
    [TestCase("modified")]
    [TestCase("language")]
    [TestCase("theme")]
    [TestCase("readOnly")]
    [TestCase("originalEditable")]
    [TestCase("sideBySide")]
    [TestCase("ignoreTrimWhitespace")]
    [TestCase("overviewRuler")]
    [TestCase("minimap")]
    [TestCase("tabSize")]
    [TestCase("fontSize")]
    [TestCase("debounceMs")]
    [TestCase("editorOptions")]
    public void Emits_the_construction_options_the_js_module_reads(string property)
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        ctx.Render<DiffEditor>();

        var options = module.Invocations["createDiff"].Single().Arguments[2]!;
        var json = JsonSerializer.SerializeToElement(options, InteropJson);

        json.TryGetProperty(property, out _).ShouldBeTrue();
    }

    [TestCase("sideBySide")]
    [TestCase("ignoreTrimWhitespace")]
    [TestCase("readOnly")]
    [TestCase("originalEditable")]
    public void Emits_the_view_options_the_js_module_reads(string property)
    {
        var (ctx, module) = Arrange();
        using var _ctx = ctx;

        var cut = ctx.Render<DiffEditor>(p => p.Add(c => c.SideBySide, true));
        cut.Render(p => p.Add(c => c.SideBySide, false));

        var options = module.Invocations["setDiffOptions"].Single().Arguments[1]!;
        var json = JsonSerializer.SerializeToElement(options, InteropJson);

        json.TryGetProperty(property, out _).ShouldBeTrue();
    }

    [Test]
    public async Task Disposes_the_diff_editor_before_releasing_the_dotnet_reference()
    {
        var (ctx, module) = Arrange();
        var cut = ctx.Render<DiffEditor>();

        await cut.Instance.DisposeAsync();

        // Two models plus an editor leak per navigation otherwise.
        module.VerifyInvoke("disposeDiff");
        ctx.Dispose();
    }
}
