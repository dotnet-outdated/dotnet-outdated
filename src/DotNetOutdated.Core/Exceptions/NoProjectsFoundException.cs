namespace DotNetOutdated.Core.Exceptions;

/// <summary>A directory contains no projects or solutions to analyze.</summary>
public sealed class NoProjectsFoundException(string message) : CommandValidationException(message);
