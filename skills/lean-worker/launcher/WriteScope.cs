using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LeanWorker;

/// <summary>
/// Which files a run changed in its git working tree, and which of them fall outside the task's write scope.
/// A file counts as changed when its state (content hash, or deleted) differs between the snapshots taken
/// before and after the worker, so edits to files that were already dirty are caught too.
/// </summary>
internal static class WriteScope
{
    /// <summary>The dirty files of a working tree: path relative to the repository root → content hash.</summary>
    public sealed record Snapshot(string Root, Dictionary<string, string> Dirty);

    /// <summary>Null when <paramref name="cwd"/> is not inside a git working tree (or git is missing).</summary>
    public static Snapshot? Take(string cwd, string? exclude = null)
    {
        var root = Git(cwd, "rev-parse", "--show-toplevel")?.Trim();
        if (string.IsNullOrEmpty(root)) return null;
        var status = Git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status is null) return null;
        var skip = exclude is null ? null : Relative(root, exclude);
        var dirty = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length < 4) continue;
            // A rename or copy is followed by its source path; a renamed source no longer exists, so it reads as deleted.
            var source = e[0] is 'R' or 'C' && i + 1 < entries.Length ? entries[++i] : null;
            foreach (var path in e[0] == 'R' && source is not null ? [e[3..], source] : new[] { e[3..] })
            {
                if (skip is not null && (path == skip || path.StartsWith(skip + "/", StringComparison.Ordinal))) continue;
                var full = Path.Combine(root, path);
                dirty[path] = File.Exists(full) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))) : "-";
            }
        }
        return new Snapshot(root, dirty);
    }

    /// <summary>Paths whose state differs between the two snapshots, in ordinal order.</summary>
    public static List<string> Changed(Snapshot before, Snapshot after) =>
        before.Dirty.Keys.Union(after.Dirty.Keys)
            .Where(p => !before.Dirty.TryGetValue(p, out var a) || !after.Dirty.TryGetValue(p, out var b) || a != b)
            .Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Whether a repository-relative path is in the scope. Patterns are globs relative to the repository root:
    /// <c>*</c> and <c>?</c> stay within one path segment, <c>**</c> crosses segments, and a pattern that names a
    /// directory (<c>src/Foo</c> or <c>src/Foo/</c>) covers everything under it.
    /// </summary>
    public static bool InScope(string path, IReadOnlyList<string> patterns) => patterns.Any(p => ToRegex(p).IsMatch(path));

    private static Regex ToRegex(string glob)
    {
        var g = glob.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        if (g.StartsWith("./", StringComparison.Ordinal)) g = g[2..];
        var sb = new StringBuilder("^");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                var slash = i + 2 < g.Length && g[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append("(?:/.*)?$").ToString(), RegexOptions.CultureInvariant);
    }

    private static string? Relative(string root, string path)
    {
        var rel = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : rel;
    }

    private static string? Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // git is not installed
    }
}
