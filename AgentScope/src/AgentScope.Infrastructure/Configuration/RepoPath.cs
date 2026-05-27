namespace AgentScope.Infrastructure.Configuration;

/// <summary>
/// Resolves configured paths against the repository root (the directory containing
/// <c>.git</c> or a <c>.sln</c>), not the process CWD. Lets default relative paths in
/// <see cref="EvalsOptions"/> work no matter which directory the Web/Eval host was
/// launched from — <c>dotnet run</c> from <c>src/AgentScope.Web</c> would otherwise
/// resolve relative paths against that project folder rather than the repo root.
/// </summary>
public static class RepoPath
{
    private static readonly Lazy<string> _root = new(FindRoot);

    public static string Root => _root.Value;

    public static string Resolve(string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || dir.EnumerateFiles("*.sln").Any())
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        // No repo markers found (e.g. published as a tarball) — preserve prior CWD behavior.
        return Directory.GetCurrentDirectory();
    }
}
