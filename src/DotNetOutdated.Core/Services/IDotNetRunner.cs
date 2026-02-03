using System;

namespace DotNetOutdated.Core.Services
{
    public interface IDotNetRunner
    {
        TimeSpan MinTimeout { get; set; }
        
        RunStatus Run(string workingDirectory, string[] arguments);
    }
}