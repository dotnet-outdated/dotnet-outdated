using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using DotNetOutdated.Core.Models;

namespace DotNetOutdated.Services
{
    internal interface IReportingService
    {
        Task WriteReport(string filename, List<Project> projects);
    }

    public class JsonReportingService : IReportingService
    {
        public async Task WriteReport(string filename, List<Project> projects)
        {
            using (FileStream createStream = File.Create(filename))
            {
                await JsonSerializer.SerializeAsync(createStream, projects).ConfigureAwait(false);
            }
        }
    }

    public class ToStringJsonConverter : JsonConverter<object>
    {
        public override bool CanConvert(Type typeToConvert)
        {
            return true;
        }

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }

        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException("This converter cannot be used for reading JSON.");
        }
    }
}