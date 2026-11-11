namespace DotNetOutdated.Core.Services;

/// <summary>
/// Receives warnings that don't stop an operation, such as a version variable that is replaced by a literal version
/// because its expression can't be kept.
/// </summary>
public interface IWarningReporter
{
    void Warn(string message);
}
