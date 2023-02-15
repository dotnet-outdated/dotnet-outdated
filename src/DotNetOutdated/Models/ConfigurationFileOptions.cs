using System.Collections.Generic;
using DotNetOutdated.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DotNetOutdated.Models;

public class ConfigurationFileOptions
{
    public bool? IncludeAutoReferences { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public PrereleaseReporting? Prerelease { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public VersionLock? VersionLock { get; set; }
    
    public bool? Transitive { get; set; }
    
    public int? TransitiveDepth { get; set; }
    
    [JsonConverter(typeof(StringEnumConverter))]
    public UpgradeType? Upgrade { get; set; }
    
    public bool? FailOnUpdates { get; set; }
    
    [JsonProperty("include")]
    public List<string> FilterInclude = new();
    
    [JsonProperty("exclude")]
    public List<string> FilterExclude = new();
    
    public string OutputFilename { get; set; }
    
    public OutputFormat? OutputFileFormat { get; set; }
    
    public int? OlderThanDays { get; set; }
    
    public bool? NoRestore { get; set; }
    
    public bool? Recursive { get; set; }
    
    public bool? IgnoreFailedSources { get; set; }
}