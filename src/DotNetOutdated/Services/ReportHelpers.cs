using System;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace DotNetOutdated.Services
{
    class ToStringJsonConverter : JsonConverter
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

        internal static string GetTextReportLine(Project project, Project.TargetFramework targetFramework, Project.Dependency dependency)
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

        public static string GetTextReportContent(List<Project> projects)
        {
            var sb = new StringBuilder();
            foreach (var project in projects)
            {
                foreach (var targetFramework in project.TargetFrameworks)
                {
                    foreach (var dependency in targetFramework.Dependencies)
                    {
                        sb.AppendLine(Report.GetTextReportLine(project, targetFramework, dependency));
                    }
                }
            }
            return sb.ToString();
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