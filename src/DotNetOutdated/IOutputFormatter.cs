using DotNetOutdated.Models;
using System.Collections.Generic;
using System.IO;

namespace DotNetOutdated;

internal delegate bool OutputFormatterFactory(string name, out IOutputFormatter formatter);

internal interface IOutputFormatter
{
    void Format(IReadOnlyList<AnalyzedProject> projects, IDictionary<string,string> options);
}
