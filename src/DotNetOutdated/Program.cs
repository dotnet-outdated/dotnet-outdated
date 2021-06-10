using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Models;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Services;
using DotNetOutdated.Core.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;
using DotNetOutdated.Core;
using NuGet.Credentials;

[assembly: InternalsVisibleTo("DotNetOutdated.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace DotNetOutdated
{
    using System.Collections.Concurrent;
    using System.Diagnostics;

    [Command(
        Name = "dotnet outdated",
        FullName = "A .NET Core global tool to list outdated Nuget packages.")]
    [VersionOptionFromMember(MemberName = nameof(GetVersion))]
    class Program : CommandBase
    {
        private readonly IFileSystem _fileSystem;
        private readonly IReporter _reporter;
        private readonly INuGetPackageResolutionService _nugetService;
        private readonly IProjectAnalysisService _projectAnalysisService;
        private readonly IProjectDiscoveryService _projectDiscoveryService;
        private readonly IDotNetAddPackageService _dotNetAddPackageService;

        [Option(CommandOptionType.NoValue, Description = "Specifies whether to include auto-referenced packages.",
            LongName = "include-auto-references")]
        public bool IncludeAutoReferences { get; set; } = false;

        [Argument(0, Description = "The path to a .sln, .slnf, .csproj or .fsproj file, or to a directory containing a .NET Core solution/project. " +
                                   "If none is specified, the current directory will be used.")]
        public string Path { get; set; }

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether to look for pre-release versions of packages. " +
                                                             "Possible values: Auto (default), Always or Never.",
            ShortName = "pre", LongName = "pre-release")]
        public PrereleaseReporting Prerelease { get; set; } = PrereleaseReporting.Auto;

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether the package should be locked to the current Major or Minor version. " +
                                                             "Possible values: None (default), Major or Minor.",
            ShortName = "vl", LongName = "version-lock")]
        public VersionLock VersionLock { get; set; } = VersionLock.None;

        [Option(CommandOptionType.NoValue, Description = "Specifies whether it should detect transitive dependencies.",
            ShortName = "t", LongName = "transitive")]
        public bool Transitive { get; set; } = false;

        [Option(CommandOptionType.SingleValue, Description = "Defines how many levels deep transitive dependencies should be analyzed. " +
                                                             "Integer value (default = 1)",
            ShortName="td", LongName = "transitive-depth")]
        public int TransitiveDepth { get; set; } = 1;

        [Option(CommandOptionType.SingleOrNoValue, Description = "Specifies whether outdated packages should be upgraded. " +
                                                             "Possible values for <TYPE> is Auto (default) or Prompt.",
            ShortName = "u", LongName = "upgrade", ValueName = "TYPE")]
        public (bool HasValue, UpgradeType UpgradeType) Upgrade { get; set; }

        [Option(CommandOptionType.NoValue, Description = "Specifies whether it should return a non-zero exit code when updates are found.",
            ShortName = "f", LongName = "fail-on-updates")]
        public bool FailOnUpdates { get; set; } = false;

        [Option(CommandOptionType.MultipleValue, Description = "Specifies to only look at packages where the name contains the provided string. Culture and case insensitive. If provided multiple times, a single match is enough to include a package.",
            ShortName = "inc", LongName = "include")]
        public List<string> FilterInclude { get; set; } = new List<string>();

        [Option(CommandOptionType.MultipleValue, Description = "Specifies to only look at packages where the name does not contain the provided string. Culture and case insensitive. If provided multiple times, a single match is enough to exclude a package.",
            ShortName = "exc", LongName = "exclude")]
        public List<string> FilterExclude { get; set; } = new List<string>();

        [Option(CommandOptionType.SingleValue, Description = "Specifies the filename for a generated report. " +
                                                             "(Use the -of|--output-format option to specify the format. JSON by default.)",
            ShortName = "o", LongName = "output")]
        public string OutputFilename { get; set; } = null;

        [Option(CommandOptionType.SingleValue, Description = "Specifies the output format for the generated report. " +
                                                             "Possible values: json (default) or csv.",
            ShortName = "of", LongName = "output-format")]
        public OutputFormat OutputFileFormat { get; set; } = OutputFormat.Json;

        [Option(CommandOptionType.SingleValue, Description = "Only include package versions that are older than the specified number of days.",
            ShortName = "ot", LongName = "older-than")]
        public int OlderThanDays { get; set; }

        [Option(CommandOptionType.NoValue, Description = "Add the reference without performing restore preview and compatibility check.",
            ShortName = "n", LongName = "no-restore")]
        public bool NoRestore { get; set; } = false;

        [Option(CommandOptionType.NoValue, Description = "Recursively search for all projects within the provided directory.",
            ShortName = "r", LongName = "recursive")]
        public bool Recursive { get; set; } = false;

        public static int Main(string[] args)
        {
            using (var services = new ServiceCollection()
                    .AddSingleton<IConsole>(PhysicalConsole.Singleton)
                    .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                    .AddSingleton<IFileSystem, FileSystem>()
                    .AddSingleton<IProjectDiscoveryService, ProjectDiscoveryService>()
                    .AddSingleton<IProjectAnalysisService, ProjectAnalysisService>()
                    .AddSingleton<IDotNetRunner, DotNetRunner>()
                    .AddSingleton<IDependencyGraphService, DependencyGraphService>()
                    .AddSingleton<IDotNetRestoreService, DotNetRestoreService>()
                    .AddSingleton<IDotNetAddPackageService, DotNetAddPackageService>()
                    .AddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>()
                    .AddSingleton<INuGetPackageResolutionService, NuGetPackageResolutionService>()
                    .BuildServiceProvider())
            {
                var app = new CommandLineApplication<Program>();
                app.Conventions
                    .UseDefaultConventions()
                    .UseConstructorInjection(services);

                return app.Execute(args);
            }
        }

        public static string GetVersion() => typeof(Program)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            .InformationalVersion;

        public Program(IFileSystem fileSystem, IReporter reporter, INuGetPackageResolutionService nugetService, IProjectAnalysisService projectAnalysisService,
            IProjectDiscoveryService projectDiscoveryService, IDotNetAddPackageService dotNetAddPackageService)
        {
            _fileSystem = fileSystem;
            _reporter = reporter;
            _nugetService = nugetService;
            _projectAnalysisService = projectAnalysisService;
            _projectDiscoveryService = projectDiscoveryService;
            _dotNetAddPackageService = dotNetAddPackageService;
        }

        public async Task<int> OnExecute(CommandLineApplication app, IConsole console)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();

                // If no path is set, use the current directory
                if (string.IsNullOrEmpty(Path))
                    Path = _fileSystem.Directory.GetCurrentDirectory();

                // Get all the projects
                console.Write("Discovering projects...");

                DefaultCredentialServiceUtility.SetupDefaultCredentialService(new NuGet.Common.NullLogger(), true);

                var projectPaths = _projectDiscoveryService.DiscoverProjects(Path, Recursive);

                if (!console.IsOutputRedirected)
                    ClearCurrentConsoleLine();
                else
                    console.WriteLine();

                // Analyze the projects
                console.Write("Analyzing project(s)...");
                
                var projects = projectPaths.SelectMany(path => _projectAnalysisService.AnalyzeProject(path, false, Transitive, TransitiveDepth)).ToList();

                if (!console.IsOutputRedirected)
                    ClearCurrentConsoleLine();
                else
                    console.WriteLine();

                // Analyze the dependencies
                var outdatedProjects = await AnalyzeDependencies(projects, console);

                if (outdatedProjects.Any())
                {
                    // Report on the outdated dependencies
                    ReportOutdatedDependencies(outdatedProjects, console);

                    // Upgrade the packages
                    var success = UpgradePackages(outdatedProjects, console);

                    if (!Upgrade.HasValue)
                    {
                        console.WriteLine();
                        console.WriteLine("You can upgrade packages to the latest version by passing the -u or -u:prompt option.");
                    }

                    // Output report file
                    GenerateOutputFile(outdatedProjects);

                    if (FailOnUpdates)
                        return 2;

                    if (!success)
                        return 3;
                }
                else
                {
                    console.WriteLine("No outdated dependencies were detected");
                }

                console.WriteLine($"Elapsed: {stopwatch.Elapsed}");

                return 0;
            }
            catch (CommandValidationException e)
            {
                _reporter.Error(e.Message);

                return 1;
            }
        }

        private bool UpgradePackages(List<AnalyzedProject> projects, IConsole console)
        {
            bool success = true;

            if (Upgrade.HasValue)
            {
                console.WriteLine();

                var consolidatedPackages = projects.ConsolidatePackages();

                foreach (var package in consolidatedPackages)
                {
                    bool upgrade = true;

                    if (Upgrade.UpgradeType == UpgradeType.Prompt)
                    {
                        string resolvedVersion = package.ResolvedVersion?.ToString() ?? "";
                        string latestVersion = package.LatestVersion?.ToString() ?? "";

                        console.Write($"The package ");
                        console.Write(package.Description, Constants.ReporingColors.PackageName);
                        console.Write($" can be upgraded from {resolvedVersion} to ");
                        console.Write(latestVersion, GetUpgradeSeverityColor(package.UpgradeSeverity));
                        console.WriteLine(". The following project(s) will be affected:");
                        foreach (var project in package.Projects)
                        {
                            WriteProjectName(project.Description, console);
                        }

                        upgrade = Prompt.GetYesNo("Do you wish to upgrade this package?", true);
                    }

                    if (upgrade)
                    {
                        console.Write("Upgrading package ");
                        console.Write(package.Description, Constants.ReporingColors.PackageName);
                        console.Write("...");
                        console.WriteLine();

                        foreach (var project in package.Projects)
                        {
                            var status = _dotNetAddPackageService.AddPackage(project.ProjectFilePath, package.Name, project.Framework.ToString(), package.LatestVersion, NoRestore);

                            if (status.IsSuccess)
                            {
                                console.Write($"Project {project.Description} upgraded successfully", Constants.ReporingColors.UpgradeSuccess);
                                console.WriteLine();
                            }
                            else
                            {
                                success = false;
                                console.Write($"An error occurred while upgrading {project.Project}", Constants.ReporingColors.UpgradeFailure);
                                console.WriteLine();
                                console.Write(status.Errors, Constants.ReporingColors.UpgradeFailure);
                                console.WriteLine();
                            }
                        }
                    }

                    console.WriteLine();
                }
            }

            return success;
        }

        private void PrintColorLegend(IConsole console)
        {
            console.WriteLine("Version color legend:");

            console.Write("<red>".PadRight(8), Constants.ReporingColors.MajorVersionUpgrade);
            console.WriteLine(": Major version update or pre-release version. Possible breaking changes.");
            console.Write("<yellow>".PadRight(8), Constants.ReporingColors.MinorVersionUpgrade);
            console.WriteLine(": Minor version update. Backwards-compatible features added.");
            console.Write("<green>".PadRight(8), Constants.ReporingColors.PatchVersionUpgrade);
            console.WriteLine(": Patch version update. Backwards-compatible bug fixes.");
        }

        public static void WriteColoredUpgrade(DependencyUpgradeSeverity? upgradeSeverity, NuGetVersion resolvedVersion, NuGetVersion latestVersion, int resolvedWidth, int latestWidth, IConsole console)
        {
            console.Write((resolvedVersion?.ToString() ?? "").PadRight(resolvedWidth));
            console.Write(" -> ");

            // Exit early to avoid having to handle nulls later
            if (latestVersion == null)
            {
                console.Write("".PadRight(resolvedWidth));
                return;
            }
            var latestString = latestVersion.ToString().PadRight(latestWidth);
            if (resolvedVersion == null)
            {
                console.Write(latestString);
                return;
            }

            if (resolvedVersion.IsPrerelease)
            {
                console.Write(latestString, GetUpgradeSeverityColor(upgradeSeverity));
                return;
            }

            var matching = string.Join(".", resolvedVersion.GetParts()
                .Zip(latestVersion.GetParts(), (p1, p2) => (part: p2, matches: p1 == p2))
                .TakeWhile(p => p.matches)
                .Select(p => p.part));
            if (matching.Length > 0) { matching += "."; }
            var rest = new Regex($"^{matching}").Replace(latestString, "");

            console.Write($"{matching}");
            console.Write(rest, GetUpgradeSeverityColor(upgradeSeverity));
        }

        private void ReportOutdatedDependencies(List<AnalyzedProject> projects, IConsole console)
        {
            foreach (var project in projects)
            {
                WriteProjectName(project.Name, console);

                // Process each target framework with its related dependencies
                foreach (var targetFramework in project.TargetFrameworks)
                {
                    WriteTargetFramework(targetFramework, console);

                    var dependencies = targetFramework.Dependencies
                        .OrderBy(d => d.Name)
                        .ToList();

                    int[] columnWidths = dependencies.DetermineColumnWidths();

                    foreach (var dependency in dependencies)
                    {
                        console.WriteIndent();
                        console.Write(dependency.Description?.PadRight(columnWidths[0] + 2));

                        WriteColoredUpgrade(dependency.UpgradeSeverity, dependency.ResolvedVersion, dependency.LatestVersion, columnWidths[1], columnWidths[2], console);

                        console.WriteLine();
                    }
                }

                console.WriteLine();
            }

            if (projects.SelectMany(p => p.TargetFrameworks).SelectMany(f => f.Dependencies).Any(d => d.UpgradeSeverity == DependencyUpgradeSeverity.Unknown))
            {
                console.WriteLine("Errors occurred while analyzing dependencies for some of your projects. Are you sure you can connect to all your configured NuGet servers?", ConsoleColor.Red);
                if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")))
                {
                    // Issue #255: Sometimes the dotnet executable sets this
                    // variable for you, sometimes it doesn't. If it's not
                    // present, credential providers will be skipped.
                    console.WriteLine();
                    console.WriteLine("Unable to find DOTNET_HOST_PATH environment variable. If you use credential providers for your NuGet sources you need to have this set to the path to the `dotnet` executable.", ConsoleColor.Red);
                }

                console.WriteLine();
            }

            PrintColorLegend(console);
        }

        private async Task<List<AnalyzedProject>> AnalyzeDependencies(List<Project> projects, IConsole console)
        {
            var outdatedProjects = new ConcurrentBag<AnalyzedProject>();

            console.Write("Analyzing dependencies...");

            var tasks = new Task[projects.Count];

            for (var index = 0; index < projects.Count; index++)
            {
                var project = projects[index];
                tasks[index] = AddOutdatedProjectsIfNeeded(project, outdatedProjects, console);
            }

            await Task.WhenAll(tasks);
            
            if (!console.IsOutputRedirected)
                ClearCurrentConsoleLine();
            else
                console.WriteLine();

            return outdatedProjects.ToList();
        }

        private bool AnyIncludeFilterMatches(Dependency dep) =>
            FilterInclude.Any(f => NameContains(dep, f));

        private bool NoExcludeFilterMatches(Dependency dep) =>
            !FilterExclude.Any(f => NameContains(dep, f));

        private bool NameContains(Dependency dep, string part) =>
            dep.Name.Contains(part, StringComparison.InvariantCultureIgnoreCase);

        private async Task AddOutdatedProjectsIfNeeded(Project project, ConcurrentBag<AnalyzedProject> outdatedProjects, IConsole console)
        {
            var outdatedFrameworks = new ConcurrentBag<AnalyzedTargetFramework>();

            var tasks = new Task[project.TargetFrameworks.Count];

            // Process each target framework with its related dependencies
            for (var index = 0; index < project.TargetFrameworks.Count; index++)
            {
                var targetFramework = project.TargetFrameworks[index];
                tasks[index] = AddOutdatedFrameworkIfNeeded(targetFramework, project, outdatedFrameworks, console);
            }

            await Task.WhenAll(tasks);

            if (outdatedFrameworks.Count > 0)
                outdatedProjects.Add(new AnalyzedProject(project.Name, project.FilePath, outdatedFrameworks));
        }

        private async Task AddOutdatedFrameworkIfNeeded(TargetFramework targetFramework, Project project, ConcurrentBag<AnalyzedTargetFramework> outdatedFrameworks, IConsole console)
        {
            var outdatedDependencies = new ConcurrentBag<AnalyzedDependency>();

            var deps = targetFramework.Dependencies.Where(d => this.IncludeAutoReferences || d.IsAutoReferenced == false);

            if (FilterInclude.Any())
                deps = deps.Where(AnyIncludeFilterMatches);

            if (FilterExclude.Any())
                deps = deps.Where(NoExcludeFilterMatches);

            var dependencies = deps.OrderBy(dependency => dependency.IsTransitive)
                .ThenBy(dependency => dependency.Name)
                .ToList();

            var tasks = new Task[dependencies.Count];

            for (var index = 0; index < dependencies.Count; index++)
            {
                var dependency = dependencies[index];

                tasks[index] = this.AddOutdatedDependencyIfNeeded(project, targetFramework, dependency, outdatedDependencies);
            }

            await Task.WhenAll(tasks);

            if (outdatedDependencies.Count > 0)
                outdatedFrameworks.Add(new AnalyzedTargetFramework(targetFramework.Name, outdatedDependencies));
        }

        private async Task AddOutdatedDependencyIfNeeded(Project project, TargetFramework targetFramework, Dependency dependency, ConcurrentBag<AnalyzedDependency> outdatedDependencies)
        {
            var referencedVersion = dependency.ResolvedVersion;
            NuGetVersion latestVersion = null;

            if (referencedVersion != null)
            {
                latestVersion = await _nugetService.ResolvePackageVersions(dependency.Name, referencedVersion, project.Sources, dependency.VersionRange,
                    VersionLock, Prerelease, targetFramework.Name, project.FilePath, dependency.IsDevelopmentDependency, OlderThanDays);
            }

            if (referencedVersion == null || latestVersion == null || referencedVersion != latestVersion)
            {
                if (dependency.VersionRange.HasLowerAndUpperBounds)
                {
                    // do nothing, only log somewhere?
                    return;
                }
                
                // special case when there is version installed which is not older than "OlderThan" days makes "latestVersion" to be null
                if (OlderThanDays > 0 && latestVersion == null)
                {
                    NuGetVersion absoluteLatestVersion = await _nugetService.ResolvePackageVersions(dependency.Name, referencedVersion, project.Sources, dependency.VersionRange,
                        VersionLock, Prerelease, targetFramework.Name, project.FilePath, dependency.IsDevelopmentDependency);

                    if (absoluteLatestVersion == null || referencedVersion > absoluteLatestVersion)
                    {
                        outdatedDependencies.Add(new AnalyzedDependency(dependency, latestVersion));
                    }
                }
                else
                {
                    outdatedDependencies.Add(new AnalyzedDependency(dependency, latestVersion));
                }
            }
        }

        private static ConsoleColor GetUpgradeSeverityColor(DependencyUpgradeSeverity? upgradeSeverity)
        {
            switch (upgradeSeverity)
            {
                case DependencyUpgradeSeverity.Major:
                    return Constants.ReporingColors.MajorVersionUpgrade;
                case DependencyUpgradeSeverity.Minor:
                    return Constants.ReporingColors.MinorVersionUpgrade;
                case DependencyUpgradeSeverity.Patch:
                    return Constants.ReporingColors.PatchVersionUpgrade;
                default:
                    return Console.ForegroundColor;
            }
        }

        private void GenerateOutputFile(List<AnalyzedProject> projects)
        {
            if (OutputFilename != null)
            {
                Console.WriteLine();
                Console.WriteLine($"Generating {OutputFileFormat.ToString().ToUpper()} report...");
                string reportContent;
                switch (OutputFileFormat)
                {
                    case OutputFormat.Csv:
                        reportContent = Report.GetCsvReportContent(projects);
                        break;
                    default:
                        reportContent = Report.GetJsonReportContent(projects);
                        break;
                }
                _fileSystem.File.WriteAllText(OutputFilename, reportContent);

                Console.WriteLine($"Report written to {OutputFilename}");
                Console.WriteLine();
            }
        }

        private static void WriteProjectName(string name, IConsole console)
        {
            console.Write($"» {name}", Constants.ReporingColors.ProjectName);
            console.WriteLine();
        }

        private static void WriteTargetFramework(AnalyzedTargetFramework targetFramework, IConsole console)
        {
            console.WriteIndent();
            console.Write($"[{targetFramework.Name}]", Constants.ReporingColors.TargetFrameworkName);
            console.WriteLine();
        }

        public static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }
    }
}
