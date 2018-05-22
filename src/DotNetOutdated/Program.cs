using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Buildalyzer;
using DotNetOutdated.Exceptions;
using DotNetOutdated.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Build.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
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
        private readonly IDependencyGraphService _dependencyGraphService;
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

        public Program(IFileSystem fileSystem, IReporter reporter, INuGetPackageInfoService nugetService, IDependencyGraphService dependencyGraphService, 
            IProjectDiscoveryService projectDiscoveryService)
        {
            _fileSystem = fileSystem;
            _reporter = reporter;
            _nugetService = nugetService;
            _dependencyGraphService = dependencyGraphService;
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
                string project = _projectDiscoveryService.DiscoverProject(Path);
                console.WriteLine(project);
                
                return 0; // for now...
                
                /*
                foreach (var project in projects)
                {
                    console.WriteHeader(project.FullPath);
                    
                    List<ReportedPackage> reportedPackages = new List<ReportedPackage>();

                    // Get package references
                    var packageRerefences = project.Items.Where(i => i.ItemType == "PackageReference" && i.IsImported == false).ToList();
                    if (packageRerefences.Count == 0)
                    {
                        console.WriteLine("-- Project contains no package references --");
                    }
                    else
                    {
                        // Analyze packages
                        console.Write("Analyzing packages...");
                        foreach (var packageRerefence in packageRerefences)
                        {
                            // Get the current version
                            NuGetVersion referencedVersion = NuGetVersion.Parse(packageRerefence.GetMetadataValue("version"));
                            
                            // Get the latest version
                            bool includePrerelease = referencedVersion.IsPrerelease;
                            if (Prerelease == PrereleaseReporting.Always)
                                includePrerelease = true;
                            else if (Prerelease == PrereleaseReporting.Never)
                                includePrerelease = false;
                            NuGetVersion latestVersion = await _nugetService.GetLatestVersion(packageRerefence.EvaluatedInclude, includePrerelease);

                            reportedPackages.Add(new ReportedPackage(packageRerefence.EvaluatedInclude, referencedVersion, latestVersion));
                        }

                        // Report on packages
                        console.Write("\r");
                        int[] columnWidths = reportedPackages.DetermineColumnWidths();
                        WriteHeader(console, columnWidths);

                        foreach (var reportedPackage in reportedPackages)
                        {
                            string referencedVersion = reportedPackage.ReferencedVersion?.ToString() ?? Constants.Reporting.UnknownValue;
                            string latestVersion = reportedPackage.LatestVersion?.ToString() ?? Constants.Reporting.UnknownValue;
                            
                            console.Write(reportedPackage.Name.PadRight(columnWidths[0]),
                                reportedPackage.LatestVersion > reportedPackage.ReferencedVersion ? ConsoleColor.Red : ConsoleColor.Green);
                            console.Write("  ");
                            console.Write(referencedVersion.PadRight(columnWidths[1]));
                            console.Write("  ");
                            console.Write(latestVersion.PadRight(columnWidths[2]),
                                reportedPackage.LatestVersion > reportedPackage.ReferencedVersion ? ConsoleColor.Blue : console.ForegroundColor);
                            console.WriteLine();
                        }
                    }

                    console.WriteLine();
                }
            
                return 0;
                */

            }
            catch (CommandValidationException e)
            {
                _reporter.Error(e.Message);
                
                return 1;
            }
        }
        
        private static void WriteHeader(IConsole console, int[] columnWidths)
        {
            console.Write(Constants.Reporting.Headers.PackageName.PadRight(columnWidths[0]));
            console.Write("  ");
            console.Write(Constants.Reporting.Headers.ReferencedVersion.PadRight(columnWidths[1]));
            console.Write("  ");
            console.WriteLine(Constants.Reporting.Headers.LatestVersion.PadRight(columnWidths[2]));

            console.Write(new String('-', columnWidths[0]));
            console.Write("  ");
            console.Write(new String('-', columnWidths[1]));
            console.Write("  ");
            console.WriteLine(new String('-', columnWidths[2]));
        }

        private IEnumerable<Project> DiscoverProjectsFromSolution(string solutionPath)
        {
            AnalyzerManager manager = new AnalyzerManager(solutionPath);

            return manager.Projects.Values.Select(pa => pa.Project).ToList();
        }
    }
}
