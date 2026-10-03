using System.Diagnostics;
using Xunit;

namespace LeanWorker.Tests;

public class WriteScopeTests
{
    [Theory]
    [InlineData("src/Foo/A.cs", "src/Foo/**", true)]
    [InlineData("src/Foo/Deep/A.cs", "src/Foo", true)]
    [InlineData("src/Foo/Deep/A.cs", "src/Foo/", true)]
    [InlineData("src/FooBar/A.cs", "src/Foo", false)]
    [InlineData("src/Foo/Deep/A.cs", "src/Foo/*.cs", false)]
    [InlineData("src/Foo/A.cs", "src/Foo/*.cs", true)]
    [InlineData("tests/x/y.golden", "**/*.golden", true)]
    [InlineData("y.golden", "**/*.golden", true)]
    [InlineData("docs/a.md", "./docs/a.md", true)]
    [InlineData("docs/a.md", "src/**", false)]
    public void Globs_match_repository_relative_paths(string path, string pattern, bool expected) =>
        Assert.Equal(expected, WriteScope.InScope(path, [pattern]));

    [Fact]
    public async Task Changes_include_new_modified_deleted_renamed_and_further_edits_to_dirty_filesAsync()
    {
        string dir = Directory.CreateTempSubdirectory("lw-scope").FullName;
        try
        {
            void Git(params string[] a)
            {
                ProcessStartInfo psi = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string? x in new[] { "-C", dir, "-c", "user.email=t@t", "-c", "user.name=t" }.Concat(a))
                {
                    psi.ArgumentList.Add(x);
                }

                using Process p = Process.Start(psi)!;
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            Git("init", "-q");
            foreach (string? f in new[] { "keep.txt", "edit.txt", "gone.txt", "move.txt", "dirty.txt" })
            {
                await File.WriteAllTextAsync(Path.Combine(dir, f), f, TestContext.Current.CancellationToken);
            }

            Git("add", "-A");
            Git("commit", "-qm", "init");
            await File.WriteAllTextAsync(Path.Combine(dir, "dirty.txt"), "dirty before the run", TestContext.Current.CancellationToken);
            _ = Directory.CreateDirectory(Path.Combine(dir, ".lean-worker", "runs"));

            WriteScope.Snapshot before = (await WriteScope.TakeAsync(dir, Path.Combine(dir, ".lean-worker")))!;
            await File.WriteAllTextAsync(Path.Combine(dir, "edit.txt"), "changed", TestContext.Current.CancellationToken);
            File.Delete(Path.Combine(dir, "gone.txt"));
            Git("mv", "move.txt", "moved.txt");
            await File.WriteAllTextAsync(Path.Combine(dir, "dirty.txt"), "edited again by the worker", TestContext.Current.CancellationToken);
            _ = Directory.CreateDirectory(Path.Combine(dir, "new"));
            await File.WriteAllTextAsync(Path.Combine(dir, "new", "file.txt"), "x", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(dir, ".lean-worker", "runs", "log.txt"), "the launcher's own files", TestContext.Current.CancellationToken);
            WriteScope.Snapshot after = (await WriteScope.TakeAsync(dir, Path.Combine(dir, ".lean-worker")))!;

            Assert.Equal(["dirty.txt", "edit.txt", "gone.txt", "move.txt", "moved.txt", "new/file.txt"], WriteScope.Changed(before, after));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Outside_a_git_tree_there_is_no_snapshotAsync()
    {
        string dir = Directory.CreateTempSubdirectory("lw-nogit").FullName;
        try { Assert.Null(await WriteScope.TakeAsync(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
