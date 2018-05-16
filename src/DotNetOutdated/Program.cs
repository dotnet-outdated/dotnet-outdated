using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection;
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

        [Argument(0, Description = "The path to a .sln or .csproj file, or to a directory containing a .NET Core solution/project. " +
                                   "If none is specified, the current directory will be used.")]
        public string Path { get; set; }
        
        public static int Main(string[] args)
        {
            using (var nuGetPackageInfoService = new NuGetPackageInfoService())
            {
                var services = new ServiceCollection()
                    .AddSingleton<IConsole, PhysicalConsole>()
                    .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                    .AddSingleton<IFileSystem, FileSystem>()
                    .AddSingleton<INuGetPackageInfoService>(nuGetPackageInfoService)
                    .BuildServiceProvider();

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

        public Program(IFileSystem fileSystem, IReporter reporter, INuGetPackageInfoService nugetService)
        {
            _fileSystem = fileSystem;
            _reporter = reporter;
            _nugetService = nugetService;
        }
        
        public async Task<int> OnExecute(CommandLineApplication app, IConsole console)
        {
            try
            {
                // If no path is set, use the current directory
                if (string.IsNullOrEmpty(Path))
                    Path = _fileSystem.Directory.GetCurrentDirectory();
            
                // Get all the projects
                var projects = DiscoverProjects(Path);
            
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
                            NuGetVersion referencedVersion = NuGetVersion.Parse(packageRerefence.GetMetadataValue("version"));
                            NuGetVersion latestVersion = await _nugetService.GetLatestVersion(packageRerefence.EvaluatedInclude, referencedVersion.IsPrerelease);

                            reportedPackages.Add(new ReportedPackage(packageRerefence.EvaluatedInclude, referencedVersion, latestVersion));
                        }

                        // Report on packages
                        console.Write("\r");
                        int[] columnWidths = reportedPackages.DetermineColumnWidths();
                        foreach (var reportedPackage in reportedPackages)
                        {
                            console.Write(reportedPackage.Name.PadRight(columnWidths[0]),
                                reportedPackage.LatestVersion > reportedPackage.ReferencedVersion ? ConsoleColor.Red : ConsoleColor.Green);
                            console.Write("  ");
                            console.Write(reportedPackage.ReferencedVersion.ToString().PadRight(columnWidths[1]));
                            console.Write("  ");
                            console.Write(reportedPackage.LatestVersion.ToString().PadRight(columnWidths[2]),
                                reportedPackage.LatestVersion > reportedPackage.ReferencedVersion ? ConsoleColor.Blue : console.ForegroundColor);
                            console.WriteLine();
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
        
        private IEnumerable<Project> DiscoverProjects(string path)
        {
            if (!(_fileSystem.File.Exists(path) || _fileSystem.Directory.Exists(path)))
                throw new CommandValidationException($"The directory or file '{path}' does not exist");

            var fileInfo = _fileSystem.File.GetAttributes(path);
            
            // 
            if (fileInfo.HasFlag(FileAttributes.Directory))
            {
                // Search for solution(s)
                var solutionFiles = _fileSystem.Directory.GetFiles(path, "*.sln");
                if (solutionFiles.Length > 0)
                {
                    return solutionFiles.Select(DiscoverProjectsFromSolution)
                        .SelectMany(p => p)
                        .GroupBy(p => p.FullPath) // Do GroupBy and Select to ensure we only select distinct projects
                        .Select(g => g.First())
                        .ToList();
                }

                // At this point, we did not find any solutions, so try and find individual projects
                var projectFiles = _fileSystem.Directory.GetFiles(path, "*.csproj");
                if (projectFiles.Length > 0)
                {
                    AnalyzerManager manager = new AnalyzerManager();
                    return projectFiles.Select(manager.GetProject)
                        .Select(pa => pa.Project)
                        .ToList();
                }
                
                // At this point the path contains no solutions or projects, so throw an exception
                throw new CommandValidationException($"The path '{path} does not contain any solutions or projects.");
            }

            return null;
        }

        private IEnumerable<Project> DiscoverProjectsFromSolution(string solutionPath)
        {
            AnalyzerManager manager = new AnalyzerManager(solutionPath);

            return manager.Projects.Values.Select(pa => pa.Project).ToList();
        }
    }

    public static class ReportingExtensions
    {
        public static int[] DetermineColumnWidths(this List<ReportedPackage> packages)
        {
            List<int> columnWidths = new List<int>();
            columnWidths.Add(packages.Select(p => p.Name).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length);
            columnWidths.Add(packages.Select(p => p.ReferencedVersion.ToString()).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length);
            columnWidths.Add(packages.Select(p => p.LatestVersion.ToString()).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length);

            return columnWidths.ToArray();
        }
    }
}
