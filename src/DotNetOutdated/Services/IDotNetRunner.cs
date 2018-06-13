namespace DotNetOutdated.Services
{
    public interface IDotNetRunner
    {
        RunStatus Run(string workingDirectory, string[] arguments);
    }
}