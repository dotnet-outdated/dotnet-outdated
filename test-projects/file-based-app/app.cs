#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property NewtonsoftJsonPackageId=Newtonsoft.Json
#:property NewtonsoftJsonPackageVersion=11.0.1
#:package $(NewtonsoftJsonPackageId)@$(NewtonsoftJsonPackageVersion)

using Newtonsoft.Json;

Console.WriteLine(JsonConvert.SerializeObject(new { Message = "Hello from a file-based app" }));
