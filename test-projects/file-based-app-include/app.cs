#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:include lib/json.cs

Console.WriteLine($"Hello from a file-based app that includes {Json.Name}");
