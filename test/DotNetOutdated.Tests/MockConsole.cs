using System;
using System.IO;

namespace DotNetOutdated.Tests
{
    public sealed class MockConsole : IConsole, IDisposable
    {
        private readonly MockTextWriter _out;
        private readonly MockTextWriter _error;
        private readonly TextReader _in;

        /// <param name="input">What the console reads, for a test that answers prompts; reading throws without it.</param>
        public MockConsole(string input = null)
        {
            _in = input == null ? null : new StringReader(input);
            _out = new MockTextWriter();
            _error = new MockTextWriter();
        }

        public string WrittenOut => _out.Contents;

        public TextWriter Out => _out;

        public TextWriter Error => _error;

        public TextReader In => _in ?? throw new NotSupportedException();

        private ConsoleColor _foreground = ConsoleColor.White;

        public ConsoleColor ForegroundColor
        {
            get => _foreground; set
            {
                _foreground = value;
                _out.Write($"[{value}]");
            }
        }

        public ConsoleColor BackgroundColor { get; set; }

        public void ResetColor()
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            using (_out)
            {
            }
            using (_error)
            {
            }
        }
    }
}
