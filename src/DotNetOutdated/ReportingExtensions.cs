using System;
using System.Collections.Generic;
using System.Linq;

namespace DotNetOutdated
{
    public static class ReportingExtensions
    {
        public static int[] DetermineColumnWidths(this List<ReportedPackage> packages)
        {
            List<int> columnWidths = new List<int>();
            columnWidths.Add(Math.Max(
                packages.Select(p => p.Name).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length, 
                Constants.Reporting.Headers.PackageName.Length));
            columnWidths.Add(Math.Max(
                packages.Select(p => p.ReferencedVersion?.ToString() ?? Constants.Reporting.UnknownValue).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length,
                Constants.Reporting.Headers.ReferencedVersion.Length));
            columnWidths.Add(Math.Max(
                packages.Select(p => p.LatestVersion?.ToString() ?? Constants.Reporting.UnknownValue).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length,
                Constants.Reporting.Headers.LatestVersion.Length));

            return columnWidths.ToArray();
        }
    }
}