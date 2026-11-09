using System;
using System.IO;

namespace DotNetOutdated
{
   /// <summary>
   /// The process's own console, <see cref="System.Console"/>.
   /// </summary>
   public sealed class PhysicalConsole : IConsole
   {
      public static IConsole Singleton { get; } = new PhysicalConsole();

      private PhysicalConsole()
      {
      }

      public TextWriter Out => Console.Out;

      public TextWriter Error => Console.Error;

      public TextReader In => Console.In;

      public ConsoleColor ForegroundColor
      {
         get => Console.ForegroundColor;
         set => Console.ForegroundColor = value;
      }

      public ConsoleColor BackgroundColor
      {
         get => Console.BackgroundColor;
         set => Console.BackgroundColor = value;
      }

      public void ResetColor() => Console.ResetColor();
   }
}
