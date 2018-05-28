# Changelog

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

[v0.3.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.3.0
[v0.2.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.2.0
[v0.1.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.1.0