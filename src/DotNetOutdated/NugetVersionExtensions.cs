using System.Collections.Generic;
using NuGet.Versioning;

namespace DotNetOutdated
{
    public static class NugetVersionExtensions
    {
        public static IEnumerable<string> GetParts(this NuGetVersion version)
        {
            yield return version.Major.ToString();
            yield return version.Minor.ToString();
            yield return version.Patch.ToString();
            yield return version.Revision.ToString();
            foreach (var label in version.ReleaseLabels)
            {
                yield return label;
            }
        }
    }
}
