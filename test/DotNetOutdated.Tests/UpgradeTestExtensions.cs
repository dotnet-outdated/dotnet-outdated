using System.Linq;
using DotNetOutdated.Core.Services;
using NuGet.Versioning;

namespace DotNetOutdated.Tests
{
    internal static class UpgradeTestExtensions
    {
        // Positional shorthand for the many upgrade tests of a single project.
        public static ProjectUpgradeResult Upgrade(
            this IReferenceUpgrader upgrader,
            string projectPath,
            string name,
            string targetFramework,
            NuGetVersion version,
            bool noRestore = false,
            bool ignoreFailedSources = false,
            ReferenceKind? kind = ReferenceKind.Package) =>
            upgrader.Upgrade(new ReferenceUpgrade
            {
                Name = name,
                Kind = kind,
                Version = version,
                Projects = [new UpgradeProject(projectPath, targetFramework)],
                NoRestore = noRestore,
                IgnoreFailedSources = ignoreFailedSources,
            }).Single();
    }
}
