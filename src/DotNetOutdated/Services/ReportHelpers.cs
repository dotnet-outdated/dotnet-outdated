using System;
using System.Text;
using System.Collections.Generic;
using System.IO;
using System.Resources;
using System.Threading.Tasks;
using CsvHelper;
using DotNetOutdated.Models;
using Newtonsoft.Json;

namespace DotNetOutdated.Services
{
    public interface IReportingService
    {
        Task WriteReport(string filename, List<Project> projects);
    }

    public class JsonReportingService : IReportingService
    {
        public Task WriteReport(string filename, List<Project> projects)
        {
            throw new NotImplementedException();
        }
    }

    internal class ToStringJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return true;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToString());
        }

        public override bool CanRead
        {
            get { return false; }
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }

    public class Report
    {
        public List<Project> Projects { get; set; }

        internal static string GetTextReportLine(Project project, TargetFramework targetFramework, Dependency dependency)
        {
            var upgradeSeverity = "";
            if (dependency.UpgradeSeverity.HasValue)
            {
                upgradeSeverity = Enum.GetName(typeof(DependencyUpgradeSeverity), dependency.UpgradeSeverity);
            }
            return string.Format("{0};{1};{2};{3};{4};{5}",
                project.Name,
                targetFramework.Name,
                dependency.Name,
                dependency.ResolvedVersion,
                dependency.LatestVersion,
                upgradeSeverity);
        }

        public static string GetCsvReportContent(List<Project> projects)
        {
            using (var sw = new StringWriter())
            using (var csv = new CsvWriter(sw))
            {
                foreach (var project in projects)
                {
                    foreach (var targetFramework in project.TargetFrameworks)
                    {
                        foreach (var dependency in targetFramework.Dependencies)
                        {
                            var upgradeSeverity = "";
                            if (dependency.UpgradeSeverity.HasValue)
                            {
                                upgradeSeverity = Enum.GetName(typeof(DependencyUpgradeSeverity), dependency.UpgradeSeverity);
                            }

                            csv.WriteRecord(new
                            {
                                ProjectName = project.Name,
                                TargetFrameworkName = targetFramework.Name.DotNetFrameworkName,
                                DependencyName = dependency.Name,
                                ResolvedVersion = dependency.ResolvedVersion?.ToString(),
                                LatestVersion = dependency.LatestVersion?.ToString(),
                                UpgradeSeverity = upgradeSeverity
                            });
                            csv.NextRecord();
                        }
                    }
                }

                return sw.ToString();
            }
        }

        public static string GetJsonReportContent(List<Project> projects)
        {
            var report = new Report
            {
                Projects = projects
            };
            return JsonConvert.SerializeObject(report, Formatting.Indented);
        }
    }
}