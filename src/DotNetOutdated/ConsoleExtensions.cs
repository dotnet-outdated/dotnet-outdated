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
        
        public static void Write(this IConsole console, object value, ConsoleColor foregroundColor, ConsoleColor backgroundColor)
        {
            ConsoleColor currentForegroundColor = console.ForegroundColor;
            ConsoleColor currentBackgroundColor = console.BackgroundColor;
            
            console.ForegroundColor = foregroundColor;
            console.BackgroundColor = backgroundColor;
            console.Write(value);
            console.ForegroundColor = currentForegroundColor;
            console.BackgroundColor = currentBackgroundColor;
        }
        
        public static void WriteIndent(this IConsole console, int level)
        {
            console.Write(new String(' ', level * 2));
        }
    }
}