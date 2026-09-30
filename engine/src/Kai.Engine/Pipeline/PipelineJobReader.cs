using System.Text.Json;
using Kai.Engine.Infrastructure;

namespace Kai.Engine.Pipeline;

/// <summary>Read-only projection of an authoritative <c>_processing/jobs/&lt;job-id&gt;/job.json</c>.</summary>
public sealed record PipelineSnapshot(
    string JobId,
    string Sha256,
    string SourceRelativePath,
    string Status,
    string? CurrentStage,
    string? CurrentStageStatus,
    int ChunksCompleted,
    int ChunksTotal,
    string? BlockedReason,
    string? TranslatedPdf,
    string? TranslatedMarkdown,
    string Fingerprint)
{
    public bool IsCompleted => Status == "completed";
    public bool IsBlocked => BlockedReason is not null;
    public bool IsSuperseded => Status == "superseded";
    public bool IsTerminal => IsCompleted || IsBlocked || IsSuperseded;
}

public sealed class PipelineJobReader
{
    private static readonly string[] StageOrder = ["convert", "chunk", "translate", "assemble", "validate"];
    private readonly RepositoryLayout _layout;
    private readonly ILogger<PipelineJobReader> _logger;

    public PipelineJobReader(RepositoryLayout layout, ILogger<PipelineJobReader> logger)
    {
        _layout = layout;
        _logger = logger;
    }

    /// <summary>Finds the live (non-superseded) pipeline job whose source hash matches.</summary>
    public PipelineSnapshot? FindBySha(string sha256)
    {
        if (!Directory.Exists(_layout.JobsRoot))
        {
            return null;
        }
        // Job ids end with the first 16 hex chars of the source SHA-256 (Initialize-TranslationJobs.ps1).
        foreach (var directory in Directory.EnumerateDirectories(_layout.JobsRoot, $"*-{sha256[..16]}"))
        {
            var snapshot = TryRead(Path.Combine(directory, "job.json"));
            if (snapshot is not null && snapshot.Sha256 == sha256 && !snapshot.IsSuperseded)
            {
                return snapshot;
            }
        }
        return null;
    }

    public PipelineSnapshot? Read(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || jobId.Contains(".."))
        {
            return null;
        }
        return TryRead(Path.Combine(_layout.JobsRoot, jobId, "job.json"));
    }

    public IEnumerable<PipelineSnapshot> EnumerateAll()
    {
        if (!Directory.Exists(_layout.JobsRoot))
        {
            yield break;
        }
        foreach (var directory in Directory.EnumerateDirectories(_layout.JobsRoot))
        {
            var snapshot = TryRead(Path.Combine(directory, "job.json"));
            if (snapshot is not null)
            {
                yield return snapshot;
            }
        }
    }

    /// <summary>Resolves a finished artifact strictly inside <c>_output</c>. Falls back to conventional names.</summary>
    public string? ResolveArtifact(PipelineSnapshot snapshot, bool markdown)
    {
        var declared = _layout.ResolveInside(markdown ? snapshot.TranslatedMarkdown : snapshot.TranslatedPdf, _layout.OutputRoot);
        if (declared is not null && File.Exists(declared))
        {
            return declared;
        }

        var outputDirectory = _layout.ResolveInside($"_output/{snapshot.JobId}", _layout.OutputRoot);
        if (outputDirectory is null || !Directory.Exists(outputDirectory))
        {
            return null;
        }
        if (markdown)
        {
            var conventional = Path.Combine(outputDirectory, "book.vi.md");
            return File.Exists(conventional) ? conventional : null;
        }
        return new DirectoryInfo(outputDirectory)
            .EnumerateFiles("*.pdf", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    private PipelineSnapshot? TryRead(string path)
    {
        // job.json is replaced atomically by the PowerShell tools; retry briefly around the replace window.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                return Project(document.RootElement);
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
            {
                _logger.LogWarning(ex, "Unable to read pipeline state {Path}", path);
                return null;
            }
        }
        return null;
    }

    private static PipelineSnapshot Project(JsonElement root)
    {
        var jobId = root.GetProperty("jobId").GetString()!;
        var status = GetString(root, "status") ?? "pending";
        var source = root.GetProperty("source");
        var sha = source.GetProperty("sha256").GetString()!;
        var relativePath = GetString(source, "relativePath") ?? string.Empty;

        string? blocked = status == "blocked" ? "Pipeline job is blocked." : null;
        string? currentStage = null;
        string? currentStageStatus = null;
        var stageFingerprint = new List<string>();
        if (root.TryGetProperty("stages", out var stages) && stages.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in StageOrder)
            {
                if (!stages.TryGetProperty(name, out var stage))
                {
                    continue;
                }
                var stageStatus = GetString(stage, "status") ?? "pending";
                stageFingerprint.Add($"{name}:{stageStatus}:{GetRaw(stage, "attempts")}");
                if (stageStatus == "blocked")
                {
                    blocked = $"Stage {name} blocked: {GetString(stage, "message") ?? GetString(stage, "lastError") ?? "no reason recorded"}";
                }
                if (currentStage is null && stageStatus is not ("completed" or "skipped"))
                {
                    currentStage = name;
                    currentStageStatus = stageStatus;
                }
            }
        }

        int completed = 0, total = 0;
        if (root.TryGetProperty("chunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
        {
            foreach (var chunk in chunks.EnumerateArray())
            {
                total++;
                var chunkStatus = GetString(chunk, "status");
                if (chunkStatus == "completed")
                {
                    completed++;
                }
                else if (chunkStatus == "blocked")
                {
                    blocked ??= $"Chunk {GetString(chunk, "id")} blocked: {GetString(chunk, "lastError") ?? "no reason recorded"}";
                }
            }
        }

        string? pdf = null, markdown = null;
        if (root.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Object)
        {
            pdf = GetString(artifacts, "translatedPdf");
            markdown = GetString(artifacts, "translatedMarkdown");
        }

        var fingerprint = $"{GetRaw(root, "updatedUtc")}|{status}|{string.Join(',', stageFingerprint)}|{completed}/{total}";
        return new PipelineSnapshot(jobId, sha, relativePath, status, currentStage, currentStageStatus, completed, total, blocked, pdf, markdown, fingerprint);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string GetRaw(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetRawText() : string.Empty;
}
