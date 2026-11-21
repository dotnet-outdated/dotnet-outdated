#nullable enable

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace DotNetOutdated.Tests
{
    // A file system with faults: files that can't be read, files whose content changes once read as text (as if
    // edited since), and directories that can't be reached though their parent lists them. Text that isn't valid
    // UTF-8 is read as File.ReadAllText reads it, with replacement characters.
    internal sealed class FaultyFileSystem : MockFileSystem
    {
        private FaultyFile? _file;
        private FaultyDirectory? _directory;

        public Func<string, bool> Unreadable { get; init; } = _ => false;

        public Func<string, string?> ChangedContent { get; init; } = _ => null;

        public Func<string, bool> Unreachable { get; init; } = _ => false;

        // A file saved with new content (as by an editor) just before it is read as bytes for the nth time.
        public (string Path, int Read, string Content)? EditedBeforeRead { get; init; }

        private readonly Dictionary<string, int> _reads = [];

        public override IFile File => _file ??= new FaultyFile(this);

        public override IDirectory Directory => _directory ??= new FaultyDirectory(this, base.Directory.GetCurrentDirectory());

        private sealed class FaultyFile(FaultyFileSystem fileSystem) : MockFile(fileSystem)
        {
            public override string ReadAllText(string path)
            {
                Check(path);
                return System.Text.Encoding.UTF8.GetString(base.ReadAllBytes(path));
            }

            public override byte[] ReadAllBytes(string path)
            {
                Check(path);
                var reads = fileSystem._reads[path] = fileSystem._reads.GetValueOrDefault(path) + 1;
                if (fileSystem.EditedBeforeRead is { } edit && edit.Path == path && edit.Read == reads)
                {
                    base.WriteAllText(path, edit.Content);
                }

                return fileSystem.ChangedContent(path) is { } changed ? System.Text.Encoding.UTF8.GetBytes(changed) : base.ReadAllBytes(path);
            }

            private void Check(string path)
            {
                if (fileSystem.Unreadable(path))
                {
                    throw new UnauthorizedAccessException($"Access to '{path}' is denied.");
                }
            }
        }

        private sealed class FaultyDirectory(FaultyFileSystem fileSystem, string currentDirectory) : MockDirectory(fileSystem, currentDirectory)
        {
            public override bool Exists(string? path) => path != null && !fileSystem.Unreachable(path) && base.Exists(path);
        }
    }
}
