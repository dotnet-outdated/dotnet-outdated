using DotNetOutdated.Core;
using NuGet.Common;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.CommandLine.Parsing;
#if NET10_0_OR_GREATER
using System.CommandLine.StaticCompletions;
#endif
using System.Threading;
using System.Threading.Tasks;

namespace DotNetOutdated
{
   /// <summary>
   /// The System.CommandLine definition of the dotnet-outdated command line.
   /// </summary>
   /// <remarks>
   /// The option names, aliases and value syntaxes match the conventions of the McMaster.Extensions.CommandLineUtils
   /// definition this replaced, so existing scripts keep working:
   /// <list type="bullet">
   /// <item>multi-letter short aliases with a single dash (<c>-pre</c>, <c>-vl</c>, <c>-td</c>, ...);</item>
   /// <item>values given as a separate argument, or attached with <c>:</c>, <c>=</c> or a space within one argument
   /// (<c>-of:csv</c>, <c>--output=report.json</c>, <c>"--runtime linux-x64"</c>);</item>
   /// <item>case-insensitive enum values;</item>
   /// <item>flags that never consume the next token, so <c>dotnet outdated -t ./src</c> still treats <c>./src</c> as the path,
   /// and that reject an attached value (<c>--recursive=false</c> is an error, not a recursive run of a path called "false");</item>
   /// <item><c>-u</c> / <c>--upgrade</c> with an optional value that can only be attached (<c>-u</c>, <c>-u:prompt</c>);</item>
   /// <item>a path that starts with <c>-</c> is rejected as an unrecognized option, even after <c>--</c>.</item>
   /// </list>
   /// POSIX bundling (<c>-tr</c> meaning <c>-t -r</c>) and <c>@file</c> response files are off, as they were before.
   /// </remarks>
   /// <remarks>Each instance parses one command line at a time; it is not safe to call <see cref="Parse"/> concurrently.</remarks>
   internal sealed class ProgramCommandLine
   {
      private readonly List<string> _flagsWithAttachedValues = [];

      public Argument<string> Path { get; } = new("Path")
      {
         Description = "The path to a .sln, .slnx, .slnf, .csproj, .fsproj or .cs file-based app, or to a directory containing a .NET Core solution/project. " +
                       "If none is specified, the current directory will be used.",
         Arity = ArgumentArity.ZeroOrOne,
      };

      // McMaster inferred the "-i" short name from the long name, so it is kept as an alias.
      public Option<bool> IncludeAutoReferences { get; } = Flag("--include-auto-references", "-i",
          "Specifies whether to include auto-referenced packages.");

      public Option<PrereleaseReporting> Prerelease { get; } = new("--pre-release", "-pre")
      {
         Description = "Specifies whether to look for pre-release versions of packages. " +
                       "Possible values: Auto (default), Always or Never.",
         DefaultValueFactory = _ => PrereleaseReporting.Auto,
      };

      public Option<VersionLock> VersionLock { get; } = new("--version-lock", "-vl")
      {
         Description = "Specifies whether the package should be locked to the current Major or Minor version. " +
                       "Possible values: None (default), Major or Minor.",
         DefaultValueFactory = _ => Core.VersionLock.None,
      };

      public Option<bool> Transitive { get; } = Flag("--transitive", "-t",
          "Specifies whether it should detect transitive dependencies.");

      public Option<int> TransitiveDepth { get; } = new("--transitive-depth", "-td")
      {
         Description = "Defines how many levels deep transitive dependencies should be analyzed. " +
                       "Integer value (default = 1)",
         DefaultValueFactory = _ => 1,
      };

      // Given on its own, -u/--upgrade is rewritten to --upgrade:Auto before parsing; see Parse.
      public Option<UpgradeType> Upgrade { get; } = new("--upgrade", "-u")
      {
         Description = "Specifies whether outdated packages should be upgraded. " +
                       "Possible values for <TYPE> is Auto (default) or Prompt, given as -u:<TYPE>.",
         HelpName = "TYPE",
      };

      public Option<bool> FailOnUpdates { get; } = Flag("--fail-on-updates", "-f",
          "Specifies whether it should return a non-zero exit code when updates are found.");

      public Option<List<string>> FilterInclude { get; } = new("--include", "-inc")
      {
         Description = "Specifies to only look at packages where the name contains the provided string. Culture and case insensitive. " +
                       "If provided multiple times, a single match is enough to include a package.",
         DefaultValueFactory = _ => [],
      };

      public Option<List<string>> FilterExclude { get; } = new("--exclude", "-exc")
      {
         Description = "Specifies to only look at packages where the name does not contain the provided string. Culture and case insensitive. " +
                       "If provided multiple times, a single match is enough to exclude a package.",
         DefaultValueFactory = _ => [],
      };

      public Option<string> OutputFilename { get; } = new("--output", "-o")
      {
         Description = "Specifies the filename for a generated report. " +
                       "(Use the -of|--output-format option to specify the format. JSON by default.)",
      };

      public Option<OutputFormat> OutputFileFormat { get; } = new("--output-format", "-of")
      {
         Description = "Specifies the output format for the generated report. " +
                       "Possible values: json (default), csv, or markdown.",
         DefaultValueFactory = _ => OutputFormat.Json,
      };

      public Option<int> OlderThanDays { get; } = new("--older-than", "-ot")
      {
         Description = "Only include package versions that are older than the specified number of days.",
         DefaultValueFactory = _ => 0,
      };

      public Option<bool> NoRestore { get; } = Flag("--no-restore", "-n",
          "Add the reference without performing restore preview and compatibility check.");

      public Option<bool> Recursive { get; } = Flag("--recursive", "-r",
          "Recursively search for all projects within the provided directory.");

      public Option<bool> IncludeFileBasedApps { get; } = Flag("--include-file-based-apps", "-fba",
          "Include loose file-based apps when recursively searching a directory.");

      public Option<bool> IgnoreFailedSources { get; } = Flag("--ignore-failed-sources", "-ifs",
          "Treat package source failures as warnings.");

      public Option<bool> IncludeUpToDate { get; } = Flag("--include-up-to-date", "-utd",
          "Include all dependencies in the report even the ones not outdated.");

      public Option<string> PrereleaseLabel { get; } = new("--pre-release-label", "-prl")
      {
         Description = "Specifies an optional label to restrict matches to when looking for pre-release versions of packages. " +
                       "For example, a label of 'rc.1' would only match pre-release packages with a pre-release label that starts with that prefix.",
         DefaultValueFactory = _ => string.Empty,
      };

      public Option<LogLevel> NuGetCredLogLevel { get; } = new("--nuget-cred-log-level", "-ncll")
      {
         Description = "Specifies the minimum level of logs for NuGet Credential Service. " +
                       "Possible values: debug, verbose, information, warning (default), or error",
         DefaultValueFactory = _ => LogLevel.Warning,
      };

      public Option<string> Runtime { get; } = new("--runtime", "-rt")
      {
         Description = "Specifies an optional runtime identifier to be used during the restore target when projects are analyzed. " +
                       "More information available on https://learn.microsoft.com/dotnet/core/rid-catalog",
         DefaultValueFactory = _ => string.Empty,
      };

      public Option<string> MaxVersion { get; } = new("--maximum-version", "-mv")
      {
         Description = "The inclusive maximum package version to upgrade to. " +
                       "For example, a value of '8.0' would upgrade System.Text.Json 6.0.0 to the latest patch version of 8.0.x",
         DefaultValueFactory = _ => string.Empty,
      };

      public Option<int> IdleTimeout { get; } = new("--idle-timeout", "-it")
      {
         Description = "Specifies the idle timeout in seconds to wait for output from the dotnet executable before assuming it has hung. " +
                       "Default is 120 seconds.",
         DefaultValueFactory = _ => 120,
      };

      public RootCommand RootCommand { get; }

      public Command McpCommand { get; } = new("mcp", "Runs the Model Context Protocol (MCP) server.");

#if NET10_0_OR_GREATER
      /// <summary>
      /// The <c>completions script [shell]</c> subcommand, which prints a shell completion script for this command line.
      /// </summary>
      /// <remarks>Only in the net10.0 build, as System.CommandLine.StaticCompletions only targets net10.0.</remarks>
      public CompletionsCommandDefinition CompletionsCommand { get; } = new();
#endif

      public ParserConfiguration ParserConfiguration { get; } = new()
      {
         EnablePosixBundling = false,
         ResponseFileTokenReplacer = null,
      };

      /// <param name="run">Runs the analysis, given a parse whose values <see cref="Bind"/> copies onto a <see cref="Program"/>.</param>
      /// <param name="runMcp">Runs the <c>mcp</c> subcommand.</param>
      public ProgramCommandLine(Func<ParseResult, CancellationToken, Task<int>> run, Func<CancellationToken, Task<int>> runMcp)
      {
         ArgumentNullException.ThrowIfNull(run);
         ArgumentNullException.ThrowIfNull(runMcp);

         Path.Validators.Add(RejectOptionLikePath);

         RootCommand = new RootCommand("A .NET Core global tool to list outdated Nuget packages.")
         {
            Path,
            IncludeAutoReferences,
            Prerelease,
            VersionLock,
            Transitive,
            TransitiveDepth,
            Upgrade,
            FailOnUpdates,
            FilterInclude,
            FilterExclude,
            OutputFilename,
            OutputFileFormat,
            OlderThanDays,
            NoRestore,
            Recursive,
            IncludeFileBasedApps,
            IgnoreFailedSources,
            IncludeUpToDate,
            PrereleaseLabel,
            NuGetCredLogLevel,
            Runtime,
            MaxVersion,
            IdleTimeout,
            McpCommand,
         };

#if NET10_0_OR_GREATER
         CompletionsCommandParser.ConfigureCommand(CompletionsCommand);
         CompletionsCommand.GenerateScriptCommand.ShellArgument.DefaultValueFactory = ShellFromEnvironment;
         CompletionsCommand.GenerateScriptCommand.SetAction(WriteCompletionScript);
         RootCommand.Subcommands.Add(CompletionsCommand);
#endif

         RootCommand.Validators.Add(RejectFlagsWithAttachedValues);
         RootCommand.SetAction(run);
         McpCommand.SetAction((_, cancellationToken) => runMcp(cancellationToken));
      }

      public ParseResult Parse(IReadOnlyList<string> args)
      {
         ArgumentNullException.ThrowIfNull(args);

         _flagsWithAttachedValues.Clear();
         return RootCommand.Parse(NormalizeLegacySyntax(args), ParserConfiguration);
      }

      /// <summary>
      /// Copies the values of a successful parse onto the properties <see cref="Program"/> runs with.
      /// </summary>
      public void Bind(ParseResult parseResult, Program program)
      {
         ArgumentNullException.ThrowIfNull(parseResult);
         ArgumentNullException.ThrowIfNull(program);

         program.Path = parseResult.GetValue(Path);
         program.IncludeAutoReferences = parseResult.GetValue(IncludeAutoReferences);
         program.Prerelease = parseResult.GetValue(Prerelease);
         program.VersionLock = parseResult.GetValue(VersionLock);
         program.Transitive = parseResult.GetValue(Transitive);
         program.TransitiveDepth = parseResult.GetValue(TransitiveDepth);
         program.Upgrade = (parseResult.GetResult(Upgrade) is not null, parseResult.GetValue(Upgrade));
         program.FailOnUpdates = parseResult.GetValue(FailOnUpdates);
         program.FilterInclude = parseResult.GetValue(FilterInclude);
         program.FilterExclude = parseResult.GetValue(FilterExclude);
         program.OutputFilename = parseResult.GetValue(OutputFilename);
         program.OutputFileFormat = parseResult.GetValue(OutputFileFormat);
         program.OlderThanDays = parseResult.GetValue(OlderThanDays);
         program.NoRestore = parseResult.GetValue(NoRestore);
         program.Recursive = parseResult.GetValue(Recursive);
         program.IncludeFileBasedApps = parseResult.GetValue(IncludeFileBasedApps);
         program.IgnoreFailedSources = parseResult.GetValue(IgnoreFailedSources);
         program.IncludeUpToDate = parseResult.GetValue(IncludeUpToDate);
         program.PrereleaseLabel = parseResult.GetValue(PrereleaseLabel);
         program.NuGetCredLogLevel = parseResult.GetValue(NuGetCredLogLevel);
         program.Runtime = parseResult.GetValue(Runtime);
         program.MaxVersion = parseResult.GetValue(MaxVersion);
         program.IdleTimeout = parseResult.GetValue(IdleTimeout);
      }

      /// <summary>
      /// A switch: present means true. It never takes a value, so the next token is left for the path argument.
      /// </summary>
      private static Option<bool> Flag(string name, string alias, string description) => new(name, alias)
      {
         Description = description,
         Arity = ArgumentArity.Zero,
      };

      /// <summary>
      /// Rewrites the McMaster syntaxes System.CommandLine has no equivalent for into ones it parses the same way.
      /// </summary>
      /// <remarks>
      /// <list type="number">
      /// <item>McMaster split an option from its value on a space within one argument, as it did on <c>:</c> and
      /// <c>=</c>, so <c>"--runtime linux-x64"</c> (one argument) is rewritten to <c>--runtime:linux-x64</c>.</item>
      /// <item>McMaster rejected a value attached to a flag, but System.CommandLine would split <c>--recursive=false</c>
      /// into the flag and a path called "false". The value is dropped here and reported as an error when the
      /// command is validated.</item>
      /// <item>McMaster's optional-value option only took a value attached to it, never the next argument, so
      /// <c>dotnet outdated -u ./src</c> upgrades <c>./src</c>, and <c>-u prompt</c> means a path called "prompt".
      /// In System.CommandLine an optional value takes the next argument whenever it can, so instead a bare
      /// <c>-u</c> / <c>--upgrade</c> is rewritten to <c>--upgrade:Auto</c> and the option always requires a value.
      /// McMaster ignored a bare <c>-u</c> once the option had been given, so <c>-u -u</c> and <c>-u:prompt -u</c>
      /// both worked; only the first is kept here. A second value, as in <c>-u -u:prompt</c>, is still an error.</item>
      /// </list>
      /// Arguments after <c>--</c> are left alone.
      /// </remarks>
      private List<string> NormalizeLegacySyntax(IReadOnlyList<string> args)
      {
         var result = new List<string>(args.Count);
         var optionsEnded = false;
         var upgradeGiven = false;

         foreach (var original in args)
         {
            var arg = original;
            optionsEnded |= arg == "--";

            if (!optionsEnded)
            {
               var separator = arg.IndexOfAny([':', '=', ' ']);
               if (separator > 0 && FindOption(arg[..separator]) is { } option)
               {
                  if (option.Arity.MaximumNumberOfValues == 0)
                  {
                     _flagsWithAttachedValues.Add(arg);
                     arg = arg[..separator];
                  }
                  else if (arg[separator] == ' ')
                  {
                     arg = $"{arg[..separator]}:{arg[(separator + 1)..]}";
                  }
               }

               if (IsUpgradeAlias(arg))
               {
                  if (upgradeGiven)
                     continue;

                  arg = $"{arg}:{UpgradeType.Auto}";
               }

               // By now an attached value is always after ":" or "=".
               var valueStart = arg.IndexOfAny([':', '=']);
               upgradeGiven |= IsUpgradeAlias(valueStart > 0 ? arg[..valueStart] : arg);
            }

            result.Add(arg);
         }

         return result;
      }

#if NET10_0_OR_GREATER
      /// <summary>
      /// Writes the completion script for the requested shell, as the package's own action does, but with LF line endings.
      /// </summary>
      /// <remarks>
      /// System.CommandLine.StaticCompletions 3.0.0 mixes CRLF into the bash, zsh, pwsh and nushell scripts, and bash
      /// fails on the stray carriage returns (<c>return\r: command not found</c>), so option values never complete.
      /// </remarks>
      private int WriteCompletionScript(ParseResult parseResult)
      {
         var shell = parseResult.GetValue(CompletionsCommand.GenerateScriptCommand.ShellArgument);
         if (!CompletionsCommandParser.ShellProviders.TryGetValue(shell, out var provider))
         {
            parseResult.InvocationConfiguration.Error.WriteLine(
               $"Shell '{shell}' is not supported. Supported shells: {string.Join(", ", CompletionsCommandParser.ShellProviders.Keys.Order())}.");
            return 1;
         }

         parseResult.InvocationConfiguration.Output.Write(provider.GenerateCompletions(RootCommand).ReplaceLineEndings("\n"));
         return 0;
      }

      /// <summary>
      /// The shell to write a completion script for when none is given: the package's default, but an unsupported
      /// <c>SHELL</c> is a parse error rather than an exception thrown out of <see cref="Parse"/>.
      /// </summary>
      private static string ShellFromEnvironment(ArgumentResult result)
      {
         try
         {
            return ShellNames.GetShellNameFromEnvironment();
         }
         catch (InvalidOperationException ex)
         {
            result.AddError($"{ex.Message.Trim()}. Name the shell, as in 'completions script bash'.");
            return null;
         }
      }
#endif

      private bool IsUpgradeAlias(string token) => Upgrade.Name == token || Upgrade.Aliases.Contains(token);

      private Option FindOption(string token)
      {
         foreach (var option in RootCommand.Options)
         {
            if (option.Name == token || option.Aliases.Contains(token))
               return option;
         }

         return null;
      }

      private void RejectFlagsWithAttachedValues(CommandResult result)
      {
         foreach (var arg in _flagsWithAttachedValues)
            result.AddError($"Option '{arg}' does not take a value.");
      }

      private static void RejectOptionLikePath(ArgumentResult result)
      {
         if (result.Tokens is [{ Value: var value }] && value.StartsWith('-'))
            result.AddError($"Unrecognized option '{value}'.");
      }
   }
}
