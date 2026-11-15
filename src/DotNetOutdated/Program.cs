using DotNetOutdated.Core;
using DotNetOutdated.Core.Exceptions;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Common;
using NuGet.Versioning;
using System;
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
        IOutdatedDependencyService outdatedDependencyService,
        IReferenceUpgrader referenceUpgrader,
        INuGetPackageInfoService nuGetPackageInfoService,
        DotNetRunnerOptions dotNetRunnerOptions)
   {
      private readonly IFileSystem _fileSystem = fileSystem;
      private readonly IReporter _reporter = reporter;
      private readonly IOutdatedDependencyService _outdatedDependencyService = outdatedDependencyService;
      private readonly IReferenceUpgrader _referenceUpgrader = referenceUpgrader;
      private readonly INuGetPackageInfoService _nuGetPackageInfoService = nuGetPackageInfoService;
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
         ProgramCommandLine commandLine = null;
         commandLine = new ProgramCommandLine(
            run: async (parseResult, _) =>
            {
               // NuGet logs at the level --nuget-cred-log-level asks for, credential providers included.
               await using var services = new ServiceCollection()
                       .AddSingleton(PhysicalConsole.Singleton)
                       .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                       .AddSingleton<ILogger>(provider => new ConsoleLogger(provider.GetService<IConsole>(), parseResult.GetValue(commandLine.NuGetCredLogLevel)))
                       .AddDotNetOutdated()
                       .BuildServiceProvider();

               var program = ActivatorUtilities.CreateInstance<Program>(services);
               commandLine.Bind(parseResult, program);
               return await program.OnExecute(services.GetRequiredService<IConsole>());
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

            var report = await _outdatedDependencyService.FindOutdatedAsync(Path, CreateOutdatedOptions(), new ConsoleProgress(console)).ConfigureAwait(false);

            // Show only the target frameworks, and the projects, with something to report.
            var outdatedProjects = report.Projects
               .Select(project => project with { TargetFrameworks = [.. project.TargetFrameworks.Where(targetFramework => targetFramework.Dependencies.Count != 0)] })
               .Where(project => project.TargetFrameworks.Count != 0)
               .ToList();

            if (outdatedProjects.Count != 0)
            {
               // Report on the outdated dependencies
               ReportOutdatedDependencies(outdatedProjects, console);

               // Upgrade the packages
               var success = await UpgradePackagesAsync(outdatedProjects, console).ConfigureAwait(false);

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

      internal async Task<bool> UpgradePackagesAsync(IReadOnlyList<OutdatedProject> projects, IConsole console)
      {
         bool success = true;

         if (!Upgrade.HasValue) return true;

         console.WriteLine();

         var consolidatedPackages = projects.ConsolidatePackages();

         foreach (var package in consolidatedPackages)
         {
            bool upgrade = true;
            string referenceNoun = package.Kind == ReferenceKind.Sdk ? "SDK" : "package";

            // A target framework the new version declares unsupported (with a build warning) keeps its version; the
            // others are upgraded.
            var heldBack = await FindUnsupportedTargetFrameworksAsync(package).ConfigureAwait(false);
            var upgradedProjects = package.Projects.Except(heldBack).ToList();
            if (upgradedProjects.Count == 0)
            {
               WriteHeldBack(package, heldBack, [], upgraded: false, console);
               console.WriteLine();
               continue;
            }

            var request = new ReferenceUpgrade
            {
               Name = package.Name,
               Kind = package.Kind,
               Version = package.LatestVersion,
               Projects = upgradedProjects.Select(ToUpgradeProject).ToList(),
               HeldBack = heldBack.Select(ToUpgradeProject).ToList(),
               NoRestore = NoRestore,
               IgnoreFailedSources = IgnoreFailedSources,
            };

            // A held-back project that shares its version with an upgraded one is upgraded too.
            var upgradedToo = heldBack.Count == 0 ? [] : _referenceUpgrader.FindHeldBackSharingVersion(request).ToHashSet();

            if (Upgrade.UpgradeType == UpgradeType.Prompt)
            {
               string resolvedVersion = package.ResolvedVersion?.ToString() ?? "";
               string latestVersion = package.LatestVersion?.ToString() ?? "";

               console.Write($"The {referenceNoun} ");
               console.Write(package.Description, Constants.ReportingColors.PackageName);
               console.Write($" can be upgraded from {resolvedVersion} to ");
               console.Write(latestVersion, GetUpgradeSeverityColor(package.UpgradeSeverity));
               console.WriteLine(". The following project(s) will be affected:");
               foreach (var project in upgradedProjects.Concat(heldBack.Where(project => upgradedToo.Contains(ToUpgradeProject(project)))))
               {
                  WriteProjectName(project.Description, console);
               }

               WriteHeldBack(package, heldBack, upgradedToo, upgraded: false, console);

               bool? answer = Prompt.GetYesNo(console, $"Do you wish to upgrade this {referenceNoun}?", true);
               if (answer is null)
               {
                  // The input has ended, so no-one can confirm this or any later upgrade (#831).
                  console.WriteLine();
                  console.WriteLine("No input is available to answer the prompt, so the remaining upgrades were skipped.");
                  break;
               }

               upgrade = answer.Value;
            }

            if (upgrade)
            {
               console.Write($"Upgrading {referenceNoun} ");
               console.Write(package.Description, Constants.ReportingColors.PackageName);
               console.Write("...");
               console.WriteLine();

               var results = _referenceUpgrader.Upgrade(request);

               foreach (var result in results)
               {
                  string projectName = package.Projects.First(project => project.ProjectFilePath == result.ProjectPath).Project;
                  foreach (var warning in result.Warnings)
                  {
                     console.WriteLine(warning, ConsoleColor.Yellow);
                  }

                  if (result.IsSuccess)
                  {
                     console.Write(
                        result.Outcome == UpgradeOutcome.AlreadyCurrent
                           ? $"Project {projectName} already has {package.Name} {package.LatestVersion}"
                           : $"Project {projectName} upgraded successfully",
                        Constants.ReportingColors.UpgradeSuccess);
                     console.WriteLine();
                  }
                  else
                  {
                     success = false;
                     console.Write($"An error occurred while upgrading {projectName}", Constants.ReportingColors.UpgradeFailure);
                     console.WriteLine();
                     if (result.FailureDetails.Length > 0)
                     {
                        console.Write(result.FailureDetails, Constants.ReportingColors.UpgradeFailure);
                        console.WriteLine();
                     }
                  }
               }

               // The prompt has said what happens to the held-back projects already. One that was to be upgraded too
               // but isn't in a result's HeldBackUpgraded failed, which its result has reported.
               if (Upgrade.UpgradeType != UpgradeType.Prompt)
               {
                  var upgradedWith = results.SelectMany(result => result.HeldBackUpgraded).ToHashSet();
                  WriteHeldBack(
                     package,
                     heldBack.Where(project => !upgradedToo.Contains(ToUpgradeProject(project)) || upgradedWith.Contains(ToUpgradeProject(project))),
                     upgradedWith,
                     upgraded: true,
                     console);
               }
            }

            console.WriteLine();
         }

         return success;
      }

      private static UpgradeProject ToUpgradeProject(PackageProjectReference project) =>
         new(project.ProjectFilePath, project.TargetAlias);

      // The projects (target frameworks) the package's new version declares unsupported.
      private async Task<List<PackageProjectReference>> FindUnsupportedTargetFrameworksAsync(ConsolidatedPackage package)
      {
         if (package.Kind != ReferenceKind.Package)
         {
            return [];
         }

         var checks = await Task.WhenAll(package.Projects.Select(async project => (
            Project: project,
            Unsupported: await _nuGetPackageInfoService.DeclaresTargetFrameworkUnsupportedAsync(new TargetFrameworkSupportQuery
            {
               PackageId = package.Name,
               Version = package.LatestVersion,
               TargetFramework = project.Framework,
               Sources = project.Sources,
               ProjectFilePath = project.ProjectFilePath,
            }).ConfigureAwait(false)))).ConfigureAwait(false);

         return checks.Where(check => check.Unsupported).Select(check => check.Project).ToList();
      }

      // Says why each held-back project isn't upgraded, or, for one that shares its version with an upgraded project
      // (in upgradedToo), that it is upgraded anyway: before the upgrade, or once it has been (upgraded).
      private static void WriteHeldBack(ConsolidatedPackage package, IEnumerable<PackageProjectReference> heldBack, HashSet<UpgradeProject> upgradedToo, bool upgraded, IConsole console)
      {
         foreach (var project in heldBack)
         {
            string framework = project.Framework.GetShortFolderName();
            console.WriteLine(
               upgradedToo.Contains(ToUpgradeProject(project))
                  ? $"{package.Name} {package.LatestVersion} doesn't support {framework}, but {project.Description} shares its version with a target framework " +
                    (upgraded ? "that was upgraded, so it was upgraded too. " : "being upgraded, so it will be upgraded too. ") +
                    $"Declare the reference separately for each target framework to keep {framework} on {package.ResolvedVersion}."
                  : $"{package.Name} {package.LatestVersion} doesn't support {framework}, so {project.Description} keeps {package.ResolvedVersion}. " +
                    "Move to a supported target framework to upgrade it.",
               ConsoleColor.Yellow);
         }
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

      private static void ReportOutdatedDependencies(List<OutdatedProject> projects, IConsole console)
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
                  console.Write(dependency.Description().PadRight(columnWidths[0] + 2));

                  WriteColoredUpgrade(dependency.UpgradeSeverity, dependency.Dependency.ResolvedVersion, dependency.LatestVersion, columnWidths[1], columnWidths[2], console);

                  console.WriteLine();
               }
            }

            console.WriteLine();
         }

         var allDependencies = projects.SelectMany(p => p.TargetFrameworks).SelectMany(f => f.Dependencies).ToList();

         // A range without a lower bound is never looked up, so it says nothing about the NuGet sources.
         var unboundedDependencies = allDependencies
            .Where(d => d.LatestVersionUnknownReason == LatestVersionUnknownReason.NoLowerBound)
            .Select(d => d.Dependency)
            .DistinctBy(d => (d.Name, d.VersionRange))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
         foreach (var dependency in unboundedDependencies)
         {
            console.WriteLine($"The latest version of {dependency.Name} can't be determined because its version range {dependency.VersionRange} has no lower bound.", ConsoleColor.Yellow);
         }

         if (unboundedDependencies.Count != 0)
         {
            console.WriteLine();
         }

         // Restore failing to resolve a package is the usual way a source problem shows up here: a lookup that can't
         // reach a source reports the resolved version instead.
         if (allDependencies.Any(d => d.LatestVersionUnknownReason is LatestVersionUnknownReason.Unresolved or LatestVersionUnknownReason.NotProvided))
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

      private OutdatedOptions CreateOutdatedOptions() => new()
      {
         Recursive = Recursive,
         IncludeFileBasedApps = IncludeFileBasedApps,
         IncludeTransitiveDependencies = Transitive,
         TransitiveDepth = TransitiveDepth,
         Runtime = Runtime,
         VersionLock = VersionLock,
         Prerelease = Prerelease,
         PrereleaseLabel = PrereleaseLabel,
         OlderThanDays = OlderThanDays,
         MaximumVersion = ParseMaximumVersion(),
         IgnoreFailedSources = IgnoreFailedSources,
         IncludeAutoReferences = IncludeAutoReferences,
         IncludeUpToDate = IncludeUpToDate,
         IncludeFilters = FilterInclude,
         ExcludeFilters = FilterExclude,
      };

      /// <summary>Says which stage of finding outdated dependencies has started, as it starts.</summary>
      private sealed class ConsoleProgress(IConsole console) : IProgress<OutdatedReportStage>
      {
         public void Report(OutdatedReportStage value) => console.WriteLine(value switch
         {
            OutdatedReportStage.DiscoveringProjects => "Discovering projects...",
            OutdatedReportStage.AnalyzingProjects => "Analyzing project(s)...",
            _ => "Analyzing dependencies...",
         });
      }

      private NuGetVersion ParseMaximumVersion()
      {
         if (string.IsNullOrEmpty(MaxVersion))
         {
            return null;
         }

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

         return new NuGetVersion(maxVersion);
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

      private async Task GenerateOutputFile(List<OutdatedProject> projects)
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

      private static void WriteTargetFramework(OutdatedTargetFramework targetFramework, IConsole console)
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
