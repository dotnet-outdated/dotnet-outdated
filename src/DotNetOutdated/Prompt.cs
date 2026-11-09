using System;

namespace DotNetOutdated
{
   /// <summary>
   /// Asks the user a question on the console.
   /// </summary>
   public static class Prompt
   {
      /// <summary>
      /// Asks a yes/no question until it gets an answer: "y" or "yes", "n" or "no", in any case. An empty answer, or
      /// the end of the input, picks <paramref name="defaultAnswer"/>.
      /// </summary>
      public static bool GetYesNo(IConsole console, string prompt, bool defaultAnswer)
      {
         ArgumentNullException.ThrowIfNull(console);

         var answerHint = defaultAnswer ? "[Y/n]" : "[y/N]";

         while (true)
         {
            console.Write($"{prompt} {answerHint} ");

            string response;
            using (ShowCursor(console))
            {
               response = console.In.ReadLine()?.ToLowerInvariant().Trim();
            }

            switch (response)
            {
               case null or "":
                  return defaultAnswer;
               case "y" or "yes":
                  return true;
               case "n" or "no":
                  return false;
               default:
                  console.WriteLine($"Invalid response '{response}'. Please answer 'y' or 'n' or CTRL+C to exit.");
                  break;
            }
         }
      }

      /// <summary>
      /// Makes the cursor visible while the user types, then restores it. Only the real console has a cursor.
      /// </summary>
      private static CursorVisibility ShowCursor(IConsole console) =>
          console is PhysicalConsole ? new CursorVisibility() : null;

      private sealed class CursorVisibility : IDisposable
      {
         private readonly bool _original = true;

         public CursorVisibility()
         {
            try
            {
#pragma warning disable CA1416 // Reading CursorVisible is Windows-only; elsewhere it throws and the cursor is assumed visible.
               _original = Console.CursorVisible;
#pragma warning restore CA1416
            }
            catch (Exception e) when (e is PlatformNotSupportedException or System.IO.IOException)
            {
            }

            TrySetVisible(true);
         }

         public void Dispose() => TrySetVisible(_original);

         private static void TrySetVisible(bool visible)
         {
            try
            {
               Console.CursorVisible = visible;
            }
            catch (Exception e) when (e is PlatformNotSupportedException or System.IO.IOException)
            {
               // Output is redirected, or the platform does not support it.
            }
         }
      }
   }
}
