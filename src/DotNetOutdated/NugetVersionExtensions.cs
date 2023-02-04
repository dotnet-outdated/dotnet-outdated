using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DotNetOutdated
{
    public static class NugetVersionExtensions
    {
        public static IEnumerable<string> GetParts(this NuGetVersion version)
        {
            ArgumentNullException.ThrowIfNull(version);

            return YieldItems();

            IEnumerable<string> YieldItems()
            {
                yield return version.Major.ToString(CultureInfo.InvariantCulture);
                yield return version.Minor.ToString(CultureInfo.InvariantCulture);
                yield return version.Patch.ToString(CultureInfo.InvariantCulture);
                yield return version.Revision.ToString(CultureInfo.InvariantCulture);

                foreach (var label in version.ReleaseLabels)
                {
                    yield return label;
                }
            }
        }
    }
}
