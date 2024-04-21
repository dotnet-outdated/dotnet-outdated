using Newtonsoft.Json.Linq;
using NuGet.ProjectModel;

namespace DotNetOutdated
{
    public class ExtendedDependencyGraphSpec : DependencyGraphSpec
    {
        public ExtendedDependencyGraphSpec(string json)
            : base()
        {
            // Parse the JSON and initialize the object.
            var jObject = JObject.Parse(json);
            foreach (var project in jObject["projects"].Children<JProperty>())
            {
                var packageSpec = JsonPackageSpecReader.GetPackageSpec(project.Value.ToString(), project.Name, project.Name);
                this.AddProject(packageSpec);
                this.AddRestore(packageSpec.RestoreMetadata.ProjectUniqueName);
            }
        }
    }
}
