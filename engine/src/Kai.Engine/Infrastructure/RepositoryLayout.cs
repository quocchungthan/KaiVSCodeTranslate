using Kai.Engine.Configuration;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Infrastructure;

/// <summary>Resolves the repository folders the engine is allowed to touch.</summary>
public sealed class RepositoryLayout
{
    private const string MarkerRelativePath = ".github/translation-pipeline.defaults.json";

    public RepositoryLayout(IOptions<EngineOptions> options)
    {
        var configured = options.Value.RepositoryRoot;
        Root = string.IsNullOrWhiteSpace(configured)
            ? DiscoverRoot()
            : Path.GetFullPath(configured);

        if (!File.Exists(Path.Combine(Root, MarkerRelativePath)))
        {
            throw new InvalidOperationException($"'{Root}' is not a translation repository (missing {MarkerRelativePath}).");
        }

        PdfInbox = Path.Combine(Root, "_pdfs");
        JobsRoot = Path.Combine(Root, "_processing", "jobs");
        OutputRoot = Path.Combine(Root, "_output");
        EngineRoot = Path.Combine(Root, "_processing", "engine");
        IncomingRoot = Path.Combine(EngineRoot, "incoming");
        ToolsRoot = Path.Combine(Root, ".github", "tools");
        DatabasePath = Path.IsPathRooted(options.Value.DatabasePath)
            ? options.Value.DatabasePath
            : Path.GetFullPath(Path.Combine(Root, options.Value.DatabasePath));
    }

    public string Root { get; }
    public string PdfInbox { get; }
    public string JobsRoot { get; }
    public string OutputRoot { get; }
    public string EngineRoot { get; }
    public string IncomingRoot { get; }
    public string ToolsRoot { get; }
    public string DatabasePath { get; }

    public string ToRelative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    /// <summary>Resolves a repository-relative path and guarantees it stays inside <paramref name="allowedRoot"/>.</summary>
    public string? ResolveInside(string? relativePath, string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string DiscoverRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, MarkerRelativePath)))
                {
                    return dir.FullName;
                }
            }
        }
        throw new InvalidOperationException("Could not locate the translation repository root. Set Engine:RepositoryRoot.");
    }
}
