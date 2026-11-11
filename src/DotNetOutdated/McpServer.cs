using DotNetOutdated.Core.Services;
using DotNetOutdated.Core.Models;
using DotNetOutdated.Core;
using DotNetOutdated.Models;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace DotNetOutdated
{
    public class McpServer
    {
        // Modern (2026-07-28+) clients declare their version per-request via _meta;
        // legacy clients negotiate one of the legacy versions through the initialize handshake.
        private const string ModernProtocolVersion = "2026-07-28";
        private static readonly string[] SupportedLegacyProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

        private const string ProtocolVersionMetaKey = "io.modelcontextprotocol/protocolVersion";
        private const string ServerInfoMetaKey = "io.modelcontextprotocol/serverInfo";

        private const int UnsupportedProtocolVersionErrorCode = -32022;

        private readonly IServiceProvider _serviceProvider;
        private readonly IProjectDiscoveryService _projectDiscoveryService;
        private readonly IProjectAnalysisService _projectAnalysisService;
        private readonly IDotNetPackageService _dotNetPackageService;
        private readonly IOutdatedDependencyService _outdatedDependencyService;
        private readonly Stream _inputStream;
        private readonly Stream _outputStream;

        public McpServer(
            IServiceProvider serviceProvider,
            IProjectDiscoveryService projectDiscoveryService,
            IProjectAnalysisService projectAnalysisService,
            IDotNetPackageService dotNetPackageService,
            IOutdatedDependencyService outdatedDependencyService,
            Stream inputStream,
            Stream outputStream)
        {
            _serviceProvider = serviceProvider;
            _projectDiscoveryService = projectDiscoveryService;
            _projectAnalysisService = projectAnalysisService;
            _dotNetPackageService = dotNetPackageService;
            _outdatedDependencyService = outdatedDependencyService;
            _inputStream = inputStream;
            _outputStream = outputStream;
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(_inputStream);
            
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var request = JsonSerializer.Deserialize<JsonRpcRequest>(line);
                    if (request != null)
                    {
                        await HandleRequestAsync(request);
                    }
                }
                catch (Exception ex)
                {
                    await SendErrorAsync(null, -32700, "Parse error", ex.Message);
                }
            }
        }

        private async Task HandleRequestAsync(JsonRpcRequest request)
        {
            try
            {
                var requestedVersion = GetRequestedProtocolVersion(request);
                if (requestedVersion != null && requestedVersion != ModernProtocolVersion)
                {
                    if (request.Id != null)
                    {
                        await SendErrorAsync(request.Id, UnsupportedProtocolVersionErrorCode, "Unsupported protocol version",
                            new { supported = new[] { ModernProtocolVersion }, requested = requestedVersion });
                    }
                    return;
                }

                switch (request.Method)
                {
                    case "initialize":
                        await HandleInitializeAsync(request);
                        break;
                    case "server/discover":
                        await HandleServerDiscoverAsync(request);
                        break;
                    case "tools/list":
                        await HandleToolsListAsync(request);
                        break;
                    case "tools/call":
                        await HandleToolsCallAsync(request);
                        break;
                    case "ping":
                        await SendResultAsync(request.Id, new { });
                        break;
                    case "notifications/initialized":
                        break;
                    default:
                        if (request.Id != null)
                        {
                            await SendErrorAsync(request.Id, -32601, "Method not found", null);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                if (request.Id != null)
                {
                    await SendErrorAsync(request.Id, -32603, "Internal error", ex.ToString());
                }
                Console.Error.WriteLine($"Error handling request: {ex}");
            }
        }

        private async Task HandleInitializeAsync(JsonRpcRequest request)
        {
            // Legacy (pre-2026-07-28) handshake: echo the requested version if we support it,
            // otherwise offer the latest legacy version we do.
            var negotiatedVersion = SupportedLegacyProtocolVersions[0];
            if (request.Params is { ValueKind: JsonValueKind.Object } initParams &&
                initParams.TryGetProperty("protocolVersion", out var versionProp) &&
                versionProp.ValueKind == JsonValueKind.String &&
                SupportedLegacyProtocolVersions.Contains(versionProp.GetString()))
            {
                negotiatedVersion = versionProp.GetString()!;
            }

            var result = new
            {
                protocolVersion = negotiatedVersion,
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = GetServerInfo()
            };

            await SendResultAsync(request.Id, result);
        }

        private async Task HandleServerDiscoverAsync(JsonRpcRequest request)
        {
            var result = new Dictionary<string, object>
            {
                ["resultType"] = "complete",
                ["supportedVersions"] = new[] { ModernProtocolVersion },
                ["capabilities"] = new { tools = new { } },
                ["instructions"] = "Analyzes .NET projects for outdated NuGet packages. " +
                    "Use discover_projects to find projects in a directory, analyze_project to check a project for outdated packages, " +
                    "and update_package to upgrade a package to a specific version.",
                ["ttlMs"] = 3600000,
                ["cacheScope"] = "public",
                ["_meta"] = GetServerInfoMeta()
            };

            await SendResultAsync(request.Id, result);
        }

        private async Task HandleToolsListAsync(JsonRpcRequest request)
        {
            var tools = new List<object>
            {
                new
                {
                    name = "discover_projects",
                    description = "Scans a directory for .NET projects (.csproj, .sln, etc.) and optionally file-based apps",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Root path to search" },
                            recursive = new { type = "boolean", description = "Whether to search recursively" },
                            includeFileBasedApps = new { type = "boolean", description = "Whether recursive discovery should include loose .cs file-based apps" }
                        },
                        required = new[] { "path" }
                    }
                },
                new
                {
                    name = "analyze_project",
                    description = "Analyzes a project or file-based app for outdated packages",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            projectPath = new { type = "string", description = "Path to the project file or .cs file-based app" },
                            includeTransitive = new { type = "boolean", description = "Check transitive dependencies" }
                        },
                        required = new[] { "projectPath" }
                    }
                },
                new
                {
                    name = "update_package",
                    description = "Updates a specific package in a project or file-based app",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            projectPath = new { type = "string", description = "Path to the project or .cs file-based app" },
                            packageName = new { type = "string", description = "Name of the package" },
                            version = new { type = "string", description = "Version to upgrade to" },
                            framework = new { type = "string", description = "Target framework" },
                            kind = new
                            {
                                type = "string",
                                @enum = new[] { "package", "sdk" },
                                description = "Whether packageName is a package or an MSBuild SDK, as reported by analyze_project. When omitted, a name the project declares as a versioned SDK is updated as an SDK, and any other name as a package; pass it when a project references a package and an SDK with the same name."
                            }
                        },
                        required = new[] { "projectPath", "packageName", "version", "framework" }
                    }
                }
            };

            var result = new Dictionary<string, object>
            {
                ["resultType"] = "complete",
                ["tools"] = tools,
                ["ttlMs"] = 3600000,
                ["cacheScope"] = "public",
                ["_meta"] = GetServerInfoMeta()
            };

            await SendResultAsync(request.Id, result);
        }

        private async Task HandleToolsCallAsync(JsonRpcRequest request)
        {
            if (request.Params == null)
            {
                await SendErrorAsync(request.Id, -32602, "Invalid params", null);
                return;
            }

            var toolCall = JsonSerializer.Deserialize<McpToolCallParams>(request.Params.Value.GetRawText());
            if (toolCall == null)
            {
                await SendErrorAsync(request.Id, -32602, "Invalid params", null);
                return;
            }

            Dictionary<string, object>? result = null;

            try
            {
                switch (toolCall.Name)
                {
                    case "discover_projects":
                        result = await HandleDiscoverProjects(toolCall.Arguments);
                        break;
                    case "analyze_project":
                        result = await HandleAnalyzeProject(toolCall.Arguments);
                        break;
                    case "update_package":
                        result = await HandleUpdatePackage(toolCall.Arguments);
                        break;
                    default:
                        await SendErrorAsync(request.Id, -32601, "Tool not found", null);
                        return;
                }
            }
            catch (Exception ex)
            {
                await SendErrorAsync(request.Id, -32603, "Tool execution error", ex.Message);
                return;
            }

            result["resultType"] = "complete";
            result["_meta"] = GetServerInfoMeta();

            await SendResultAsync(request.Id, result);
        }

        private static Dictionary<string, object> CreateToolResult(string text, bool isError = false)
        {
            var result = new Dictionary<string, object>
            {
                ["content"] = new[] { new { type = "text", text } }
            };

            if (isError)
            {
                result["isError"] = true;
            }

            return result;
        }

        private async Task<Dictionary<string, object>> HandleDiscoverProjects(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathProp) || pathProp.ValueKind == JsonValueKind.Null || pathProp.GetString() is null)
            {
                throw new ArgumentException("path is required");
            }
            string path = pathProp.GetString()!;
            bool recursive = false;
            if (arguments.TryGetProperty("recursive", out var recursiveProp))
            {
                recursive = recursiveProp.GetBoolean();
            }

            bool includeFileBasedApps = false;
            if (arguments.TryGetProperty("includeFileBasedApps", out var includeFileBasedAppsProp))
            {
                includeFileBasedApps = includeFileBasedAppsProp.GetBoolean();
            }

            var projects = _projectDiscoveryService.DiscoverProjects(path, recursive, includeFileBasedApps);

            return CreateToolResult(JsonSerializer.Serialize(projects));
        }

        private async Task<Dictionary<string, object>> HandleAnalyzeProject(JsonElement arguments)
        {
            string projectPath = arguments.GetProperty("projectPath").GetString() ?? throw new ArgumentException("projectPath is required");
            bool includeTransitive = false;
            if (arguments.TryGetProperty("includeTransitive", out var includeTransitiveProp))
            {
                includeTransitive = includeTransitiveProp.GetBoolean();
            }

            int transitiveDepth = 1;
            string? runtime = null;

            var projects = await _projectAnalysisService.AnalyzeProjectAsync(projectPath, new ProjectAnalysisOptions
            {
                IncludeTransitiveDependencies = includeTransitive,
                TransitiveDepth = transitiveDepth,
                Runtime = runtime,
            });
            
            // Every dependency is reported, with its latest version, so the client can decide what to upgrade.
            var outdatedProjects = await _outdatedDependencyService.FindOutdatedAsync(projects, new OutdatedOptions
            {
                IncludeAutoReferences = true,
                IncludeUpToDate = true,
            });

            // FindOutdatedAsync leaves out projects and target frameworks with no dependencies, but every analyzed one is
            // reported here so a client can tell "analyzed, nothing referenced" apart from "nothing found".
            var outdatedDependencies = outdatedProjects
                .SelectMany(project => project.TargetFrameworks.Select(tf => (project.FilePath, tf.Name, tf.TargetAlias, tf.Dependencies)))
                .ToDictionary(entry => (entry.FilePath, entry.Name, entry.TargetAlias), entry => entry.Dependencies);

            var result = projects.Select(project => new
            {
                name = project.Name,
                filePath = project.FilePath,
                targetFrameworks = project.TargetFrameworks.Select(tf => new
                {
                    // The string update_package takes as its framework, as the CLI passes to dotnet add package.
                    name = string.IsNullOrEmpty(tf.TargetAlias) ? tf.Name.GetShortFolderName() : tf.TargetAlias,
                    dependencies = outdatedDependencies.GetValueOrDefault((project.FilePath, tf.Name, tf.TargetAlias), []).Select(outdated => new
                    {
                        name = outdated.Dependency.Name,
                        version = outdated.Dependency.ResolvedVersion?.ToString(),
                        latestVersion = outdated.LatestVersion?.ToString(),
                        isAutoReferenced = outdated.Dependency.IsAutoReferenced,
                        isTransitive = outdated.Dependency.IsTransitive,
                        kind = outdated.Dependency.Kind == ReferenceKind.Sdk ? "sdk" : "package",
                        upgradeSeverity = outdated.UpgradeSeverity.ToString(),
                    }),
                }),
            });

            return CreateToolResult(JsonSerializer.Serialize(result));
        }

        private async Task<Dictionary<string, object>> HandleUpdatePackage(JsonElement arguments)
        {
            string projectPath = arguments.GetProperty("projectPath").GetString() ?? throw new ArgumentException("projectPath is required");
            string packageName = arguments.GetProperty("packageName").GetString() ?? throw new ArgumentException("packageName is required");
            string? versionString = arguments.GetProperty("version").GetString();
            string? framework = null;
            if (arguments.TryGetProperty("framework", out var frameworkProp))
            {
                framework = frameworkProp.GetString();
            }
            
            if (!NuGetVersion.TryParse(versionString, out var version))
            {
                 throw new ArgumentException($"Invalid version: {versionString}");
            }

            if (string.IsNullOrEmpty(framework))
            {
                 throw new ArgumentException("framework is required");
            }

            ReferenceKind? kind = null;
            if (arguments.TryGetProperty("kind", out var kindProp) && kindProp.ValueKind != JsonValueKind.Null)
            {
                kind = (kindProp.ValueKind == JsonValueKind.String ? kindProp.GetString()?.ToLowerInvariant() : null) switch
                {
                    "package" => ReferenceKind.Package,
                    "sdk" => ReferenceKind.Sdk,
                    _ => throw new ArgumentException($"Invalid kind: {kindProp.GetRawText()}. Expected \"package\" or \"sdk\"."),
                };
            }

            RunStatus status = _dotNetPackageService.Upgrade(new ReferenceUpgrade
            {
                ProjectPath = projectPath,
                Name = packageName,
                Kind = kind,
                TargetFramework = framework,
                Version = new VersionRange(version),
            });

            if (status.IsSuccess)
            {
                return CreateToolResult($"Successfully updated {packageName} to {version}");
            }
            else
            {
                return CreateToolResult($"Failed to update package: {status.FailureDetails}", isError: true);
            }
        }

        private static string? GetRequestedProtocolVersion(JsonRpcRequest request)
        {
            if (request.Params is { ValueKind: JsonValueKind.Object } requestParams &&
                requestParams.TryGetProperty("_meta", out var meta) &&
                meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty(ProtocolVersionMetaKey, out var version) &&
                version.ValueKind == JsonValueKind.String)
            {
                return version.GetString();
            }

            return null;
        }

        private static object GetServerInfo() => new
        {
            name = "dotnet-outdated",
            version = Program.GetVersion() ?? "0.0.0"
        };

        private static Dictionary<string, object> GetServerInfoMeta() => new()
        {
            [ServerInfoMetaKey] = GetServerInfo()
        };

        private async Task SendResultAsync(object? id, object result)
        {
            var response = new JsonRpcResponse
            {
                Id = id,
                Result = result
            };

            await SendResponseAsync(response);
        }

        private async Task SendErrorAsync(object? id, int code, string message, object? data)
        {
            var response = new JsonRpcResponse
            {
                Id = id,
                Error = new JsonRpcError
                {
                    Code = code,
                    Message = message,
                    Data = data
                }
            };

            await SendResponseAsync(response);
        }

        private async Task SendResponseAsync(JsonRpcResponse response)
        {
            var json = JsonSerializer.Serialize(response);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");
            await _outputStream.WriteAsync(bytes);
            await _outputStream.FlushAsync();
        }
    }
}
