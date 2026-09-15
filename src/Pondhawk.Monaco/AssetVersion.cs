using System.Reflection;

namespace Pondhawk.Monaco;

/// <summary>
/// The version the editor's static assets are requested under, so a new release is never served from an
/// old one's cache.
/// </summary>
/// <remarks>
/// <para>code-editor.js, its stylesheet and its workers sit at paths that do not change between releases,
/// and the module is loaded by a dynamic import. A browser that already holds one release's script keeps
/// running it after the application upgrades -- a hard reload does not refresh a dynamically imported
/// module -- so the fix a release carried was invisible until that cached copy happened to expire.</para>
///
/// <para>The informational version rather than the assembly version: the SDK appends the commit to it, so
/// two prereleases, or a local build and a published one, differ even when the version number does not.</para>
/// </remarks>
internal static class AssetVersion
{
    public static string Value { get; } =
        typeof(AssetVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AssetVersion).Assembly.GetName().Version?.ToString()
        ?? "0";

    /// <summary>The query string that pins an asset URL to this version.</summary>
    public static string Query { get; } = $"?v={Uri.EscapeDataString(Value)}";
}
