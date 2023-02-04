using CsvHelper;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace DotNetOutdated.Services
{
    internal interface IReportingService
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

    
}
