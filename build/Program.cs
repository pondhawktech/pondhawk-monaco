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

    public string Library => "src/Pondhawk.Blazor.CodeEditor/Pondhawk.Blazor.CodeEditor.csproj";
    public string Tests => "tests/Pondhawk.Blazor.CodeEditor.Tests/Pondhawk.Blazor.CodeEditor.Tests.csproj";
    public string Demo => "demo/Pondhawk.CodeEditor.Demo/Pondhawk.CodeEditor.Demo.csproj";

    public string[] Projects => [Library, Tests, Demo];

    /// <summary>Where the JS sources live. The bundle step runs here, and nowhere else in the ecosystem —
    /// consumers of the package never need node.</summary>
    public string JsDir => "src/Pondhawk.Blazor.CodeEditor/js";

    /// <summary>Bundled output. A build artifact, not source: gitignored and regenerated.</summary>
    public string DistDir => "src/Pondhawk.Blazor.CodeEditor/wwwroot/dist";

    public string ArtifactsDir => "artifacts";

    public BuildContext(ICakeContext context) : base(context)
        => Configuration = context.Argument("configuration", "Release");

    public int Npm(string args)
        => this.StartProcess("npm", new ProcessSettings { Arguments = args, WorkingDirectory = JsDir });
}

[TaskName("Clean")]
public sealed class CleanTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        foreach (var project in c.Projects)
        {
            var dir = System.IO.Path.GetDirectoryName(project)!;
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
    public override void Run(BuildContext c)
    {
        foreach (var project in c.Projects) c.DotNetRestore(project);
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(RestoreTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
    {
        foreach (var project in c.Projects)
            c.DotNetBuild(project, new DotNetBuildSettings
            {
                Configuration = c.Configuration,
                NoRestore = true,
            });
    }
}

[TaskName("Test")]
[IsDependentOn(typeof(BuildTask))]
public sealed class TestTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext c)
        => c.DotNetTest(c.Tests, new DotNetTestSettings
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
        });

        c.Information($"Package written to {c.ArtifactsDir}/");
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
        c.Information("Demo on http://localhost:5200 — Ctrl+C to stop.");

        c.DotNetRun(c.Demo, new ProcessArgumentBuilder().Append("--no-launch-profile"),
            new DotNetRunSettings
            {
                Configuration = c.Configuration,
                NoBuild = true,
                EnvironmentVariables =
                {
                    // Development is REQUIRED, not a convenience: Blazor only composes static web assets
                    // from referenced packages (the _content/** paths this component ships under) when
                    // running as Development. Without it the editor loads with no Monaco and no styles.
                    ["ASPNETCORE_URLS"] = "http://localhost:5200",
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                },
            });
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(TestTask))]
public sealed class DefaultTask : FrostingTask;
