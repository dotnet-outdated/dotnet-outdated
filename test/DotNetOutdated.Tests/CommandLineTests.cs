using System;
using System.CommandLine.Help;
using System.Linq;
using DotNetOutdated.Core;
using NuGet.Common;
using Xunit;
using VersionOption = System.CommandLine.VersionOption;

namespace DotNetOutdated.Tests;

/// <summary>
/// How <see cref="ProgramCommandLine"/> interprets command lines.
/// </summary>
/// <remarks>
/// The tool used McMaster.Extensions.CommandLineUtils before System.CommandLine. The expectations in
/// <see cref="CompatibleArguments"/> were recorded by running each command line through the McMaster definition, so
/// these tests check that existing scripts keep working. Where the behaviour changed on purpose, the test says so.
/// <para>
/// An expectation names only the values that differ from the defaults: "Run: Path=./src, Transitive=True".
/// "Error" means the command line is rejected; the messages are not compared.
/// </para>
/// </remarks>
public class CommandLineTests
{
    public static TheoryData<string[], string> CompatibleArguments() => new()
    {
        // No arguments, and the path alone or around options.
        { [], "Run" },
        { ["MySolution.sln"], "Run: Path=MySolution.sln" },
        { ["./src", "-t"], "Run: Path=./src, Transitive=True" },
        { ["-t", "./src"], "Run: Path=./src, Transitive=True" },
        { ["-t", "./src", "-r"], "Run: Path=./src, Transitive=True, Recursive=True" },
        { ["C:\\Projects\\My App\\My.csproj"], "Run: Path=C:\\Projects\\My App\\My.csproj" },

        // --include-auto-references, including the "-i" short name McMaster inferred.
        { ["--include-auto-references"], "Run: IncludeAutoReferences=True" },
        { ["-i"], "Run: IncludeAutoReferences=True" },
        { ["-i", "path"], "Run: Path=path, IncludeAutoReferences=True" },

        // --pre-release: every value form, case-insensitive values.
        { ["--pre-release", "Always"], "Run: Prerelease=Always" },
        { ["--pre-release", "always"], "Run: Prerelease=Always" },
        { ["--pre-release:never"], "Run: Prerelease=Never" },
        { ["--pre-release=NEVER"], "Run: Prerelease=Never" },
        { ["-pre", "Always"], "Run: Prerelease=Always" },
        { ["-pre:always"], "Run: Prerelease=Always" },
        { ["-pre=never"], "Run: Prerelease=Never" },
        { ["-pre", "auto", "path"], "Run: Path=path" },

        // --version-lock
        { ["--version-lock", "Major"], "Run: VersionLock=Major" },
        { ["-vl", "minor"], "Run: VersionLock=Minor" },
        { ["-vl:Major"], "Run: VersionLock=Major" },
        { ["-vl=none"], "Run" },

        // Flags never take the next token as a value.
        { ["--transitive"], "Run: Transitive=True" },
        { ["-t"], "Run: Transitive=True" },
        { ["-t", "true"], "Run: Path=true, Transitive=True" },
        { ["-f", "path"], "Run: Path=path, FailOnUpdates=True" },
        { ["--fail-on-updates"], "Run: FailOnUpdates=True" },
        { ["-n"], "Run: NoRestore=True" },
        { ["--no-restore"], "Run: NoRestore=True" },
        { ["-r"], "Run: Recursive=True" },
        { ["--recursive"], "Run: Recursive=True" },
        { ["-fba"], "Run: IncludeFileBasedApps=True" },
        { ["--include-file-based-apps"], "Run: IncludeFileBasedApps=True" },
        { ["-ifs"], "Run: IgnoreFailedSources=True" },
        { ["--ignore-failed-sources"], "Run: IgnoreFailedSources=True" },
        { ["-utd"], "Run: IncludeUpToDate=True" },
        { ["--include-up-to-date"], "Run: IncludeUpToDate=True" },
        { ["-t", "-r", "-f", "-n", "-i", "-fba", "-ifs", "-utd"], "Run: IncludeAutoReferences=True, Transitive=True, FailOnUpdates=True, NoRestore=True, Recursive=True, IncludeFileBasedApps=True, IgnoreFailedSources=True, IncludeUpToDate=True" },

        // Integers
        { ["--transitive-depth", "3"], "Run: TransitiveDepth=3" },
        { ["-td", "2"], "Run: TransitiveDepth=2" },
        { ["-td:5"], "Run: TransitiveDepth=5" },
        { ["-td=0"], "Run: TransitiveDepth=0" },
        { ["--older-than", "7"], "Run: OlderThanDays=7" },
        { ["-ot:30"], "Run: OlderThanDays=30" },
        { ["--idle-timeout", "300"], "Run: IdleTimeout=300" },
        { ["-it=60"], "Run: IdleTimeout=60" },

        // --upgrade has an optional value, given only with ":" or "=".
        { ["-u"], "Run: Upgrade=(True, Auto)" },
        { ["--upgrade"], "Run: Upgrade=(True, Auto)" },
        { ["-u:prompt"], "Run: Upgrade=(True, Prompt)" },
        { ["-u:Prompt"], "Run: Upgrade=(True, Prompt)" },
        { ["-u=auto"], "Run: Upgrade=(True, Auto)" },
        { ["--upgrade:prompt"], "Run: Upgrade=(True, Prompt)" },
        { ["--upgrade=Prompt"], "Run: Upgrade=(True, Prompt)" },
        { ["-u", "path"], "Run: Path=path, Upgrade=(True, Auto)" },
        { ["path", "-u"], "Run: Path=path, Upgrade=(True, Auto)" },
        { ["-u", "-t"], "Run: Transitive=True, Upgrade=(True, Auto)" },
        { ["-u", "--no-restore"], "Run: Upgrade=(True, Auto), NoRestore=True" },
        { ["-u:prompt", "path"], "Run: Path=path, Upgrade=(True, Prompt)" },

        // McMaster ignored a repeated bare -u, but rejected a second value.
        { ["-u", "-u"], "Run: Upgrade=(True, Auto)" },
        { ["--upgrade", "-u", "path"], "Run: Path=path, Upgrade=(True, Auto)" },
        { ["-u:prompt", "-u"], "Run: Upgrade=(True, Prompt)" },
        { ["-u", "--upgrade:prompt"], "Error" },
        { ["-u:prompt", "-u=auto"], "Error" },
        { ["-u", "prompt"], "Run: Path=prompt, Upgrade=(True, Auto)" },
        { ["-u", "Auto"], "Run: Path=Auto, Upgrade=(True, Auto)" },
        { ["-u:bogus"], "Error" },
        { ["-u="], "Error" },

        // Repeatable filters
        { ["--include", "Microsoft"], "Run: FilterInclude=Microsoft" },
        { ["-inc", "Microsoft", "-inc", "System"], "Run: FilterInclude=Microsoft|System" },
        { ["-inc:Microsoft", "--include=System", "-inc", "NuGet"], "Run: FilterInclude=Microsoft|System|NuGet" },
        { ["--exclude", "Test"], "Run: FilterExclude=Test" },
        { ["-exc", "Test", "--exclude:Mock"], "Run: FilterExclude=Test|Mock" },
        { ["-inc", "A", "-exc", "B", "path"], "Run: Path=path, FilterInclude=A, FilterExclude=B" },

        // Output
        { ["--output", "report.json"], "Run: OutputFilename=report.json" },
        { ["-o", "report.csv", "-of", "csv"], "Run: OutputFilename=report.csv, OutputFileFormat=Csv" },
        { ["-o:report.md", "-of:markdown"], "Run: OutputFilename=report.md, OutputFileFormat=Markdown" },
        { ["--output=out.json", "--output-format=Json"], "Run: OutputFilename=out.json" },
        { ["--output-format", "MARKDOWN"], "Run: OutputFileFormat=Markdown" },
        { ["-o", "C:\\reports\\out.json"], "Run: OutputFilename=C:\\reports\\out.json" },
        { ["-o", "./reports/out.json"], "Run: OutputFilename=./reports/out.json" },

        // Strings
        { ["--pre-release-label", "rc.1"], "Run: PrereleaseLabel=rc.1" },
        { ["-prl", "beta"], "Run: PrereleaseLabel=beta" },
        { ["-prl:preview.2"], "Run: PrereleaseLabel=preview.2" },
        { ["--runtime", "linux-x64"], "Run: Runtime=linux-x64" },
        { ["-rt:win-x64"], "Run: Runtime=win-x64" },
        { ["--maximum-version", "8.0"], "Run: MaxVersion=8.0" },
        { ["-mv", "13.0.3"], "Run: MaxVersion=13.0.3" },
        { ["-mv:8.0", "-u"], "Run: Upgrade=(True, Auto), MaxVersion=8.0" },

        // NuGet credential log level, with every value.
        { ["--nuget-cred-log-level", "debug"], "Run: NuGetCredLogLevel=Debug" },
        { ["-ncll", "Verbose"], "Run: NuGetCredLogLevel=Verbose" },
        { ["-ncll:information"], "Run: NuGetCredLogLevel=Information" },
        { ["-ncll=minimal"], "Run: NuGetCredLogLevel=Minimal" },
        { ["-ncll", "warning"], "Run" },
        { ["-ncll", "ERROR"], "Run: NuGetCredLogLevel=Error" },

        // A realistic CI invocation.
        { ["./MySolution.sln", "-u", "-vl:Major", "-pre:Never", "-inc", "Microsoft", "-exc", "Test", "-t", "-td", "2", "-f", "-o", "outdated.json", "-of", "json", "-ot", "14", "-n", "-r", "-ifs", "-utd", "-prl", "rc", "-ncll", "error", "-rt", "linux-x64", "-mv", "9.0", "-it", "60"], "Run: Path=./MySolution.sln, Prerelease=Never, VersionLock=Major, Transitive=True, TransitiveDepth=2, Upgrade=(True, Auto), FailOnUpdates=True, FilterInclude=Microsoft, FilterExclude=Test, OutputFilename=outdated.json, OlderThanDays=14, NoRestore=True, Recursive=True, IgnoreFailedSources=True, IncludeUpToDate=True, PrereleaseLabel=rc, NuGetCredLogLevel=Error, Runtime=linux-x64, MaxVersion=9.0, IdleTimeout=60" },

        // Help, version and the mcp subcommand.
        { ["--help"], "Help" },
        { ["-h"], "Help" },
        { ["-?"], "Help" },
        { ["path", "--help"], "Help" },
        { ["--version"], "Version" },
        { ["mcp"], "Mcp" },
        { ["mcp", "--help"], "Help: mcp" },

        // Errors
        { ["--bogus"], "Error" },
        { ["-x"], "Error" },
        { ["path", "--bogus"], "Error" },
        { ["a", "b"], "Error" },
        { ["-pre", "sometimes"], "Error" },
        { ["-vl:Patch"], "Error" },
        { ["-td", "x"], "Error" },
        { ["-td:1.5"], "Error" },
        { ["-ot", "-1x"], "Error" },
        { ["-of", "xml"], "Error" },
        { ["-ncll", "loud"], "Error" },
        { ["-o"], "Error" },
        { ["-td"], "Error" },
        { ["-pre"], "Error" },
        { ["path", "-inc"], "Error" },
        { ["--Transitive"], "Error" },
        { ["-T"], "Error" },
        { ["-tr"], "Error" },
        { ["mcp", "extra"], "Error" },

        // An option and its value in a single argument, separated by a space.
        { ["--runtime linux-x64"], "Run: Runtime=linux-x64" },
        { ["-rt win-x64"], "Run: Runtime=win-x64" },
        { ["-of csv", "path"], "Run: Path=path, OutputFileFormat=Csv" },
        { ["--transitive-depth 3"], "Run: TransitiveDepth=3" },
        { ["-u prompt"], "Run: Upgrade=(True, Prompt)" },
        { ["-t true"], "Error" },

        // A value attached to a flag was an error.
        { ["-t:true"], "Error" },
        { ["--recursive=false"], "Error" },
        { ["-i:x", "path"], "Error" },

        // A path starting with "-" was always rejected, even after "--".
        { ["--", "-dashed.csproj"], "Error" },
        { ["-t", "--", "-t"], "Error" },
    };

    [Theory]
    [MemberData(nameof(CompatibleArguments))]
    public void CommandLinesAreInterpretedAsTheyWereWithMcMaster(string[] args, string expected)
    {
        Assert.Equal(expected, Parse(args));
    }

    public static TheoryData<string[], string> DoubleDash() => new()
    {
        { ["--", "path"], "Run: Path=path" },
        { ["-t", "--", "path"], "Run: Path=path, Transitive=True" },
    };

    /// <summary>
    /// McMaster rejected "--"; System.CommandLine treats it as the end of the options.
    /// </summary>
    [Theory]
    [MemberData(nameof(DoubleDash))]
    public void DoubleDash_EndsTheOptions(string[] args, string expected)
    {
        Assert.Equal(expected, Parse(args));
    }

    /// <summary>
    /// McMaster took the next argument as an option's value even when it was another option, so <c>-o -u</c> wrote
    /// the report to a file called "-u" and did not upgrade. Now <c>-u</c> is an option and <c>-o</c> is missing its
    /// value. A value starting with "-" can still be attached, as it could before.
    /// </summary>
    [Fact]
    public void ValueThatIsAnotherOption_IsAnError_UnlessAttached()
    {
        Assert.Equal("Error", Parse(["-o", "-u"]));
        Assert.Equal("Run: OutputFilename=-u", Parse(["-o:-u"]));
    }

    [Theory]
    [InlineData("--recursive=false", "Option '--recursive=false' does not take a value.")]
    [InlineData("-t true", "Option '-t true' does not take a value.")]
    [InlineData("--bogus", "Unrecognized option '--bogus'.")]
    public void RejectedArguments_SayWhy(string arg, string expectedError)
    {
        var parseResult = CreateCommandLine().Parse([arg]);

        Assert.Contains(expectedError, parseResult.Errors.Select(e => e.Message));
    }

#if NET10_0_OR_GREATER
    /// <summary>
    /// The root command is named after the running executable: dotnet-outdated for the tool, testhost here.
    /// The nushell script lists no options; it asks the tool for them, see <see cref="SuggestDirective_CompletesOptionsAndValues"/>.
    /// </summary>
    [Theory]
    [InlineData("bash", "-F _{1} {0}")]
    [InlineData("zsh", "#compdef {0}")]
    [InlineData("pwsh", "Register-ArgumentCompleter -Native -CommandName '{0}'")]
    [InlineData("fish", "complete -c {0}")]
    [InlineData("nushell", "^{0} $\"[suggest:")]
    public void CompletionsScript_WritesAScriptForTheShell(string shell, string expectedFormat)
    {
        var commandLine = CreateCommandLine();
        var name = commandLine.RootCommand.Name;
        var output = new System.IO.StringWriter();

        var exitCode = commandLine.Parse(["completions", "script", shell])
            .Invoke(new System.CommandLine.InvocationConfiguration { Output = output });

        Assert.Equal(0, exitCode);
        Assert.Contains(string.Format(expectedFormat, name, name.Replace('-', '_')), output.ToString());
        Assert.DoesNotContain('\r', output.ToString());
        if (shell != "nushell")
            Assert.Contains("--transitive-depth", output.ToString());
    }

    /// <summary>
    /// Where no option, subcommand or value matches, the scripts fall back to file names, so the path argument and
    /// the --output file name still complete. PowerShell falls back on its own.
    /// </summary>
    [Theory]
    [InlineData("bash", "\ncomplete -o bashdefault -o default -F _{1} {0}", 1)]
    [InlineData("zsh", ":_files' \\\n", 3)] // the path argument, --output and -o
    [InlineData("fish", "\ncomplete -c {0} -f -a '(_{1}_or_files)'", 1)]
    [InlineData("fish", "__fish_complete_path $token", 1)]
    [InlineData("fish", "if contains -- (string lower -- $token) ", 1)] // "-of j" completes to Json, not to a file name
    [InlineData("nushell", " | lines | compact --empty | if ($in | is-empty) {{ null }} else {{ $in }}\n", 1)]
    public void CompletionsScript_FallsBackToFileNames(string shell, string expectedFormat, int expectedCount)
    {
        var commandLine = CreateCommandLine();
        var name = commandLine.RootCommand.Name;
        var output = new System.IO.StringWriter();

        commandLine.Parse(["completions", "script", shell]).Invoke(new System.CommandLine.InvocationConfiguration { Output = output });

        var expected = string.Format(expectedFormat, name, name.Replace('-', '_'));
        Assert.Equal(expectedCount, output.ToString().Split(expected).Length - 1);
    }

    /// <summary>
    /// The value of -u / --upgrade can only be attached (-u:Prompt), and the word after it is the path, so the scripts
    /// complete it as a flag rather than offering Auto and Prompt as the next word.
    /// </summary>
    [Theory]
    [InlineData("bash", "--upgrade|-u)", "--upgrade ")]
    [InlineData("zsh", "'-u=[", "'-u[")]
    [InlineData("fish", "'Prompt'", "'-u'")]
    [InlineData("pwsh", "'Prompt'", "'-u'")]
    public void CompletionsScript_CompletesUpgradeAsAFlag(string shell, string unexpected, string expected)
    {
        var commandLine = CreateCommandLine();
        var output = new System.IO.StringWriter();

        commandLine.Parse(["completions", "script", shell]).Invoke(new System.CommandLine.InvocationConfiguration { Output = output });

        Assert.DoesNotContain(unexpected, output.ToString());
        Assert.Contains(expected, output.ToString());

        // Only the script is generated that way: the option still takes its attached value.
        Assert.Equal(UpgradeType.Prompt, commandLine.Parse(["-u:prompt"]).GetValue(commandLine.Upgrade));
    }

    /// <summary>
    /// The package's bash script would offer "(bash" and "zsh)".
    /// </summary>
    [Fact]
    public void CompletionsScript_ForBash_CompletesShellNamesWithoutBrackets()
    {
        var output = new System.IO.StringWriter();

        CreateCommandLine().Parse(["completions", "script", "bash"]).Invoke(new System.CommandLine.InvocationConfiguration { Output = output });

        Assert.Contains("opts=\"$opts bash fish nushell pwsh zsh\"", output.ToString());
    }

    [Fact]
    public void CompletionsScript_RejectsAnUnknownShell()
    {
        var error = new System.IO.StringWriter();

        var exitCode = CreateCommandLine().Parse(["completions", "script", "tcsh"])
            .Invoke(new System.CommandLine.InvocationConfiguration { Output = new System.IO.StringWriter(), Error = error });

        Assert.Equal(1, exitCode);
        Assert.Contains("Shell 'tcsh' is not supported. Supported shells: bash, fish, nushell, pwsh, zsh.", error.ToString());
    }

    /// <summary>
    /// Without a shell name, the shell comes from SHELL, and an unsupported one is a parse error. Windows ignores
    /// SHELL and always defaults to PowerShell.
    /// </summary>
    [Fact]
    public void CompletionsScript_WithoutAShell_UsesTheShellFromTheEnvironment()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL");
        try
        {
            Environment.SetEnvironmentVariable("SHELL", "/bin/tcsh");

            var commandLine = CreateCommandLine();
            var parseResult = commandLine.Parse(["completions", "script"]);

            if (OperatingSystem.IsWindows())
            {
                Assert.Empty(parseResult.Errors);
                Assert.Equal(System.CommandLine.StaticCompletions.ShellNames.PowerShell, parseResult.GetValue(commandLine.CompletionsCommand.GenerateScriptCommand.ShellArgument));
            }
            else
            {
                Assert.NotEmpty(parseResult.Errors);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHELL", shell);
        }
    }

    [Theory]
    [InlineData("dotnet-outdated --tr", "--transitive|--transitive-depth")]
    [InlineData("dotnet-outdated -vl Ma", "Major")]
    [InlineData("dotnet-outdated -u ", "")] // the word after -u is the path, not Auto or Prompt
    public void SuggestDirective_CompletesOptionsAndValues(string commandLineText, string expected)
    {
        var output = new System.IO.StringWriter();

        var exitCode = CreateCommandLine().Parse([$"[suggest:{commandLineText.Length}]", commandLineText])
            .Invoke(new System.CommandLine.InvocationConfiguration { Output = output });

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, string.Join("|", output.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)));
    }

#endif
    private static ProgramCommandLine CreateCommandLine() => new(
        run: (_, _) => throw new InvalidOperationException("Only parsed."),
        runMcp: _ => throw new InvalidOperationException("Only parsed."));

    private static string Parse(string[] args)
    {
        var commandLine = CreateCommandLine();
        var parseResult = commandLine.Parse(args);

        if (parseResult.Errors.Count > 0)
            return "Error";

        var isMcp = parseResult.CommandResult.Command == commandLine.McpCommand;

        if (parseResult.Action is HelpAction)
            return isMcp ? "Help: mcp" : "Help";

        if (isMcp)
            return "Mcp";

        if (parseResult.GetResult(commandLine.RootCommand.Options.OfType<VersionOption>().Single()) is not null)
            return "Version";

        var p = new Program(null, null, null, null, null, null, null);
        commandLine.Bind(parseResult, p);

        return Describe(new Values(
            p.Path, p.IncludeAutoReferences, p.Prerelease, p.VersionLock, p.Transitive, p.TransitiveDepth, p.Upgrade,
            p.FailOnUpdates, string.Join("|", p.FilterInclude), string.Join("|", p.FilterExclude), p.OutputFilename,
            p.OutputFileFormat, p.OlderThanDays, p.NoRestore, p.Recursive, p.IncludeFileBasedApps, p.IgnoreFailedSources,
            p.IncludeUpToDate, p.PrereleaseLabel, p.NuGetCredLogLevel, p.Runtime, p.MaxVersion, p.IdleTimeout));
    }

    private static string Describe(Values values)
    {
        var changed = typeof(Values).GetProperties()
            .Where(p => !p.GetMethod.IsStatic && p.Name != "EqualityContract")
            .Where(p => !Equals(p.GetValue(values), p.GetValue(Values.Default)))
            .Select(p => $"{p.Name}={p.GetValue(values) ?? "null"}");

        var description = string.Join(", ", changed);
        return description.Length == 0 ? "Run" : $"Run: {description}";
    }

    /// <summary>
    /// The values a run uses, in a fixed order for <see cref="Describe"/>. Filters are joined with "|".
    /// </summary>
    private sealed record Values(
        string Path,
        bool IncludeAutoReferences,
        PrereleaseReporting Prerelease,
        VersionLock VersionLock,
        bool Transitive,
        int TransitiveDepth,
        (bool HasValue, UpgradeType UpgradeType) Upgrade,
        bool FailOnUpdates,
        string FilterInclude,
        string FilterExclude,
        string OutputFilename,
        OutputFormat OutputFileFormat,
        int OlderThanDays,
        bool NoRestore,
        bool Recursive,
        bool IncludeFileBasedApps,
        bool IgnoreFailedSources,
        bool IncludeUpToDate,
        string PrereleaseLabel,
        LogLevel NuGetCredLogLevel,
        string Runtime,
        string MaxVersion,
        int IdleTimeout)
    {
        public static Values Default { get; } = new(
            null, false, PrereleaseReporting.Auto, VersionLock.None, false, 1, default, false, "", "", null,
            OutputFormat.Json, 0, false, false, false, false, false, string.Empty, LogLevel.Warning, string.Empty,
            string.Empty, 120);
    }
}
