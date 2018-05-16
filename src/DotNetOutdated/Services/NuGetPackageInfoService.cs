using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NuGet.Versioning;

namespace DotNetOutdated.Services
{
    public class NuGetPackageInfoService : INuGetPackageInfoService
    {
        private class NuGetVersionConverter : JsonConverter<NuGetVersion>
        {
            public override void WriteJson(JsonWriter writer, NuGetVersion value, JsonSerializer serializer)
            {
                writer.WriteValue(value.ToString());
            }

            public override NuGetVersion ReadJson(JsonReader reader, Type objectType, NuGetVersion existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                string s = (string)reader.Value;

                return NuGetVersion.Parse(s);
            }
        }
        private class NuGetVersions
        {
            [JsonProperty("versions", NullValueHandling = NullValueHandling.Ignore)]
            public NuGetVersion[] Versions { get; set; }
        }

        // TODO: This is horrible. We are hardcoding working against NuGet.org, but until such time that the NuGet
        // client libraries support .NET Standard, this is the way we'll do it.
        private const string PackageBaseAddress = "https://api.nuget.org/v3-flatcontainer/{0}/index.json";
        
        public async Task<NuGetVersion> GetLatestVersion(string package, bool includePrerelease)
        {
            string url = string.Format(PackageBaseAddress, package.ToLowerInvariant());

            HttpClient httpClient = new HttpClient();
            var response = await httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<NuGetVersions>(content, new NuGetVersionConverter());

                return result.Versions.OrderByDescending(version => version).FirstOrDefault();
            }

            return null;
        }
    }
    
}