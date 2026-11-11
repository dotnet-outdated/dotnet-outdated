using DotNetOutdated.Core.Services;
using NuGet.Versioning;

namespace DotNetOutdated.Tests
{
    internal static class UpgradeTestExtensions
    {
        // Positional shorthand for the many upgrade tests that differ only in these values.
        public static RunStatus Upgrade(
            this IDotNetPackageService service,
            string projectPath,
            string name,
            string targetFramework,
            VersionRange version,
            bool noRestore = false,
            bool ignoreFailedSources = false,
            ReferenceKind? kind = ReferenceKind.Package) =>
            service.Upgrade(new ReferenceUpgrade
            {
                ProjectPath = projectPath,
                Name = name,
                Kind = kind,
                TargetFramework = targetFramework,
                Version = version,
                NoRestore = noRestore,
                IgnoreFailedSources = ignoreFailedSources,
            });
    }
}
