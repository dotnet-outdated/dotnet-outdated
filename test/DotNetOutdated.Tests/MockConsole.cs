using System;
using System.IO;

namespace DotNetOutdated.Tests
{
    public sealed class MockConsole : IConsole, IDisposable
    {
        private readonly MockTextWriter _out;
        private readonly MockTextWriter _error;

        public MockConsole()
        {
            _out = new MockTextWriter();
            _error = new MockTextWriter();
        }

        public string WrittenOut => _out.Contents;

        public TextWriter Out => _out;

        public TextWriter Error => _error;

        public TextReader In => throw new NotSupportedException();

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
