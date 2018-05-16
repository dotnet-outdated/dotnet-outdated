using System;
using McMaster.Extensions.CommandLineUtils;

namespace DotNetOutdated
{
    public static class ConsoleExtensions
    {
        public static void Write(this IConsole console, object value, ConsoleColor color)
        {
            ConsoleColor currentColor = console.ForegroundColor;
            
            console.ForegroundColor = color;
            console.Write(value);
            console.ForegroundColor = currentColor;
        }
        
        public static void WriteHeader(this IConsole console, string value)
        {
            console.Write($"» {value}", ConsoleColor.DarkYellow);
            console.WriteLine();
        }
    }
}