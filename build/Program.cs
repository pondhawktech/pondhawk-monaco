using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cake.Common;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Common.Tools.DotNet.MSBuild;
using Cake.Common.Tools.DotNet.Pack;
using Cake.Common.Tools.DotNet.NuGet.Push;
using Cake.Common.Tools.DotNet.Restore;
using Cake.Common.Tools.DotNet.Run;
using Cake.Common.Tools.DotNet.Test;
using Cake.Core;
using Cake.Core.IO;
using Cake.Frosting;

return new CakeHost().UseContext<BuildContext>().Run(args);

public sealed class BuildContext : FrostingContext
{
    public string Configuration { get; }

    /// <summary>
    /// A version to stamp over the one in <see cref="VersionFile"/>, supplied as
    /// <c>--packageVersion=1.2.3</c>.
    ///
    /// <para>Empty for a local build and for a release, where the version file is authoritative. CI uses
    /// it for prerelease builds, which suffix that version with <c>-ci.&lt;run&gt;</c>.</para>
    /// </summary>
    public string PackageVersion { get; }

    /// <summary>The solution, and the single source of truth for what this repo contains. Restore, Build
    /// and Test run against it, so a project added to the solution is picked up here without this file
    /// being edited — and cannot silently fall out of the build by being forgotten.</summary>
    public string Solution => "Pondhawk.Monaco.slnx";

    /// <summary>The packable project. Pack names it directly rather than running against the solution:
    /// the demo and this build project are ordinary non-packable projects, and packing the solution would
    /// emit nupkgs for them too.</summary>
    public string Library => "src/Pondhawk.Monaco/Pondhawk.Monaco.csproj";

    public string Demo => "demo/Pondhawk.Monaco.Demo/Pondhawk.Monaco.Demo.csproj";

    /// <summary>This build project's own directory. Cake is executing out of <c>build/bin</c> whenever a
    /// target runs, so it is the one directory Clean must leave alone.</summary>
    private const string BuildProjectDir = "build";

    /// <summary>
    /// Directories whose bin/ and obj/ Clean empties, read out of the solution rather than listed again
    /// here — a second list would be free to drift from the first.
    ///
    /// <para>Read from the solution rather than globbed: a <c>**/bin</c> pattern would reach into
    /// <c>js/node_modules</c>, where emptying a package's bin/ breaks the npm install.</para>
    ///
    /// <para><see cref="BuildProjectDir"/> is excluded. Deleting the assembly Cake is currently running
    /// from is legal on Linux and fails outright on Windows, where a loaded assembly is locked.</para>
    /// </summary>
    public IEnumerable<string> CleanableDirectories =>
        XDocument.Load(Solution)
            .Descendants("Project")
            .Select(project => (string)project.Attribute("Path")!)
            // .slnx records separators however the machine that added the project wrote them.
            .Select(path => System.IO.Path.GetDirectoryName(path.Replace('\\', '/'))!)
            .Where(dir => dir != BuildProjectDir);

    /// <summary>Where the JS sources live. The bundle step runs here, and nowhere else in the ecosystem —
    /// consumers of the package never need node.</summary>
    public string JsDir => "src/Pondhawk.Monaco/js";

    /// <summary>Bundled output. A build artifact, not source: gitignored and regenerated.</summary>
    public string DistDir => "src/Pondhawk.Monaco/wwwroot/dist";

    public string ArtifactsDir => "artifacts";

    /// <summary>The published package id. Named once, so the file-name convention lives in one place.</summary>
    public string PackageId => "Pondhawk.Monaco";

    /// <summary>Where Pack writes the package for a given version.</summary>
    public string PackagePath(string version) => $"{ArtifactsDir}/{PackageId}.{version}.nupkg";

    /// <summary>Where the demo serves. One constant, because the URL printed and the URL bound must agree —
    /// they did not before: the task advertised 5200 while the launch profile quietly bound 5292.</summary>
    public string DemoUrl => "http://localhost:5200";

    public BuildContext(ICakeContext context) : base(context)
    {
        Configuration = context.Argument("configuration", "Release");
        PackageVersion = context.Argument("packageVersion", string.Empty);
    }

    /// <summary>
    /// MSBuild properties shared by Build and Pack.
    ///
    /// <para>Both must receive the same version. Pack runs with <c>NoBuild</c>, so a version given only to
    /// Pack would produce a nuspec that disagrees with the assembly inside it.</para>
    /// </summary>
    public DotNetMSBuildSettings MsBuildSettings()
    {
        var settings = new DotNetMSBuildSettings();

        if (!string.IsNullOrWhiteSpace(PackageVersion))
            settings.WithProperty("Version", PackageVersion);

        return settings;
    }

    /// <summary>
    /// Runs npm in the JS directory.
    ///
    /// <para>The executable name is not "npm" on Windows: npm ships as a <c>.cmd</c> shim, and
    /// Process.Start does not apply PATHEXT, so plain "npm" fails to resolve there while working
    /// perfectly on Linux and macOS — the classic way a build is cross-platform right up until someone
    /// tries it.</para>
    /// </summary>
    public int Npm(string args) => this.StartProcess(
        this.IsRunningOnWindows() ? "npm.cmd" : "npm",
        new ProcessSettings { Arguments = args, WorkingDirectory = JsDir });

    // --- Version ------------------------------------------------------------------------------------

    /// <summary>The file holding the released version, and the single source of truth for it.</summary>
    public string VersionFile => "Directory.Build.props";

    private static readonly Regex VersionPattern =
        new(@"<VersionPrefix>(?<version>\d+\.\d+\.\d+)</VersionPrefix>", RegexOptions.Compiled);

    /// <summary>Reads the released version out of <see cref="VersionFile"/>.</summary>
    public string ReadVersion()
    {
        var match = VersionPattern.Match(System.IO.File.ReadAllText(VersionFile));

        return match.Success
            ? match.Groups["version"].Value
            : throw new CakeException($"No <VersionPrefix>MAJOR.MINOR.PATCH</VersionPrefix> in {VersionFile}.");
    }

    /// <summary>
    /// The next patch after the last released version, WITHOUT rewriting the file.
    ///
    /// <para>What CI prereleases are built from. The file records what shipped, so <c>1.0.0-ci.7</c>
    /// would sort below the 1.0.0 it comes after; <c>1.0.1-ci.7</c> sits above 1.0.0 and below the
    /// eventual 1.0.1, which is what a prerelease is supposed to mean.</para>
    /// </summary>
    public string NextPatch()
    {
        var parts = ReadVersion().Split('.').Select(int.Parse).ToArray();
        return $"{parts[0]}.{parts[1]}.{parts[2] + 1}";
    }

    /// <summary>
    /// Applies a semantic bump and rewrites <see cref="VersionFile"/> in place, returning the new value.
    ///
    /// <para>Rewritten by regex rather than by loading and saving the XML, which would reformat the file
    /// and discard the comment explaining what it is for.</para>
    /// </summary>
    public string BumpVersion(string part)
    {
        var current = ReadVersion();
        var parts = current.Split('.').Select(int.Parse).ToArray();

        var (major, minor, patch) = part switch
        {
            "major" => (parts[0] + 1, 0, 0),
            "minor" => (parts[0], parts[1] + 1, 0),
            "patch" => (parts[0], parts[1], parts[2] + 1),
            _ => throw new CakeException($"Unknown bump '{part}' (expected major, minor or patch)."),
        };

        var next = $"{major}.{minor}.{patch}";
        var text = System.IO.File.ReadAllText(VersionFile);
        System.IO.File.WriteAllText(VersionFile,
            text.Replace($"<VersionPrefix>{current}</VersionPrefix>", $"<VersionPrefix>{next}</VersionPrefix>"));

        // A silent no-op here would publish the old version under a new tag.
        if (ReadVersion() != next)
            throw new CakeException($"Rewrite failed: {VersionFile} still reads {ReadVersion()}.");

        this.Information($"{current} -> {next} ({part})");
        return next;
    }

    // --- Bundle comparison --------------------------------------------------------------------------

    /// <summary>
    /// SHA-256 of every bundled web asset in a package, keyed by path.
    ///
    /// <para>Only <c>staticwebassets/</c> is hashed. Everything else in a nupkg legitimately differs
    /// between two versions — the nuspec, the assembly with its stamped version, the relationship parts —
    /// so comparing them would be noise that trains you to ignore the result.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> BundleHashes(string nupkg)
    {
        using var archive = ZipFile.OpenRead(nupkg);

        var assets = archive.Entries
            .Where(e => e.FullName.StartsWith("staticwebassets/", StringComparison.Ordinal) && e.Length > 0)
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();

        if (assets.Count == 0)
            throw new CakeException($"{nupkg} contains no staticwebassets/ — wrong package?");

        return assets.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                return Convert.ToHexString(SHA256.HashData(ReadAll(stream))).ToLowerInvariant();
            },
            StringComparer.Ordinal);

        static byte[] ReadAll(Stream stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}

[TaskName("Clean")]
public sealed class CleanTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        foreach (var dir in c.CleanableDirectories)
        {
            c.CleanDirectories($"{dir}/bin");
            c.CleanDirectories($"{dir}/obj");
        }

        if (c.DirectoryExists(c.DistDir)) c.CleanDirectory(c.DistDir);
        if (c.DirectoryExists(c.ArtifactsDir)) c.CleanDirectory(c.ArtifactsDir);
    }
}

/// <summary>
/// Bundles Monaco and its language workers into wwwroot/dist.
///
/// <para>Exposed as its own target because it is the slowest step and the one most often re-run alone
/// while iterating on the JavaScript. A normal Build also triggers it — the library's csproj runs the
/// same npm script BeforeBuild — so this target exists for convenience, not because Build would skip it.</para>
/// </summary>
[TaskName("Bundle")]
public sealed class BundleTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
        // Delegates to the library's BundleJs target rather than running npm itself. That target has to
        // exist regardless — it is what makes a plain `dotnet build` produce wwwroot/dist, and its
        // Inputs/Outputs give the slow step its incremental behaviour — so implementing the bundle here
        // as well would be two code paths that only happen to issue the same npm commands.
        => c.DotNetBuild(c.Library, new DotNetBuildSettings
        {
            Configuration = c.Configuration,
            MSBuildSettings = new DotNetMSBuildSettings { Targets = { "BundleJs" } },
        });
}

[TaskName("Restore")]
public sealed class RestoreTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c) => c.DotNetRestore(c.Solution);
}

[TaskName("Build")]
[IsDependentOn(typeof(RestoreTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
        => c.DotNetBuild(c.Solution, new DotNetBuildSettings
        {
            Configuration = c.Configuration,
            NoRestore = true,
            MSBuildSettings = c.MsBuildSettings(),
        });
}

/// <summary>
/// Exercises code-editor.js against a stubbed Monaco, under Node's own test runner.
///
/// <para>The bUnit suite covers one half of the interop boundary — what .NET SENDS. This covers the
/// other: what the module DOES with it. The payload tests say outright that they cannot check the
/// consuming side, and nearly every defect found in this component has been on the JavaScript side of
/// that line.</para>
/// </summary>
[TaskName("TestJs")]
public sealed class TestJsTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        if (!c.DirectoryExists($"{c.JsDir}/node_modules") && c.Npm("ci --no-audit --no-fund") != 0)
            throw new CakeException("npm ci failed.");

        if (c.Npm("test") != 0) throw new CakeException("JavaScript tests failed.");
    }
}

/// <summary>Runs every test project in the solution — so a second one added later is run without this
/// task being told about it — and the JavaScript tests alongside them. Pack depends on this, so neither
/// half of the boundary can regress into a published package.</summary>
[TaskName("Test")]
[IsDependentOn(typeof(BuildTask))]
[IsDependentOn(typeof(TestJsTask))]
public sealed class TestTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
        => c.DotNetTest(c.Solution, new DotNetTestSettings
        {
            Configuration = c.Configuration,
            NoBuild = true,
        });
}

/// <summary>
/// Produces the NuGet package.
///
/// <para>Depends on Test rather than Build: the package embeds the bundled JavaScript, so shipping one
/// that failed its tests would put a broken editor into every consuming app with no local signal that
/// anything is wrong.</para>
/// </summary>
[TaskName("Pack")]
[IsDependentOn(typeof(TestTask))]
public sealed class PackTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        c.DotNetPack(c.Library, new DotNetPackSettings
        {
            Configuration = c.Configuration,
            NoBuild = true,
            OutputDirectory = c.ArtifactsDir,
            MSBuildSettings = c.MsBuildSettings(),
        });

        var version = string.IsNullOrWhiteSpace(c.PackageVersion) ? "the project default" : c.PackageVersion;
        c.Information($"Package written to {c.ArtifactsDir}/ at version {version}.");
    }
}

/// <summary>
/// Resolves the version to release, optionally bumping the version file first.
///
/// <code>
/// ./build.sh --target Version                            # print the last released version
/// ./build.sh --target Version --bump=minor               # rewrite the file, print the new value
/// ./build.sh --target Version --next=true --suffix=ci.42 # 1.0.1-ci.42, file untouched
/// </code>
///
/// <para>Writes <c>version=X.Y.Z</c> to <c>$GITHUB_OUTPUT</c> when running under Actions, so a workflow
/// reads it as a step output rather than scraping stdout past Cake's banner. The suffix is composed here
/// rather than in YAML so that the whole version comes from one place.</para>
/// </summary>
[TaskName("Version")]
public sealed class VersionTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        var bump = c.Argument("bump", "none");
        var suffix = c.Argument("suffix", string.Empty);
        var next = c.Argument("next", false);

        var version = (bump, next) switch
        {
            (not ("none" or ""), _) => c.BumpVersion(bump),

            // The file records what was LAST released, so a prerelease has to sit above it — hence the
            // next patch. Suffixing the file's own value would produce 1.0.0-ci.7 AFTER 1.0.0 shipped,
            // which NuGet orders below the release it follows.
            (_, true) => c.NextPatch(),

            _ => c.ReadVersion(),
        };

        if (!string.IsNullOrWhiteSpace(suffix)) version = $"{version}-{suffix}";

        c.Information($"Version: {version}");

        var output = c.EnvironmentVariable("GITHUB_OUTPUT");
        if (!string.IsNullOrEmpty(output))
            System.IO.File.AppendAllText(output, $"version={version}{Environment.NewLine}");
    }
}

/// <summary>
/// Compares the bundled web assets of the package just built against another package — in practice the
/// one CI produced for the same commit.
///
/// <code>./build.sh --target VerifyBundle --against=path/to/ci.nupkg</code>
///
/// <para>The release rebuilds rather than promoting CI's package, because a NuGet version is baked into
/// the nuspec and the filename and there is no retag. This checks the one part that could drift without
/// any test noticing: wwwroot/dist, the output of npm and esbuild.</para>
/// </summary>
[TaskName("VerifyBundle")]
public sealed class VerifyBundleTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        var against = c.Argument<string>("against");
        var mine = c.PackagePath(c.ReadVersion());

        foreach (var package in new[] { against, mine })
            if (!c.FileExists(package)) throw new CakeException($"No such package: {package}");

        var theirs = BuildContext.BundleHashes(against);
        var ours = BuildContext.BundleHashes(mine);

        var differences = theirs.Keys.Union(ours.Keys, StringComparer.Ordinal)
            .Where(path => !theirs.TryGetValue(path, out var a)
                        || !ours.TryGetValue(path, out var b)
                        || a != b)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (differences.Count == 0)
        {
            c.Information($"Bundled assets identical ({ours.Count} files).");
            return;
        }

        foreach (var path in differences)
            c.Error($"  {path}: {theirs.GetValueOrDefault(path, "<absent>")} != {ours.GetValueOrDefault(path, "<absent>")}");

        throw new CakeException(
            "Bundled assets differ from the package CI built. Same commit and the same locked toolchain " +
            "should give the same bundle, so treat this as the build having become non-deterministic.");
    }
}

/// <summary>
/// Pushes the packages in <c>artifacts/</c> to a feed.
///
/// <code>PUBLISH_API_KEY=... ./build.sh --target Publish --source=https://api.nuget.org/v3/index.json</code>
///
/// <para>The key comes from the environment, not from an argument. A secret on a command line is visible
/// in process listings and turns up in echoed commands, and Cake's own parser rejects an empty
/// <c>--apiKey=</c> before any task runs — so a missing secret would fail with "Expected an option
/// value" rather than anything that points at the secret.</para>
///
/// <para>Deliberately NOT dependent on Pack: the release packs once and pushes the same file to more
/// than one feed, and re-packing between pushes would send bytes nobody verified.</para>
/// </summary>
[TaskName("Publish")]
public sealed class PublishTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        var source = c.Argument<string>("source");
        var apiKey = c.EnvironmentVariable("PUBLISH_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new CakeException(
                $"PUBLISH_API_KEY is not set, so nothing can be pushed to {source}. An empty key returns a " +
                "403, which reads as a permissions problem rather than as a missing secret.");

        // Exactly one package, named by version — NOT a glob over artifacts/. Pack does not clear that
        // directory, so a glob would happily push a stale package left there by an earlier build, and a
        // version pushed to nuget.org can never be withdrawn.
        var version = string.IsNullOrWhiteSpace(c.PackageVersion) ? c.ReadVersion() : c.PackageVersion;
        var package = c.PackagePath(version);

        if (!c.FileExists(package))
            throw new CakeException(
                $"No such package: {package}. artifacts/ holds: " +
                string.Join(", ", c.GetFiles($"{c.ArtifactsDir}/*.nupkg").Select(f => f.GetFilename().ToString())));

        c.Information($"Pushing {package} to {source}");
        c.DotNetNuGetPush(package, new DotNetNuGetPushSettings
        {
            Source = source,
            ApiKey = apiKey,
            // Re-runs against a CI feed are routine; on a feed where versions are permanent a
            // duplicate means something is wrong, so the caller decides.
            SkipDuplicate = c.Argument("skipDuplicate", false),
        });
    }
}

/// <summary>Runs the demo harness. Development environment is required: Blazor only composes static web
/// assets from referenced packages (the _content/** paths) when running as Development.</summary>
[TaskName("Demo")]
[IsDependentOn(typeof(BuildTask))]
public sealed class DemoTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        c.Information($"Demo on {c.DemoUrl} — Ctrl+C to stop.");

        // Deliberately not DotNetRun. Cake appends its ProcessArgumentBuilder AFTER the `--` separator,
        // which is where APP arguments go — but `--no-launch-profile` is an option to `dotnet run` itself.
        // Passed on the wrong side it sailed through to blazor-devserver, which ignores unknown arguments,
        // so the launch profile stayed in effect: its applicationUrl (5292) beat ASPNETCORE_URLS, and the
        // task printed a URL nothing was listening on. Building the command line here keeps the option on
        // the correct side of the separator.
        c.StartProcess("dotnet", new ProcessSettings
        {
            Arguments = new ProcessArgumentBuilder()
                .Append("run")
                .Append("--project").AppendQuoted(c.Demo)
                .Append("--configuration").Append(c.Configuration)
                .Append("--no-build")
                .Append("--no-launch-profile"),
            // Assigned rather than collection-initialized: ProcessSettings leaves this null, unlike
            // DotNetRunSettings, and `EnvironmentVariables = { ... }` on a null property is an NRE.
            EnvironmentVariables = new Dictionary<string, string>
            {
                // Development is REQUIRED, not a convenience: Blazor only composes static web assets
                // from referenced packages (the _content/** paths this component ships under) when
                // running as Development. Without it the editor loads with no Monaco and no styles.
                ["ASPNETCORE_URLS"] = c.DemoUrl,
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            },
        });

        // No exit-code check: Ctrl+C is the normal way this ends, and it is not a build failure.
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(TestTask))]
public sealed class DefaultTask : FrostingTask;
