using System.Security.Cryptography;
using System.Text.Json;
using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Kai.Engine.Storage;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Tests;

/// <summary>Disposable, isolated translation repository on disk.</summary>
public sealed class TestRepository : IDisposable
{
    public const string SecretKey = "test-secret-key-0123456789abcdef-0123456789";

    public TestRepository()
    {
        Root = Path.Combine(Path.GetTempPath(), "kai-engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, ".github", "agents"));
        File.WriteAllText(Path.Combine(Root, ".github", "translation-pipeline.defaults.json"), "{}");
        File.WriteAllText(Path.Combine(Root, ".github", "agents", "Huong.agent.md"), "---\nname: Huong\ndescription: \"Test agent\"\n---\n\nTranslate.");
        Directory.CreateDirectory(Path.Combine(Root, "_pdfs"));
    }

    public string Root { get; }

    public EngineOptions CreateOptions(Action<EngineOptions>? configure = null)
    {
        var options = new EngineOptions
        {
            RepositoryRoot = Root,
            SecretKey = SecretKey,
            Agents = [new AgentOptions { Name = "Huong" }, new AgentOptions { Name = "Mai" }],
        };
        configure?.Invoke(options);
        return options;
    }

    public (JobStore Store, EngineDatabase Database, RepositoryLayout Layout) CreateStore(TimeProvider? time = null)
    {
        var layout = new RepositoryLayout(Options.Create(CreateOptions()));
        var database = new EngineDatabase(layout);
        database.InitializeAsync().GetAwaiter().GetResult();
        return (new JobStore(database, time ?? TimeProvider.System), database, layout);
    }

    public static byte[] CreatePdfBytes(string marker, int size = 4096)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        "%PDF-1.7\n"u8.CopyTo(bytes);
        System.Text.Encoding.ASCII.GetBytes(marker).CopyTo(bytes, 16);
        return bytes;
    }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string JobIdFor(string slug, string sha) => $"{slug}-{sha[..16]}";

    /// <summary>Writes a job.json with the same shape as Initialize-TranslationJobs.ps1.</summary>
    public string WritePipelineJob(string jobId, string sha, string relativeSource, string status, int chunksCompleted = 0, int chunksTotal = 2, string? translatedPdf = null, string updatedUtc = "2026-01-01T00:00:00Z")
    {
        var directory = Path.Combine(Root, "_processing", "jobs", jobId);
        Directory.CreateDirectory(directory);
        var stageStatus = status == "completed" ? "completed" : "pending";
        var job = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["jobId"] = jobId,
            ["status"] = status,
            ["source"] = new { relativePath = relativeSource, sha256 = sha, sizeBytes = 1 },
            ["stages"] = new Dictionary<string, object>
            {
                ["convert"] = new { status = status == "pending" ? "pending" : "completed", attempts = 1 },
                ["chunk"] = new { status = status == "pending" ? "pending" : "completed", attempts = 1 },
                ["translate"] = new { status = stageStatus, attempts = 0 },
                ["assemble"] = new { status = stageStatus, attempts = 0 },
                ["validate"] = new { status = stageStatus, attempts = 0 },
            },
            ["chunks"] = Enumerable.Range(1, chunksTotal)
                .Select(i => new { id = $"c{i:000}", sequence = i, status = i <= chunksCompleted ? "completed" : "pending" })
                .ToArray(),
            ["artifacts"] = new { translatedPdf },
            ["updatedUtc"] = updatedUtc,
        };
        var path = Path.Combine(directory, "job.json");
        File.WriteAllText(path, JsonSerializer.Serialize(job));
        return path;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
