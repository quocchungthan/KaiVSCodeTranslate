using System.Net;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Configuration;

public sealed class EngineOptionsValidator : IValidateOptions<EngineOptions>
{
    public const int MinimumSecretKeyLength = 32;

    public ValidateOptionsResult Validate(string? name, EngineOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.SecretKey) || options.SecretKey.Length < MinimumSecretKeyLength)
        {
            failures.Add($"Engine:SecretKey must be at least {MinimumSecretKeyLength} characters. Set it with the Engine__SecretKey environment variable or user secrets.");
        }
        if (!IPAddress.TryParse(options.ListenAddress, out _))
        {
            failures.Add("Engine:ListenAddress must be an IP address.");
        }
        if (options.Port is <= 0 or > 65535)
        {
            failures.Add("Engine:Port must be between 1 and 65535.");
        }
        if (options.MaxUploadBytes <= 0)
        {
            failures.Add("Engine:MaxUploadBytes must be positive.");
        }

        var agents = options.Agents;
        if (agents.Count == 0)
        {
            failures.Add("Engine:Agents must declare at least one agent.");
        }
        if (agents.Any(a => string.IsNullOrWhiteSpace(a.Name)))
        {
            failures.Add("Every Engine:Agents entry needs a Name.");
        }
        if (agents.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            failures.Add("Engine:Agents names must be unique.");
        }
        if (agents.Any(a => a.MaxConcurrentJobs < 1))
        {
            failures.Add("Engine:Agents MaxConcurrentJobs must be at least 1.");
        }
        if (!agents.Any(a => string.Equals(a.Name, options.DefaultAgent, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add($"Engine:DefaultAgent '{options.DefaultAgent}' is not declared in Engine:Agents.");
        }

        var scheduler = options.Scheduler;
        if (scheduler.IntervalSeconds < 1 || scheduler.LeaseSeconds < 10 || scheduler.LeaseRenewSeconds < 1)
        {
            failures.Add("Engine:Scheduler intervals are too small.");
        }
        if (scheduler.LeaseRenewSeconds * 2 > scheduler.LeaseSeconds)
        {
            failures.Add("Engine:Scheduler:LeaseRenewSeconds must be at most half of LeaseSeconds so a single missed renewal does not expire the lease.");
        }
        if (scheduler.MaxAttempts < 1 || scheduler.MaxConcurrentRuns < 1)
        {
            failures.Add("Engine:Scheduler MaxAttempts and MaxConcurrentRuns must be at least 1.");
        }

        var copilot = options.Copilot;
        if (copilot.MaxContinuations < 0 || copilot.MaxStalledTurns < 1 || copilot.TurnTimeoutMinutes < 1 || copilot.RunTimeoutHours < 1)
        {
            failures.Add("Engine:Copilot limits are invalid.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
