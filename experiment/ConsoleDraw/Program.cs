using System;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleDraw
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine($"{Console.WindowWidth} x {Console.WindowHeight}");
            Console.WriteLine($"{Console.BufferWidth} x {Console.BufferHeight}");
            Console.WriteLine($"{Console.CursorTop}:{Console.CursorLeft}");
            
            int startRow = Console.CursorTop;
            Console.CursorVisible = false;

            for (int i = 0; i < 600; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    Console.WriteLine($"Hello world {i}");
                }

                Console.WriteLine("last one...");

                await Task.Delay(100);

                int currentRow = Console.CursorTop;
                Console.CursorTop = startRow;
                while (Console.CursorTop < currentRow)
                {
                    ClearCurrentConsoleLine();
                    Console.CursorTop = Console.CursorTop + 1;
                }

                Console.CursorTop = startRow;
            }

            Console.CursorVisible = true;
        }
        
        public static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.BufferWidth)); 
            Console.SetCursorPosition(0, currentLineCursor);
        }
    }
}