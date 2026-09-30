using Kai.Engine.Storage;

namespace Kai.Engine.Tests;

public sealed class JobStoreTests : IDisposable
{
    private readonly TestRepository _repo = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly JobStore _store;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public JobStoreTests()
    {
        _store = _repo.CreateStore(_time).Store;
    }

    public void Dispose() => _repo.Dispose();

    private static string Sha(char c) => new(c, 64);

    private Task<bool> InsertAsync(char c, string agent = "Huong") =>
        _store.TryInsertAsync(new NewJob(Sha(c), $"{c}.pdf", $"_pdfs/{c}.pdf", 10, agent, "user-1"));

    [Fact]
    public async Task Insert_is_idempotent_per_sha()
    {
        Assert.True(await InsertAsync('a'));
        Assert.False(await InsertAsync('a'));
        var job = await _store.GetAsync(Sha('a'));
        Assert.NotNull(job);
        Assert.Equal(JobRecordState.Queued, job.State);
        Assert.Equal(1, job.QueueSeq);
    }

    [Fact]
    public async Task Acquire_is_fifo_and_isolated_per_agent()
    {
        await InsertAsync('a');
        await InsertAsync('b', "Mai");
        await InsertAsync('c');

        Assert.Equal(2, await _store.GetQueuePositionAsync((await _store.GetAsync(Sha('c')))!));
        Assert.Equal(1, await _store.GetQueuePositionAsync((await _store.GetAsync(Sha('b')))!));

        var first = await _store.TryAcquireNextAsync("Huong", "i1:r1", Lease);
        var second = await _store.TryAcquireNextAsync("Huong", "i1:r2", Lease);
        var none = await _store.TryAcquireNextAsync("Huong", "i1:r3", Lease);

        Assert.Equal(Sha('a'), first!.Sha256);
        Assert.Equal(JobRecordState.Running, first.State);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(Sha('c'), second!.Sha256);
        Assert.Null(none);
        Assert.Equal(JobRecordState.Queued, (await _store.GetAsync(Sha('b')))!.State);
    }

    [Fact]
    public async Task Lease_fencing_rejects_stale_owner()
    {
        await InsertAsync('a');
        await _store.TryAcquireNextAsync("Huong", "i1:r1", Lease);

        Assert.False(await _store.RenewLeaseAsync(Sha('a'), "i1:other", Lease));
        Assert.False(await _store.CompleteAsync(Sha('a'), "i1:other", null));
        Assert.True(await _store.RenewLeaseAsync(Sha('a'), "i1:r1", Lease));
        Assert.True(await _store.CompleteAsync(Sha('a'), "i1:r1", "done"));

        var job = await _store.GetAsync(Sha('a'));
        Assert.Equal(JobRecordState.Completed, job!.State);
        Assert.Null(job.LeaseOwner);
        Assert.NotNull(job.CompletedUtc);
    }

    [Fact]
    public async Task Failed_attempt_requeues_with_backoff_then_fails_when_exhausted()
    {
        await InsertAsync('a');
        await _store.TryAcquireNextAsync("Huong", "i1:r1", Lease);
        var state = await _store.FailAttemptAsync(Sha('a'), "i1:r1", "boom", maxAttempts: 2, TimeSpan.FromMinutes(5), terminal: false);
        Assert.Equal(JobRecordState.Queued, state);

        Assert.Null(await _store.TryAcquireNextAsync("Huong", "i1:r2", Lease));
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.NotNull(await _store.TryAcquireNextAsync("Huong", "i1:r2", Lease));

        state = await _store.FailAttemptAsync(Sha('a'), "i1:r2", "boom again", maxAttempts: 2, TimeSpan.FromMinutes(5), terminal: false);
        Assert.Equal(JobRecordState.Failed, state);

        Assert.True(await _store.RequeueFailedAsync(Sha('a'), null));
        var job = await _store.GetAsync(Sha('a'));
        Assert.Equal(JobRecordState.Queued, job!.State);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public async Task Terminal_failure_skips_retries()
    {
        await InsertAsync('a');
        await _store.TryAcquireNextAsync("Huong", "i1:r1", Lease);
        Assert.Equal(JobRecordState.Failed, await _store.FailAttemptAsync(Sha('a'), "i1:r1", "blocked", 3, TimeSpan.Zero, terminal: true));
    }

    [Fact]
    public async Task Release_refunds_the_attempt()
    {
        await InsertAsync('a');
        await _store.TryAcquireNextAsync("Huong", "i1:r1", Lease);
        Assert.True(await _store.ReleaseAsync(Sha('a'), "i1:r1", "shutdown"));
        var job = await _store.GetAsync(Sha('a'));
        Assert.Equal(JobRecordState.Queued, job!.State);
        Assert.Equal(0, job.Attempts);
    }

    [Fact]
    public async Task Orphaned_leases_of_dead_instances_are_recovered()
    {
        await InsertAsync('a');
        await InsertAsync('b');
        await _store.TryAcquireNextAsync("Huong", "dead:r1", Lease);
        await _store.TryAcquireNextAsync("Huong", "alive:r1", Lease);

        Assert.Equal(1, await _store.RecoverOrphanedLeasesAsync("alive", maxAttempts: 3));
        Assert.Equal(JobRecordState.Queued, (await _store.GetAsync(Sha('a')))!.State);
        Assert.Equal(JobRecordState.Running, (await _store.GetAsync(Sha('b')))!.State);

        var events = await _store.GetEventsAsync(Sha('a'));
        Assert.Contains(events, e => e.Kind == "lease-recovered");
    }

    [Fact]
    public async Task Poison_job_fails_after_max_attempts_on_recovery()
    {
        await InsertAsync('a');
        await _store.TryAcquireNextAsync("Huong", "dead:r1", Lease);
        Assert.Equal(1, await _store.RecoverOrphanedLeasesAsync("alive", maxAttempts: 1));
        Assert.Equal(JobRecordState.Failed, (await _store.GetAsync(Sha('a')))!.State);
    }

    [Fact]
    public async Task Reconciliation_completes_queued_job_but_not_running_one()
    {
        await InsertAsync('a');
        await InsertAsync('b');
        await _store.TryAcquireNextAsync("Huong", "i1:r1", Lease);

        Assert.False(await _store.MarkCompletedFromPipelineAsync(Sha('a'), "job-a", "reconciled"));
        Assert.True(await _store.MarkCompletedFromPipelineAsync(Sha('b'), "job-b", "reconciled"));
        Assert.Equal(JobRecordState.Completed, (await _store.GetAsync(Sha('b')))!.State);
    }

    [Fact]
    public async Task List_pages_newest_first()
    {
        foreach (var c in "abcde")
        {
            await InsertAsync(c);
        }
        var page1 = await _store.ListAsync(null, null, 2);
        var page2 = await _store.ListAsync(null, page1[^1].QueueSeq, 2);
        Assert.Equal([Sha('e'), Sha('d')], page1.Select(j => j.Sha256));
        Assert.Equal([Sha('c'), Sha('b')], page2.Select(j => j.Sha256));

        var counts = await _store.CountByStateAsync();
        Assert.Equal(5, counts[JobRecordState.Queued]);
    }
}
