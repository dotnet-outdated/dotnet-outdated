using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McMaster.Extensions.CommandLineUtils;
using Xunit.Abstractions;

namespace DotNetOutdated.Tests
{
    public class MockConsole : IConsole
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

        public TextReader In => throw new NotImplementedException();

        public bool IsInputRedirected => throw new NotImplementedException();

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

        public event ConsoleCancelEventHandler CancelKeyPress;

        public void ResetColor()
        {
            throw new NotImplementedException();
        }

    }

    internal class MockTextWriter : TextWriter
    {
        private readonly StringBuilder _sb;

        public MockTextWriter()
        {
            _sb = new StringBuilder();
        }

        public override void Write(char c)
        {
            _sb.Append(c);
        }

        public string Contents => _sb.ToString();

        public override Encoding Encoding => Encoding.Unicode;
    }
}
