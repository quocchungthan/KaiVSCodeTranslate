namespace Kai.Engine.Agents;

public enum AgentTurnEnd
{
    /// <summary>The agent finished its turn and went idle.</summary>
    Idle,
    /// <summary>job.json reached a terminal state and the agent did not stop within the grace period.</summary>
    PipelineTerminal,
    /// <summary>Autopilot went silent after an intermediate idle for the configured quiet period.</summary>
    Quiet,
    /// <summary>The turn exceeded its hard timeout and was aborted.</summary>
    Timeout,
    /// <summary>The session reported an error.</summary>
    Error,
}

public sealed record AgentTurnResult(AgentTurnEnd End, string? Detail = null);

public sealed record AgentSessionRequest(string AgentName, string? ResumeSessionId, string NewSessionId);

public interface IAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>True when an existing conversation was resumed, so the agent already has the job context.</summary>
    bool Resumed { get; }

    /// <summary>
    /// Sends one prompt and waits until the turn ends. <paramref name="isPipelineTerminal"/> is polled so a
    /// finished job is detected even if the agent keeps chatting.
    /// </summary>
    Task<AgentTurnResult> RunTurnAsync(string prompt, Func<bool> isPipelineTerminal, CancellationToken cancellationToken);
}

public interface IAgentSessionFactory
{
    Task<IAgentSession> OpenAsync(AgentSessionRequest request, CancellationToken cancellationToken);
}
