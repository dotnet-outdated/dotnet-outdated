namespace DotNetOutdated.Services
{
    internal interface IDotNetRestoreService
    {
        RunStatus Restore(string projectPath);
    }
}