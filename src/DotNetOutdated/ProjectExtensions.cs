using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using DotNetOutdated.Core;
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
                               TargetAlias = f.TargetAlias,
                               Dependency = d.Name,
                               Kind = d.Kind,
                               ResolvedVersion = d.ResolvedVersion,
                               LatestVersion = d.LatestVersion,
                               IsAutoReferenced = d.IsAutoReferenced,
                               IsTransitive = d.IsTransitive,
                               UpgradeSeverity = d.UpgradeSeverity,
                               OriginalVersionRange = d.OriginalVersionRange,
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
                        Framework = v.TargetFramework,
                        TargetAlias = v.TargetAlias,
                        OriginalVersionRange = v.OriginalVersionRange,
                    }).ToList()
                })
                .ToList();

            return consolidatedPackages;
        }

        public static bool IsProjectSdkStyle(this PackageProjectReference project)
        {
            // A file-based app (a .cs file) has no project file to be anything but SDK-style.
            if (project.ProjectFilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

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
                // SDKs can also be declared with child elements, e.g. <Sdk Name="Microsoft.Build.NoTargets" Version="3.7.0" />
                // or <Import Project="Sdk.props" Sdk="Microsoft.Build.NoTargets" Version="3.7.0" />
                return xml.Root.Name == (ns + "Project") &&
                       (!string.IsNullOrEmpty(xml.Root.Attribute("Sdk")?.Value) ||
                        xml.Root.Elements(ns + "Sdk").Any() ||
                        xml.Root.Descendants(ns + "Import").Any(import => !string.IsNullOrEmpty(import.Attribute("Sdk")?.Value)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while reading project file: {ex.Message}", Constants.ReportingColors.UpgradeFailure);
                return false;
            }
        }
    }
}
