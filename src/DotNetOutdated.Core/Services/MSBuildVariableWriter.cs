using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Xml.Linq;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// Writes a new version to the MSBuild property behind a package version (<c>Version="$(V)"</c> or
/// <c>Version="[$(V), 2.0.0)"</c>) and makes the reference read that property again, in case <c>dotnet add package</c>
/// replaced it with a literal. Edits are made to the text so the rest of each file keeps its formatting, encoding
/// and byte order mark.
/// </summary>
internal sealed class MSBuildVariableWriter(IFileSystem fileSystem, Action<string> onWarning = null)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly Action<string> _onWarning = onWarning;

    /// <returns><see langword="true"/> when any file was changed.</returns>
    public bool TryUpdate(PackageVariableInfo variableInfo, VersionRange newRange)
    {
        ArgumentNullException.ThrowIfNull(newRange);
        if (!newRange.HasLowerBound)
        {
            throw new ArgumentException($"Version range '{newRange}' must have a lower bound.", nameof(newRange));
        }

        try
        {
            return Update(variableInfo, newRange);
        }
        catch (Exception ex)
        {
            _onWarning?.Invoke(
                $"Failed to update package reference '{variableInfo.PackageName}' ({variableInfo.ElementType}): {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether the variable already holds a value that makes the reference evaluate to <paramref name="versionRange"/>.</summary>
    public static bool IsAtVersionRange(PackageVariableInfo variableInfo, VersionRange versionRange)
    {
        var expression = string.IsNullOrWhiteSpace(variableInfo.PackageReferenceVersion)
            ? $"$({variableInfo.VariableName})"
            : variableInfo.PackageReferenceVersion;

        return VersionExpression.TryEvaluate(expression, variableInfo.VariableValue, out var current) &&
               current.Equals(versionRange);
    }

    private bool Update(PackageVariableInfo variableInfo, VersionRange newRange)
    {
        if (variableInfo.UnsupportedReason != null)
        {
            _onWarning?.Invoke(
                $"Not preserving the version variable of '{variableInfo.PackageName}': {variableInfo.UnsupportedReason}.");
            return false;
        }

        // $(V) holds the whole requested range; in [$(V), upper) it holds only the lower bound.
        var isRangeExpression = !string.IsNullOrWhiteSpace(variableInfo.PackageReferenceVersion) &&
                                VersionExpression.TryParse(variableInfo.PackageReferenceVersion, out _, out var isRange) &&
                                isRange;
        var propertyValueToWrite = isRangeExpression ? newRange.MinVersion.ToString() : newRange.ToVersionString();

        // Step 1: Update the property value in the file where it's defined (or insert it if implicit/SDK-defined)
        var propertyWrite = TryWriteProperty(variableInfo, propertyValueToWrite);
        if (propertyWrite == PropertyWrite.Failed)
        {
            // Restoring the reference now would make the package resolve to the property's old value.
            return false;
        }

        var updated = propertyWrite == PropertyWrite.Written;

        // Step 2: Restore the variable reference in the PackageReference file (might be different from property file)
        string packageRefFilePath = variableInfo.PackageReferenceFilePath;
        if (_fileSystem.File.Exists(packageRefFilePath))
        {
            var packageRefFile = EncodedText.Read(_fileSystem, packageRefFilePath);
            string packageRefContent = packageRefFile.Content;
            _ = XDocument.Parse(packageRefContent);

            var versionExpression = string.IsNullOrEmpty(variableInfo.PackageReferenceVersion)
                ? $"$({variableInfo.VariableName})"
                : isRangeExpression
                    ? VersionExpression.Compute(variableInfo.PackageReferenceVersion, ReferencedVariableName(variableInfo), newRange)
                    : variableInfo.PackageReferenceVersion;

            var newPackageRefContent = PackageVersionMarkup.ReplacePackageVersion(
                packageRefContent,
                [variableInfo.ElementType],
                variableInfo.PackageName,
                versionExpression,
                variableInfo.PackageReferenceVersion,
                newRange);

            if (!string.Equals(packageRefContent, newPackageRefContent, StringComparison.Ordinal))
            {
                packageRefFile.Write(_fileSystem, packageRefFilePath, newPackageRefContent);
                updated = true;
            }
        }

        return updated;
    }

    private static string ReferencedVariableName(PackageVariableInfo variableInfo) =>
        VersionExpression.TryParse(variableInfo.PackageReferenceVersion, out var name, out _) ? name : variableInfo.VariableName;

    private enum PropertyWrite { Written, AlreadyCurrent, Failed }

    private PropertyWrite TryWriteProperty(PackageVariableInfo variableInfo, string newValue)
    {
        if (string.IsNullOrEmpty(variableInfo.FilePath) ||
            string.IsNullOrEmpty(variableInfo.VariableName) ||
            !_fileSystem.File.Exists(variableInfo.FilePath))
        {
            return PropertyWrite.Failed;
        }

        var propertyFile = EncodedText.Read(_fileSystem, variableInfo.FilePath);
        var content = propertyFile.Content;
        var doc = XDocument.Parse(content);
        string newContent;

        if (variableInfo.InsertBeforeProperty is { } anchor)
        {
            newContent = PackageVersionMarkup.InsertPropertyBefore(content, anchor.Name, anchor.Index, variableInfo.VariableName, newValue);
        }
        else if (variableInfo.InsertProperty || variableInfo.PropertyElementIndex < 0)
        {
            var elements = PropertyElements(doc, variableInfo.VariableName);
            var topLevel = elements.FindLastIndex(e => IsUnconditionalTopLevel(e.Parent) && e.Attribute("Condition") == null);
            newContent = topLevel >= 0
                // Already defined (for example by an earlier upgrade that inserted it): update that definition.
                ? PackageVersionMarkup.ReplacePropertyValueAt(content, variableInfo.VariableName, topLevel, newValue)
                : !variableInfo.InsertProperty && elements.Count > 0
                    // Callers that don't say which definition: the last one.
                    ? PackageVersionMarkup.ReplacePropertyValueAt(content, variableInfo.VariableName, elements.Count - 1, newValue)
                    : PackageVersionMarkup.InsertProperty(content, InsertionGroupIndex(doc), variableInfo.VariableName, newValue);
        }
        else
        {
            var elements = PropertyElements(doc, variableInfo.VariableName);
            var index = variableInfo.PropertyElementIndex;
            if (index < elements.Count && string.Equals(elements[index].Value.Trim(), newValue, StringComparison.Ordinal))
            {
                return PropertyWrite.AlreadyCurrent;
            }

            if (index >= elements.Count ||
                (!string.IsNullOrEmpty(variableInfo.VariableValue) &&
                 !string.Equals(elements[index].Value.Trim(), variableInfo.VariableValue, StringComparison.Ordinal)))
            {
                _onWarning?.Invoke(
                    $"Not updating $({variableInfo.VariableName}) in '{variableInfo.FilePath}': the file changed since it was scanned.");
                return PropertyWrite.Failed;
            }

            newContent = PackageVersionMarkup.ReplacePropertyValueAt(content, variableInfo.VariableName, index, newValue);
        }

        if (string.Equals(content, newContent, StringComparison.Ordinal))
        {
            return PropertyWrite.AlreadyCurrent;
        }

        if (!IsExpectedPropertyEdit(doc, newContent, variableInfo.VariableName, newValue))
        {
            _onWarning?.Invoke(
                $"Not updating $({variableInfo.VariableName}) in '{variableInfo.FilePath}': the edit did not change exactly one definition as expected.");
            return PropertyWrite.Failed;
        }

        propertyFile.Write(_fileSystem, variableInfo.FilePath, newContent);
        return PropertyWrite.Written;
    }

    // Guards the text-level edit against regex/XML disagreements: the edited document must still parse, and
    // exactly one definition of the property must differ (holding newValue) or have been added; nothing else
    // about the property's definitions may change.
    private static bool IsExpectedPropertyEdit(XDocument before, string newContent, string propertyName, string newValue)
    {
        XDocument after;
        try
        {
            after = XDocument.Parse(newContent);
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }

        var oldValues = PropertyElements(before, propertyName).Select(e => e.Value.Trim()).ToList();
        var newValues = PropertyElements(after, propertyName).Select(e => e.Value.Trim()).ToList();

        if (newValues.Count == oldValues.Count)
        {
            var changed = Enumerable.Range(0, oldValues.Count).Where(i => oldValues[i] != newValues[i]).ToList();
            return changed.Count == 1 && newValues[changed[0]] == newValue;
        }

        if (newValues.Count != oldValues.Count + 1)
        {
            return false;
        }

        // Inserted: removing one occurrence of newValue must give back the original sequence.
        for (var i = 0; i < newValues.Count; i++)
        {
            if (newValues[i] == newValue && newValues.Where((_, j) => j != i).SequenceEqual(oldValues))
            {
                return true;
            }
        }

        return false;
    }

    private static List<XElement> PropertyElements(XDocument doc, string propertyName) =>
        doc.Descendants()
            .Where(e => MSBuildEvaluator.IsPropertyElement(e) && e.Name.LocalName.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static bool IsUnconditionalTopLevel(XElement propertyGroup) =>
        propertyGroup.Parent != null &&
        propertyGroup.Parent == propertyGroup.Document?.Root &&
        propertyGroup.Attribute("Condition") == null;

    // The first unconditional PropertyGroup directly under <Project> (not in a target, Choose or conditional
    // group), as an index among the non-empty PropertyGroups that PackageVersionMarkup counts; -1 for a new group.
    private static int InsertionGroupIndex(XDocument doc) =>
        doc.Descendants()
            .Where(e => e.Name.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase) && !e.IsEmpty)
            .ToList()
            .FindIndex(IsUnconditionalTopLevel);
}
