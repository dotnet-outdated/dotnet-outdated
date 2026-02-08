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
        private readonly IServiceProvider _serviceProvider;
        private readonly IProjectDiscoveryService _projectDiscoveryService;
        private readonly IProjectAnalysisService _projectAnalysisService;
        private readonly IDotNetPackageService _dotNetPackageService;
        private readonly ICentralPackageVersionManagementService _centralPackageVersionManagementService;
        private readonly INuGetPackageResolutionService _nugetService;
        private readonly Stream _inputStream;
        private readonly Stream _outputStream;

        public McpServer(
            IServiceProvider serviceProvider,
            IProjectDiscoveryService projectDiscoveryService,
            IProjectAnalysisService projectAnalysisService,
            IDotNetPackageService dotNetPackageService,
            ICentralPackageVersionManagementService centralPackageVersionManagementService,
            INuGetPackageResolutionService nugetService,
            Stream inputStream,
            Stream outputStream)
        {
            _serviceProvider = serviceProvider;
            _projectDiscoveryService = projectDiscoveryService;
            _projectAnalysisService = projectAnalysisService;
            _dotNetPackageService = dotNetPackageService;
            _centralPackageVersionManagementService = centralPackageVersionManagementService;
            _nugetService = nugetService;
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
                switch (request.Method)
                {
                    case "initialize":
                        await HandleInitializeAsync(request);
                        break;
                    case "tools/list":
                        await HandleToolsListAsync(request);
                        break;
                    case "tools/call":
                        await HandleToolsCallAsync(request);
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
            var result = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = new
                {
                    name = "dotnet-outdated",
                    version = "1.0.0"
                }
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
                    description = "Scans a directory for .NET projects (.csproj, .sln, etc.)",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Root path to search" },
                            recursive = new { type = "boolean", description = "Whether to search recursively" }
                        },
                        required = new[] { "path" }
                    }
                },
                new
                {
                    name = "analyze_project",
                    description = "Analyzes a project for outdated packages",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            projectPath = new { type = "string", description = "Path to the project file" },
                            includeTransitive = new { type = "boolean", description = "Check transitive dependencies" }
                        },
                        required = new[] { "projectPath" }
                    }
                },
                new
                {
                    name = "update_package",
                    description = "Updates a specific package in a project",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            projectPath = new { type = "string", description = "Path to the project" },
                            packageName = new { type = "string", description = "Name of the package" },
                            version = new { type = "string", description = "Version to upgrade to" },
                            framework = new { type = "string", description = "Target framework (optional, but recommended)" }
                        },
                        required = new[] { "projectPath", "packageName", "version" }
                    }
                }
            };

            await SendResultAsync(request.Id, new { tools });
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

            object result = null;

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

            await SendResultAsync(request.Id, result);
        }

        private async Task<object> HandleDiscoverProjects(JsonElement arguments)
        {
            if (!arguments.TryGetProperty("path", out var pathProp) || pathProp.ValueKind == JsonValueKind.Null || pathProp.GetString() is null)
            {
                throw new ArgumentException("path is required");
            }
            string path = pathProp.GetString();
            bool recursive = false;
            if (arguments.TryGetProperty("recursive", out var recursiveProp))
            {
                recursive = recursiveProp.GetBoolean();
            }

            var projects = _projectDiscoveryService.DiscoverProjects(path, recursive);
            
            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(projects)
                    }
                }
            };
        }

        private async Task<object> HandleAnalyzeProject(JsonElement arguments)
        {
            string projectPath = arguments.GetProperty("projectPath").GetString();
            bool includeTransitive = false;
            if (arguments.TryGetProperty("includeTransitive", out var includeTransitiveProp))
            {
                includeTransitive = includeTransitiveProp.GetBoolean();
            }

            int transitiveDepth = 1;
            string runtime = null;

            var projects = await _projectAnalysisService.AnalyzeProjectAsync(projectPath, false, includeTransitive, transitiveDepth, runtime);
            
            var result = new List<object>();
            foreach (var project in projects)
            {
                var frameworks = new List<object>();
                foreach (var tf in project.TargetFrameworks)
                {
                    var dependencies = new List<object>();
                    foreach (var dep in tf.Dependencies.Values)
                    {
                        var referencedVersion = dep.ResolvedVersion;
                        NuGetVersion latestVersion = null;
                        
                        if (referencedVersion != null)
                        {
                            try 
                            {
                                latestVersion = await _nugetService.ResolvePackageVersions(
                                    dep.Name,
                                    referencedVersion,
                                    project.Sources,
                                    dep.VersionRange,
                                    VersionLock.None,
                                    PrereleaseReporting.Auto,
                                    null,
                                    tf.Name,
                                    project.FilePath,
                                    dep.IsDevelopmentDependency);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"Error resolving version for {dep.Name}: {ex.Message}");
                            }
                        }

                        dependencies.Add(new
                        {
                            name = dep.Name,
                            version = dep.ResolvedVersion?.ToString(),
                            latestVersion = latestVersion?.ToString(),
                            isAutoReferenced = dep.IsAutoReferenced,
                            isTransitive = dep.IsTransitive,
                            upgradeSeverity = GetUpgradeSeverity(dep.ResolvedVersion, latestVersion)
                        });
                    }
                    frameworks.Add(new { name = tf.Name, dependencies });
                }
                result.Add(new { name = project.Name, filePath = project.FilePath, targetFrameworks = frameworks });
            }

            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(result)
                    }
                }
            };
        }

        private string GetUpgradeSeverity(NuGetVersion resolved, NuGetVersion latest)
        {
            if (latest == null || resolved == null)
                return "Unknown";

            if (latest.Major > resolved.Major || resolved.IsPrerelease)
                return "Major";
            if (latest.Minor > resolved.Minor)
                return "Minor";
            if (latest.Patch > resolved.Patch || latest.Revision > resolved.Revision)
                return "Patch";

            return "None";
        }

        private async Task<object> HandleUpdatePackage(JsonElement arguments)
        {
            string projectPath = arguments.GetProperty("projectPath").GetString();
            string packageName = arguments.GetProperty("packageName").GetString();
            string versionString = arguments.GetProperty("version").GetString();
            string framework = null;
            if (arguments.TryGetProperty("framework", out var frameworkProp))
            {
                framework = frameworkProp.GetString();
            }
            
            if (!NuGetVersion.TryParse(versionString, out var version))
            {
                 throw new ArgumentException($"Invalid version: {versionString}");
            }

            RunStatus status;
            
            if (!string.IsNullOrEmpty(framework))
            {
                 status = _dotNetPackageService.AddPackage(projectPath, packageName, framework, version);
            }
            else
            {
                status = _centralPackageVersionManagementService.AddPackage(projectPath, packageName, version, false);
                
                if (!status.IsSuccess)
                {
                    return new 
                    {
                        content = new[] { new { type = "text", text = $"Failed to update package. If this is not a CPM project, please provide the 'framework' argument. Error: {status.Errors}" } },
                        isError = true
                    };
                }
            }

            if (status.IsSuccess)
            {
                return new
                {
                    content = new[] { new { type = "text", text = $"Successfully updated {packageName} to {version}" } }
                };
            }
            else
            {
                return new
                {
                    content = new[] { new { type = "text", text = $"Failed to update package: {status.Errors}" } },
                    isError = true
                };
            }
        }

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
