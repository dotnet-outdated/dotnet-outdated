# Changelog

## [v1.2.0]

- Works with secure feeds. Read more in the _Working with secure feeds_ section of the readme.
- Excludes auto-references (i.e. the framework packages) by default. Read more in the _Auto-references_ section of the readme.
- Fixed various unhandled exceptions.

## [v1.1.0]

- Changed the way in which project dependencies are detected. We now run the `dotnet restore` command and make use of the `project.assets.json` file to determine the dependencies. This ensures parity with what the .NET CLI is doing.
- Support for F# projects ([#7](https://github.com/jerriep/dotnet-outdated/issues/17)) - Thank you [John Ruble](https://github.com/jrr)
- Support reporing on transitive dependencies ([#13](https://github.com/jerriep/dotnet-outdated/issues/13)) - Thank you [James McCutcheon](https://github.com/jamesmcc)
- Fixed issue which displayed packages that were unavailable for the TargetFramework ([#20](https://github.com/jerriep/dotnet-outdated/issues/20))
- Fixed issue with paths that contain spaces ([#23](https://github.com/jerriep/dotnet-outdated/issues/23))
- Fixed issue which caused unlisted NuGet packages to be shown ([#15](https://github.com/jerriep/dotnet-outdated/issues/15))

## [v1.0.0]

- Updated for RTM of .NET Core 2.1

## [v0.3.0]

- Updated to use MSBuild to generate the dependency graph for the project ([#2](https://github.com/jerriep/dotnet-outdated/issues/2))
- Scans all NuGet feeds configured for the project ([#7](https://github.com/jerriep/dotnet-outdated/issues/7))
- Better reporting when running against incompatible project types ([#11](https://github.com/jerriep/dotnet-outdated/issues/11))
- Allow you to lock to the current major or minor version ([#5](https://github.com/jerriep/dotnet-outdated/issues/5))

## [v0.2.0]

- Display column headers ([#1](https://github.com/jerriep/dotnet-outdated/issues/1))
- Allow specifying whether to include pre-release versions ([#4](https://github.com/jerriep/dotnet-outdated/issues/4))
- Fix bug when latest version cannot be found ([#10](https://github.com/jerriep/dotnet-outdated/issues/10))

## [v0.1.0]

Initial release

- A .NET Core global tool to display outdated NuGet packages in a project

[v1.2.0]: https://github.com/jerriep/dotnet-outdated/tree/v1.2.0
[v1.1.0]: https://github.com/jerriep/dotnet-outdated/tree/v1.1.0
[v1.0.0]: https://github.com/jerriep/dotnet-outdated/tree/v1.0.0
[v0.3.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.3.0
[v0.2.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.2.0
[v0.1.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.1.0