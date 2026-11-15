using System;
using System.IO;

#nullable enable

namespace DotNetOutdated.Services
{
    /// <summary>
    /// The console for the mcp subcommand, where standard output carries the protocol: everything the tool would print
    /// goes to standard error instead, without colours.
    /// </summary>
    public class McpConsole : IConsole
    {
        public static McpConsole Singleton { get; } = new McpConsole();

        public TextWriter Out => Console.Error;

        public TextWriter Error => Console.Error;

        public TextReader In => Console.In;

        public ConsoleColor ForegroundColor
        {
            get => Console.ForegroundColor;
            set { } 
        }

        public ConsoleColor BackgroundColor
        {
            get => Console.BackgroundColor;
            set { }
        }

        public void ResetColor() { }
    }
}
