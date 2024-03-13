using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotNetOutdated.Core.Services
{
    public class DependencyFileAddPackageService : IDotNetAddPackageService
    {
        private readonly Regex _versionVariable = new Regex(@"^\$\((?<version>\S+)\)$");

        private readonly IFileSystem _fileSystem;
        private readonly Lazy<IReadOnlyDictionary<string, Updater>> _updaterPerName;
        private readonly Lazy<XDocument> _document;


        public DependencyFileAddPackageService(IFileSystem fileSystem, string dependencyFile)
        {
            _fileSystem = fileSystem;
            DependencyFile = dependencyFile;
            _document = new Lazy<XDocument>(InitDocument);
            _updaterPerName = new Lazy<IReadOnlyDictionary<string, Updater>>(Init);
        }

        private XDocument InitDocument()
        {
            var fi = _fileSystem.FileInfo.New(DependencyFile);
            using var stream = fi.OpenRead();
            return XDocument.Load(stream);
        }

        private IReadOnlyDictionary<string, Updater> Init()
        {
            XDocument document = _document.Value;
            var properties = new Dictionary<string, XElement>(StringComparer.Ordinal);
            var references = new Dictionary<string, Updater>(StringComparer.Ordinal);
            foreach (var item in document.Root.Elements())
            {
                if ("PropertyGroup".Equals(item.Name.LocalName))
                {
                    AddProperties(item);
                }
                else if ("ItemGroup".Equals(item.Name.LocalName))
                {
                    AddReferences(item);
                }
            }

            return references;

            void AddProperties(XElement propertyGroup)
            {
                foreach (var item in propertyGroup.Elements())
                {
                    properties[item.Name.LocalName] = item;
                }
            }

            void AddReferences(XElement itemGroup)
            {
                foreach (var item in itemGroup.Elements())
                {
                    if ("PackageReference".Equals(item.Name.LocalName))
                    {
                        var name = TryGetAttributeValue(item, "Update", out var value)
                            ? value
                            : TryGetAttributeValue(item, "Include", out value)
                                ? value
                                : throw new FormatException($"Unknown package reference: {item}");
                        var version = TryGetAttributeValue(item, "Version", out var v)
                            ? v
                            : throw new FormatException($"Can't find version: {item}");

                        if (TryGetVariableName(version, out var variable))
                        {
                            references[name] = new VariableUpdater(name, variable, properties);
                        }
                        else
                        {
                            references[name] = new XElementUpdater(name, item);
                        }
                    }
                }
            }

            string GetAttributeValue(XElement element, string name)
            {
                var match = GetAttribute(element, name);
                return match != null
                    ? match.Value
                    : string.Empty;
            }

            bool TryGetAttributeValue(XElement element, string name, out string value)
            {
                value = GetAttributeValue(element, name);
                return !string.IsNullOrWhiteSpace(value);
            }

            bool TryGetVariableName(string version, out string variableName)
            {
                var match = _versionVariable.Match(version);
                if (match.Success)
                {
                    variableName = match.Groups["version"].Value;
                    return true;
                }

                variableName = null;
                return false;
            }
        }

        public string DependencyFile { get; }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version)
        {
            return AddPackage(projectPath, packageName, frameworkName, version, false);
        }

        public RunStatus AddPackage(string projectPath, string packageName, string frameworkName, NuGetVersion version, bool noRestore, bool ignoreFailedSources = false)
        {
            var update = _updaterPerName.Value.TryGetValue(packageName, out var updater)
                ? updater.Update(version)
                : new RunStatus($"Failed: {packageName}", "Not found", -1);
            if (update.IsSuccess)
            {
                // Save the document
                var fi = _fileSystem.FileInfo.New(DependencyFile);
                using var stream = fi.Create();
                _document.Value.Save(stream);
            }

            return update;
        }

        private XAttribute GetAttribute(XElement element, string name)
        {
            return element.Attributes().LastOrDefault(a => name.Equals(a.Name.LocalName, StringComparison.OrdinalIgnoreCase));
        }

        private abstract class Updater
        {
            protected Updater(string packageName)
            {
                PackageName = packageName;
            }

            public string PackageName { get; }

            internal abstract RunStatus Update(NuGetVersion version);
        }

        private sealed class VariableUpdater : Updater
        {
            private string _name;
            private Dictionary<string, XElement> _properties;

            public VariableUpdater(string packageName, string name, Dictionary<string, XElement> properties) : base(packageName)
            {
                _name = name;
                _properties = properties;
            }

            internal override RunStatus Update(NuGetVersion version)
            {
                var versionStr = version.ToNormalizedString();
                if (_properties.TryGetValue(_name, out var element))
                {
                    // Check if contains another variable, if so skip it
                    var currentValue = element.Value;
                    if (!string.IsNullOrWhiteSpace(currentValue) && element.Value.Contains("$("))
                    {
                        // NOOP: Points to another variable
                        return new RunStatus($"{PackageName} Variable {_name} = {versionStr}, (NOOP)", string.Empty, 0);
                    }

                    element.SetValue(versionStr);
                    return new RunStatus($"{PackageName} Variable {_name} = {versionStr}", string.Empty, 0);
                }

                return new RunStatus($"{PackageName} Variable {_name} = {versionStr}", "Not found", -1);
            }
        }

        private sealed class XElementUpdater : Updater
        {
            private readonly XElement _item;

            public XElementUpdater(string packageName, XElement item) : base(packageName)
            {
                _item = item;
            }

            internal override RunStatus Update(NuGetVersion version)
            {
                var versionStr = version.ToNormalizedString();
                var attributeKey = _item.Name.Namespace.GetName("Version");
                 _item.SetAttributeValue(attributeKey, versionStr);
                return new RunStatus($"{PackageName} = {versionStr}", string.Empty, 0);
            }
        }
    }
}
