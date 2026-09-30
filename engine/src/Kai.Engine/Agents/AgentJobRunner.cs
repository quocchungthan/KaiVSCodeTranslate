using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Kai.Engine.Pipeline;
using Kai.Engine.Storage;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Agents;

public enum AgentRunOutcomeKind
{
    Completed,
    /// <summary>The pipeline recorded a blocker or the source vanished: retrying without intervention is pointless.</summary>
    Blocked,
    /// <summary>Transient failure or no progress; the scheduler retries with backoff until attempts are exhausted.</summary>
    Retryable,
    Cancelled,
}

public sealed record AgentRunOutcome(AgentRunOutcomeKind Kind, string Message);

/// <summary>
/// Drives one leased job to completion in a single Copilot session. job.json is the only source of truth
/// for progress: after every turn the runner re-reads it and continues, stops, or gives up on stalls.
/// </summary>
public sealed class AgentJobRunner
{
    private readonly IAgentSessionFactory _sessions;
    private readonly IPipelineScriptRunner _scripts;
    private readonly PipelineJobReader _pipeline;
    private readonly JobStore _store;
    private readonly RepositoryLayout _layout;
    private readonly CopilotAgentOptions _options;
    private readonly ILogger<AgentJobRunner> _logger;

    public AgentJobRunner(
        IAgentSessionFactory sessions,
        IPipelineScriptRunner scripts,
        PipelineJobReader pipeline,
        JobStore store,
        RepositoryLayout layout,
        IOptions<EngineOptions> options,
        ILogger<AgentJobRunner> logger)
    {
        _sessions = sessions;
        _scripts = scripts;
        _pipeline = pipeline;
        _store = store;
        _layout = layout;
        _options = options.Value.Copilot;
        _logger = logger;
    }

    public async Task<AgentRunOutcome> RunAsync(JobRecord job, string leaseOwner, CancellationToken cancellationToken)
    {
        using var runTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runTimeout.CancelAfter(TimeSpan.FromHours(_options.RunTimeoutHours));
        try
        {
            return await RunCoreAsync(job, leaseOwner, runTimeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.Cancelled, "Run cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.Retryable, $"Run exceeded {_options.RunTimeoutHours} hours.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent run for {Sha} failed", job.Sha256);
            return new AgentRunOutcome(AgentRunOutcomeKind.Retryable, $"Run failed: {ex.Message}");
        }
    }

    private async Task<AgentRunOutcome> RunCoreAsync(JobRecord job, string leaseOwner, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(_layout.Root, job.SourceRelativePath)))
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.Blocked, $"Source file {job.SourceRelativePath} is missing.");
        }

        var snapshot = _pipeline.FindBySha(job.Sha256);
        if (snapshot is null)
        {
            await _scripts.InitializeJobsAsync(ct);
            snapshot = _pipeline.FindBySha(job.Sha256);
            if (snapshot is null)
            {
                return new AgentRunOutcome(AgentRunOutcomeKind.Retryable, "Pipeline job was not initialized for this source.");
            }
        }
        var jobId = snapshot.JobId;
        if (job.JobId != jobId && !await _store.SetJobIdAsync(job.Sha256, leaseOwner, jobId, ct))
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.Cancelled, "Lease lost.");
        }
        if (Terminal(snapshot) is { } already)
        {
            return already;
        }

        await using var session = await _sessions.OpenAsync(
            new AgentSessionRequest(job.AssignedAgent, job.CopilotSessionId, $"kai-engine-{job.Sha256[..16]}-{Guid.NewGuid():N}"[..44]),
            ct);
        if (session.SessionId != job.CopilotSessionId && !await _store.SetCopilotSessionAsync(job.Sha256, leaseOwner, session.SessionId, ct))
        {
            return new AgentRunOutcome(AgentRunOutcomeKind.Cancelled, "Lease lost.");
        }

        var stalledTurns = 0;
        var lastFingerprint = snapshot.Fingerprint;
        for (var turn = 0; turn <= _options.MaxContinuations; turn++)
        {
            var prompt = turn == 0 && !session.Resumed
                ? BuildInitialPrompt(jobId, job.SourceRelativePath)
                : BuildContinuationPrompt(jobId, snapshot);
            _logger.LogInformation("Job {JobId}: starting turn {Turn} in session {SessionId}", jobId, turn, session.SessionId);

            var result = await session.RunTurnAsync(prompt, () => _pipeline.Read(jobId)?.IsTerminal == true, ct);
            snapshot = _pipeline.Read(jobId) ?? snapshot;
            if (Terminal(snapshot) is { } done)
            {
                return done;
            }

            var progressed = snapshot.Fingerprint != lastFingerprint;
            lastFingerprint = snapshot.Fingerprint;
            stalledTurns = progressed ? 0 : stalledTurns + 1;
            var status = $"{snapshot.CurrentStage ?? "pending"}: {snapshot.ChunksCompleted}/{snapshot.ChunksTotal} chunks; turn {turn} ended ({result.End}{(result.Detail is null ? string.Empty : $": {result.Detail}")}).";
            if (!await _store.SetProgressMessageAsync(job.Sha256, leaseOwner, Truncate(status), ct))
            {
                return new AgentRunOutcome(AgentRunOutcomeKind.Cancelled, "Lease lost.");
            }
            if (stalledTurns >= _options.MaxStalledTurns)
            {
                return new AgentRunOutcome(AgentRunOutcomeKind.Retryable, Truncate($"No pipeline progress in {stalledTurns} consecutive turns. Last: {status}"));
            }
        }
        return new AgentRunOutcome(AgentRunOutcomeKind.Retryable, $"Job not finished after {_options.MaxContinuations} continuations.");
    }

    private static AgentRunOutcome? Terminal(PipelineSnapshot snapshot) => snapshot switch
    {
        { IsCompleted: true } => new AgentRunOutcome(AgentRunOutcomeKind.Completed, "Translation completed and validated."),
        { IsSuperseded: true } => new AgentRunOutcome(AgentRunOutcomeKind.Blocked, "Pipeline job was superseded by a different source."),
        { IsBlocked: true } => new AgentRunOutcome(AgentRunOutcomeKind.Blocked, Truncate(snapshot.BlockedReason!)),
        _ => null,
    };

    internal static string BuildInitialPrompt(string jobId, string sourceRelativePath) => $"""
        You are running unattended inside the Kai translation engine. No human is watching and nobody will answer questions.

        Work only on pipeline job `{jobId}` (source `{sourceRelativePath}`) and finish the whole book in this session.
        1. Run `.github/tools/Get-NextTranslationAction.ps1 -JobId {jobId}` with PowerShell and perform the returned action using the matching skill.
        2. Persist state through the `.github/tools` scripts after every durable unit, then query the next action again immediately.
        3. Repeat until job.json reports status `completed` (validated Vietnamese PDF rendered), or a concrete blocker is recorded.

        Rules:
        - Never pause for approval or report intermediate progress; keep translating chunk after chunk.
        - Do not touch other jobs, `_pdfs/`, or `_processing/engine/`.
        - Do not install software, download packages, or send book content to external services. If a missing tool or
          permission prevents progress, record the blocker in job.json with `Update-TranslationState.ps1` and stop.
        """;

    internal static string BuildContinuationPrompt(string jobId, PipelineSnapshot snapshot) => $"""
        Continue unattended work on pipeline job `{jobId}`. job.json currently reports status `{snapshot.Status}`,
        stage `{snapshot.CurrentStage ?? "pending"}` ({snapshot.CurrentStageStatus ?? "pending"}), {snapshot.ChunksCompleted}/{snapshot.ChunksTotal} chunks completed.
        Run `.github/tools/Get-NextTranslationAction.ps1 -JobId {jobId}` and keep working until the job is completed or a
        concrete blocker is recorded. Do not stop to summarize progress.
        """;

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];
}
