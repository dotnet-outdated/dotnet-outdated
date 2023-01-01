using McMaster.Extensions.CommandLineUtils;
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

        public bool IsInputRedirected => throw new NotSupportedException();

        public bool IsOutputRedirected => true;

        public bool IsErrorRedirected => true;

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

        // build warning because it is not used
#pragma warning disable 67

        public event ConsoleCancelEventHandler CancelKeyPress;

#pragma warning restore 67

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
