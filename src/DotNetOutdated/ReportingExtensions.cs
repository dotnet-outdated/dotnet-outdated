using DotNetOutdated.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DotNetOutdated
{
    public static class ReportingExtensions
    {
        public static int[] DetermineColumnWidths(this IReadOnlyList<AnalyzedDependency> packages)
        {
            ArgumentNullException.ThrowIfNull(packages);

            return
            [
                packages.Max(p => p.Description.Length),
                packages.Max(p => p.ResolvedVersion?.ToString().Length ?? 0),
                packages.Max(p => p.LatestVersion?.ToString().Length ?? 0)
            ];
        }
    }
}
