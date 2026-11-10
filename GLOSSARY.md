# dotnet-outdated

Reports the packages and SDKs a .NET project references that have newer versions, and upgrades them in place.

## Language

**Reference**:
A package or SDK that a project declares directly, identified by its id together with its reference kind.
_Avoid_: Dependency (when meaning only direct declarations)

**Reference kind**:
Whether a reference is a package (`PackageReference`, `GlobalPackageReference`, `#:package`) or an SDK (`Sdk="Id/Version"`, `<Sdk>`, `#:sdk`). A project can reference a package and an SDK with the same id; they are different references.

**Dependency**:
A package a project resolves for one target framework: a reference, or a transitive package that a reference brings in.

**Version location**:
Where a reference's version is written: a literal in the project or `Directory.Packages.props`, an MSBuild property, an SDK declaration, a file-based app directive or `#:property`, or nowhere editable.
_Avoid_: Variable info, element type

**Version variable**:
An MSBuild property (or `#:property`) whose value is a reference's version, read by the reference as `$(Name)` or as the lower bound of a range.
_Avoid_: Package variable

**File-based app**:
A single `.cs` file run with `dotnet run`, whose references are declared with `#:package`, `#:sdk` and `#:property` directives.

**Upgrade**:
Changing a reference's version at its version location to a newer version, keeping version variables and the file's formatting.
