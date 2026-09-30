using Google.Protobuf.WellKnownTypes;
using Kai.Engine.Contracts.V1;
using Kai.Engine.Pipeline;
using Kai.Engine.Storage;

namespace Kai.Engine.Rpc;

/// <summary>
/// Builds the public status: queue/lease facts come from SQLite, progress and result availability from job.json.
/// A job is reported completed only when both agree, or when job.json proves it.
/// </summary>
public sealed class JobStatusService
{
    private readonly JobStore _store;
    private readonly PipelineJobReader _pipeline;

    public JobStatusService(JobStore store, PipelineJobReader pipeline)
    {
        _store = store;
        _pipeline = pipeline;
    }

    public async Task<JobStatus> BuildAsync(JobRecord job, CancellationToken ct)
    {
        var snapshot = job.JobId is { Length: > 0 } ? _pipeline.Read(job.JobId) : _pipeline.FindBySha(job.Sha256);
        var state = job.State;
        var message = job.Message;
        if (state != JobRecordState.Completed && snapshot?.IsCompleted == true && state != JobRecordState.Running)
        {
            state = JobRecordState.Completed;
            message = "Completed (awaiting reconciliation).";
        }

        var status = new JobStatus
        {
            Sha256 = job.Sha256,
            JobId = snapshot?.JobId ?? job.JobId ?? string.Empty,
            FileName = job.FileName,
            State = Map(state),
            QueuePosition = state == JobRecordState.Queued ? await _store.GetQueuePositionAsync(job, ct) : 0,
            AssignedAgent = job.AssignedAgent,
            Attempts = job.Attempts,
            CurrentStage = snapshot is { IsCompleted: false } ? snapshot.CurrentStage ?? string.Empty : string.Empty,
            ChunksCompleted = snapshot?.ChunksCompleted ?? 0,
            ChunksTotal = snapshot?.ChunksTotal ?? 0,
            Message = message ?? string.Empty,
            SizeBytes = job.SizeBytes,
            ResultAvailable = state == JobRecordState.Completed && snapshot is not null && _pipeline.ResolveArtifact(snapshot, markdown: false) is not null,
            CreatedAt = Timestamp.FromDateTimeOffset(job.CreatedUtc),
            UpdatedAt = Timestamp.FromDateTimeOffset(job.UpdatedUtc),
        };
        if (job.StartedUtc is { } started)
        {
            status.StartedAt = Timestamp.FromDateTimeOffset(started);
        }
        if (job.CompletedUtc is { } completed)
        {
            status.CompletedAt = Timestamp.FromDateTimeOffset(completed);
        }
        return status;
    }

    public static JobState Map(JobRecordState state) => state switch
    {
        JobRecordState.Queued => JobState.Queued,
        JobRecordState.Running => JobState.Running,
        JobRecordState.Completed => JobState.Completed,
        JobRecordState.Failed => JobState.Failed,
        _ => JobState.Unspecified,
    };

    public static JobRecordState? Map(JobState state) => state switch
    {
        JobState.Queued => JobRecordState.Queued,
        JobState.Running => JobRecordState.Running,
        JobState.Completed => JobRecordState.Completed,
        JobState.Failed => JobRecordState.Failed,
        _ => null,
    };
}
