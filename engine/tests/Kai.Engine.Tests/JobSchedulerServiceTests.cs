using Kai.Engine.Agents;
using Kai.Engine.Infrastructure;
using Kai.Engine.Pipeline;
using Kai.Engine.Scheduling;
using Kai.Engine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Tests;

public sealed class JobSchedulerServiceTests : IDisposable
{
    private readonly TestRepository _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Tick_leases_runs_and_completes_jobs_and_reconciles_finished_ones()
    {
        var (store, _, layout) = _repo.CreateStore();
        var reader = new PipelineJobReader(layout, NullLogger<PipelineJobReader>.Instance);
        var options = Options.Create(_repo.CreateOptions());

        var bytes = TestRepository.CreatePdfBytes("sched");
        var sha = TestRepository.Sha(bytes);
        var jobId = TestRepository.JobIdFor("sched", sha);
        File.WriteAllBytes(Path.Combine(_repo.Root, "_pdfs", "sched.pdf"), bytes);
        _repo.WritePipelineJob(jobId, sha, "_pdfs/sched.pdf", "running");
        await store.TryInsertAsync(new NewJob(sha, "sched.pdf", "_pdfs/sched.pdf", bytes.Length, "Huong", null));

        // A queued row whose book was already finished elsewhere must be healed, not re-run.
        var doneSha = new string('d', 64);
        _repo.WritePipelineJob(TestRepository.JobIdFor("done", doneSha), doneSha, "_pdfs/done.pdf", "completed", 2, 2);
        await store.TryInsertAsync(new NewJob(doneSha, "done.pdf", "_pdfs/done.pdf", 1, "Mai", null));

        var sessions = new CompletingSessionFactory(() => _repo.WritePipelineJob(jobId, sha, "_pdfs/sched.pdf", "completed", 2, 2));
        var runner = new AgentJobRunner(sessions, new NoopScripts(), reader, store, layout, options, NullLogger<AgentJobRunner>.Instance);
        using var instance = new EngineInstance(layout);
        using var scheduler = new JobSchedulerService(store, reader, runner, instance, new SchedulerTrigger(), options, NullLogger<JobSchedulerService>.Instance);

        await scheduler.TickAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await store.GetAsync(sha))!.State != JobRecordState.Completed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var job = await store.GetAsync(sha);
        Assert.Equal(JobRecordState.Completed, job!.State);
        Assert.Null(job.LeaseOwner);
        Assert.Equal(1, job.Attempts);
        Assert.Equal(1, sessions.Opened);

        var healed = await store.GetAsync(doneSha);
        Assert.Equal(JobRecordState.Completed, healed!.State);
        Assert.Equal(0, healed.Attempts);

        var events = await store.GetEventsAsync(sha);
        Assert.Contains(events, e => e.Kind == "acquired");
        Assert.Contains(events, e => e.Kind == "completed");
    }

    private sealed class NoopScripts : IPipelineScriptRunner
    {
        public Task InitializeJobsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CompletingSessionFactory(Action onTurn) : IAgentSessionFactory, IAgentSession
    {
        public int Opened { get; private set; }
        public string SessionId => "fake";
        public bool Resumed => false;

        public Task<IAgentSession> OpenAsync(AgentSessionRequest request, CancellationToken cancellationToken)
        {
            Opened++;
            return Task.FromResult<IAgentSession>(this);
        }

        public Task<AgentTurnResult> RunTurnAsync(string prompt, Func<bool> isPipelineTerminal, CancellationToken cancellationToken)
        {
            onTurn();
            return Task.FromResult(new AgentTurnResult(AgentTurnEnd.Idle));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
