using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Kai.Engine.Ingest;
using Kai.Engine.Pipeline;
using Kai.Engine.Storage;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Scheduling;

/// <summary>Startup sequence that must finish before the RPC port opens or the scheduler ticks.</summary>
public sealed class EngineBootstrapper
{
    private readonly EngineInstance _instance;
    private readonly EngineDatabase _database;
    private readonly JobStore _store;
    private readonly FileIngestService _ingest;
    private readonly PipelineJobReader _pipeline;
    private readonly RepositoryLayout _layout;
    private readonly EngineOptions _options;
    private readonly ILogger<EngineBootstrapper> _logger;

    public EngineBootstrapper(
        EngineInstance instance,
        EngineDatabase database,
        JobStore store,
        FileIngestService ingest,
        PipelineJobReader pipeline,
        RepositoryLayout layout,
        IOptions<EngineOptions> options,
        ILogger<EngineBootstrapper> logger)
    {
        _instance = instance;
        _database = database;
        _store = store;
        _ingest = ingest;
        _pipeline = pipeline;
        _layout = layout;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _instance.AcquireExclusiveLock();
        await _database.InitializeAsync(ct);

        var recovered = await _store.RecoverOrphanedLeasesAsync(_instance.InstanceId, _options.Scheduler.MaxAttempts, ct);
        if (recovered > 0)
        {
            _logger.LogWarning("Recovered {Count} job(s) interrupted by a previous engine process", recovered);
        }
        _ingest.CleanupStaleIncoming();

        if (_options.ImportExistingCompletedJobs)
        {
            await ImportCompletedJobsAsync(ct);
        }
        _logger.LogInformation("Engine {Instance} ready for repository {Root}", _instance.InstanceId, _layout.Root);
    }

    /// <summary>Makes books translated locally (before the engine existed) visible to the gallery. Idempotent.</summary>
    private async Task ImportCompletedJobsAsync(CancellationToken ct)
    {
        var imported = 0;
        foreach (var snapshot in _pipeline.EnumerateAll().Where(s => s.IsCompleted))
        {
            if (await _store.GetAsync(snapshot.Sha256, ct) is not null)
            {
                continue;
            }
            var sourcePath = Path.Combine(_layout.Root, snapshot.SourceRelativePath);
            var size = File.Exists(sourcePath) ? new FileInfo(sourcePath).Length : 0;
            if (await _store.TryInsertAsync(new NewJob(
                    snapshot.Sha256,
                    Path.GetFileName(snapshot.SourceRelativePath),
                    snapshot.SourceRelativePath.Replace('\\', '/'),
                    size,
                    _options.DefaultAgent,
                    RequestedBy: null,
                    Origin: JobOrigin.Imported,
                    State: JobRecordState.Completed,
                    JobId: snapshot.JobId,
                    Message: "Imported from an existing completed pipeline job."), ct))
            {
                imported++;
            }
        }
        if (imported > 0)
        {
            _logger.LogInformation("Imported {Count} completed pipeline job(s)", imported);
        }
    }
}
