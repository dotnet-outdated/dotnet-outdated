using DotNetOutdated.Core.Models;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DotNetOutdated.Core.Services
{
    public interface IRepositoryStatusService
    {
        Task<RepositoryStatus> GetRepositoryStatus(
            string packageName,
            NuGetVersion packageVersion,
            IEnumerable<Uri> sources,
            string projectFilePath);
    }
}
