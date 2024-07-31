using System;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetRunner
    {
        RunStatus Run(string workingDirectory, string[] arguments, TimeSpan timeout);
    }
}