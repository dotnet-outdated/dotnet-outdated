using System;
using System.IO;
using System.Text;

namespace DotNetOutdated.Tests;

/// <summary>
/// A console that records, in order, what is written to each stream and every colour change, as text such as
/// <c>[fg:Red]{err}boom\n[reset]</c>. Output starts on standard output; <c>{err}</c> and <c>{out}</c> mark a switch.
/// Lines end in <c>\n</c> on every platform.
/// </summary>
internal sealed class RecordingConsole : IConsole
{
    private readonly StringBuilder _transcript = new();
    private string _stream = "out";

    public RecordingConsole(string input = "")
    {
        In = new StringReader(input);
        Out = new Writer(this, "out");
        Error = new Writer(this, "err");
    }

    public string Transcript => _transcript.ToString();

    public TextWriter Out { get; }

    public TextWriter Error { get; }

    public TextReader In { get; }

    public ConsoleColor ForegroundColor
    {
        get => ConsoleColor.Gray;
        set => _transcript.Append($"[fg:{value}]");
    }

    public ConsoleColor BackgroundColor
    {
        get => ConsoleColor.Black;
        set => _transcript.Append($"[bg:{value}]");
    }

    public void ResetColor() => _transcript.Append("[reset]");

    private void Append(string stream, char value)
    {
        if (stream != _stream)
        {
            _transcript.Append($"{{{stream}}}");
            _stream = stream;
        }

        _transcript.Append(value);
    }

    private sealed class Writer : TextWriter
    {
        private readonly RecordingConsole _console;
        private readonly string _stream;

        public Writer(RecordingConsole console, string stream)
        {
            _console = console;
            _stream = stream;
            NewLine = "\n";
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => _console.Append(_stream, value);
    }
}
