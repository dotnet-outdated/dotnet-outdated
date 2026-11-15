namespace DotNetOutdated.Core.Services;

/// <summary>
/// A package version written through an MSBuild property (<c>Version="$(V)"</c>, <c>#:package Id@$(V)</c>):
/// where the property is defined and where the reference that reads it is.
/// </summary>
internal sealed class PackageVariableInfo
{
    public const string FileBasedPackageDirectiveElementType = "FileBasedPackageDirective";

    public const string FileBasedSdkDirectiveElementType = "FileBasedSdkDirective";

    public required string PackageName { get; set; }
    public string VariableName { get; set; } = string.Empty;
    public required string VariableValue { get; set; }
    public string FilePath { get; set; } = string.Empty;  // Where the property is DEFINED
    public required string PackageReferenceFilePath { get; set; }  // Where the PackageReference/PackageVersion is USED
    public required string ElementType { get; set; }
    public string? PackageReferenceName { get; set; }
    public string PackageReferenceVersion { get; set; } = string.Empty;
    public bool IsVersionOverride { get; set; }

    /// <summary>
    /// Why the variable can't be updated safely (for example an unsupported version expression, or a property
    /// assigned under a condition that can't be evaluated statically); <see langword="null"/> when it can.
    /// </summary>
    public string? UnsupportedReason { get; set; }

    /// <summary>Which same-named property element in <see cref="FilePath"/> is in effect; -1 when unknown.</summary>
    public int PropertyElementIndex { get; set; } = -1;

    /// <summary>The variable isn't defined in any evaluated file and must be added to <see cref="FilePath"/>.</summary>
    public bool InsertProperty { get; set; }

    /// <summary>The variable is SDK-defined and read by this alias property: add an override just before it.</summary>
    public (string Name, int Index)? InsertBeforeProperty { get; set; }

    public bool IsFileBasedDirective =>
        ElementType is FileBasedPackageDirectiveElementType or FileBasedSdkDirectiveElementType;

    /// <summary>The kind of reference that reads the variable.</summary>
    public ReferenceKind ReferenceKind =>
        ElementType == FileBasedSdkDirectiveElementType ? ReferenceKind.Sdk : ReferenceKind.Package;
}
