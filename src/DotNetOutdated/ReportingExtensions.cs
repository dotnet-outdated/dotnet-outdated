using System.Collections.Generic;
using System.Linq;

namespace DotNetOutdated
{
    public static class ReportingExtensions
    {
        public static int[] DetermineColumnWidths(this List<ConsolidatedPackage> packages)
        {
            List<int> columnWidths = new List<int>();
            columnWidths.Add(packages.Select(p => p.Title).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length);
            columnWidths.Add(packages.Select(p => p.ResolvedVersion.ToString()).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length);
            columnWidths.Add(packages.Select(p => p.LatestVersion.ToString()).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length);
            columnWidths.Add(packages.SelectMany(p => p.Projects).Select(p => p.Name).Aggregate("", (max, cur) => max.Length > cur.Length ? max : cur).Length);

            return columnWidths.ToArray();
        }
    }
}