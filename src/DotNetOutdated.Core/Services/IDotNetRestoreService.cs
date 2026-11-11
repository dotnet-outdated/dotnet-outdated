namespace DotNetOutdated.Core.Services
{
    internal interface IDotNetRestoreService
    {
        RunStatus Restore(string projectPath);
    }
}