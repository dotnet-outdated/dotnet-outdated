using System;
using System.IO;

namespace DotNetOutdated
{
   /// <summary>
   /// Reports warnings and errors that are not part of the report itself.
   /// </summary>
   public interface IReporter
   {
      /// <summary>Writes a warning to standard output, in yellow.</summary>
      void Warn(string message);

      /// <summary>Writes an error to standard error, in red.</summary>
      void Error(string message);
   }

   public sealed class ConsoleReporter(IConsole console) : IReporter
   {
      private readonly IConsole _console = console ?? throw new ArgumentNullException(nameof(console));
      private readonly object _writeLock = new();

      public void Warn(string message) => WriteLine(_console.Out, message, ConsoleColor.Yellow);

      public void Error(string message) => WriteLine(_console.Error, message, ConsoleColor.Red);

      private void WriteLine(TextWriter writer, string message, ConsoleColor color)
      {
         // Warnings come from parallel project analysis, so a message and its colour change must not interleave.
         lock (_writeLock)
         {
            _console.ForegroundColor = color;
            writer.WriteLine(message);
            _console.ResetColor();
         }
      }
   }
}
