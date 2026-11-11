using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DotNetOutdated.Tests")]

// Lets the tests' NSubstitute mocks implement internal interfaces such as IDotNetRunner.
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
