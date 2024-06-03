using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

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

        public static (string matching, string rest) MatchVersionString(this NuGetVersion resolvedVersion, NuGetVersion latestVersion, string latestString)
        {
            var matching = string.Join('.', resolvedVersion.GetParts()
                .Zip(latestVersion.GetParts(), (p1, p2) => (part: p2, matches: p1 == p2))
                .TakeWhile(p => p.matches)
                .Select(p => p.part));
            if (matching.Length > 0) { matching += '.'; }
            var rest = new Regex($"^{matching}").Replace(latestString, "");

            return (matching, rest);
        }
    }
}
