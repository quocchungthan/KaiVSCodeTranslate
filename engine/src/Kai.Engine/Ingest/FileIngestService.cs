using System.Security.Cryptography;
using System.Text;
using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Kai.Engine.Pipeline;
using Kai.Engine.Storage;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Ingest;

public sealed class IngestException(string message) : Exception(message);

public sealed record IngestRequest(string FileName, long DeclaredSize, string? ExpectedSha256, string AgentName, string? RequestedBy);

public sealed record IngestResult(JobRecord Job, bool Deduplicated);

/// <summary>
/// Streams an upload to a temp file while hashing, then publishes it into <c>_pdfs/</c> exactly once per SHA-256.
/// Crash safety: bytes land in _processing/engine/incoming first; a move (same volume, atomic) then the DB insert.
/// A crash between the two leaves a PDF that a re-upload adopts idempotently.
/// </summary>
public sealed class FileIngestService
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private const int MaxFileNameLength = 120;

    private readonly RepositoryLayout _layout;
    private readonly JobStore _store;
    private readonly PipelineJobReader _pipeline;
    private readonly EngineOptions _options;
    private readonly ILogger<FileIngestService> _logger;
    private readonly SemaphoreSlim _commitLock = new(1, 1);

    public FileIngestService(RepositoryLayout layout, JobStore store, PipelineJobReader pipeline, IOptions<EngineOptions> options, ILogger<FileIngestService> logger)
    {
        _layout = layout;
        _store = store;
        _pipeline = pipeline;
        _options = options.Value;
        _logger = logger;
    }

    public void CleanupStaleIncoming()
    {
        if (!Directory.Exists(_layout.IncomingRoot))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(_layout.IncomingRoot, "*.part"))
        {
            try { File.Delete(file); }
            catch (IOException ex) { _logger.LogWarning(ex, "Could not delete stale upload {File}", file); }
        }
    }

    public async Task<IngestResult> IngestAsync(IngestRequest request, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken ct)
    {
        Validate(request);
        Directory.CreateDirectory(_layout.IncomingRoot);
        var tempPath = Path.Combine(_layout.IncomingRoot, $"{Guid.NewGuid():N}.part");
        try
        {
            var sha256 = await ReceiveAsync(request, chunks, tempPath, ct);
            return await CommitAsync(request, sha256, tempPath, ct);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task<string> ReceiveAsync(IngestRequest request, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, string tempPath, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        var header = new byte[PdfMagic.Length];
        var headerLength = 0;

        await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                if (chunk.IsEmpty)
                {
                    continue;
                }
                written += chunk.Length;
                if (written > request.DeclaredSize || written > _options.MaxUploadBytes)
                {
                    throw new IngestException("Upload exceeds the declared or maximum size.");
                }
                if (headerLength < header.Length)
                {
                    var take = Math.Min(header.Length - headerLength, chunk.Length);
                    chunk.Span[..take].CopyTo(header.AsSpan(headerLength));
                    headerLength += take;
                }
                hash.AppendData(chunk.Span);
                await output.WriteAsync(chunk, ct);
            }
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
        }

        if (written != request.DeclaredSize)
        {
            throw new IngestException($"Upload truncated: received {written} of {request.DeclaredSize} bytes.");
        }
        if (headerLength < PdfMagic.Length || !header.AsSpan().SequenceEqual(PdfMagic))
        {
            throw new IngestException("Only PDF files are accepted.");
        }

        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (request.ExpectedSha256 is { Length: > 0 } expected && !string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IngestException("SHA-256 mismatch between client and received bytes.");
        }
        return sha256;
    }

    private async Task<IngestResult> CommitAsync(IngestRequest request, string sha256, string tempPath, CancellationToken ct)
    {
        await _commitLock.WaitAsync(ct);
        try
        {
            var existing = await _store.GetAsync(sha256, ct);
            if (existing is not null)
            {
                if (existing.State == JobRecordState.Failed && File.Exists(Path.Combine(_layout.Root, existing.SourceRelativePath)))
                {
                    await _store.RequeueFailedAsync(sha256, request.AgentName, ct);
                    existing = await _store.GetAsync(sha256, ct) ?? existing;
                }
                return new IngestResult(existing, Deduplicated: true);
            }

            var (sourcePath, placed) = PlaceSource(request.FileName, sha256, tempPath);
            var pipelineJob = _pipeline.FindBySha(sha256);
            var alreadyCompleted = pipelineJob?.IsCompleted == true;
            try
            {
                var inserted = await _store.TryInsertAsync(new NewJob(
                    sha256,
                    Path.GetFileName(sourcePath),
                    _layout.ToRelative(sourcePath),
                    request.DeclaredSize,
                    request.AgentName,
                    request.RequestedBy,
                    State: alreadyCompleted ? JobRecordState.Completed : JobRecordState.Queued,
                    JobId: pipelineJob?.JobId,
                    Message: alreadyCompleted ? "Already translated locally." : null), ct);
                if (!inserted)
                {
                    throw new InvalidOperationException("Job insert raced unexpectedly.");
                }
            }
            catch when (placed)
            {
                TryDelete(sourcePath);
                throw;
            }

            var job = await _store.GetAsync(sha256, ct) ?? throw new InvalidOperationException("Inserted job not found.");
            _logger.LogInformation("Accepted upload {Sha} as {Path} for agent {Agent}", sha256, job.SourceRelativePath, job.AssignedAgent);
            return new IngestResult(job, Deduplicated: false);
        }
        finally
        {
            _commitLock.Release();
        }
    }

    /// <summary>Moves the temp file into _pdfs without ever overwriting a different existing source.</summary>
    private (string Path, bool Placed) PlaceSource(string fileName, string sha256, string tempPath)
    {
        var pipelineJob = _pipeline.FindBySha(sha256);
        if (pipelineJob is not null)
        {
            var known = Path.Combine(_layout.Root, pipelineJob.SourceRelativePath);
            if (File.Exists(known))
            {
                return (known, false);
            }
        }

        Directory.CreateDirectory(_layout.PdfInbox);
        var baseName = SanitizeFileName(fileName);
        var candidate = Path.Combine(_layout.PdfInbox, baseName + ".pdf");
        if (File.Exists(candidate))
        {
            if (HashFile(candidate) == sha256)
            {
                return (candidate, false);
            }
            candidate = Path.Combine(_layout.PdfInbox, $"{baseName}-{sha256[..8]}.pdf");
            if (File.Exists(candidate))
            {
                if (HashFile(candidate) == sha256)
                {
                    return (candidate, false);
                }
                throw new IngestException("A different source already uses this file name.");
            }
        }

        File.Move(tempPath, candidate, overwrite: false);
        return (candidate, true);
    }

    internal static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName.Replace('\\', '/').Split('/').Last()));
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }
        var cleaned = builder.ToString().Trim().Trim('.');
        if (cleaned.Length > MaxFileNameLength)
        {
            cleaned = cleaned[..MaxFileNameLength].Trim();
        }
        return string.IsNullOrWhiteSpace(cleaned) ? "book" : cleaned;
    }

    private void Validate(IngestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new IngestException("file_name is required.");
        }
        if (request.DeclaredSize <= 0 || request.DeclaredSize > _options.MaxUploadBytes)
        {
            throw new IngestException($"size_bytes must be between 1 and {_options.MaxUploadBytes}.");
        }
        if (request.ExpectedSha256 is { Length: > 0 } expected && !IsSha256(expected))
        {
            throw new IngestException("expected_sha256 must be 64 hex characters.");
        }
    }

    internal static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
