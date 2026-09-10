using MemoryCache.Abstractions;

namespace MemoryCache.Tests;

internal sealed class FakeSwitchLoader : IEntityLoader<SampleSwitch>
{
    public Task<IReadOnlyCollection<SampleSwitch>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyCollection<SampleSwitch>>(Array.Empty<SampleSwitch>());
}

internal sealed class FakeCompositeLoader : IEntityLoader<SampleComposite>
{
    public Task<IReadOnlyCollection<SampleComposite>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyCollection<SampleComposite>>(Array.Empty<SampleComposite>());
}

internal sealed class SequenceLoader<TEntity> : IEntityLoader<TEntity>
    where TEntity : class
{
    private readonly Func<IReadOnlyCollection<TEntity>> _dataFactory;
    private int _loadCount;

    public SequenceLoader(Func<IReadOnlyCollection<TEntity>> dataFactory)
    {
        _dataFactory = dataFactory;
    }

    public int LoadCount => Volatile.Read(ref _loadCount);

    public Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        return Task.FromResult(_dataFactory());
    }
}

internal sealed class FailingAfterFirstLoader<TEntity> : IEntityLoader<TEntity>
    where TEntity : class
{
    private readonly IReadOnlyCollection<TEntity> _items;
    private int _loadCount;

    public FailingAfterFirstLoader(params TEntity[] items)
    {
        _items = items;
    }

    public int LoadCount => Volatile.Read(ref _loadCount);

    public Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _loadCount);
        if (call == 2)
        {
            throw new InvalidOperationException($"Simulated loader failure (call {call}).");
        }

        return Task.FromResult(_items);
    }
}

internal sealed class AlwaysFailingLoader<TEntity> : IEntityLoader<TEntity>
    where TEntity : class
{
    public Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated loader failure.");
}

internal sealed class GateLoader<TEntity> : IEntityLoader<TEntity>
    where TEntity : class
{
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IReadOnlyCollection<TEntity> _items;
    private int _loadCount;

    public GateLoader(params TEntity[] items)
    {
        _items = items;
    }

    public int LoadCount => Volatile.Read(ref _loadCount);

    public void Release()
        => _release.TrySetResult();

    public async Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _items;
    }
}

internal sealed class ScopedTrackingLoader : IEntityLoader<SampleSwitch>, IDisposable
{
    private static int _disposeCount;

    public static int DisposeCount => Volatile.Read(ref _disposeCount);

    public Task<IReadOnlyCollection<SampleSwitch>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyCollection<SampleSwitch>>([new SampleSwitch { Code = "A" }]);

    public void Dispose()
        => Interlocked.Increment(ref _disposeCount);
}
