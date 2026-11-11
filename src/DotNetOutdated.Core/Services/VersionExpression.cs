using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace DotNetOutdated.Core.Services;

/// <summary>
/// MSBuild version expressions as written in a version attribute or property: <c>$(V)</c>, <c>[$(V), 2.0.0)</c>,
/// or anything else, which can't be updated through its variable.
/// </summary>
internal static partial class VersionExpression
{
    /// <summary>A single <c>$(Name)</c> property reference.</summary>
    [GeneratedRegex(@"\$\(\s*([A-Za-z_][A-Za-z0-9_.-]*)\s*\)")]
    public static partial Regex PropertyReferenceRegex();

    /// <summary>Whether <paramref name="expression"/> contains an MSBuild property reference such as <c>$(Name)</c>.</summary>
    public static bool ContainsPropertyReference(string expression) =>
        !string.IsNullOrEmpty(expression) && expression.Contains("$(");

    /// <summary>Whether <paramref name="expression"/> is exactly one property reference, and if so its name.</summary>
    public static bool TryGetSinglePropertyReference(string expression, out string propertyName)
    {
        propertyName = string.Empty;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var trimmed = expression.Trim();
        var match = PropertyReferenceRegex().Match(trimmed);
        if (!match.Success || match.Index != 0 || match.Length != trimmed.Length)
        {
            return false;
        }

        propertyName = match.Groups[1].Value;
        return true;
    }

    // Matches "[ $(Name) , upper ]" style ranges whose lower bound is exactly one property reference.
    private static readonly Regex LowerBoundRangeRegex = new(
        @"^\s*(?<open>[\[(])\s*\$\(\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*\)\s*,(?<upper>[^,$\[\]()]*)(?<close>[\])])\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses the version expressions whose variable can be updated without changing what the expression means:
    /// a single property reference (<c>$(V)</c>) or a range whose lower bound is a property (<c>[$(V), 2.0.0)</c>).
    /// </summary>
    public static bool TryParse(string expression, out string variableName, out bool isRange)
    {
        isRange = false;
        if (TryGetSinglePropertyReference(expression, out variableName))
        {
            return true;
        }

        var match = LowerBoundRangeRegex.Match(expression ?? string.Empty);
        if (!match.Success)
        {
            variableName = string.Empty;
            return false;
        }

        variableName = match.Groups["name"].Value;
        isRange = true;
        return true;
    }

    /// <summary>
    /// Evaluates a supported version expression for a given value of its variable.
    /// </summary>
    public static bool TryEvaluate(string expression, string? variableValue, [NotNullWhen(true)] out VersionRange? range)
    {
        range = null;
        if (!TryParse(expression, out var variableName, out var isRange))
        {
            return false;
        }

        if (!isRange)
        {
            return VersionRange.TryParse(variableValue ?? string.Empty, out range);
        }

        var substituted = PropertyReferenceRegex().Replace(expression, variableValue ?? string.Empty);
        return VersionRange.TryParse(substituted, out range);
    }

    /// <summary>
    /// The expression to write back for <paramref name="newRange"/>: the original when it already evaluates to
    /// the requested range with the variable at its new value, otherwise the requested range with the variable
    /// as its lower bound (so a dropped or changed upper bound is never resurrected).
    /// </summary>
    public static string Compute(string originalExpression, string variableName, VersionRange newRange)
    {
        if (!TryParse(originalExpression, out _, out var isRange) || !isRange)
        {
            return originalExpression;
        }

        if (TryEvaluate(originalExpression, newRange.MinVersion!.ToString(), out var evaluated) &&
            evaluated.Equals(newRange))
        {
            return originalExpression;
        }

        var reference = $"$({variableName})";
        if (newRange.IsPlainVersion())
        {
            return reference;
        }

        var upper = newRange.HasUpperBound ? newRange.MaxVersion.ToNormalizedString() : string.Empty;
        return $"{(newRange.IsMinInclusive ? "[" : "(")}{reference}, {upper}{(newRange.IsMaxInclusive ? "]" : ")")}";
    }
}
