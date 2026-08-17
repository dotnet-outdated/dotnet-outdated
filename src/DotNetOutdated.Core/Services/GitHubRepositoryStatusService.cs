using DotNetOutdated.Core.Models;
using NuGet.Versioning;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    public sealed class GitHubRepositoryStatusService(
        INuGetPackageInfoService packageInfoService,
        HttpClient httpClient) : IRepositoryStatusService
    {
        private readonly INuGetPackageInfoService _packageInfoService = packageInfoService;
        private readonly HttpClient _httpClient = httpClient;
        private readonly ConcurrentDictionary<string, Lazy<Task<RepositoryStatus>>> _statusRequests = new(StringComparer.OrdinalIgnoreCase);

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Repository checks must never fail package analysis")]
        public async Task<RepositoryStatus> GetRepositoryStatus(
            string packageName,
            NuGetVersion packageVersion,
            IEnumerable<Uri> sources,
            string projectFilePath)
        {
            if (packageVersion == null)
                return RepositoryStatus.Unknown;

            try
            {
                string repositoryUrl = await _packageInfoService.GetRepositoryUrl(
                    packageName,
                    packageVersion,
                    sources,
                    projectFilePath).ConfigureAwait(false);

                return await GetRepositoryStatus(repositoryUrl).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return RepositoryStatus.Unknown;
            }
        }

        internal Task<RepositoryStatus> GetRepositoryStatus(string repositoryUrl)
        {
            // Issue #200 intentionally supports GitHub only; other hosts need provider-specific status semantics and APIs.
            if (!TryGetGitHubRepository(repositoryUrl, out string owner, out string repository))
                return Task.FromResult(RepositoryStatus.Unknown);

            string cacheKey = $"{owner}/{repository}";
            var request = new Lazy<Task<RepositoryStatus>>(() => GetGitHubRepositoryStatus(owner, repository));
            return _statusRequests.GetOrAdd(cacheKey, request).Value;
        }

        internal static bool TryGetGitHubRepository(string repositoryUrl, out string owner, out string repository)
        {
            owner = null;
            repository = null;

            if (string.IsNullOrWhiteSpace(repositoryUrl))
                return false;

            string scpPrefix = "git@github.com:";
            if (repositoryUrl.StartsWith(scpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return TryGetOwnerAndRepository(repositoryUrl[scpPrefix.Length..], out owner, out repository);
            }

            if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) ||
                !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return TryGetOwnerAndRepository(uri.AbsolutePath, out owner, out repository);
        }

        private static bool TryGetOwnerAndRepository(string path, out string owner, out string repository)
        {
            owner = null;
            repository = null;

            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                return false;

            owner = segments[0];
            repository = segments[1];
            if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                repository = repository[..^4];

            return owner.Length > 0 && repository.Length > 0;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Repository checks must never fail package analysis")]
        private async Task<RepositoryStatus> GetGitHubRepositoryStatus(string owner, string repository)
        {
            try
            {
                var apiUrl = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}");
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.UserAgent.ParseAdd("dotnet-outdated");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return RepositoryStatus.NotFound;
                if (response.StatusCode != HttpStatusCode.OK)
                    return RepositoryStatus.Unknown;

                await using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(content).ConfigureAwait(false);
                if (!document.RootElement.TryGetProperty("archived", out var archived) ||
                    (archived.ValueKind != JsonValueKind.True && archived.ValueKind != JsonValueKind.False))
                {
                    return RepositoryStatus.Unknown;
                }

                return archived.GetBoolean() ? RepositoryStatus.Archived : RepositoryStatus.Active;
            }
            catch (Exception)
            {
                return RepositoryStatus.Unknown;
            }
        }
    }
}
