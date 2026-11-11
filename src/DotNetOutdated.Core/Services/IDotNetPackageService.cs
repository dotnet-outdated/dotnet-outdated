namespace DotNetOutdated.Core.Services
{
    public interface IDotNetPackageService
    {
        /// <summary>
        /// Sets the version of a project's package or SDK reference wherever that version is written: the project,
        /// <c>Directory.Packages.props</c>, an MSBuild property, an <c>Sdk</c> declaration, or a file-based app directive.
        /// Property references and the files' formatting are kept.
        /// </summary>
        RunStatus Upgrade(ReferenceUpgrade upgrade);

        RunStatus RemovePackage(string projectPath, string packageName);
    }
}
