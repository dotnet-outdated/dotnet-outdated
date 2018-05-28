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
  Path                               The path to a .sln or .csproj file, or to a directory containing a .NET Core solution/project. If none is specified, the current directory will be used.

Options:
  --version                          Show version information
  -?|-h|--help                       Show help information
  -pr|--pre-release <PRERELEASE>     Specifies whether to look for pre-release versions of packages. Possible values: Auto (default), Always or Never.
  -vl|--version-lock <VERSION_LOCK>  Specifies whether the package should be locked to the current Major or Minor version. Possible values: None (default), Major or Minor.                                                                                               ```
```

![](screenshot.png)

## Specifying the path

You can run **dotnet-outdated** without specifying the `Path` argument. In this case, it will look in the current directory for a solution (`.sln`) and if one is found it will analyze that solution. If no solution is found it will look for a project (`.csproj`) and if one is found it will analyze that project. If more than one solution or project is found in the current folder, **dotnet-outdated** will report an error.

You can also pass a directory in the `Path` argument, in which case the same logic described above will be used, but in the directory specified.

Lastly, you can specify the path to a solution (`.sln`) or project (`.csproj`) which **dotnet-outdated** must analyze.

## Handling pre-release versions

**dotnet-outdated** allows you to specify whether to use pre-release versions of packages or not by passing the `-pr|--pre-release` option.

The default value of `Auto` will determine whether to use pre-release versions of a package based on whether the referenced version itself is a pre-release version. If the referenced version is a pre-release version, **dotnet-outdated** will include newer pre-release versions of the package. If the referenced version is not a pre-release version, **dotnet-outdated** will ignore pre-release versions.

You can also tell **dotnet-outdated** to always include pre-release versions by passing the `Always` value for the option. Conversely, you can tell it to never include pre-release versions by passing the `Never` value for the option.

## Locking to the current major or minor release

**dotnet-outdated** allows you to lock the version to the current major or minor version by passing the `-vl|--version-lock` option.

The default value of `None` will return the absolute latest package, regardless of whether it is a major or minor version upgrade.

Passing a value of `Major` will only report on later packages in the current major version range. For example, if the current version for a package is `4.1.0`, **dotnet-outdated** will only report on later packages in the `4.x` version range.

Passing a value of `Minor` will only report on later packages in the current minor version range. For example, if the current version for a package is `4.1.0`, **dotnet-outdated** will only report on later packages in the `4.1.x` version range.