using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeWatch;

/// <summary>
/// Minimal glob matching over forward-slash-normalized relative paths.
/// Supports **, *, ?; matching is case-insensitive (Windows-first tool).
/// </summary>
public static class Globs
{
    public static string Normalize(string path, string repoRoot)
    {
        var full = Path.GetFullPath(path, repoRoot);
        var relative = Path.GetRelativePath(repoRoot, full);
        return relative.Replace('\\', '/');
    }

    public static bool IsMatch(string normalizedPath, string glob) =>
        ToRegex(glob).IsMatch(normalizedPath);

    public static bool MatchesAny(string normalizedPath, IEnumerable<string> globs) =>
        globs.Any(g => IsMatch(normalizedPath, g));

    private static readonly Dictionary<string, Regex> Cache = [];
    private static readonly Lock CacheLock = new();

    public static Regex ToRegex(string glob)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(glob, out var cached)) return cached;
            var regex = new Regex(Convert(glob), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            Cache[glob] = regex;
            return regex;
        }
    }

    private static string Convert(string glob)
    {
        var pattern = glob.Replace('\\', '/').TrimStart('/');
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                var isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDouble)
                {
                    var followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    if (followedBySlash)
                    {
                        sb.Append("(?:.*/)?"); // "**/" matches zero or more directories
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 1;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}
