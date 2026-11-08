using System;

namespace DotNetOutdated.Core.Services
{
    public class DotNetRunnerOptions
    {
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>The executable to run. Only tests change this, to run a controlled process.</summary>
        internal string FileName { get; set; } = "dotnet";
    }
}
