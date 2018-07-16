using System;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DotNetOutdated.Exceptions;
using DotNetOutdated.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;

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

        [Option(CommandOptionType.NoValue, Description = "Specifies whether to include auto-referenced packages.",
            LongName = "include-auto-references")]
        public bool IncludeAutoReferences { get; set; } = false;

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

        [Option(CommandOptionType.SingleValue, Description = "Defines how many levels deep transitive dependencies should be analyzed. " +
                                                             "Integer value (default = 1)",
            ShortName="td", LongName = "transitive-depth")]
        public int TransitiveDepth { get; set; } = 1;

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
                console.Write("Discovering projects...");
                
                string projectPath = _projectDiscoveryService.DiscoverProject(Path);

                if (!console.IsOutputRedirected)
                    ClearCurrentConsoleLine();
                else
                    console.WriteLine();

                // Analyze the projects
                console.Write("Analyzing project and restoring packages...");
                
                var projects = _projectAnalysisService.AnalyzeProject(projectPath, Transitive, TransitiveDepth);

                if (!console.IsOutputRedirected)
                    ClearCurrentConsoleLine();
                else
                    console.WriteLine();

                if (console.IsOutputRedirected)
                    console.WriteLine("Analyzing packages...");
                
                foreach (var project in projects)
                {
                    // Process each target framework with its related dependencies
                    foreach (var targetFramework in project.TargetFrameworks)
                    {
                        var dependencies = targetFramework.Dependencies
                            .Where(d => IncludeAutoReferences || d.IsAutoReferenced == false)
                            .OrderBy(dependency => dependency.IsTransitive)
                            .ThenBy(dependency => dependency.Name)
                            .ToList();

                        for (var index = 0; index < dependencies.Count; index++)
                        {
                            var dependency = dependencies[index];
                            if (!console.IsOutputRedirected)
                                console.Write($"Analyzing packages for {project.Name} [{targetFramework.Name}] ({index + 1}/{dependencies.Count})");
                                    
                            var referencedVersion = dependency.ResolvedVersion;

                            dependency.LatestVersion = await _nugetService.ResolvePackageVersions(dependency.Name, referencedVersion, project.Sources, dependency.VersionRange,
                                VersionLock, Prerelease, targetFramework.Name, project.FilePath);

                            if (!console.IsOutputRedirected)
                                ClearCurrentConsoleLine();
                        }
                    }
                }

                // Get a flattened view of all the outdated packages
                var outdated = from p in projects
                    from f in p.TargetFrameworks
                    from d in f.Dependencies
                    where d.LatestVersion > d.ResolvedVersion
                    select new
                    {
                        Project = p.Name,
                        TargetFramework = f.Name,
                        Dependency = d.Name,
                        ResolvedVersion = d.ResolvedVersion,
                        LatestVersion = d.LatestVersion,
                        IsAutoReferenced = d.IsAutoReferenced,
                        IsTransitive = d.IsTransitive
                    };
                
                // Now group them by package
                var consolidatedPackages = outdated.GroupBy(p => new
                    {
                        p.Dependency,
                        p.ResolvedVersion,
                        p.LatestVersion,
                        p.IsTransitive,
                        p.IsAutoReferenced
                    })
                    .Select(gp => new ConsolidatedPackage
                    {
                        Name = gp.Key.Dependency,
                        ResolvedVersion = gp.Key.ResolvedVersion,
                        LatestVersion = gp.Key.LatestVersion,
                        IsTransitive = gp.Key.IsTransitive,
                        IsAutoReferenced = gp.Key.IsAutoReferenced,
                        Projects = gp.Select(v => new ConsolidatedPackage.PackageProjectReference
                        {
                            Project = v.Project, 
                            Framework = v.TargetFramework
                        }).ToList()
                    })
                    .ToList();

                // Report on packages
                int[] columnWidths = consolidatedPackages.DetermineColumnWidths();
                
                // Write header
                console.Write(ReportingExtensions.PackageTitle.PadRight(columnWidths[0]));
                console.Write(" | ");
                console.Write(ReportingExtensions.CurrentVersionTitle.PadRight(columnWidths[1]));
                console.Write(" | ");
                console.Write(ReportingExtensions.LatestVersionTitle.PadRight(columnWidths[2]));
                console.Write(" | ");
                console.Write(ReportingExtensions.ProjectTitle.PadRight(columnWidths[3]));
                console.WriteLine();
                
                // Write header separator
                console.Write(new String('-', columnWidths[0]));
                console.Write("-|-");
                console.Write(new String('-', columnWidths[1]));
                console.Write("-|-");
                console.Write(new String('-', columnWidths[2]));
                console.Write("-|-");
                console.Write(new String('-', columnWidths[3]));
                console.WriteLine();
                
                foreach (var package in consolidatedPackages)
                {
                    for (var index = 0; index < package.Projects.Count; index++)
                    {
                        var project = package.Projects[index];
                        if (index == 0)
                        {
                            console.Write(package.Title.PadRight(columnWidths[0]));
                            console.Write(" | ");
                            console.Write(package.ResolvedVersion.ToString().PadRight(columnWidths[1]));
                            console.Write(" | ");
                            console.Write(package.LatestVersion.ToString().PadRight(columnWidths[2]));
                            console.Write(" | ");
                        }
                        else
                        {
                            console.Write(new String(' ', columnWidths[0]));
                            console.Write(" | ");
                            console.Write(new String(' ', columnWidths[1]));
                            console.Write(" | ");
                            console.Write(new String(' ', columnWidths[2]));
                            console.Write(" | ");
                        }

                        console.Write(project.Name.PadRight(columnWidths[3]));
                        console.WriteLine();
                    }
                        
                    console.Write(new String('-', columnWidths[0]));
                    console.Write("-|-");
                    console.Write(new String('-', columnWidths[1]));
                    console.Write("-|-");
                    console.Write(new String('-', columnWidths[2]));
                    console.Write("-|-");
                    console.Write(new String('-', columnWidths[3]));
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

        public static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }
    }
}
