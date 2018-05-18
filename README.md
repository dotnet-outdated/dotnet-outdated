# dotnet-outdated

[![AppVeyor build status][appveyor-badge]](https://ci.appveyor.com/project/jerriep/dotnet-outdated/branch/master)

[appveyor-badge]: https://img.shields.io/appveyor/ci/jerriep/dotnet-outdated/master.svg?label=appveyor&style=flat-square

[![NuGet][main-nuget-badge]][main-nuget] [![MyGet][main-myget-badge]][main-myget]

[main-nuget]: https://www.nuget.org/packages/dotnet-outdated/
[main-nuget-badge]: https://img.shields.io/nuget/v/dotnet-outdated.svg?style=flat-square&label=nuget
[main-myget]: https://www.myget.org/feed/jerriep/package/nuget/dotnet-outdated
[main-myget-badge]: https://img.shields.io/www.myget/jerriep/vpre/dotnet-outdated.svg?style=flat-square&label=myget

A .NET Core global tool to display outdated NuGet packages in a project

## Installation

The latest release of dotnet-outdated requires the [.NET Core SDK 2.1.300-rc1](https://www.microsoft.com/net/download/dotnet-core/sdk-2.1.300-rc1) or newer.

Once installed, run this command:

```bash
dotnet tool install --global dotnet-outdated
```

## Usage

```text
Usage: dotnet outdated [arguments] [options]

Arguments:
  Path                            The path to a .sln or .csproj file, or to a directory containing a .NET Core solution/project. If none is specified, the current directory will be used.

Options:
  --version                       Show version information
  -?|-h|--help                    Show help information
  -pr|--pre-release <PRERELEASE>  Specifies whether to look for pre-release versions of packages. Possible Values: Auto (default), Always or Never.
```

![](screenshot.png)

### Handling pre-release versions

**dotnet-outdated** allows you to specify whether to use pre-release versions of packages or not, but passing the `-pr|--pre-release` option.

The default value of `Auto` will determine whether to use pre-release versions of a package based on whether the referenced version itself is a pre-release version. If the referenced version is a pre-release version, **dotnet-outdated** will include newer pre-release versions of the package. If the referenced version is not a pre-release version, **dotnet-outdated** will ignore pre-release versions.

You can also tell **dotnet-outdated** to always include pre-release versions by passing the `Always` value for the option. Conversely, you can tell it to never include pre-release versions by passing the `Never` value for the option.

## Examples

### 1. Display outdated packages for all solutions or projects in the current directory

```text
dotnet outdated
```

This will search the current directory for any solution files (`*.sln`) and display the outdated packages for the projects in those solutions. If no solution files are found, it will scan the directory for individual project files (`*.csproj`) and display the outdated packages for those projects.

### 2. Display outdated packages for all solutions or projects in a specific directory

```text
dotnet outdated C:\Development\jerriep\github-issues-cli
```

This will search the directory `C:\Development\jerriep\github-issues-cli` for any solution files (`*.sln`) and display the outdated packages for the projects in those solutions. If no solution files are found, it will scan the directory for individual project files (`*.csproj`) and display the outdated packages for those projects.

### 3. Display outdated packages for a specific solution

```text
dotnet outdated C:\Development\jerriep\github-issues-cli\GitHubIssues.sln
```

This will display the outdated packages for all the projects in the solution `C:\Development\jerriep\github-issues-cli\GitHubIssues.sln`.

### 4. Display outdated packages for a specific project

```text
dotnet outdated C:\Development\jerriep\github-issues-cli\src\GitHubIssuesCli\GitHubIssuesCli.csproj
```

This will display the outdated packages for the project `C:\Development\jerriep\github-issues-cli\src\GitHubIssuesCli\GitHubIssuesCli.csproj`.
