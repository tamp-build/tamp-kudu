using Tamp;
using Tamp.NetCli.V10;
using Tamp.Telegram;
using Tamp.Components;
using Tamp.Components.NetCli.V10;

class Build : TampBuild, IDotNetTest, IDotNetPack
{
    public static int Main(string[] args) => Execute<Build>(args);

    // TAM-227 — Telegram failure notify. Pulls TELEGRAM_BOT_TOKEN /
    // TELEGRAM_CHAT_ID / TELEGRAM_BUILD_LABEL from the environment;
    // returns null when missing, framework silently skips null reporters.
    [BuildReporter] readonly IBuildReporter? TelegramNotify =
        TelegramBuildReporter.FromEnvironment();

    [Parameter("Build configuration")]
    public Configuration Configuration { get; set; } = IsLocalBuild ? Configuration.Debug : Configuration.Release;


    [Solution] public Solution Solution { get; set; } = null!;
    [GitRepository] readonly GitRepository Git = null!;

    [Secret("NuGet API key", EnvironmentVariable = "NUGET_API_KEY")]
    readonly Secret NuGetApiKey = null!;

    AbsolutePath Artifacts => RootDirectory / "artifacts";

    public AbsolutePath ArtifactsDirectory => Artifacts;

    Target Info => _ => _.Executes(() =>
    {
        Console.WriteLine($"  Branch:        {Git.Branch ?? "<detached>"}");
        Console.WriteLine($"  Commit:        {Git.Commit[..7]}");
        Console.WriteLine($"  Configuration: {Configuration}");
    });

    Target Clean => _ => _
        .Description("Delete bin/obj and the artifacts directory.")
        .Executes(() => CleanArtifacts());

    Target Push => _ => _
        .DependsOn(nameof(IPack.Pack))
        .Requires(() => NuGetApiKey != null)
        .Executes(() => Artifacts.GlobFiles("*.nupkg")
            .Select(p => DotNet.NuGetPush(s => s
                .SetPackagePath(p)
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetApiKey(NuGetApiKey)
                .SetSkipDuplicate(true))));

    Target Ci => _ => _
        .DependsOn(nameof(Info), nameof(Clean), nameof(ITest.Test), nameof(IPack.Pack));

    Target Default => _ => _.DependsOn(nameof(ICompile.Compile));
}
