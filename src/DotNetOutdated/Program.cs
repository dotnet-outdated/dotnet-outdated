using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DotNetOutdated.Exceptions;
using DotNetOutdated.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;

[assembly:InternalsVisibleTo("DotNetOutdated.Tests")]

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
        private readonly INuGetPackageInfoService _nugetService;
        private readonly IProjectAnalysisService _projectAnalysisService;
        private readonly IProjectDiscoveryService _projectDiscoveryService;

        [Argument(0, Description = "The path to a .sln or .csproj file, or to a directory containing a .NET Core solution/project. " +
                                   "If none is specified, the current directory will be used.")]
        public string Path { get; set; }

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether to look for pre-release versions of packages. " +
                                                             "Possible Values: Auto (default), Always or Never.",
            ShortName = "pr", LongName = "pre-release")]
        public PrereleaseReporting Prerelease { get; set; } = PrereleaseReporting.Auto;
        
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
                    .AddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>()
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

        public Program(IFileSystem fileSystem, IReporter reporter, INuGetPackageInfoService nugetService, IProjectAnalysisService projectAnalysisService,
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
                var projects = _projectAnalysisService.AnalyzeProject(projectPath);
                
                foreach (var project in projects)
                {
                    WriteProjectName(console, project);
                    
                    foreach (var dependency in project.Dependencies)
                    {
                        await ReportDependency(console, dependency, project.Sources, 1);
                    }
                    
                    foreach (var targetFramework in project.TargetFrameworks)
                    {
                        WriteTargetFramework(console, targetFramework);

                        foreach (var dependency in targetFramework.Dependencies)
                        {
                            await ReportDependency(console, dependency, project.Sources, 2);
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

        private static void WriteTargetFramework(IConsole console, Project.TargetFramework targetFramework)
        {
            console.WriteIndent(1);
            console.Write($"[{targetFramework.Name}]", ConsoleColor.Cyan);
            console.WriteLine();
        }

        private async Task ReportDependency(IConsole console, Project.Dependency dependency, List<Uri> sources, int level)
        {
            // Get the current version
            NuGetVersion referencedVersion = dependency.VersionRange.MinVersion;

            // Get the latest version
            bool includePrerelease = referencedVersion.IsPrerelease;
            if (Prerelease == PrereleaseReporting.Always)
                includePrerelease = true;
            else if (Prerelease == PrereleaseReporting.Never)
                includePrerelease = false;
            NuGetVersion latestVersion = await _nugetService.GetLatestVersion(dependency.Name, sources, includePrerelease);

            console.WriteIndent(level);
            console.Write($"{dependency.Name} ");
            console.Write(referencedVersion, latestVersion > referencedVersion ? ConsoleColor.Red : ConsoleColor.Green);

            if (latestVersion > referencedVersion)
            {
                console.Write(" (");
                console.Write(latestVersion, ConsoleColor.Blue);
                console.Write(")");
            }
            console.WriteLine();
        }
    }
}
