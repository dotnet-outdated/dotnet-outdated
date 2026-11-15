using DotNetOutdated.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DotNetOutdated
{
    public static class ReportingExtensions
    {
        /// <summary>The dependency's name, marked [A] when the SDK adds it or [T] when a reference brings it in.</summary>
        public static string Description(this OutdatedDependency dependency)
        {
            ArgumentNullException.ThrowIfNull(dependency);

            string description = dependency.Dependency.Name;

            if (dependency.Dependency.IsAutoReferenced)
                description += " [A]";
            else if (dependency.Dependency.IsTransitive)
                description += " [T]";

            return description;
        }

        public static int[] DetermineColumnWidths(this IReadOnlyList<OutdatedDependency> packages)
        {
            ArgumentNullException.ThrowIfNull(packages);

            return
            [
                packages.Max(p => p.Description().Length),
                packages.Max(p => p.Dependency.ResolvedVersion?.ToString().Length ?? 0),
                packages.Max(p => p.LatestVersion?.ToString().Length ?? 0)
            ];
        }
    }
}
