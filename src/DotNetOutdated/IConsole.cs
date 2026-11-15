using System;
using System.IO;

namespace DotNetOutdated
{
   /// <summary>
   /// The console the tool reads from and writes its report to; <see cref="PhysicalConsole"/> in normal use.
   /// </summary>
   public interface IConsole
   {
      TextWriter Out { get; }

      TextWriter Error { get; }

      TextReader In { get; }

      ConsoleColor ForegroundColor { get; set; }

      ConsoleColor BackgroundColor { get; set; }

      void ResetColor();
   }
}
