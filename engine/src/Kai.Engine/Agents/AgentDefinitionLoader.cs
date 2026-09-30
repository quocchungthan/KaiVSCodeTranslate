namespace Kai.Engine.Agents;

public sealed record AgentDefinition(string Name, string? Description, string Prompt, string SourcePath);

/// <summary>Loads <c>.github/agents/*.agent.md</c> (YAML front matter + Markdown body) as SDK custom agents.</summary>
public static class AgentDefinitionLoader
{
    public static IReadOnlyList<AgentDefinition> LoadAll(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }
        return Directory.EnumerateFiles(directory, "*.agent.md")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(Load)
            .ToList();
    }

    public static AgentDefinition Load(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var fallbackName = Path.GetFileName(path).Replace(".agent.md", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new AgentDefinition(fallbackName, null, text.Trim(), path);
        }

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException($"Unterminated front matter in {path}.");
        }
        var frontMatter = ParseFrontMatter(text[4..end]);
        var bodyStart = text.IndexOf('\n', end + 4);
        var body = bodyStart < 0 ? string.Empty : text[(bodyStart + 1)..].Trim();

        return new AgentDefinition(
            frontMatter.TryGetValue("name", out var name) && name.Length > 0 ? name : fallbackName,
            frontMatter.GetValueOrDefault("description"),
            body,
            path);
    }

    /// <summary>Reads top-level scalar keys only; lists (tools, agents) are intentionally ignored.</summary>
    private static Dictionary<string, string> ParseFrontMatter(string block)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in block.Split('\n'))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#'))
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            var value = line[(colon + 1)..].Trim();
            if (value.StartsWith('[') || value.Length == 0)
            {
                continue;
            }
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1].Replace(value[0] == '"' ? "\\\"" : "''", value[0] == '"' ? "\"" : "'");
            }
            values[line[..colon].Trim()] = value;
        }
        return values;
    }
}
