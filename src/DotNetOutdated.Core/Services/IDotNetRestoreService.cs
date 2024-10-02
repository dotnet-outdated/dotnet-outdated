using System;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetRestoreService
    {
        RunStatus Restore(string projectPath, TimeSpan timeout);
    }
}