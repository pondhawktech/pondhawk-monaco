using System.Xml.Linq;
using Cake.Common;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Common.Tools.DotNet.MSBuild;
using Cake.Common.Tools.DotNet.Pack;
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
    /// The version to stamp, supplied by CI as <c>--packageVersion=1.2.3</c>.
    ///
    /// <para>Empty for a local build, where the csproj's own default applies. Releases are versioned from
    /// git tags rather than from a number checked into the repo, so nothing here needs a default: only a
    /// release knows what version it is publishing.</para>
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

    public int Npm(string args)
        => this.StartProcess("npm", new ProcessSettings { Arguments = args, WorkingDirectory = JsDir });
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
    {
        // `npm ci` rather than `npm install`: reproducible from the lockfile, and it fails loudly if the
        // lockfile and package.json have drifted apart rather than quietly reconciling them.
        if (!c.DirectoryExists($"{c.JsDir}/node_modules") && c.Npm("ci --no-audit --no-fund") != 0)
            throw new CakeException("npm ci failed.");

        if (c.Npm("run build") != 0) throw new CakeException("Bundling Monaco failed.");
    }
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
