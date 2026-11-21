using Tamp;
using Tamp.NetCli.V10;
using Tamp.Telegram;
using Tamp.Components;
using Tamp.Components.NetCli.V10;
using Tamp.SonarScanner.V10;

class Build : TampBuild, IDotNetTest, IDotNetPack
{
    public static int Main(string[] args) => Execute<Build>(args);

    // ----- SonarQube Cloud -----
    // dotnet-sonarscanner is a DLL-based .NET tool; CI installs it globally and resolves it
    // from PATH, so it is Optional here and the Sonar targets simply do not run without it.
    [FromPath("dotnet-sonarscanner", Optional = true)]
    readonly Tool SonarTool = null!;

    [Secret("SonarCloud token", EnvironmentVariable = "SONAR_TOKEN")]
    readonly Secret SonarToken = null!;

    [Parameter("Sonar host URL", EnvironmentVariable = "SONAR_HOST_URL")]
    readonly string SonarHostUrl = "https://sonarcloud.io";

    [Parameter("SonarCloud organization")]
    readonly string SonarOrganization = "tamp-build";

    [Parameter("SonarCloud project key")]
    readonly string SonarProjectKey = "tamp-build_tamp-kudu";

    // Set by ci.yml on a pull request so SonarCloud analyses it as a PR and decorates
    // it with the new-code gate; empty on a nightly main-branch baseline run.
    // Declared here rather than reused from the repo's own properties: tamp-go happens to
    // have a TestResultsDir, most wrappers do not, and assuming it failed to compile.
    // This is where the IDotNetTest component writes its coverage output.
    AbsolutePath SonarTestResultsDir => RootDirectory / "artifacts" / "test-results";

    [Parameter("Pull-request number", EnvironmentVariable = "SONAR_PR_KEY")] readonly string SonarPrKey = "";
    [Parameter("Pull-request head branch", EnvironmentVariable = "SONAR_PR_BRANCH")] readonly string SonarPrBranch = "";
    [Parameter("Pull-request base branch", EnvironmentVariable = "SONAR_PR_BASE")] readonly string SonarPrBase = "";

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

    // SonarCloud analysis is a two-phase scan: Begin before the build, End after tests.
    Target SonarBegin => _ => _
        .Description("Initialize the SonarCloud pre-build phase.")
        .Before(nameof(ICompile.Compile))
        .Requires(() => SonarToken != null)
        .Executes(() => SonarScanner.Begin(SonarTool, s =>
        {
            s.SetProjectKey(SonarProjectKey)
             .SetOrganization(SonarOrganization)
             .SetHostUrl(SonarHostUrl)
             .SetToken(SonarToken)
             .SetProperty("sonar.exclusions", "build/**")
             .SetProperty("sonar.cs.opencover.reportsPaths", $"{SonarTestResultsDir.Value}/**/coverage.opencover.xml");

            // On a PR run, analyze as a pull request so SonarCloud decorates it with the new-code gate.
            if (!string.IsNullOrEmpty(SonarPrKey))
                s.SetProperty("sonar.pullrequest.key", SonarPrKey)
                 .SetProperty("sonar.pullrequest.branch", SonarPrBranch)
                 .SetProperty("sonar.pullrequest.base", SonarPrBase);
        }));

    Target SonarEnd => _ => _
        .After(nameof(ITest.Test))
        .DependsOn(nameof(SonarBegin))
        .Description("Finalize SonarCloud and submit results.")
        .Executes(() => SonarScanner.End(SonarTool, s => s.SetToken(SonarToken)));

    Target Sonar => _ => _
        .DependsOn(nameof(SonarBegin), nameof(ITest.Test), nameof(SonarEnd))
        .Description("Full SonarCloud analysis: begin, build + test coverage (all TFMs), end. Requires SONAR_TOKEN.");

}
