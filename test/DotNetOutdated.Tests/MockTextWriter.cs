using System.IO;
using System.Text;

namespace DotNetOutdated.Tests
{
    internal sealed class MockTextWriter : TextWriter
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
