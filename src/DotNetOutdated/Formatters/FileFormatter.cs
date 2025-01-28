using DotNetOutdated.Models;
using McMaster.Extensions.CommandLineUtils;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;

namespace DotNetOutdated.Formatters;

/// <summary>
/// <see cref="FileFormatter"/> is base class for format that should write on fileSystem
/// </summary>
internal abstract class FileFormatter : IOutputFormatter
{
    private readonly IFileSystem _fileSystem;

    public FileFormatter(IFileSystem fileSystem, IConsole console)
    {
        _fileSystem = fileSystem;
        Console = console;
    }

    protected IConsole Console { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="projects"></param>
    /// <param name="options"></param>
    public virtual async Task FormatAsync(IReadOnlyList<AnalyzedProject> projects
        , IDictionary<string, string> options)
    {
        Console.WriteLine();
        Console.Write($"Generating {GetType().Name.Replace("Formatter", "", System.StringComparison.OrdinalIgnoreCase).ToLowerInvariant()} report ...");
        if (options.TryGetValue("outputFile", out var outputFile) && !string.IsNullOrWhiteSpace(outputFile))
        {
            outputFile = _fileSystem.Path.ChangeExtension(outputFile, Extension);
            using var stream = _fileSystem.File.Create(outputFile);
            using var sw = new StreamWriter(stream);
            await FormatAsync(projects, options, sw);
        }
        else
        {
            Console.WriteLine();
            Console.Error.Write("Output file option not set.", Constants.ReportingColors.Error);
            Console.WriteLine();
        }
    }

    internal protected abstract Task FormatAsync(IReadOnlyList<AnalyzedProject> projects
        , IDictionary<string, string> options
        , TextWriter writer);

    protected abstract string Extension { get; }
}
