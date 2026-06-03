using System.Text.RegularExpressions;

namespace DotNetOutdated.Core.Services;

public sealed partial class VariableTrackingService
{
    [GeneratedRegex(@"^[ \t]*#:[ \t]*package[ \t]+(.+?)\s*$")]
    private static partial Regex PackageDirectiveLineRegex();

    [GeneratedRegex(@"^[ \t]*#:[ \t]*sdk[ \t]+(.+?)\s*$")]
    private static partial Regex SdkDirectiveLineRegex();

    [GeneratedRegex(@"^[ \t]*#:[ \t]*property[ \t]+([^=\s]+)[ \t]*=[ \t]*(.*?)\s*$")]
    private static partial Regex PropertyDirectiveLineRegex();

    [GeneratedRegex(@"\$\(([^)]+)\)")]
    private static partial Regex MsBuildPropertyReferenceRegex();

    [GeneratedRegex(@"(^[ \t]*#:[ \t]*package[ \t]+)([^\r\n]*)(\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex PackageDirectiveReplaceRegex();

    [GeneratedRegex(@"(^[ \t]*#:[ \t]*sdk[ \t]+)([^\r\n]*)(\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex SdkDirectiveReplaceRegex();
}
