using System;
using System.Collections.Generic;
using System.Linq;
using DotNetOutdated.Core.Models;

namespace DotNetOutdated
{
    internal static class ProjectExtensions
    {
        public static List<ConsolidatedPackage> ConsolidatePackages(this IReadOnlyList<OutdatedProject> projects)
        {
            // Get a flattened view of all the outdated packages
            var outdated = from p in projects
                           from f in p.TargetFrameworks
                           from d in f.Dependencies
                           where d.LatestVersion > d.Dependency.ResolvedVersion
                           select new
                           {
                               Project = p.Name,
                               ProjectFilePath = p.FilePath,
                               Sources = p.Sources,
                               TargetFramework = f.Name,
                               TargetAlias = f.TargetAlias,
                               Dependency = d.Dependency.Name,
                               Kind = d.Dependency.Kind,
                               ResolvedVersion = d.Dependency.ResolvedVersion,
                               LatestVersion = d.LatestVersion,
                               IsAutoReferenced = d.Dependency.IsAutoReferenced,
                               IsTransitive = d.Dependency.IsTransitive,
                               UpgradeSeverity = d.UpgradeSeverity,
                           };

            // Now group them by package
            var consolidatedPackages = outdated.GroupBy(p => new
            {
                p.Dependency,
                p.Kind,
                p.ResolvedVersion,
                p.LatestVersion,
                p.IsTransitive,
                p.IsAutoReferenced,
                p.UpgradeSeverity
            })
                .Select(gp => new ConsolidatedPackage
                {
                    Name = gp.Key.Dependency,
                    Kind = gp.Key.Kind,
                    ResolvedVersion = gp.Key.ResolvedVersion,
                    LatestVersion = gp.Key.LatestVersion,
                    IsTransitive = gp.Key.IsTransitive,
                    IsAutoReferenced = gp.Key.IsAutoReferenced,
                    UpgradeSeverity = gp.Key.UpgradeSeverity,
                    Projects = gp.Select(v => new PackageProjectReference
                    {
                        Project = v.Project,
                        ProjectFilePath = v.ProjectFilePath,
                        Sources = v.Sources,
                        Framework = v.TargetFramework,
                        TargetAlias = v.TargetAlias,
                    }).ToList()
                })
                .ToList();

            return consolidatedPackages;
        }
    }
}
