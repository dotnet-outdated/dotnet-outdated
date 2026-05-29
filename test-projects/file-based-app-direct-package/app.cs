#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:package Newtonsoft.Json@11.0.1

using Newtonsoft.Json;

Console.WriteLine(JsonConvert.SerializeObject(new { Message = "Hello from a direct package directive" }));
