#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property FakeJsonPackageId=DotNetOutdated.Fake.Json
#:property FakeJsonPackageVersion=11.0.1
#:package $(FakeJsonPackageId)@$(FakeJsonPackageVersion)

Console.WriteLine("Hello from a file-based app");
