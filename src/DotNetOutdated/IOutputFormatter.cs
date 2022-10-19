using DotNetOutdated.Models;
using System.Collections.Generic;
using System.IO;

namespace DotNetOutdated;

internal interface IOutputFormatter
{
    void Format(IReadOnlyList<AnalyzedProject> projects,TextWriter writer);
}
