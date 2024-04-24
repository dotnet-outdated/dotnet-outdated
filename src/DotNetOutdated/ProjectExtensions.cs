using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DotNetOutdated.Models;

namespace DotNetOutdated
{
    internal static class ProjectExtensions
    {
        public static List<ConsolidatedPackage> ConsolidatePackages(this List<AnalyzedProject> projects)
        {
            // Get a flattened view of all the outdated packages
            var outdated = from p in projects
                           from f in p.TargetFrameworks
                           from d in f.Dependencies
                           where d.LatestVersion > d.ResolvedVersion
                           select new
                           {
                               Project = p.Name,
                               ProjectFilePath = p.FilePath,
                               TargetFramework = f.Name,
                               Dependency = d.Name,
                               ResolvedVersion = d.ResolvedVersion,
                               LatestVersion = d.LatestVersion,
                               IsAutoReferenced = d.IsAutoReferenced,
                               IsTransitive = d.IsTransitive,
                               IsVersionCentrallyManaged = d.IsVersionCentrallyManaged,
                               UpgradeSeverity = d.UpgradeSeverity
                           };

            // Now group them by package
            var consolidatedPackages = outdated.GroupBy(p => new
            {
                p.Dependency,
                p.ResolvedVersion,
                p.LatestVersion,
                p.IsTransitive,
                p.IsAutoReferenced,
                p.IsVersionCentrallyManaged,
                p.UpgradeSeverity
            })
                .Select(gp => new ConsolidatedPackage
                {
                    Name = gp.Key.Dependency,
                    ResolvedVersion = gp.Key.ResolvedVersion,
                    LatestVersion = gp.Key.LatestVersion,
                    IsTransitive = gp.Key.IsTransitive,
                    IsAutoReferenced = gp.Key.IsAutoReferenced,
                    IsVersionCentrallyManaged = gp.Key.IsVersionCentrallyManaged,
                    UpgradeSeverity = gp.Key.UpgradeSeverity,
                    Projects = gp.Select(v => new PackageProjectReference
                    {
                        Project = v.Project,
                        ProjectFilePath = v.ProjectFilePath,
                        Framework = v.TargetFramework
                    }).ToList()
                })
                .ToList();

            return consolidatedPackages;
        }

        public static bool IsProjectSdkStyle(this PackageProjectReference project)
        {
            try
            {
                var xml = XDocument.Load(project.ProjectFilePath);

                if (xml.Root == null)
                {
                    return false;
                }

                // If the project file declares the xmlns attribute, we need to account for it in queries for elements.
                // Otherwise the query will return no results and the project type will be misidentified.
                // e.g. <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003" Sdk="Microsoft.NET.Sdk">
                XNamespace ns = xml.Root.GetDefaultNamespace();
                return xml.Root.Name == (ns + "Project") && !string.IsNullOrEmpty(xml.Root.Attribute("Sdk")?.Value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while reading project file: {ex.Message}", Constants.ReportingColors.UpgradeFailure);
                return false;
            }
        }
    }
}
