using McMaster.Extensions.CommandLineUtils;
using System;
using System.IO;

#nullable enable

namespace DotNetOutdated.Services
{
    public class McpConsole : IConsole
    {
        public static McpConsole Singleton { get; } = new McpConsole();

        public TextWriter Out => Console.Error;

        public TextWriter Error => Console.Error;

        public TextReader In => Console.In;

        public bool IsInputRedirected => Console.IsInputRedirected;

        public bool IsOutputRedirected => Console.IsOutputRedirected;

        public bool IsErrorRedirected => Console.IsErrorRedirected;

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

        public event ConsoleCancelEventHandler? CancelKeyPress
        {
            add => Console.CancelKeyPress += value;
            remove => Console.CancelKeyPress -= value;
        }

        public void ResetColor() { }
    }
}
