using DotNetOutdated.Core.Models;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DotNetOutdated;

internal interface IOutputFormatter
{
    /// <summary>Writes the projects of an outdated report, as the command line shows them, to a report file.</summary>
    Task FormatAsync(IReadOnlyList<OutdatedProject> projects, TextWriter writer);
}
