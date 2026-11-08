using System;
using System.IO;
using System.IO.Abstractions;
using System.Text;

namespace DotNetOutdated.Core.Services
{
    /// <summary>
    /// The text of a file together with the encoding and byte order mark it was stored with, so it can be
    /// written back in the same form.
    /// </summary>
    internal sealed class EncodedText
    {
        private EncodedText(string content, Encoding encoding, byte[] preamble)
        {
            Content = content;
            Encoding = encoding;
            Preamble = preamble;
        }

        public string Content { get; }

        public Encoding Encoding { get; }

        public byte[] Preamble { get; }

        /// <summary>
        /// Decodes <paramref name="bytes"/> using its byte order mark (UTF-8 when there is none).
        /// Returns null when re-encoding the text would not reproduce the original bytes.
        /// </summary>
        public static EncodedText TryDecode(byte[] bytes)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
            var content = reader.ReadToEnd();
            var encoding = reader.CurrentEncoding;
            var preamble = encoding.GetPreamble();
            if (!bytes.AsSpan().StartsWith(preamble))
            {
                preamble = [];
            }

            return encoding.GetBytes(content).AsSpan().SequenceEqual(bytes.AsSpan(preamble.Length))
                ? new EncodedText(content, encoding, preamble)
                : null;
        }

        /// <summary>
        /// Reads a file's text and how it was encoded. When the bytes cannot be decoded losslessly, the text is
        /// read as <see cref="IFile.ReadAllText(string)"/> would and <see cref="Write"/> falls back to UTF-8.
        /// </summary>
        public static EncodedText Read(IFileSystem fileSystem, string path) =>
            TryDecode(fileSystem.File.ReadAllBytes(path))
            ?? new EncodedText(fileSystem.File.ReadAllText(path), null, []);

        public byte[] Encode(string content) => [.. Preamble, .. Encoding.GetBytes(content)];

        /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> with this text's encoding and byte order mark.</summary>
        public void Write(IFileSystem fileSystem, string path, string content)
        {
            if (Encoding == null)
            {
                fileSystem.File.WriteAllText(path, content);
                return;
            }

            fileSystem.File.WriteAllBytes(path, Encode(content));
        }
    }
}
