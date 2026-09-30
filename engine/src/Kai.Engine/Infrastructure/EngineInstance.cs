namespace Kai.Engine.Infrastructure;

/// <summary>
/// Identifies this engine process and holds an exclusive lock so only one engine drives a repository.
/// Leases written by any other (dead) instance are therefore known to be orphaned at startup.
/// </summary>
public sealed class EngineInstance : IDisposable
{
    private readonly RepositoryLayout _layout;
    private FileStream? _lock;

    public EngineInstance(RepositoryLayout layout)
    {
        _layout = layout;
    }

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public void AcquireExclusiveLock()
    {
        if (_lock is not null)
        {
            return;
        }
        Directory.CreateDirectory(_layout.EngineRoot);
        var path = Path.Combine(_layout.EngineRoot, "engine.lock");
        try
        {
            _lock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Another engine instance is already running for '{_layout.Root}'.", ex);
        }
    }

    public void Dispose() => _lock?.Dispose();
}
