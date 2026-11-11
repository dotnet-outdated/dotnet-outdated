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
        IOutdatedDependencyService outdatedDependencyService,
        IProjectAnalysisService projectAnalysisService,
        IProjectDiscoveryService projectDiscoveryService,
        IDotNetPackageService dotNetPackageService,
        DotNetRunnerOptions dotNetRunnerOptions)
   {
      private readonly IFileSystem _fileSystem = fileSystem;
      private readonly IReporter _reporter = reporter;
      private readonly IOutdatedDependencyService _outdatedDependencyService = outdatedDependencyService;
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
                 .AddSingleton<IWarningReporter>(provider => provider.GetRequiredService<IReporter>())
                 .AddSingleton<ILogger>(provider => new ConsoleLogger(provider.GetService<IConsole>(), LogLevel.Warning))
                 .AddDotNetOutdated()
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

            // The restore graph of a project holds the projects it references too, so with --recursive a referenced
            // project is analyzed both on its own and as part of each project that references it (issue #830).
            // Report and upgrade each project file once. The paths are compared with symbolic links resolved, because MSBuild
            // reports a referenced project by its real path (on macOS the temp folder /var is a link to /private/var).
            var projects = projectLists
               .SelectMany(p => p)
               .DistinctBy(p => CanonicalPath(p.FilePath), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
               .ToList();

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

      /// <summary>
      /// The full path of <paramref name="path"/> with every symbolic link along it resolved, so two paths to the same
      /// file compare equal. A part that can't be resolved is kept as it is.
      /// </summary>
      private string CanonicalPath(string path)
      {
         var fullPath = _fileSystem.Path.GetFullPath(path);
         var root = _fileSystem.Path.GetPathRoot(fullPath) ?? string.Empty;
         var current = root;

         foreach (var segment in fullPath[root.Length..].Split(
            [_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
         {
            current = _fileSystem.Path.Combine(current, segment);

            try
            {
               if (_fileSystem.FileInfo.New(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
               {
                  current = target.FullName;
               }
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
               // Not resolvable (e.g. a broken link or no permission): compare this part as written.
            }
         }

         return current;
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
         console.WriteLine("Analyzing dependencies...");

         var options = new OutdatedOptions
         {
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

         var outdatedProjects = await _outdatedDependencyService.FindOutdatedAsync(projects, options).ConfigureAwait(false);

         return outdatedProjects.Select(AnalyzedProject.From).ToList();
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
