namespace Kai.Engine.Storage;

public enum JobRecordState
{
    Queued,
    Running,
    Completed,
    Failed,
}

public static class JobOrigin
{
    public const string Upload = "upload";
    public const string Imported = "imported";
}

/// <summary>Engine-side view of a job. Pipeline progress itself stays authoritative in job.json.</summary>
public sealed record JobRecord(
    string Sha256,
    long QueueSeq,
    string? JobId,
    string FileName,
    string SourceRelativePath,
    long SizeBytes,
    JobRecordState State,
    string AssignedAgent,
    string? RequestedBy,
    int Attempts,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresUtc,
    DateTimeOffset? NotBeforeUtc,
    string? CopilotSessionId,
    string? Message,
    string Origin,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc);

public sealed record NewJob(
    string Sha256,
    string FileName,
    string SourceRelativePath,
    long SizeBytes,
    string AssignedAgent,
    string? RequestedBy,
    string Origin = JobOrigin.Upload,
    JobRecordState State = JobRecordState.Queued,
    string? JobId = null,
    string? Message = null);
