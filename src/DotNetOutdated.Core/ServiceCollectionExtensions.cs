using System.IO.Abstractions;
using DotNetOutdated.Core.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NuGet.Common;

namespace Microsoft.Extensions.DependencyInjection;

public static class DotNetOutdatedServiceCollectionExtensions
{
    /// <summary>
    /// Registers the dotnet-outdated services: <see cref="IProjectDiscoveryService"/>, <see cref="IProjectAnalysisService"/>,
    /// <see cref="IOutdatedDependencyService"/>, <see cref="IDotNetPackageService"/>, <see cref="INuGetPackageResolutionService"/>
    /// and <see cref="INuGetPackageInfoService"/>, as singletons.
    /// </summary>
    /// <remarks>
    /// Services that are already registered are kept, so register a replacement (for example an
    /// <see cref="INuGetPackageInfoService"/> backed by a test feed) before calling this. Also used when registered:
    /// <see cref="IFileSystem"/> (the real file system by default), a NuGet <see cref="ILogger"/> (none by default),
    /// an <see cref="IWarningReporter"/> (warnings are dropped by default), and <see cref="DotNetRunnerOptions"/>.
    /// </remarks>
    public static IServiceCollection AddDotNetOutdated(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IFileSystem, FileSystem>();
        services.TryAddSingleton<ILogger>(NullLogger.Instance);
        services.TryAddSingleton<DotNetRunnerOptions>();

        services.TryAddSingleton<IDotNetRunner, DotNetRunner>();
        services.TryAddSingleton<IDotNetRestoreService, DotNetRestoreService>();
        services.TryAddSingleton<IProjectEvaluator>(provider => new DotNetProjectEvaluator(
            new DependencyGraphService(provider.GetRequiredService<IDotNetRunner>(), provider.GetRequiredService<IFileSystem>()),
            provider.GetRequiredService<IFileSystem>()));

        services.TryAddSingleton<IProjectDiscoveryService, ProjectDiscoveryService>();
        services.TryAddSingleton<IProjectAnalysisService>(provider => new ProjectAnalysisService(
            provider.GetRequiredService<IProjectEvaluator>(),
            provider.GetRequiredService<IDotNetRestoreService>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<ILogger>()));
        services.TryAddSingleton<IDotNetPackageService>(provider => new DotNetPackageService(
            provider.GetRequiredService<IDotNetRunner>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IProjectEvaluator>(),
            provider.GetService<IWarningReporter>() is { } warnings ? warnings.Warn : null));
        services.TryAddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>();
        services.TryAddSingleton<INuGetPackageResolutionService, NuGetPackageResolutionService>();
        services.TryAddSingleton<IOutdatedDependencyService, OutdatedDependencyService>();

        return services;
    }
}
