using CsvHelper;

namespace DotNetOutdated.Models
{
    /// <summary>A row of the CSV report.</summary>
    [CsvRecord]
    public partial class CsvDependency
    {
        public string ProjectName { get; set; }
        public string TargetFrameworkName { get; set; }
        public string DependencyName { get; set; }
        public string ResolvedVersion { get; set; }
        public string LatestVersion { get; set; }
        public string UpgradeSeverity { get; set; }
    }
}
