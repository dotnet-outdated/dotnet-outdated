using System.Collections.Generic;

namespace DotNetOutdated.Core.Services;

public interface IProjectDiscoveryService
{
    IList<string> DiscoverProjects(string path, bool recursive = false);
}