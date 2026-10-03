namespace LeanWorker.Tests;

/// <summary>
/// Prepends a directory with a stub executable of the given name to PATH, so Launcher.FindOnPath finds it;
/// restores PATH on dispose.
/// </summary>
internal sealed class FakeOnPath : IDisposable
{
    private readonly string? _old = Environment.GetEnvironmentVariable("PATH");

    public FakeOnPath(string name)
    {
        string dir = Directory.CreateTempSubdirectory("lw-path").FullName;
        File.WriteAllText(Path.Combine(dir, name), "#!/bin/sh\ncat >/dev/null\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(dir, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + _old);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _old);
}
