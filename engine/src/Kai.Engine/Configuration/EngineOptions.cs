namespace Kai.Engine.Configuration;

public sealed class EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>Loopback by default: the reverse SSL/SSH tunnel terminates on this machine.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>The single RPC port (gRPC over HTTP/2).</summary>
    public int Port { get; set; } = 7443;

    /// <summary>Repository root; auto-detected from the working/app directory when empty.</summary>
    public string? RepositoryRoot { get; set; }

    /// <summary>Shared secret required in the <c>x-engine-key</c> header. Supply through environment or user secrets only.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Relative to the repository root unless absolute. Kept under the gitignored _processing folder.</summary>
    public string DatabasePath { get; set; } = "_processing/engine/engine.db";

    public long MaxUploadBytes { get; set; } = 1L << 30;

    public string DefaultAgent { get; set; } = "Huong";

    /// <summary>Surface jobs completed locally (outside the engine) in the gallery.</summary>
    public bool ImportExistingCompletedJobs { get; set; } = true;

    public SchedulerOptions Scheduler { get; set; } = new();

    public List<AgentOptions> Agents { get; set; } = [];

    public CopilotAgentOptions Copilot { get; set; } = new();
}

public sealed class SchedulerOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    public int LeaseSeconds { get; set; } = 300;
    public int LeaseRenewSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;
    public int RetryBackoffSeconds { get; set; } = 300;
    public int MaxConcurrentRuns { get; set; } = 1;
}

public sealed class AgentOptions
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int MaxConcurrentJobs { get; set; } = 1;
    public string? Model { get; set; }
    public string? ReasoningEffort { get; set; }
}

public sealed class CopilotAgentOptions
{
    public string PowerShellExecutable { get; set; } = "pwsh";
    public int ScriptTimeoutMinutes { get; set; } = 15;
    public bool UseAutopilot { get; set; } = true;
    public int TurnTimeoutMinutes { get; set; } = 240;
    public int AutopilotQuietMinutes { get; set; } = 10;
    public int TerminalGraceSeconds { get; set; } = 120;
    public int WatchdogPollSeconds { get; set; } = 30;
    public int MaxContinuations { get; set; } = 25;
    public int MaxStalledTurns { get; set; } = 3;
    public int RunTimeoutHours { get; set; } = 48;
    public string AgentsDirectory { get; set; } = ".github/agents";
    public string SkillsDirectory { get; set; } = ".github/skills";

    /// <summary>Optional name of an environment variable holding a GitHub token. Defaults to the logged-in Copilot user.</summary>
    public string? GitHubTokenEnvironmentVariable { get; set; }

    /// <summary>Optional explicit Copilot runtime path; defaults to the runtime bundled by the SDK package.</summary>
    public string? CliPath { get; set; }
}
