namespace DotNetOutdated.Core.Services;

/// <summary>How <see cref="IProjectAnalysisService.AnalyzeProjectAsync"/> analyzes a project.</summary>
public sealed record ProjectAnalysisOptions
{
    /// <summary>The default options: no restore, direct references only.</summary>
    public static ProjectAnalysisOptions Default { get; } = new();

    /// <summary>Run <c>dotnet restore</c> on each project before reading its assets file.</summary>
    public bool RunRestore { get; init; }

    /// <summary>Also report the packages that references bring in, down to <see cref="TransitiveDepth"/> levels.</summary>
    public bool IncludeTransitiveDependencies { get; init; }

    /// <summary>How many levels of transitive dependencies to report; 1 for the dependencies of references only.</summary>
    public int TransitiveDepth { get; init; } = 1;

    /// <summary>The runtime identifier to restore for, if any.</summary>
    public string? Runtime { get; init; }
}
