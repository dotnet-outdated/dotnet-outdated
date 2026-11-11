namespace DotNetOutdated.Core.Services
{
    internal interface IDotNetRunner
    {
        RunStatus Run(string? workingDirectory, string[] arguments);
    }
}
