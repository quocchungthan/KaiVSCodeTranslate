using Kai.Engine.Agents;
using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Kai.Engine.Pipeline;
using Kai.Engine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Tests;

public sealed class AgentJobRunnerTests : IDisposable
{
    private const string Owner = "inst:run1";
    private readonly TestRepository _repo = new();
    private readonly JobStore _store;
    private readonly RepositoryLayout _layout;
    private readonly PipelineJobReader _reader;
    private readonly string _sha;
    private readonly string _jobId;

    public AgentJobRunnerTests()
    {
        (_store, _, _layout) = _repo.CreateStore();
        _reader = new PipelineJobReader(_layout, NullLogger<PipelineJobReader>.Instance);
        var bytes = TestRepository.CreatePdfBytes("runner");
        _sha = TestRepository.Sha(bytes);
        _jobId = TestRepository.JobIdFor("book", _sha);
        File.WriteAllBytes(Path.Combine(_repo.Root, "_pdfs", "book.pdf"), bytes);
    }

    public void Dispose() => _repo.Dispose();

    private async Task<JobRecord> LeaseAsync()
    {
        await _store.TryInsertAsync(new NewJob(_sha, "book.pdf", "_pdfs/book.pdf", 1, "Huong", null));
        return (await _store.TryAcquireNextAsync("Huong", Owner, TimeSpan.FromMinutes(5)))!;
    }

    private AgentJobRunner CreateRunner(FakeSessionFactory sessions, FakeScripts scripts, int maxStalled = 2) =>
        new(sessions, scripts, _reader, _store, _layout,
            Options.Create(_repo.CreateOptions(o => { o.Copilot.MaxStalledTurns = maxStalled; o.Copilot.MaxContinuations = 10; })),
            NullLogger<AgentJobRunner>.Instance);

    [Fact]
    public async Task Initializes_pipeline_job_and_finishes_across_turns()
    {
        var job = await LeaseAsync();
        var scripts = new FakeScripts(() => _repo.WritePipelineJob(_jobId, _sha, "_pdfs/book.pdf", "running"));
        var turn = 0;
        var sessions = new FakeSessionFactory(_ =>
        {
            turn++;
            _repo.WritePipelineJob(_jobId, _sha, "_pdfs/book.pdf", turn == 3 ? "completed" : "running", chunksCompleted: turn, chunksTotal: 3, updatedUtc: $"2026-01-0{turn}T00:00:00Z");
            return new AgentTurnResult(AgentTurnEnd.Idle);
        });

        var outcome = await CreateRunner(sessions, scripts).RunAsync(job, Owner, CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(1, scripts.Calls);
        Assert.Equal(3, sessions.Prompts.Count);
        Assert.Contains($"-JobId {_jobId}", sessions.Prompts[0]);
        Assert.Contains("unattended", sessions.Prompts[0]);
        Assert.StartsWith("Continue", sessions.Prompts[1].TrimStart());

        var stored = await _store.GetAsync(_sha);
        Assert.Equal(_jobId, stored!.JobId);
        Assert.Equal(sessions.LastSessionId, stored.CopilotSessionId);
    }

    [Fact]
    public async Task Stalled_agent_is_retryable()
    {
        var job = await LeaseAsync();
        _repo.WritePipelineJob(_jobId, _sha, "_pdfs/book.pdf", "running");
        var sessions = new FakeSessionFactory(_ => new AgentTurnResult(AgentTurnEnd.Idle));

        var outcome = await CreateRunner(sessions, new FakeScripts(() => { }), maxStalled: 2).RunAsync(job, Owner, CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.Retryable, outcome.Kind);
        Assert.Equal(2, sessions.Prompts.Count);
        Assert.Contains("No pipeline progress", outcome.Message);
    }

    [Fact]
    public async Task Already_completed_job_does_not_open_a_session()
    {
        var job = await LeaseAsync();
        _repo.WritePipelineJob(_jobId, _sha, "_pdfs/book.pdf", "completed", 2, 2);
        var sessions = new FakeSessionFactory(_ => throw new InvalidOperationException("must not run"));

        var outcome = await CreateRunner(sessions, new FakeScripts(() => { })).RunAsync(job, Owner, CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(0, sessions.Opened);
    }

    [Fact]
    public async Task Blocked_pipeline_is_terminal()
    {
        var job = await LeaseAsync();
        _repo.WritePipelineJob(_jobId, _sha, "_pdfs/book.pdf", "blocked");

        var outcome = await CreateRunner(new FakeSessionFactory(_ => new AgentTurnResult(AgentTurnEnd.Idle)), new FakeScripts(() => { }))
            .RunAsync(job, Owner, CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.Blocked, outcome.Kind);
    }

    [Fact]
    public async Task Missing_source_is_terminal_and_session_errors_are_retryable()
    {
        var job = await LeaseAsync();
        File.Delete(Path.Combine(_repo.Root, "_pdfs", "book.pdf"));
        var outcome = await CreateRunner(new FakeSessionFactory(_ => new AgentTurnResult(AgentTurnEnd.Idle)), new FakeScripts(() => { }))
            .RunAsync(job, Owner, CancellationToken.None);
        Assert.Equal(AgentRunOutcomeKind.Blocked, outcome.Kind);
    }

    [Fact]
    public async Task Lost_lease_cancels_the_run()
    {
        var job = await LeaseAsync();
        _repo.WritePipelineJob(_jobId, _sha, "_pdfs/book.pdf", "running");
        var outcome = await CreateRunner(new FakeSessionFactory(_ => new AgentTurnResult(AgentTurnEnd.Idle)), new FakeScripts(() => { }))
            .RunAsync(job, "inst:someone-else", CancellationToken.None);
        Assert.Equal(AgentRunOutcomeKind.Cancelled, outcome.Kind);
    }

    [Fact]
    public async Task Resumed_session_gets_a_continuation_prompt()
    {
        var job = await LeaseAsync();
        await _store.SetCopilotSessionAsync(_sha, Owner, "existing-session");
        job = (await _store.GetAsync(_sha))!;
        _repo.WritePipelineJob(_jobId, _sha, "_pdfs/book.pdf", "running");
        var sessions = new FakeSessionFactory(_ =>
        {
            _repo.WritePipelineJob(_jobId, _sha, "_pdfs/book.pdf", "completed", 2, 2);
            return new AgentTurnResult(AgentTurnEnd.Idle);
        });

        var outcome = await CreateRunner(sessions, new FakeScripts(() => { })).RunAsync(job, Owner, CancellationToken.None);

        Assert.Equal(AgentRunOutcomeKind.Completed, outcome.Kind);
        Assert.Equal("existing-session", sessions.LastRequest!.ResumeSessionId);
        Assert.StartsWith("Continue", sessions.Prompts[0].TrimStart());
    }

    private sealed class FakeScripts(Action onInitialize) : IPipelineScriptRunner
    {
        public int Calls { get; private set; }

        public Task InitializeJobsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            onInitialize();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessionFactory(Func<string, AgentTurnResult> onTurn) : IAgentSessionFactory
    {
        public List<string> Prompts { get; } = [];
        public Func<string, AgentTurnResult> OnTurn { get; } = onTurn;
        public int Opened { get; private set; }
        public string? LastSessionId { get; private set; }
        public AgentSessionRequest? LastRequest { get; private set; }

        public Task<IAgentSession> OpenAsync(AgentSessionRequest request, CancellationToken cancellationToken)
        {
            Opened++;
            LastRequest = request;
            var resumed = request.ResumeSessionId is not null;
            LastSessionId = resumed ? request.ResumeSessionId : request.NewSessionId;
            return Task.FromResult<IAgentSession>(new FakeSession(LastSessionId!, resumed, this));
        }

        private sealed class FakeSession(string id, bool resumed, FakeSessionFactory owner) : IAgentSession
        {
            public string SessionId => id;
            public bool Resumed => resumed;

            public Task<AgentTurnResult> RunTurnAsync(string prompt, Func<bool> isPipelineTerminal, CancellationToken cancellationToken)
            {
                owner.Prompts.Add(prompt);
                return Task.FromResult(owner.OnTurn(prompt));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
