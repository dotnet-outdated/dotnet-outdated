using DotNetOutdated.Models;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DotNetOutdated;

internal delegate bool OutputFormatterFactory(string name, out IOutputFormatter formatter);

internal interface IOutputFormatter
{
    Task FormatAsync(IReadOnlyList<AnalyzedProject> projects, IDictionary<string,string> options);
}
