using Kai.Engine.Scheduling;

namespace Kai.Engine.Infrastructure;

/// <summary>
/// Runs the bootstrap sequence as the first hosted service, so it completes before the scheduler starts and
/// before Kestrel opens the RPC port (the web server is always started after user hosted services).
/// </summary>
public sealed class EngineStartupService(EngineBootstrapper bootstrapper) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => bootstrapper.RunAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
