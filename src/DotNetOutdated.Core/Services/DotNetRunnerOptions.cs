using System;

namespace DotNetOutdated.Core.Services
{
    public class DotNetRunnerOptions
    {
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
    }
}
