using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Kai.Engine.Configuration;
using Kai.Engine.Contracts.V1;
using Kai.Engine.Ingest;
using Kai.Engine.Pipeline;
using Kai.Engine.Scheduling;
using Kai.Engine.Storage;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Rpc;

public sealed class TranslationEngineRpcService : TranslationEngine.TranslationEngineBase
{
    internal const int DownloadChunkBytes = 256 * 1024;
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), string> ArtifactHashes = new();

    private readonly FileIngestService _ingest;
    private readonly JobStore _store;
    private readonly JobStatusService _status;
    private readonly PipelineJobReader _pipeline;
    private readonly SchedulerTrigger _trigger;
    private readonly EngineOptions _options;
    private readonly ILogger<TranslationEngineRpcService> _logger;

    public TranslationEngineRpcService(
        FileIngestService ingest,
        JobStore store,
        JobStatusService status,
        PipelineJobReader pipeline,
        SchedulerTrigger trigger,
        IOptions<EngineOptions> options,
        ILogger<TranslationEngineRpcService> logger)
    {
        _ingest = ingest;
        _store = store;
        _status = status;
        _pipeline = pipeline;
        _trigger = trigger;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task<UploadFileResponse> UploadFile(IAsyncStreamReader<UploadFileRequest> requestStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (!await requestStream.MoveNext(ct) || requestStream.Current.PayloadCase != UploadFileRequest.PayloadOneofCase.Metadata)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must be metadata."));
        }
        var metadata = requestStream.Current.Metadata;
        var agent = ResolveAgent(metadata.AgentName);
        var request = new IngestRequest(
            metadata.FileName,
            metadata.SizeBytes,
            string.IsNullOrWhiteSpace(metadata.ExpectedSha256) ? null : metadata.ExpectedSha256.Trim().ToLowerInvariant(),
            agent,
            string.IsNullOrWhiteSpace(metadata.RequestedBy) ? null : metadata.RequestedBy.Trim());

        IngestResult result;
        try
        {
            result = await _ingest.IngestAsync(request, ReadChunks(requestStream, ct), ct);
        }
        catch (IngestException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        if (!result.Deduplicated || result.Job.State == JobRecordState.Queued)
        {
            _trigger.Notify();
        }
        return new UploadFileResponse
        {
            Sha256 = result.Job.Sha256,
            Deduplicated = result.Deduplicated,
            Status = await _status.BuildAsync(result.Job, ct),
        };
    }

    public override async Task<JobStatus> GetJobStatus(GetJobStatusRequest request, ServerCallContext context)
    {
        var job = await GetJobAsync(request.Sha256, context.CancellationToken);
        return await _status.BuildAsync(job, context.CancellationToken);
    }

    public override async Task<ListJobsResponse> ListJobs(ListJobsRequest request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var pageSize = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);
        long? before = null;
        if (!string.IsNullOrEmpty(request.PageToken))
        {
            before = DecodePageToken(request.PageToken);
        }

        var rows = await _store.ListAsync(JobStatusService.Map(request.StateFilter), before, pageSize + 1, ct);
        var response = new ListJobsResponse();
        foreach (var job in rows.Take(pageSize))
        {
            response.Jobs.Add(await _status.BuildAsync(job, ct));
        }
        if (rows.Count > pageSize)
        {
            response.NextPageToken = EncodePageToken(rows[pageSize - 1].QueueSeq);
        }
        return response;
    }

    public override async Task DownloadResult(DownloadResultRequest request, IServerStreamWriter<DownloadResultChunk> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var job = await GetJobAsync(request.Sha256, ct);
        var snapshot = (job.JobId is { Length: > 0 } ? _pipeline.Read(job.JobId) : null) ?? _pipeline.FindBySha(job.Sha256);
        if (snapshot is not { IsCompleted: true })
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The translation is not completed yet."));
        }
        var markdown = request.Kind == ResultKind.Markdown;
        var path = _pipeline.ResolveArtifact(snapshot, markdown)
                   ?? throw new RpcException(new Status(StatusCode.NotFound, "The translated artifact is missing on the engine."));

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var info = new FileInfo(path);
        if (request.Offset < 0 || request.Offset > stream.Length)
        {
            throw new RpcException(new Status(StatusCode.OutOfRange, $"offset must be between 0 and {stream.Length}."));
        }
        var sha256 = await GetArtifactHashAsync(path, info, ct);

        await responseStream.WriteAsync(new DownloadResultChunk
        {
            Header = new DownloadHeader
            {
                FileName = $"{Path.GetFileNameWithoutExtension(job.FileName)}.vi{(markdown ? ".md" : ".pdf")}",
                ContentType = markdown ? "text/markdown; charset=utf-8" : "application/pdf",
                TotalSize = stream.Length,
                Sha256 = sha256,
                Offset = request.Offset,
            },
        }, ct);

        stream.Seek(request.Offset, SeekOrigin.Begin);
        var buffer = new byte[DownloadChunkBytes];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await responseStream.WriteAsync(new DownloadResultChunk { Data = ByteString.CopyFrom(buffer, 0, read) }, ct);
        }
    }

    public override async Task<EngineInfo> GetEngineInfo(GetEngineInfoRequest request, ServerCallContext context)
    {
        var counts = await _store.CountByStateAsync(context.CancellationToken);
        var info = new EngineInfo
        {
            Version = typeof(TranslationEngineRpcService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            Queued = counts.GetValueOrDefault(JobRecordState.Queued),
            Running = counts.GetValueOrDefault(JobRecordState.Running),
            ServerTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        info.Agents.AddRange(_options.Agents.Where(a => a.Enabled).Select(a => a.Name));
        return info;
    }

    private string ResolveAgent(string? requested)
    {
        var name = string.IsNullOrWhiteSpace(requested) ? _options.DefaultAgent : requested.Trim();
        var agent = _options.Agents.FirstOrDefault(a => a.Enabled && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        return agent?.Name ?? throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown or disabled agent '{name}'."));
    }

    private async Task<JobRecord> GetJobAsync(string? sha256, CancellationToken ct)
    {
        var normalized = sha256?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!FileIngestService.IsSha256(normalized))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sha256 must be 64 hex characters."));
        }
        return await _store.GetAsync(normalized, ct)
               ?? throw new RpcException(new Status(StatusCode.NotFound, "No job with this sha256."));
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunks(IAsyncStreamReader<UploadFileRequest> stream, [EnumeratorCancellation] CancellationToken ct)
    {
        while (await stream.MoveNext(ct))
        {
            if (stream.Current.PayloadCase != UploadFileRequest.PayloadOneofCase.Chunk)
            {
                throw new IngestException("Only chunk messages may follow the metadata.");
            }
            yield return stream.Current.Chunk.Memory;
        }
    }

    private static async Task<string> GetArtifactHashAsync(string path, FileInfo info, CancellationToken ct)
    {
        var key = (path, info.Length, info.LastWriteTimeUtc);
        if (ArtifactHashes.TryGetValue(key, out var cached))
        {
            return cached;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        ArtifactHashes[key] = hash;
        return hash;
    }

    private static string EncodePageToken(long queueSeq) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(queueSeq.ToString(CultureInfo.InvariantCulture)));

    private static long DecodePageToken(string token)
    {
        try
        {
            return long.Parse(Encoding.ASCII.GetString(Convert.FromBase64String(token)), NumberStyles.None, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid page_token."));
        }
    }
}
