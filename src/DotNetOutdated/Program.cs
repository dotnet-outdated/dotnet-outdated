using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DotNetOutdated.Exceptions;
using DotNetOutdated.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using NuGet.ProjectModel;
using NuGet.Versioning;

[assembly: InternalsVisibleTo("DotNetOutdated.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace DotNetOutdated
{
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

        [Argument(0, Description = "The path to a .sln or .csproj file, or to a directory containing a .NET Core solution/project. " +
                                   "If none is specified, the current directory will be used.")]
        public string Path { get; set; }

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether to look for pre-release versions of packages. " +
                                                             "Possible values: Auto (default), Always or Never.",
            ShortName = "pr", LongName = "pre-release")]
        public PrereleaseReporting Prerelease { get; set; } = PrereleaseReporting.Auto;

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether the package should be locked to the current Major or Minor version. " +
                                                             "Possible values: None (default), Major or Minor.",
            ShortName = "vl", LongName = "version-lock")]
        public VersionLock VersionLock { get; set; } = VersionLock.None;

        [Option(CommandOptionType.NoValue, Description = "Specifies whether it should detect transitive dependencies.",
            ShortName = "t", LongName = "transitive")]
        public bool Transitive { get; set; } = false;

        public static int Main(string[] args)
        {
            using (var services = new ServiceCollection()
                    .AddSingleton<IConsole, PhysicalConsole>()
                    .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                    .AddSingleton<IFileSystem, FileSystem>()
                    .AddSingleton<IProjectDiscoveryService, ProjectDiscoveryService>()
                    .AddSingleton<IProjectAnalysisService, ProjectAnalysisService>()
                    .AddSingleton<IDotNetRunner, DotNetRunner>()
                    .AddSingleton<IDependencyGraphService, DependencyGraphService>()
                    .AddSingleton<IDotNetRestoreService, DotNetRestoreService>()
                    .AddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>()
                    .AddSingleton<INuGetPackageResolutionService, NuGetPackageResolutionService>()
                    .BuildServiceProvider())
            {
                var app = new CommandLineApplication<Program>
                {
                    ThrowOnUnexpectedArgument = false
                };
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
            IProjectDiscoveryService projectDiscoveryService)
        {
            _fileSystem = fileSystem;
            _reporter = reporter;
            _nugetService = nugetService;
            _projectAnalysisService = projectAnalysisService;
            _projectDiscoveryService = projectDiscoveryService;
        }

        public async Task<int> OnExecute(CommandLineApplication app, IConsole console)
        {
            try
            {
                // If no path is set, use the current directory
                if (string.IsNullOrEmpty(Path))
                    Path = _fileSystem.Directory.GetCurrentDirectory();

                // Get all the projects
                string projectPath = _projectDiscoveryService.DiscoverProject(Path);

                // Analyze the projects
                var projects = _projectAnalysisService.AnalyzeProject(projectPath, Transitive);

                foreach (var project in projects)
                {
                    int indentLevel = 1;

                    WriteProjectName(console, project);

                    // Process each target framework with its related dependencies
                    foreach (var targetFramework in project.TargetFrameworks)
                    {
                        WriteTargetFramework(console, targetFramework, indentLevel);

                        if (targetFramework.Dependencies.Count > 0)
                        {
                            foreach (var dependency in targetFramework.Dependencies)
                            {
                                await ReportDependency(console, dependency, dependency.VersionRange, project.Sources, indentLevel, targetFramework);
                            }
                        }
                        else
                        {
                            console.WriteIndent(indentLevel);
                            console.WriteLine("-- No dependencies --");
                        }
                    }

                    console.WriteLine();
                }

                return 0;
            }
            catch (CommandValidationException e)
            {
                _reporter.Error(e.Message);

                return 1;
            }
        }

        private static void WriteProjectName(IConsole console, Project project)
        {
            console.Write($"» {project.Name}", ConsoleColor.Yellow);
            console.WriteLine();
        }

        private static void WriteTargetFramework(IConsole console, Project.TargetFramework targetFramework, int indentLevel)
        {
            console.WriteIndent(indentLevel);
            console.Write($"[{targetFramework.Name}]", ConsoleColor.Cyan);
            console.WriteLine();
        }

        private async Task ReportDependency(IConsole console, Project.Dependency dependency, VersionRange versionRange, List<Uri> sources,  int indentLevel, Project.TargetFramework targetFramework)
        {
            console.WriteIndent(indentLevel);
            console.Write($"{dependency.Name} ");

            console.Write("...");

            var (referencedVersion, latestVersion) = await _nugetService.ResolvePackageVersions(dependency.Name, sources, versionRange, VersionLock, Prerelease);
            referencedVersion = dependency.ResolvedVersion;
                
            console.Write("\b\b\b");

            console.Write(referencedVersion, latestVersion > referencedVersion ? ConsoleColor.Red : ConsoleColor.Green);

            if (latestVersion > referencedVersion)
            {
                console.Write(" (");
                console.Write(latestVersion, ConsoleColor.Blue);
                console.Write(")");
            }
            console.WriteLine();
            
            foreach (var childDependency in dependency.Dependencies)
            {
                await ReportDependency(console, childDependency, childDependency.VersionRange, sources, indentLevel + 1, targetFramework);
            }
        }
    }
}
