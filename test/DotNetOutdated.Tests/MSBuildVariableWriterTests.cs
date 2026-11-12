using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using DotNetOutdated.Core.Services;
using NuGet.Versioning;
using Xunit;
using XFS = System.IO.Abstractions.TestingHelpers.MockUnixSupport;

namespace DotNetOutdated.Tests
{
    public class MSBuildVariableWriterTests
    {
        private static readonly string ProjectPath = XFS.Path(@"c:\repo\app\app.csproj");
        private static readonly string PropsPath = XFS.Path(@"c:\repo\Directory.Build.props");

        // As `dotnet add package` leaves it: the new version written as a literal.
        private const string LiteralReference =
            "<Project><ItemGroup><PackageReference Include=\"A\" Version=\"2.0.0\" /></ItemGroup></Project>";

        private const string RestoredReference =
            "<Project><ItemGroup><PackageReference Include=\"A\" Version=\"$(AVersion)\" /></ItemGroup></Project>";

        private readonly List<string> _warnings = [];

        private MockFileSystem FileSystem(string props) => new(new Dictionary<string, MockFileData>
        {
            [ProjectPath] = new(LiteralReference),
            [PropsPath] = new(props),
        });

        private static PackageVariableInfo Info(string variableValue = "1.0.0", int propertyElementIndex = -1, bool insertProperty = false) => new()
        {
            PackageName = "A",
            VariableName = "AVersion",
            VariableValue = variableValue,
            FilePath = PropsPath,
            PackageReferenceFilePath = ProjectPath,
            ElementType = "PackageReference",
            PropertyElementIndex = propertyElementIndex,
            InsertProperty = insertProperty,
        };

        private bool Update(MockFileSystem fileSystem, PackageVariableInfo info, string newVersion = "2.0.0") =>
            new MSBuildVariableWriter(fileSystem, _warnings.Add).TryUpdate(info, VersionRange.Parse(newVersion));

        // ── IsAtVersionRange ──────────────────────────────────────────────────────

        [Theory]
        [InlineData("", "1.0.0", "1.0.0", true)]
        [InlineData("", "1.0.0", "2.0.0", false)]
        [InlineData("$(AVersion)", "[1.0.0, 2.0.0)", "[1.0.0, 2.0.0)", true)]
        [InlineData("[$(AVersion), 2.0.0)", "1.0.0", "[1.0.0, 2.0.0)", true)]
        [InlineData("[$(AVersion), 2.0.0)", "1.0.0", "1.0.0", false)]
        public void IsAtVersionRange_EvaluatesTheReferenceExpression(string referenceVersion, string variableValue, string range, bool expected)
        {
            var info = Info(variableValue);
            info.PackageReferenceVersion = referenceVersion;

            Assert.Equal(expected, MSBuildVariableWriter.IsAtVersionRange(info, VersionRange.Parse(range)));
        }

        // ── Where the property is written ─────────────────────────────────────────

        [Fact]
        public void InsertProperty_AlreadyDefinedAtTopLevel_UpdatesThatDefinition()
        {
            var fileSystem = FileSystem("<Project><PropertyGroup><AVersion>1.0.0</AVersion></PropertyGroup></Project>");

            Assert.True(Update(fileSystem, Info(insertProperty: true)));

            Assert.Equal("<Project><PropertyGroup><AVersion>2.0.0</AVersion></PropertyGroup></Project>", fileSystem.File.ReadAllText(PropsPath));
            Assert.Equal(RestoredReference, fileSystem.File.ReadAllText(ProjectPath));
            Assert.Empty(_warnings);
        }

        [Theory]
        [InlineData(true, "<Project><PropertyGroup><AVersion>2.0.0</AVersion></PropertyGroup></Project>")]
        [InlineData(true, "<Project><PropertyGroup><AVersion>2&#46;0&#46;0</AVersion></PropertyGroup></Project>")]
        [InlineData(false, "<Project><PropertyGroup Condition=\"'$(C)' == 'b'\"><AVersion>2&#46;0&#46;0</AVersion></PropertyGroup></Project>")]
        public void DefinitionAlreadyAtNewValue_OnlyRestoresTheReference(bool insertProperty, string props)
        {
            var fileSystem = FileSystem(props);

            Assert.True(Update(fileSystem, Info(insertProperty: insertProperty)));

            Assert.Equal(props, fileSystem.File.ReadAllText(PropsPath));
            Assert.Equal(RestoredReference, fileSystem.File.ReadAllText(ProjectPath));
            Assert.Empty(_warnings);
        }

        [Theory]
        // Definitions in a conditional group or one with its own condition don't count as the top-level definition.
        [InlineData(
            "<Project><PropertyGroup><X>1</X></PropertyGroup><PropertyGroup Condition=\"'$(C)' == 'true'\"><AVersion>1.0.0</AVersion></PropertyGroup></Project>",
            "<Project><PropertyGroup><X>1</X><AVersion>2.0.0</AVersion></PropertyGroup><PropertyGroup Condition=\"'$(C)' == 'true'\"><AVersion>1.0.0</AVersion></PropertyGroup></Project>")]
        [InlineData(
            "<Project><PropertyGroup><AVersion Condition=\"'$(C)' == 'true'\">1.0.0</AVersion></PropertyGroup></Project>",
            "<Project><PropertyGroup><AVersion Condition=\"'$(C)' == 'true'\">1.0.0</AVersion><AVersion>2.0.0</AVersion></PropertyGroup></Project>")]
        [InlineData(
            "<Project><Target Name=\"T\"><PropertyGroup><AVersion>1.0.0</AVersion></PropertyGroup></Target></Project>",
            "<Project><PropertyGroup><AVersion>2.0.0</AVersion></PropertyGroup><Target Name=\"T\"><PropertyGroup><AVersion>1.0.0</AVersion></PropertyGroup></Target></Project>")]
        public void InsertProperty_OnlyConditionalDefinitions_InsertsUnconditionalDefinition(string props, string expected)
        {
            var fileSystem = FileSystem(props);

            Assert.True(Update(fileSystem, Info(insertProperty: true)));

            Assert.Equal(expected, fileSystem.File.ReadAllText(PropsPath));
            Assert.Empty(_warnings);
        }

        [Fact]
        public void UnknownDefinition_OnlyConditionalDefinitions_UpdatesTheLast()
        {
            var fileSystem = FileSystem(
                "<Project><PropertyGroup Condition=\"'$(C)' == 'a'\"><AVersion>1.0.0</AVersion></PropertyGroup>" +
                "<PropertyGroup Condition=\"'$(C)' == 'b'\"><AVersion>1.0.0</AVersion></PropertyGroup></Project>");

            Assert.True(Update(fileSystem, Info()));

            Assert.Equal(
                "<Project><PropertyGroup Condition=\"'$(C)' == 'a'\"><AVersion>1.0.0</AVersion></PropertyGroup>" +
                "<PropertyGroup Condition=\"'$(C)' == 'b'\"><AVersion>2.0.0</AVersion></PropertyGroup></Project>",
                fileSystem.File.ReadAllText(PropsPath));
            Assert.Empty(_warnings);
        }

        [Fact]
        public void UnknownDefinition_NotDefined_InsertsIt()
        {
            var fileSystem = FileSystem("<Project>\n  <PropertyGroup>\n    <X>1</X>\n  </PropertyGroup>\n</Project>");

            Assert.True(Update(fileSystem, Info()));

            Assert.Equal(
                "<Project>\n  <PropertyGroup>\n    <X>1</X>\n    <AVersion>2.0.0</AVersion>\n  </PropertyGroup>\n</Project>",
                fileSystem.File.ReadAllText(PropsPath));
            Assert.Equal(RestoredReference, fileSystem.File.ReadAllText(ProjectPath));
            Assert.Empty(_warnings);
        }

        // ── Known definition (PropertyElementIndex) ───────────────────────────────

        [Fact]
        public void KnownDefinition_AlreadyAtNewValue_OnlyRestoresTheReference()
        {
            const string props = "<Project><PropertyGroup><AVersion>2.0.0</AVersion></PropertyGroup></Project>";
            var fileSystem = FileSystem(props);

            Assert.True(Update(fileSystem, Info(variableValue: "1.0.0", propertyElementIndex: 0)));

            Assert.Equal(props, fileSystem.File.ReadAllText(PropsPath));
            Assert.Equal(RestoredReference, fileSystem.File.ReadAllText(ProjectPath));
            Assert.Empty(_warnings);
        }

        [Theory]
        // The definition the scan saw is gone, or now holds a different value.
        [InlineData(1, "1.0.0")]
        [InlineData(0, "1.5.0")]
        public void KnownDefinition_FileChangedSinceScan_DoesNothing(int propertyElementIndex, string scannedValue)
        {
            const string props = "<Project><PropertyGroup><AVersion>1.0.0</AVersion></PropertyGroup></Project>";
            var fileSystem = FileSystem(props);

            Assert.False(Update(fileSystem, Info(variableValue: scannedValue, propertyElementIndex: propertyElementIndex)));

            Assert.Equal(props, fileSystem.File.ReadAllText(PropsPath));
            Assert.Equal(LiteralReference, fileSystem.File.ReadAllText(ProjectPath));
            Assert.Contains("changed since it was scanned", Assert.Single(_warnings));
        }

        // The text edit and the XML disagree when a commented-out closing tag sits inside the value: the regex
        // edit then uncomments markup. The result is checked against the XML before anything is written.
        [Theory]
        [InlineData("<AVersion>1.0.0<!-- </AVersion> --></AVersion>")]
        [InlineData("<AVersion>1.0.0<!-- </AVersion><AVersion> --></AVersion>")]
        [InlineData("<AVersion>1.0.0<!-- </AVersion><AVersion>9</AVersion><AVersion> --></AVersion>")]
        public void KnownDefinition_EditWouldChangeOtherDefinitions_DoesNothing(string definition)
        {
            var props = $"<Project><PropertyGroup>{definition}</PropertyGroup></Project>";
            var fileSystem = FileSystem(props);

            Assert.False(Update(fileSystem, Info(propertyElementIndex: 0)));

            Assert.Equal(props, fileSystem.File.ReadAllText(PropsPath));
            Assert.Equal(LiteralReference, fileSystem.File.ReadAllText(ProjectPath));
            Assert.Contains("did not change exactly one definition", Assert.Single(_warnings));
        }

        // ── Property file can't be written ────────────────────────────────────────

        [Theory]
        [InlineData("AVersion", @"c:\repo\missing.props")]
        [InlineData("", @"c:\repo\Directory.Build.props")]
        public void PropertyFileOrNameMissing_LeavesTheReferenceAlone(string variableName, string filePath)
        {
            const string props = "<Project><PropertyGroup><AVersion>1.0.0</AVersion></PropertyGroup></Project>";
            var fileSystem = FileSystem(props);
            var info = Info(propertyElementIndex: 0);
            info.VariableName = variableName;
            info.FilePath = XFS.Path(filePath);

            Assert.False(Update(fileSystem, info));

            Assert.Equal(props, fileSystem.File.ReadAllText(PropsPath));
            Assert.Equal(LiteralReference, fileSystem.File.ReadAllText(ProjectPath));
            Assert.Empty(_warnings);
        }
    }
}
