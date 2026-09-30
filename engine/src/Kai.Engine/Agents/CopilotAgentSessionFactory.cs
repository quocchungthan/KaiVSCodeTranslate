using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Kai.Engine.Configuration;
using Kai.Engine.Infrastructure;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Agents;

/// <summary>
/// Owns one long-lived Copilot runtime (started lazily, restarted after failures) and opens sessions
/// that run the repository's custom agents unattended: all permissions approved, no human input.
/// </summary>
public sealed class CopilotAgentSessionFactory : IAgentSessionFactory, IAsyncDisposable
{
    internal const string UnattendedAnswer =
        "No human is available: this is an unattended engine run. Choose the safest documented default and continue. " +
        "If the task truly cannot proceed, record a concrete blocker in job.json with the state tools and stop.";

    private readonly RepositoryLayout _layout;
    private readonly EngineOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CopilotAgentSessionFactory> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;

    public CopilotAgentSessionFactory(RepositoryLayout layout, IOptions<EngineOptions> options, ILoggerFactory loggerFactory, TimeProvider time)
    {
        _layout = layout;
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CopilotAgentSessionFactory>();
        _time = time;
    }

    public async Task<IAgentSession> OpenAsync(AgentSessionRequest request, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(request.ResumeSessionId))
            {
                try
                {
                    var resumeConfig = new ResumeSessionConfig { ContinuePendingWork = false };
                    Configure(resumeConfig, request.AgentName);
                    var resumed = await client.ResumeSessionAsync(request.ResumeSessionId, resumeConfig, cancellationToken);
                    _logger.LogInformation("Resumed Copilot session {SessionId} for agent {Agent}", request.ResumeSessionId, request.AgentName);
                    return new CopilotAgentSession(resumed, resumed: true, _options.Copilot, _time, _loggerFactory.CreateLogger<CopilotAgentSession>());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not resume Copilot session {SessionId}; starting a new one", request.ResumeSessionId);
                }
            }

            var config = new SessionConfig { SessionId = request.NewSessionId };
            Configure(config, request.AgentName);
            var session = await client.CreateSessionAsync(config, cancellationToken);
            _logger.LogInformation("Created Copilot session {SessionId} for agent {Agent}", session.SessionId, request.AgentName);
            return new CopilotAgentSession(session, resumed: false, _options.Copilot, _time, _loggerFactory.CreateLogger<CopilotAgentSession>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ResetClientAsync(client);
            throw;
        }
    }

    private void Configure(SessionConfigBase config, string agentName)
    {
        var agentOptions = _options.Agents.First(a => string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase));
        var definitions = AgentDefinitionLoader.LoadAll(Path.Combine(_layout.Root, _options.Copilot.AgentsDirectory));
        if (!definitions.Any(d => string.Equals(d.Name, agentName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Agent '{agentName}' has no definition in {_options.Copilot.AgentsDirectory}.");
        }

        config.WorkingDirectory = _layout.Root;
        config.Model = agentOptions.Model;
        if (!string.IsNullOrWhiteSpace(agentOptions.ReasoningEffort))
        {
            config.ReasoningEffort = agentOptions.ReasoningEffort;
        }
        config.CustomAgents = definitions
            .Select(d => new CustomAgentConfig { Name = d.Name, DisplayName = d.Name, Description = d.Description, Prompt = d.Prompt })
            .ToList();
        config.Agent = definitions.First(d => string.Equals(d.Name, agentName, StringComparison.OrdinalIgnoreCase)).Name;
        var skills = Path.Combine(_layout.Root, _options.Copilot.SkillsDirectory);
        if (Directory.Exists(skills))
        {
            config.SkillDirectories = [skills];
        }
        config.OnPermissionRequest = PermissionHandler.ApproveAll;
        config.OnUserInputRequest = (request, _) => Task.FromResult(AnswerUnattended(request));
        config.InfiniteSessions = new InfiniteSessionConfig { Enabled = true };
    }

    internal static UserInputResponse AnswerUnattended(UserInputRequest request)
    {
        if (request.AllowFreeform != false || request.Choices is not { Count: > 0 })
        {
            return new UserInputResponse { Answer = UnattendedAnswer, WasFreeform = true };
        }
        return new UserInputResponse { Answer = request.Choices[0], WasFreeform = false };
    }

    private async Task<CopilotClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await _clientLock.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return _client;
            }
            var clientOptions = new CopilotClientOptions
            {
                WorkingDirectory = _layout.Root,
                Logger = _loggerFactory.CreateLogger("GitHub.Copilot"),
            };
            var tokenVariable = _options.Copilot.GitHubTokenEnvironmentVariable;
            var token = string.IsNullOrWhiteSpace(tokenVariable) ? null : Environment.GetEnvironmentVariable(tokenVariable);
            if (!string.IsNullOrWhiteSpace(token))
            {
                clientOptions.GitHubToken = token;
                clientOptions.UseLoggedInUser = false;
            }
            else
            {
                clientOptions.UseLoggedInUser = true;
            }
            if (!string.IsNullOrWhiteSpace(_options.Copilot.CliPath))
            {
                clientOptions.Connection = RuntimeConnection.ForStdio(path: _options.Copilot.CliPath);
            }

            var client = new CopilotClient(clientOptions);
            try
            {
                await client.StartAsync(cancellationToken);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
            _client = client;
            return client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task ResetClientAsync(CopilotClient failed)
    {
        await _clientLock.WaitAsync();
        try
        {
            if (!ReferenceEquals(_client, failed))
            {
                return;
            }
            _client = null;
        }
        finally
        {
            _clientLock.Release();
        }
        try { await failed.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Ignoring error while disposing a failed Copilot client"); }
    }

    public async ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Ignoring error while disposing the Copilot client"); }
        }
    }
}

/// <summary>One Copilot conversation. Each turn is supervised by a watchdog so an unattended run can never hang.</summary>
internal sealed class CopilotAgentSession : IAgentSession
{
    private readonly CopilotSession _session;
    private readonly CopilotAgentOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public CopilotAgentSession(CopilotSession session, bool resumed, CopilotAgentOptions options, TimeProvider time, ILogger logger)
    {
        _session = session;
        Resumed = resumed;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public string SessionId => _session.SessionId;

    public bool Resumed { get; }

    public async Task<AgentTurnResult> RunTurnAsync(string prompt, Func<bool> isPipelineTerminal, CancellationToken cancellationToken)
    {
        var finished = new TaskCompletionSource<AgentTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastEventTicks = _time.GetTimestamp();
        var autopilotIdleSeen = 0;

        using var subscription = _session.On<SessionEvent>(e =>
        {
            Interlocked.Exchange(ref lastEventTicks, _time.GetTimestamp());
            switch (e)
            {
                case SessionIdleEvent idle when idle.Data.Mode == SessionMode.Autopilot && idle.Data.Aborted != true:
                    // Intermediate autopilot boundary: the runtime continues on its own.
                    Interlocked.Exchange(ref autopilotIdleSeen, 1);
                    break;
                case SessionIdleEvent idle:
                    finished.TrySetResult(new AgentTurnResult(AgentTurnEnd.Idle, idle.Data.Aborted == true ? "aborted" : null));
                    break;
                case SessionErrorEvent error:
                    finished.TrySetResult(new AgentTurnResult(AgentTurnEnd.Error, error.Data.Message));
                    break;
            }
        });

        var started = _time.GetTimestamp();
        long? terminalSince = null;
        await _session.Rpc.SendAsync(
            prompt,
            agentMode: _options.UseAutopilot ? SendAgentMode.Autopilot : null,
            cancellationToken: cancellationToken);

        var poll = TimeSpan.FromSeconds(_options.WatchdogPollSeconds);
        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(finished.Task, Task.Delay(poll, _time, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (completed == finished.Task)
                {
                    return await finished.Task;
                }

                if (isPipelineTerminal())
                {
                    terminalSince ??= _time.GetTimestamp();
                    if (_time.GetElapsedTime(terminalSince.Value) >= TimeSpan.FromSeconds(_options.TerminalGraceSeconds))
                    {
                        await AbortQuietlyAsync();
                        return new AgentTurnResult(AgentTurnEnd.PipelineTerminal);
                    }
                }

                if (Volatile.Read(ref autopilotIdleSeen) == 1
                    && _time.GetElapsedTime(Interlocked.Read(ref lastEventTicks)) >= TimeSpan.FromMinutes(_options.AutopilotQuietMinutes))
                {
                    await AbortQuietlyAsync();
                    return new AgentTurnResult(AgentTurnEnd.Quiet);
                }

                if (_time.GetElapsedTime(started) >= TimeSpan.FromMinutes(_options.TurnTimeoutMinutes))
                {
                    await AbortQuietlyAsync();
                    return new AgentTurnResult(AgentTurnEnd.Timeout, $"Turn exceeded {_options.TurnTimeoutMinutes} minutes.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            await AbortQuietlyAsync();
            throw;
        }
    }

    private async Task AbortQuietlyAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _session.AbortAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Abort of session {SessionId} failed", SessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _session.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Ignoring error while disposing session {SessionId}", SessionId); }
    }
}
