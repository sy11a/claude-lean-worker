using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Tests below mutate PATH, Console.Out and the current directory: process-global state that must not
/// race with another test running in parallel.
/// </summary>
[CollectionDefinition("launcher-process-state", DisableParallelization = true)]
[SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Justification = "xUnit requires collection definition classes to be public (xUnit1027).")]
public sealed class LauncherProcessStateDefinition;
