using DotNetOutdated.Models;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DotNetOutdated;

internal interface IOutputFormatter
{
    Task FormatAsync(IReadOnlyList<AnalyzedProject> projects,TextWriter writer);
}
