# Changelog

## [Unreleased]

- Allow the `--include` and `--exclude` parameters to be passed multiple times

## [v2.3.0]

- Split core functionality into a stand-alone library (`DotNetOutdated.Core`) which can be used in your own applications or libraries - Thank you [Gianluca Stoob](https://github.com/GStoob)

## [v2.2.0]

- Add ability to filter packages with the `--include` and `--exclude` option (See [#55](https://github.com/jerriep/dotnet-outdated/issues/55)) - Thank you [Jeppe Ravn Christiansen](https://github.com/jepperc)
- Add ability to save results to a JSON or CSV file (See [#29](https://github.com/jerriep/dotnet-outdated/issues/29) and [#57](https://github.com/jerriep/dotnet-outdated/issues/57)) - Thank you [Patrick Dwyer](https://github.com/patros)
- Fix some scenarios where current or latest versions for certain packages could not be resolved 

## [v2.1.0]

- Excludes unsupported projects (See [#58](https://github.com/jerriep/dotnet-outdated/issues/58)) - Thank you [Thomas Levesque](https://github.com/thomaslevesque)
- Add option to return non-zero exit code when updates are found (See [#94](https://github.com/jerriep/dotnet-outdated/pull/94)) - Thank you [Patrick Dwyer](https://github.com/patros)
- Fixed `NullReferenceException` when unable to determine either the current or latest version of a package (See [#96](https://github.com/jerriep/dotnet-outdated/issues/96))

## [v2.0.0]

- Now only displays outdated packages (See [#16](https://github.com/jerriep/dotnet-outdated/issues/16))
- Supports upgrading package using `-u` option. To prompt for each package, your can use `-p:prompt` (See [#6](https://github.com/jerriep/dotnet-outdated/issues/6))
- Transitive packages are not displayed in a hierarchical view anymore. Transitive packages are simply indicated with a `[T]` indicator behind the package name.
- Performance improvements due to caching (See [#43](https://github.com/jerriep/dotnet-outdated/pull/43)) - Thank you [thoemmi](https://github.com/thoemmi)
- Support for V2 feeds (See [#42](https://github.com/jerriep/dotnet-outdated/issues/42)) - Thank you [thoemmi](https://github.com/thoemmi)
- Highlights the new latest version of a package according to the severity of the upgrade (See [#45](https://github.com/jerriep/dotnet-outdated/issues/45)) - Thank you [tlycken](https://github.com/tlycken)
- The `-pr` (pre-release) option has been renamed to `-pre`
- Supports redirection and piping of output (See [#28](https://github.com/jerriep/dotnet-outdated/issues/28) and [#40](https://github.com/jerriep/dotnet-outdated/issues/40))
- Supports running tool from Package Manager Console and Git Bash (See [#39](https://github.com/jerriep/dotnet-outdated/issues/39))

## [v1.3.0]

This is mostly a bug fix release.

- Fixed some typos - Thank you [Scott Hanselman](https://github.com/shanselman)
- Fixed issue where colors were not displayed correctly on all terminals ([#32](https://github.com/jerriep/dotnet-outdated/issues/32)) - Thank you [Scott Hanselman](https://github.com/shanselman)
- Fixed issue where project was reported an not being a .NET Core project when user's temp path contained a space character. ([#23](https://github.com/jerriep/dotnet-outdated/issues/23))
- Fixed issue where current version package was not picked up due to case-sensitive string comparison ([#36](https://github.com/jerriep/dotnet-outdated/issues/36))
- Fixed issue where latest version of non-library packages was not picked up ([#27](https://github.com/jerriep/dotnet-outdated/issues/27)) and ([#31](https://github.com/jerriep/dotnet-outdated/issues/31))

## [v1.2.0]

- Works with secure feeds. Read more in the _Working with secure feeds_ section of the readme.
- Excludes auto-references (i.e. the framework packages) by default. Read more in the _Auto-references_ section of the readme.
- Fixed various unhandled exceptions.

## [v1.1.0]

- Changed the way in which project dependencies are detected. We now run the `dotnet restore` command and make use of the `project.assets.json` file to determine the dependencies. This ensures parity with what the .NET CLI is doing.
- Support for F# projects ([#17](https://github.com/jerriep/dotnet-outdated/issues/17)) - Thank you [John Ruble](https://github.com/jrr)
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

[v2.3.0]: https://github.com/jerriep/dotnet-outdated/tree/v2.3.0
[v2.2.0]: https://github.com/jerriep/dotnet-outdated/tree/v2.2.0
[v2.1.0]: https://github.com/jerriep/dotnet-outdated/tree/v2.1.0
[v2.0.0]: https://github.com/jerriep/dotnet-outdated/tree/v2.0.0
[v1.3.0]: https://github.com/jerriep/dotnet-outdated/tree/v1.3.0
[v1.2.0]: https://github.com/jerriep/dotnet-outdated/tree/v1.2.0
[v1.1.0]: https://github.com/jerriep/dotnet-outdated/tree/v1.1.0
[v1.0.0]: https://github.com/jerriep/dotnet-outdated/tree/v1.0.0
[v0.3.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.3.0
[v0.2.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.2.0
[v0.1.0]: https://github.com/jerriep/dotnet-outdated/tree/v0.1.0