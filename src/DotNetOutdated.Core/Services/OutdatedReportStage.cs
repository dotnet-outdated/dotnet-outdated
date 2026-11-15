namespace DotNetOutdated.Core.Services;

/// <summary>The stage <see cref="IOutdatedDependencyService.FindOutdatedAsync"/> is starting, reported as it starts.</summary>
public enum OutdatedReportStage
{
    /// <summary>Finding the projects at the path.</summary>
    DiscoveringProjects,

    /// <summary>Reading the references and resolved dependencies of each project.</summary>
    AnalyzingProjects,

    /// <summary>Looking up the latest version of each dependency.</summary>
    FindingLatestVersions,
}
