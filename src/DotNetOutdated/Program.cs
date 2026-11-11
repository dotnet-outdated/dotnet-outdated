using DotNetOutdated.Core;
using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using DotNetOutdated.Models;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Common;
using NuGet.Credentials;
using NuGet.Versioning;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("DotNetOutdated.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace DotNetOutdated
{
   internal class Program(
        IFileSystem fileSystem,
        IReporter reporter,
        INuGetPackageResolutionService nugetService,
        IProjectAnalysisService projectAnalysisService,
        IProjectDiscoveryService projectDiscoveryService,
        IDotNetPackageService dotNetPackageService,
        DotNetRunnerOptions dotNetRunnerOptions)
   {
      private readonly IFileSystem _fileSystem = fileSystem;
      private readonly IReporter _reporter = reporter;
      private readonly INuGetPackageResolutionService _nugetService = nugetService;
      private readonly IProjectAnalysisService _projectAnalysisService = projectAnalysisService;
      private readonly IProjectDiscoveryService _projectDiscoveryService = projectDiscoveryService;
      private readonly IDotNetPackageService _dotNetPackageService = dotNetPackageService;
      private readonly DotNetRunnerOptions _dotNetRunnerOptions = dotNetRunnerOptions;

      public bool IncludeAutoReferences { get; set; } = false;

      public string Path { get; set; }

      public PrereleaseReporting Prerelease { get; set; } = PrereleaseReporting.Auto;

      public VersionLock VersionLock { get; set; } = VersionLock.None;

      public bool Transitive { get; set; } = false;

      public int TransitiveDepth { get; set; } = 1;

      public (bool HasValue, UpgradeType UpgradeType) Upgrade { get; set; }

      public bool FailOnUpdates { get; set; } = false;

      public List<string> FilterInclude { get; set; } = [];

      public List<string> FilterExclude { get; set; } = [];

      public string OutputFilename { get; set; } = null;

      public OutputFormat OutputFileFormat { get; set; } = OutputFormat.Json;

      public int OlderThanDays { get; set; }

      public bool NoRestore { get; set; } = false;

      public bool Recursive { get; set; } = false;

      public bool IncludeFileBasedApps { get; set; } = false;

      public bool IgnoreFailedSources { get; set; } = false;

      public bool IncludeUpToDate { get; set; } = false;

      public string PrereleaseLabel { get; set; } = string.Empty;

      public LogLevel NuGetCredLogLevel { get; set; } = LogLevel.Warning;
      
      public string Runtime { get; set; } = string.Empty;

      public string MaxVersion { get; set; } = string.Empty;

      public int IdleTimeout { get; set; } = 120;
      
      public static int Main(string[] args)
      {
         using var services = new ServiceCollection()
                 .AddSingleton(PhysicalConsole.Singleton)
                 .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                 .AddSingleton<IFileSystem, FileSystem>()
                 .AddSingleton<ILogger>(provider => new ConsoleLogger(provider.GetService<IConsole>(), LogLevel.Warning))
                 .AddSingleton<IProjectDiscoveryService, ProjectDiscoveryService>()
                 .AddSingleton<IProjectAnalysisService, ProjectAnalysisService>()
                 .AddSingleton<DotNetRunnerOptions>()
                .AddSingleton<IDotNetRunner, DotNetRunner>()
                 .AddSingleton<IDependencyGraphService, DependencyGraphService>()
                 .AddSingleton<IDotNetRestoreService, DotNetRestoreService>()
                 .AddSingleton<IDotNetPackageService>(provider =>
                     new DotNetPackageService(
                         provider.GetService<IDotNetRunner>(),
                         provider.GetService<IFileSystem>(),
                         msg => provider.GetService<IReporter>().Warn(msg)))
                 .AddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>()
                 .AddSingleton<INuGetPackageResolutionService, NuGetPackageResolutionService>()
                 .BuildServiceProvider();

         ProgramCommandLine commandLine = null;
         commandLine = new ProgramCommandLine(
            run: (parseResult, _) =>
            {
               var program = ActivatorUtilities.CreateInstance<Program>(services);
               commandLine.Bind(parseResult, program);
               return program.OnExecute(services.GetRequiredService<IConsole>());
            },
            runMcp: _ => new McpCommand().OnExecuteAsync());

         return commandLine.Parse(args).Invoke();
      }

      public static string GetVersion() => typeof(Program)
          .Assembly
          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
          .InformationalVersion;

      public async Task<int> OnExecute(IConsole console)
      {
         ArgumentNullException.ThrowIfNull(console);
         
         _dotNetRunnerOptions.IdleTimeout = TimeSpan.FromSeconds(IdleTimeout);

         try
         {
            var stopwatch = Stopwatch.StartNew();

            // If no path is set, use the current directory
            if (string.IsNullOrEmpty(Path))
               Path = _fileSystem.Directory.GetCurrentDirectory();

            // Get all the projects
            console.WriteLine("Discovering projects...");

            DefaultCredentialServiceUtility.SetupDefaultCredentialService(new ConsoleLogger(console, NuGetCredLogLevel), true);

            var projectPaths = _projectDiscoveryService.DiscoverProjects(Path, Recursive, IncludeFileBasedApps);

            // Analyze the projects
            console.WriteLine("Analyzing project(s)...");

            var projectLists = new ConcurrentBag<IReadOnlyList<Project>>();
            await Parallel.ForEachAsync(projectPaths, async (path, _) =>
            {
                projectLists.Add(await _projectAnalysisService.AnalyzeProjectAsync(path, new ProjectAnalysisOptions
                {
                    IncludeTransitiveDependencies = Transitive,
                    TransitiveDepth = TransitiveDepth,
                    Runtime = Runtime,
                }));
            });

            var projects = projectLists.SelectMany(p => p).ToList();

            // Analyze the dependencies
            var outdatedProjects = await AnalyzeDependencies(projects, console).ConfigureAwait(false);

            if (outdatedProjects.Count != 0)
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
               await GenerateOutputFile(outdatedProjects);

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

      internal bool UpgradePackages(List<AnalyzedProject> projects, IConsole console)
      {
         bool success = true;

         if (!Upgrade.HasValue) return true;

         console.WriteLine();

         var consolidatedPackages = projects.ConsolidatePackages();

         foreach (var package in consolidatedPackages)
         {
            bool upgrade = true;
            string referenceNoun = package.Kind == ReferenceKind.Sdk ? "SDK" : "package";

            if (Upgrade.UpgradeType == UpgradeType.Prompt)
            {
               string resolvedVersion = package.ResolvedVersion?.ToString() ?? "";
               string latestVersion = package.LatestVersion?.ToString() ?? "";

               console.Write($"The {referenceNoun} ");
               console.Write(package.Description, Constants.ReportingColors.PackageName);
               console.Write($" can be upgraded from {resolvedVersion} to ");
               console.Write(latestVersion, GetUpgradeSeverityColor(package.UpgradeSeverity));
               console.WriteLine(". The following project(s) will be affected:");
               foreach (var project in package.Projects)
               {
                  WriteProjectName(project.Description, console);
               }

               upgrade = Prompt.GetYesNo(console, $"Do you wish to upgrade this {referenceNoun}?", true);
            }

            if (upgrade)
            {
               console.Write($"Upgrading {referenceNoun} ");
               console.Write(package.Description, Constants.ReportingColors.PackageName);
               console.Write("...");
               console.WriteLine();

               foreach (var project in package.Projects)
               {
                  RunStatus status = null;

                  if (package.Kind == ReferenceKind.Package && !project.IsProjectSdkStyle())
                  {
                     console.WriteLine("Project format not SDK style or centrally managed, removing package before upgrade.");
                     status = _dotNetPackageService.RemovePackage(project.ProjectFilePath, package.Name);
                  }

                  if (status is null || status.IsSuccess)
                  {
                     var newVersionRange = UpgradeVersionRange.Compute(project.OriginalVersionRange, package.LatestVersion, package.IsTransitive);
                     var frameworkName = string.IsNullOrEmpty(project.TargetAlias)
                         ? project.Framework.GetShortFolderName()
                         : project.TargetAlias;

                     status = _dotNetPackageService.Upgrade(new ReferenceUpgrade
                     {
                        ProjectPath = project.ProjectFilePath,
                        Name = package.Name,
                        Kind = package.Kind,
                        TargetFramework = frameworkName,
                        Version = newVersionRange,
                        NoRestore = NoRestore,
                        IgnoreFailedSources = IgnoreFailedSources,
                     });
                  }

                  if (status.IsSuccess)
                  {
                     console.Write($"Project {project.Description} upgraded successfully", Constants.ReportingColors.UpgradeSuccess);
                     console.WriteLine();
                  }
                  else
                  {
                     success = false;
                     console.Write($"An error occurred while upgrading {project.Project}", Constants.ReportingColors.UpgradeFailure);
                     console.WriteLine();
                     string failureDetails = status.FailureDetails;
                     if (failureDetails.Length > 0)
                     {
                        console.Write(failureDetails, Constants.ReportingColors.UpgradeFailure);
                        console.WriteLine();
                     }
                  }
               }
            }

            console.WriteLine();
         }

         return success;
      }

      private static void PrintColorLegend(IConsole console)
      {
         console.WriteLine("Version color legend:");

         console.Write("<red>".PadRight(8), Constants.ReportingColors.MajorVersionUpgrade);
         console.WriteLine(": Major version update or pre-release version. Possible breaking changes.");
         console.Write("<yellow>".PadRight(8), Constants.ReportingColors.MinorVersionUpgrade);
         console.WriteLine(": Minor version update. Backwards-compatible features added.");
         console.Write("<green>".PadRight(8), Constants.ReportingColors.PatchVersionUpgrade);
         console.WriteLine(": Patch version update. Backwards-compatible bug fixes.");
      }

      public static void WriteColoredUpgrade(DependencyUpgradeSeverity? upgradeSeverity, NuGetVersion resolvedVersion, NuGetVersion latestVersion, int resolvedWidth, int latestWidth, IConsole console)
      {
         console.Write((resolvedVersion?.ToString() ?? "").PadRight(resolvedWidth));
         console.Write(resolvedVersion?.Equals(latestVersion) ?? false ? " == " : " -> ");

         // Exit early to avoid having to handle nulls later
         if (latestVersion == null)
         {
            console.Write("".PadRight(resolvedWidth));
            return;
         }

         var latestString = latestVersion.ToString().PadRight(latestWidth);
         if (resolvedVersion == null || resolvedVersion.Equals(latestVersion))
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

      private static void ReportOutdatedDependencies(List<AnalyzedProject> projects, IConsole console)
      {
         foreach (var project in projects)
         {
            WriteProjectName(project.Name, console);

            // Process each target framework with its related dependencies
            foreach (var targetFramework in project.TargetFrameworks)
            {
               WriteTargetFramework(targetFramework, console);

               var dependencies = targetFramework.Dependencies;

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

         console.WriteLine("Analyzing dependencies...");

         await Parallel.ForEachAsync(projects, async (project, _) => await AddOutdatedProjectsIfNeeded(project, outdatedProjects));

         return outdatedProjects
             .OrderBy(p => p.Name)
             .ThenBy(p => p.FilePath)
             .ToList();
      }

      private bool AnyIncludeFilterMatches(Dependency dep) =>
          FilterInclude.Any(f => NameContains(dep, f));

      private bool NoExcludeFilterMatches(Dependency dep) =>
          !FilterExclude.Any(f => NameContains(dep, f));

      private static bool NameContains(Dependency dep, string part) =>
          dep.Name.Contains(part, StringComparison.InvariantCultureIgnoreCase);

      private async Task AddOutdatedProjectsIfNeeded(Project project, ConcurrentBag<AnalyzedProject> outdatedProjects)
      {
         var outdatedFrameworks = new ConcurrentBag<AnalyzedTargetFramework>();

         var tasks = new Task[project.TargetFrameworks.Count];

         // Process each target framework with its related dependencies
         for (var index = 0; index < project.TargetFrameworks.Count; index++)
         {
            var targetFramework = project.TargetFrameworks[index];
            tasks[index] = AddOutdatedFrameworkIfNeeded(targetFramework, project, outdatedFrameworks);
         }

         await Task.WhenAll(tasks).ConfigureAwait(false);

         if (!outdatedFrameworks.IsEmpty)
            outdatedProjects.Add(new AnalyzedProject(project.Name, project.FilePath, outdatedFrameworks));
      }

      private async Task AddOutdatedFrameworkIfNeeded(TargetFramework targetFramework, Project project, ConcurrentBag<AnalyzedTargetFramework> outdatedFrameworks)
      {
         var outdatedDependencies = new ConcurrentBag<AnalyzedDependency>();

         IEnumerable<Dependency> deps = targetFramework.Dependencies.Values;
         
         if (!this.IncludeAutoReferences)
         {
            deps = deps.Where(d => !d.IsAutoReferenced);
         }

         if (FilterInclude.Count != 0)
            deps = deps.Where(AnyIncludeFilterMatches);

         if (FilterExclude.Count != 0)
            deps = deps.Where(NoExcludeFilterMatches);

         NuGetVersion maximumVersion = null;

         if (!string.IsNullOrEmpty(MaxVersion))
         {
             if (!Version.TryParse(MaxVersion, out var maxVersion))
             {
                 throw new CommandValidationException($"The specified maximum version '{MaxVersion}' is not a valid version string.");
             }

             // NuGetVersion normalizes no build or revision to 0, when we actually want
             // those to mean "any version", so we need to force them to int.MaxValue.
             if (maxVersion.Build is -1 || maxVersion.Revision is -1)
             {
                 var build = maxVersion.Build == -1 ? int.MaxValue : maxVersion.Build;
                 var revision = maxVersion.Revision == -1 ? int.MaxValue : maxVersion.Revision;
                 maxVersion = new(maxVersion.Major, maxVersion.Minor, build, revision);
             }

             maximumVersion = new NuGetVersion(maxVersion);
         }

         var dependencies = deps.OrderBy(dependency => dependency.IsTransitive)
             .ThenBy(dependency => dependency.Name)
             .ToArray();

         var tasks = new Task[dependencies.Length];

         for (var index = 0; index < dependencies.Length; index++)
         {
            var dependency = dependencies[index];

            tasks[index] = AddOutdatedDependencyIfNeeded(project, targetFramework, dependency, maximumVersion, outdatedDependencies);
         }

         await Task.WhenAll(tasks).ConfigureAwait(false);

         if (!outdatedDependencies.IsEmpty)
            outdatedFrameworks.Add(new AnalyzedTargetFramework(targetFramework.Name, outdatedDependencies)
            {
               TargetAlias = targetFramework.TargetAlias,
            });
      }

      private async Task AddOutdatedDependencyIfNeeded(
          Project project,
          TargetFramework targetFramework,
          Dependency dependency,
          NuGetVersion maximumVersion,
          ConcurrentBag<AnalyzedDependency> outdatedDependencies)
      {
         var referencedVersion = dependency.ResolvedVersion;
         var versionRange = dependency.VersionRange;
         NuGetVersion latestVersion = null;

        if (maximumVersion is not null &&
            (versionRange.MaxVersion is null || maximumVersion < versionRange.MaxVersion))
        {
            // Cap the declared version range at the user-specified maximum: the effective
            // upper bound is whichever of the two is lower, so pinned versions (e.g. [1.0.0])
            // and bounded ranges (e.g. [1.0.0, 2.0.0)) are never widened by --maximum-version.
            versionRange = new(
                versionRange.MinVersion,
                versionRange.IsMinInclusive,
                maximumVersion,
                includeMaxVersion: true,
                versionRange.Float,
                versionRange.OriginalString);
        }

         LatestVersionQuery query = null;
         if (referencedVersion != null)
         {
            query = new LatestVersionQuery
            {
                PackageId = dependency.Name,
                CurrentVersion = referencedVersion,
                AllowedRange = versionRange,
                Sources = project.Sources.ToList(),
                TargetFramework = targetFramework.Name,
                ProjectFilePath = project.FilePath,
                IsDevelopmentDependency = dependency.IsDevelopmentDependency,
                VersionLock = VersionLock,
                Prerelease = Prerelease,
                PrereleaseLabel = PrereleaseLabel,
                OlderThanDays = OlderThanDays,
                IgnoreFailedSources = IgnoreFailedSources,
            };
            latestVersion = await _nugetService.ResolveLatestVersionAsync(query).ConfigureAwait(false);
         }

         if (referencedVersion == null || latestVersion == null || referencedVersion != latestVersion || IncludeUpToDate)
         {
            // special case when there is version installed which is not older than "OlderThan" days makes "latestVersion" to be null
            if (OlderThanDays > 0 && latestVersion == null)
            {
               NuGetVersion absoluteLatestVersion = query == null
                   ? null
                   : await _nugetService.ResolveLatestVersionAsync(query with { OlderThanDays = 0, IgnoreFailedSources = false }).ConfigureAwait(false);

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
         return upgradeSeverity switch
         {
            DependencyUpgradeSeverity.Major => Constants.ReportingColors.MajorVersionUpgrade,
            DependencyUpgradeSeverity.Minor => Constants.ReportingColors.MinorVersionUpgrade,
            DependencyUpgradeSeverity.Patch => Constants.ReportingColors.PatchVersionUpgrade,
            _ => Console.ForegroundColor,
         };
      }

      private async Task GenerateOutputFile(List<AnalyzedProject> projects)
      {
         if (OutputFilename != null)
         {
            Console.WriteLine();
            Console.WriteLine($"Generating {OutputFileFormat.ToString().ToUpperInvariant()} report...");
            var sw = _fileSystem.File.CreateText(OutputFilename);
            await using (sw.ConfigureAwait(false))
            {
               IOutputFormatter formatter = OutputFileFormat switch
               {
                  OutputFormat.Csv => new Formatters.CsvFormatter(),
                  OutputFormat.Markdown => new Formatters.MarkdownFormatter(),
                  _ => new Formatters.JsonFormatter(),
               };
               await formatter.FormatAsync(projects, sw).ConfigureAwait(false);
               Console.WriteLine($"Report written to {OutputFilename}");
               Console.WriteLine();
            }
         }
      }

      private static void WriteProjectName(string name, IConsole console)
      {
         console.Write($"» {name}", Constants.ReportingColors.ProjectName);
         console.WriteLine();
      }

      private static void WriteTargetFramework(AnalyzedTargetFramework targetFramework, IConsole console)
      {
         console.WriteIndent();
         console.Write($"[{targetFramework.Name}]", Constants.ReportingColors.TargetFrameworkName);
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
