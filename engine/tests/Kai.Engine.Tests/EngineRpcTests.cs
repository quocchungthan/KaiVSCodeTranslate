using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Kai.Engine.Contracts.V1;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Kai.Engine.Tests;

public sealed class EngineRpcTests : IDisposable
{
    private readonly TestRepository _repo = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly GrpcChannel _channel;
    private readonly TranslationEngine.TranslationEngineClient _client;
    private readonly Metadata _auth = new() { { "x-engine-key", TestRepository.SecretKey } };
    private readonly string _importedSha;

    public EngineRpcTests()
    {
        // A book translated locally before the engine started must show up in the gallery.
        var importedBytes = TestRepository.CreatePdfBytes("imported");
        _importedSha = TestRepository.Sha(importedBytes);
        File.WriteAllBytes(Path.Combine(_repo.Root, "_pdfs", "old-book.pdf"), importedBytes);
        var importedJobId = TestRepository.JobIdFor("old-book", _importedSha);
        _repo.WritePipelineJob(importedJobId, _importedSha, "_pdfs/old-book.pdf", "completed", 2, 2, $"_output/{importedJobId}/book.vi.pdf");
        Directory.CreateDirectory(Path.Combine(_repo.Root, "_output", importedJobId));
        File.WriteAllBytes(Path.Combine(_repo.Root, "_output", importedJobId, "book.vi.pdf"), TestRepository.CreatePdfBytes("translated", 700_000));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:RepositoryRoot"] = _repo.Root,
                ["Engine:SecretKey"] = TestRepository.SecretKey,
                ["Engine:Scheduler:Enabled"] = "false",
                ["Engine:MaxUploadBytes"] = "1000000",
            }));
        });
        _channel = GrpcChannel.ForAddress(_factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        _client = new TranslationEngine.TranslationEngineClient(_channel);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _factory.Dispose();
        _repo.Dispose();
    }

    private async Task<UploadFileResponse> UploadAsync(string fileName, byte[] bytes, long? declaredSize = null, string? expectedSha = null, string? agent = null)
    {
        using var call = _client.UploadFile(_auth);
        await call.RequestStream.WriteAsync(new UploadFileRequest
        {
            Metadata = new UploadMetadata
            {
                FileName = fileName,
                SizeBytes = declaredSize ?? bytes.Length,
                ExpectedSha256 = expectedSha ?? string.Empty,
                AgentName = agent ?? string.Empty,
                RequestedBy = "user-42",
            },
        });
        foreach (var chunk in bytes.Chunk(1000))
        {
            await call.RequestStream.WriteAsync(new UploadFileRequest { Chunk = ByteString.CopyFrom(chunk) });
        }
        await call.RequestStream.CompleteAsync();
        return await call;
    }

    [Fact]
    public async Task Calls_without_the_secret_are_rejected()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => _client.GetEngineInfoAsync(new GetEngineInfoRequest()).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);

        var wrong = new Metadata { { "x-engine-key", "not-the-key-not-the-key-not-the-key" } };
        ex = await Assert.ThrowsAsync<RpcException>(() => _client.GetEngineInfoAsync(new GetEngineInfoRequest(), wrong).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task Upload_returns_hash_queue_position_and_deduplicates()
    {
        var first = TestRepository.CreatePdfBytes("first");
        var second = TestRepository.CreatePdfBytes("second");

        var response1 = await UploadAsync("My Book.pdf", first, expectedSha: TestRepository.Sha(first));
        var response2 = await UploadAsync("../../evil\\Other Book.pdf", second);

        Assert.Equal(TestRepository.Sha(first), response1.Sha256);
        Assert.False(response1.Deduplicated);
        Assert.Equal(JobState.Queued, response1.Status.State);
        Assert.Equal(1, response1.Status.QueuePosition);
        Assert.Equal("Huong", response1.Status.AssignedAgent);
        Assert.Equal(2, response2.Status.QueuePosition);
        Assert.True(File.Exists(Path.Combine(_repo.Root, "_pdfs", "My Book.pdf")));
        Assert.True(File.Exists(Path.Combine(_repo.Root, "_pdfs", "Other Book.pdf")));

        var again = await UploadAsync("renamed.pdf", first);
        Assert.True(again.Deduplicated);
        Assert.Equal(response1.Sha256, again.Sha256);
        Assert.False(File.Exists(Path.Combine(_repo.Root, "_pdfs", "renamed.pdf")));

        var status = await _client.GetJobStatusAsync(new GetJobStatusRequest { Sha256 = response2.Sha256 }, _auth);
        Assert.Equal(JobState.Queued, status.State);
        Assert.Equal(2, status.QueuePosition);
    }

    [Fact]
    public async Task Same_name_with_different_content_never_overwrites_a_source()
    {
        var first = TestRepository.CreatePdfBytes("one");
        var second = TestRepository.CreatePdfBytes("two");
        await UploadAsync("book.pdf", first);
        var response = await UploadAsync("book.pdf", second);

        Assert.Equal(TestRepository.Sha(first), TestRepository.Sha(File.ReadAllBytes(Path.Combine(_repo.Root, "_pdfs", "book.pdf"))));
        Assert.True(File.Exists(Path.Combine(_repo.Root, "_pdfs", $"book-{response.Sha256[..8]}.pdf")));
    }

    [Fact]
    public async Task Invalid_uploads_are_rejected_without_leftovers()
    {
        var notPdf = new byte[2048];
        var ex = await Assert.ThrowsAsync<RpcException>(() => UploadAsync("x.pdf", notPdf));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        var pdf = TestRepository.CreatePdfBytes("size");
        ex = await Assert.ThrowsAsync<RpcException>(() => UploadAsync("x.pdf", pdf, declaredSize: pdf.Length + 10));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        ex = await Assert.ThrowsAsync<RpcException>(() => UploadAsync("x.pdf", pdf, expectedSha: new string('0', 64)));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        ex = await Assert.ThrowsAsync<RpcException>(() => UploadAsync("x.pdf", pdf, agent: "Nobody"));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        ex = await Assert.ThrowsAsync<RpcException>(() => UploadAsync("big.pdf", TestRepository.CreatePdfBytes("big", 1_000_001)));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        Assert.Empty(Directory.GetFiles(Path.Combine(_repo.Root, "_pdfs"), "x*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_repo.Root, "_processing", "engine", "incoming")));
    }

    [Fact]
    public async Task Status_validates_and_reports_unknown_hashes()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(() => _client.GetJobStatusAsync(new GetJobStatusRequest { Sha256 = "abc" }, _auth).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        ex = await Assert.ThrowsAsync<RpcException>(() => _client.GetJobStatusAsync(new GetJobStatusRequest { Sha256 = new string('f', 64) }, _auth).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Imported_completed_job_is_listed_and_downloadable_with_resume()
    {
        var list = await _client.ListJobsAsync(new ListJobsRequest { StateFilter = JobState.Completed }, _auth);
        var imported = Assert.Single(list.Jobs);
        Assert.Equal(_importedSha, imported.Sha256);
        Assert.True(imported.ResultAvailable);

        var full = await DownloadAsync(0);
        var expected = File.ReadAllBytes(Directory.GetFiles(Path.Combine(_repo.Root, "_output"), "book.vi.pdf", SearchOption.AllDirectories).Single());
        Assert.Equal(expected, full.Data);
        Assert.Equal("old-book.vi.pdf", full.Header.FileName);
        Assert.Equal("application/pdf", full.Header.ContentType);
        Assert.Equal(TestRepository.Sha(expected), full.Header.Sha256);

        var resumed = await DownloadAsync(500_000);
        Assert.Equal(expected[500_000..], resumed.Data);

        var ex = await Assert.ThrowsAsync<RpcException>(() => DownloadAsync(expected.Length + 1));
        Assert.Equal(StatusCode.OutOfRange, ex.StatusCode);
    }

    [Fact]
    public async Task Download_of_queued_job_is_a_failed_precondition()
    {
        var upload = await UploadAsync("pending.pdf", TestRepository.CreatePdfBytes("pending"));
        using var call = _client.DownloadResult(new DownloadResultRequest { Sha256 = upload.Sha256 }, _auth);
        var ex = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext());
        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
    }

    [Fact]
    public async Task Engine_info_reports_agents_and_counts()
    {
        await UploadAsync("a.pdf", TestRepository.CreatePdfBytes("a"));
        var info = await _client.GetEngineInfoAsync(new GetEngineInfoRequest(), _auth);
        Assert.Contains("Huong", info.Agents);
        Assert.Equal(1, info.Queued);
        Assert.Equal(0, info.Running);
    }

    [Fact]
    public async Task List_pagination_walks_all_jobs()
    {
        for (var i = 0; i < 5; i++)
        {
            await UploadAsync($"p{i}.pdf", TestRepository.CreatePdfBytes($"p{i}"));
        }
        var seen = new List<string>();
        var token = string.Empty;
        do
        {
            var page = await _client.ListJobsAsync(new ListJobsRequest { PageSize = 2, PageToken = token }, _auth);
            seen.AddRange(page.Jobs.Select(j => j.Sha256));
            token = page.NextPageToken;
        } while (!string.IsNullOrEmpty(token));
        Assert.Equal(6, seen.Distinct().Count());
    }

    private async Task<(DownloadHeader Header, byte[] Data)> DownloadAsync(long offset)
    {
        using var call = _client.DownloadResult(new DownloadResultRequest { Sha256 = _importedSha, Kind = ResultKind.Pdf, Offset = offset }, _auth);
        DownloadHeader? header = null;
        using var data = new MemoryStream();
        await foreach (var chunk in call.ResponseStream.ReadAllAsync())
        {
            if (chunk.PayloadCase == DownloadResultChunk.PayloadOneofCase.Header)
            {
                header = chunk.Header;
            }
            else
            {
                chunk.Data.WriteTo(data);
            }
        }
        return (header!, data.ToArray());
    }
}
