using System.Diagnostics;
using System.Text;
using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Pipeline;

public interface IPipelineScriptRunner
{
    /// <summary>Runs Initialize-TranslationJobs.ps1 so job.json exists for every PDF in _pdfs (idempotent).</summary>
    Task InitializeJobsAsync(CancellationToken cancellationToken);
}

/// <summary>Invokes the repository's deterministic PowerShell tools; the engine never writes job.json itself.</summary>
public sealed class PowerShellPipelineScriptRunner : IPipelineScriptRunner
{
    private readonly RepositoryLayout _layout;
    private readonly CopilotAgentOptions _options;
    private readonly ILogger<PowerShellPipelineScriptRunner> _logger;

    public PowerShellPipelineScriptRunner(RepositoryLayout layout, IOptions<EngineOptions> options, ILogger<PowerShellPipelineScriptRunner> logger)
    {
        _layout = layout;
        _options = options.Value.Copilot;
        _logger = logger;
    }

    public Task InitializeJobsAsync(CancellationToken cancellationToken) =>
        RunScriptAsync("Initialize-TranslationJobs.ps1", cancellationToken);

    private async Task RunScriptAsync(string scriptName, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.PowerShellExecutable)
        {
            WorkingDirectory = _layout.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(_layout.ToolsRoot, scriptName), "-RepositoryRoot", _layout.Root })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) { stdout.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(_options.ScriptTimeoutMinutes));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new TimeoutException($"{scriptName} exceeded {_options.ScriptTimeoutMinutes} minutes.");
        }

        if (process.ExitCode != 0)
        {
            var error = stderr.ToString().Trim();
            throw new InvalidOperationException($"{scriptName} exited with {process.ExitCode}: {(error.Length > 1000 ? error[..1000] : error)}");
        }
        _logger.LogDebug("{Script} completed: {Output}", scriptName, stdout.ToString().Trim());
    }
}
