using System.Text.Json;
using NUnit.Framework;
using Pondhawk.Monaco;
using Shouldly;

namespace Pondhawk.Monaco.Tests;

/// <summary>
/// What .NET actually puts on the wire for the JavaScript module to read.
///
/// <para>The C#/JS boundary is untyped in both directions, so a renamed or wrongly-cased property does not
/// throw — it arrives as <c>undefined</c>, and the symptom is a missing squiggle or an ignored option,
/// noticed far from the cause. Blazor serializes interop arguments camelCase; these tests pin the shape
/// <c>code-editor.js</c> reads.</para>
///
/// <para>Note the limit: this asserts what C# EMITS, not what the JS consumes. Keeping the two aligned
/// still depends on changing them together.</para>
/// </summary>
[TestFixture]
public class EditorMarkerSerializationTests
{
    // Matches Blazor's JS interop serializer configuration.
    private static readonly JsonSerializerOptions InteropJson = new(JsonSerializerDefaults.Web);

    private static JsonElement Serialize(EditorMarker marker) =>
        JsonSerializer.SerializeToElement(marker, InteropJson);

    private static EditorMarker Marker(MarkerSeverity severity = MarkerSeverity.Error) => new()
    {
        StartLine = 3, StartColumn = 1, EndLine = 3, EndColumn = 40,
        Message = "boom", Severity = severity, Source = "tests",
    };

    [TestCase("startLine")]
    [TestCase("startColumn")]
    [TestCase("endLine")]
    [TestCase("endColumn")]
    [TestCase("message")]
    [TestCase("severity")]
    [TestCase("source")]
    public void Emits_the_property_names_the_js_module_reads(string property) =>
        Serialize(Marker()).TryGetProperty(property, out _).ShouldBeTrue();

    [TestCase(MarkerSeverity.Error, "error")]
    [TestCase(MarkerSeverity.Warning, "warning")]
    [TestCase(MarkerSeverity.Info, "info")]
    public void Severity_serializes_as_the_lowercase_string_js_switches_on(
        MarkerSeverity severity, string expected)
    {
        // The JS maps 'warning'/'info' and treats anything else as an error. A numeric or PascalCase
        // enum would silently downgrade every marker to Error rather than failing.
        Serialize(Marker(severity)).GetProperty("severity").GetString().ShouldBe(expected);
    }

    [Test]
    public void Positions_survive_the_round_trip_unchanged()
    {
        // Monaco is 1-based with exclusive end columns. An off-by-one here underlines the wrong text,
        // which is the kind of bug that gets blamed on the host application's diagnostics.
        var json = Serialize(Marker());

        json.GetProperty("startLine").GetInt32().ShouldBe(3);
        json.GetProperty("startColumn").GetInt32().ShouldBe(1);
        json.GetProperty("endColumn").GetInt32().ShouldBe(40);
    }

    [Test]
    public void A_marker_without_a_source_still_serializes()
    {
        var marker = new EditorMarker
        {
            StartLine = 1, StartColumn = 1, EndLine = 1, EndColumn = 2, Message = "x",
        };

        Should.NotThrow(() => Serialize(marker));
    }
}
