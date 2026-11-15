using System;

namespace DotNetOutdated
{
    public static class ConsoleExtensions
    {
        public static void Write(this IConsole console, string value)
        {
            ArgumentNullException.ThrowIfNull(console);
            console.Out.Write(value);
        }

        public static void Write(this IConsole console, object value)
        {
            ArgumentNullException.ThrowIfNull(console);
            console.Out.Write(value);
        }

        public static void WriteLine(this IConsole console)
        {
            ArgumentNullException.ThrowIfNull(console);
            console.Out.WriteLine();
        }

        public static void WriteLine(this IConsole console, string value)
        {
            ArgumentNullException.ThrowIfNull(console);
            console.Out.WriteLine(value);
        }

        public static void WriteLine(this IConsole console, object value)
        {
            ArgumentNullException.ThrowIfNull(console);
            console.Out.WriteLine(value);
        }

        public static void Write(this IConsole console, object value, ConsoleColor color)
        {
            ArgumentNullException.ThrowIfNull(console);

            ConsoleColor currentColor = console.ForegroundColor;

            console.ForegroundColor = color;
            console.Write(value);
            console.ForegroundColor = currentColor;
        }

        public static void Write(this IConsole console, object value, ConsoleColor foregroundColor, ConsoleColor backgroundColor)
        {
            ArgumentNullException.ThrowIfNull(console);

            ConsoleColor currentForegroundColor = console.ForegroundColor;
            ConsoleColor currentBackgroundColor = console.BackgroundColor;

            console.ForegroundColor = foregroundColor;
            console.BackgroundColor = backgroundColor;
            console.Write(value);
            console.ForegroundColor = currentForegroundColor;
            console.BackgroundColor = currentBackgroundColor;
        }

        public static void WriteLine(this IConsole console, object value, ConsoleColor color)
        {
            ArgumentNullException.ThrowIfNull(console);

            ConsoleColor currentColor = console.ForegroundColor;

            console.ForegroundColor = color;
            console.WriteLine(value);
            console.ForegroundColor = currentColor;
        }

        public static void WriteIndent(this IConsole console)
        {
            console.Write(new String(' ', 2));
        }
    }
}
