using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Buildalyzer;
using DotNetOutdated.Exceptions;
using GitStatusCli;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Build.Evaluation;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetOutdated
{
    [Command(
        Name = "dotnet outdated", 
        FullName = "A .NET Core global tool to list outdated Nuget packages.")]
    [VersionOptionFromMember(MemberName = nameof(GetVersion))]
    class Program : CommandBase
    {
        private readonly IFileSystem _fileSystem;

        [Argument(0, Description = "The path to a .sln or .csproj file, or to a directory containing a .NET Core solution/project. " +
                                   "If none is specified, the current directory will be used.")]
        public string Path { get; set; }
        
        public static int Main(string[] args)
        {
            var services = new ServiceCollection()
                .AddSingleton<IConsole, PhysicalConsole>()
                .AddSingleton<IFileSystem, FileSystem>() 
                .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
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
        
        public static string GetVersion() => typeof(Program)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            .InformationalVersion;

        public Program(IFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }
        
        public async Task<int> OnExecute(CommandLineApplication app, IConsole console)
        {
            // If no path is set, use the current directory
            if (string.IsNullOrEmpty(Path))
                Path = _fileSystem.Directory.GetCurrentDirectory();
            
            // Get all the projects
            var projects = DiscoverProjects(Path);
            
            foreach (var project in projects)
            {
                console.WriteLine(project.FullPath);
            }
            
            return 0;
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
                        .GroupBy(p => p.FullPath) // Do GroupBy and Select to ensure we only select distinc projects
                        .Select(g => g.First())
                        .ToList();
                }

                // At this point, we did not find any solutions, so try and find individual projects
                var projectFiles = _fileSystem.Directory.GetFiles(path, "*.csproj");
                if (projectFiles.Length > 0)
                {
                    
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
}
