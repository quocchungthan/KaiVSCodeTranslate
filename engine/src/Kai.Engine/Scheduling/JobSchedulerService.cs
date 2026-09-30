using System.Collections.Concurrent;
using Kai.Engine.Agents;
using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Kai.Engine.Pipeline;
using Kai.Engine.Storage;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Scheduling;

/// <summary>Lets RPC calls wake the scheduler early instead of waiting for the next tick.</summary>
public sealed class SchedulerTrigger
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>
/// Every tick (default one minute): heal inconsistent rows, then lease queued jobs for each agent with free
/// capacity and run them. Lease + heartbeat + fencing guarantee a job is driven by at most one run at a time.
/// </summary>
public sealed class JobSchedulerService : BackgroundService
{
    private readonly JobStore _store;
    private readonly PipelineJobReader _pipeline;
    private readonly AgentJobRunner _runner;
    private readonly EngineInstance _instance;
    private readonly SchedulerTrigger _trigger;
    private readonly EngineOptions _options;
    private readonly ILogger<JobSchedulerService> _logger;
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new();
    private readonly CancellationTokenSource _shutdown = new();

    public JobSchedulerService(
        JobStore store,
        PipelineJobReader pipeline,
        AgentJobRunner runner,
        EngineInstance instance,
        SchedulerTrigger trigger,
        IOptions<EngineOptions> options,
        ILogger<JobSchedulerService> logger)
    {
        _store = store;
        _pipeline = pipeline;
        _runner = runner;
        _instance = instance;
        _trigger = trigger;
        _options = options.Value;
        _logger = logger;
    }

    public int ActiveRunCount => _runs.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Scheduler.Enabled)
        {
            _logger.LogWarning("Scheduler disabled; queued jobs will not be processed.");
            return;
        }
        var interval = TimeSpan.FromSeconds(_options.Scheduler.IntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduler tick failed; retrying next interval");
            }
            try
            {
                await _trigger.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        await RecoverLocalOrphansAsync(ct);
        await ReconcileAsync(ct);

        var lease = TimeSpan.FromSeconds(_options.Scheduler.LeaseSeconds);
        foreach (var agent in _options.Agents.Where(a => a.Enabled))
        {
            while (_runs.Count < _options.Scheduler.MaxConcurrentRuns
                   && _runs.Values.Count(r => string.Equals(r.Agent, agent.Name, StringComparison.OrdinalIgnoreCase)) < agent.MaxConcurrentJobs
                   && !_shutdown.IsCancellationRequested)
            {
                var runId = Guid.NewGuid().ToString("N");
                var owner = $"{_instance.InstanceId}:{runId}";
                var job = await _store.TryAcquireNextAsync(agent.Name, owner, lease, ct);
                if (job is null)
                {
                    break;
                }
                Launch(runId, owner, agent.Name, job);
            }
        }
    }

    /// <summary>Own-instance leases without a live run (e.g. a run task that died unexpectedly) go back to the queue.</summary>
    private async Task RecoverLocalOrphansAsync(CancellationToken ct)
    {
        foreach (var job in await _store.ListByStatesAsync([JobRecordState.Running], ct))
        {
            if (job.LeaseOwner is null || !job.LeaseOwner.StartsWith(_instance.InstanceId + ":", StringComparison.Ordinal))
            {
                continue;
            }
            var runId = job.LeaseOwner[(_instance.InstanceId.Length + 1)..];
            if (!_runs.ContainsKey(runId))
            {
                _logger.LogWarning("Releasing orphaned in-process lease for {Sha}", job.Sha256);
                await _store.FailAttemptAsync(job.Sha256, job.LeaseOwner, "Run ended unexpectedly; job requeued.",
                    _options.Scheduler.MaxAttempts, TimeSpan.Zero, terminal: false, ct);
            }
        }
    }

    /// <summary>Heals rows whose book was finished outside the engine (or before a crash was recorded).</summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        foreach (var job in await _store.ListByStatesAsync([JobRecordState.Queued, JobRecordState.Failed], ct))
        {
            var snapshot = _pipeline.FindBySha(job.Sha256);
            if (snapshot?.IsCompleted == true)
            {
                await _store.MarkCompletedFromPipelineAsync(job.Sha256, snapshot.JobId, "Completed (reconciled from job.json).", ct);
            }
        }
    }

    private void Launch(string runId, string owner, string agent, JobRecord job)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var run = new ActiveRun(agent, job.Sha256, owner, cts);
        _runs[runId] = run;
        _logger.LogInformation("Agent {Agent} acquired {Sha} (attempt {Attempt})", agent, job.Sha256, job.Attempts);
        run.Task = Task.Run(async () =>
        {
            try
            {
                await ExecuteRunAsync(run, job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Run bookkeeping failed for {Sha}; the lease will be recovered", job.Sha256);
            }
            finally
            {
                _runs.TryRemove(runId, out _);
                cts.Dispose();
                _trigger.Notify();
            }
        });
    }

    private async Task ExecuteRunAsync(ActiveRun run, JobRecord job)
    {
        var heartbeat = HeartbeatAsync(run);
        AgentRunOutcome outcome;
        try
        {
            outcome = await _runner.RunAsync(job, run.LeaseOwner, run.Cancellation.Token);
        }
        finally
        {
            run.StopHeartbeat();
            await heartbeat;
        }

        if (run.LeaseLost)
        {
            _logger.LogWarning("Lease for {Sha} was lost; discarding run outcome {Outcome}", job.Sha256, outcome.Kind);
            return;
        }

        // Bookkeeping must survive shutdown cancellation, so it uses its own bounded token.
        using var bookkeeping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var scheduler = _options.Scheduler;
        switch (outcome.Kind)
        {
            case AgentRunOutcomeKind.Completed:
                await _store.CompleteAsync(job.Sha256, run.LeaseOwner, outcome.Message, bookkeeping.Token);
                _logger.LogInformation("Job {Sha} completed", job.Sha256);
                break;
            case AgentRunOutcomeKind.Blocked:
                await _store.FailAttemptAsync(job.Sha256, run.LeaseOwner, outcome.Message, scheduler.MaxAttempts, TimeSpan.Zero, terminal: true, bookkeeping.Token);
                _logger.LogWarning("Job {Sha} blocked: {Message}", job.Sha256, outcome.Message);
                break;
            case AgentRunOutcomeKind.Retryable:
                var state = await _store.FailAttemptAsync(job.Sha256, run.LeaseOwner, outcome.Message, scheduler.MaxAttempts,
                    TimeSpan.FromSeconds(scheduler.RetryBackoffSeconds), terminal: false, bookkeeping.Token);
                _logger.LogWarning("Job {Sha} attempt failed ({State}): {Message}", job.Sha256, state, outcome.Message);
                break;
            case AgentRunOutcomeKind.Cancelled:
                await _store.ReleaseAsync(job.Sha256, run.LeaseOwner, "Engine stopped; job requeued.", bookkeeping.Token);
                _logger.LogInformation("Job {Sha} released for a later run", job.Sha256);
                break;
        }
    }

    private async Task HeartbeatAsync(ActiveRun run)
    {
        var lease = TimeSpan.FromSeconds(_options.Scheduler.LeaseSeconds);
        var every = TimeSpan.FromSeconds(_options.Scheduler.LeaseRenewSeconds);
        var failures = 0;
        while (!run.HeartbeatStopped.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, run.HeartbeatStopped);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (!await _store.RenewLeaseAsync(run.Sha256, run.LeaseOwner, lease, CancellationToken.None))
                {
                    _logger.LogError("Lease for {Sha} was taken away; cancelling run", run.Sha256);
                    run.LeaseLost = true;
                    await run.Cancellation.CancelAsync();
                    return;
                }
                failures = 0;
            }
            catch (Exception ex) when (++failures * every < lease)
            {
                _logger.LogWarning(ex, "Lease renewal for {Sha} failed ({Failures}); will retry", run.Sha256, failures);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lease renewal for {Sha} keeps failing; cancelling run", run.Sha256);
                run.LeaseLost = true;
                await run.Cancellation.CancelAsync();
                return;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        var pending = _runs.Values.Select(r => r.Task).Where(t => t is not null).Cast<Task>().ToArray();
        if (pending.Length > 0)
        {
            _logger.LogInformation("Waiting for {Count} run(s) to release their jobs", pending.Length);
            try
            {
                await Task.WhenAll(pending).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Shutdown timeout reached; unreleased leases are recovered on next start");
            }
        }
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _shutdown.Dispose();
        base.Dispose();
    }

    private sealed class ActiveRun(string agent, string sha256, string leaseOwner, CancellationTokenSource cancellation)
    {
        private readonly CancellationTokenSource _heartbeatStop = new();

        public string Agent { get; } = agent;
        public string Sha256 { get; } = sha256;
        public string LeaseOwner { get; } = leaseOwner;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken HeartbeatStopped => _heartbeatStop.Token;
        private volatile bool _leaseLost;

        public bool LeaseLost { get => _leaseLost; set => _leaseLost = value; }
        public Task? Task { get; set; }

        public void StopHeartbeat() => _heartbeatStop.Cancel();
    }
}
