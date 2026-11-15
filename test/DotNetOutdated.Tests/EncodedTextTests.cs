using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using DotNetOutdated.Core.Services;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class EncodedTextTests
    {
        private readonly string _path = XFS.Path(@"c:\repo\Directory.Packages.props");

        public static TheoryData<byte[]> RoundTrippedEncodings => new()
        {
            WithPreamble(new UTF8Encoding(false), "<Project>\r\n</Project>"),
            WithPreamble(Encoding.UTF8, "<Project>\n</Project>"),
            WithPreamble(Encoding.Unicode, "<Project>\n</Project>"),
            WithPreamble(Encoding.BigEndianUnicode, "<Project>\n</Project>"),
        };

        private static byte[] WithPreamble(Encoding encoding, string content) => [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];

        [Theory]
        [MemberData(nameof(RoundTrippedEncodings))]
        public void Write_KeepsTheEncodingAndByteOrderMark(byte[] original)
        {
            var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData> { [_path] = new(original) });
            var text = EncodedText.Read(fileSystem, _path);

            text.Write(fileSystem, _path, text.Content);

            Assert.Equal(original, fileSystem.File.ReadAllBytes(_path));
        }

        [Fact]
        public void TextThatCannotBeDecodedLosslessly_IsWrittenAsUtf8WithoutAByteOrderMark()
        {
            // 0xFF is not valid UTF-8, so decoding replaces it and re-encoding would not give the original bytes.
            // The real file system is used because MockFileSystem's ReadAllText throws on invalid UTF-8.
            var fileSystem = new FileSystem();
            var path = Path.Combine(Path.GetTempPath(), "dotnet-outdated-encoded-text-" + Guid.NewGuid().ToString("N") + ".props");
            File.WriteAllBytes(path, [(byte)'<', 0xFF, (byte)'>']);
            try
            {
                var text = EncodedText.Read(fileSystem, path);

                Assert.Null(text.Encoding);
                Assert.Equal("<�>", text.Content);
                Assert.Equal(Encoding.UTF8.GetBytes("<é>"), text.Encode("<é>"));

                text.Write(fileSystem, path, "<é>");

                Assert.Equal(Encoding.UTF8.GetBytes("<é>"), File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
