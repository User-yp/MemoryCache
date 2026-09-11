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

internal sealed class FakeTimeoutLoader : IEntityLoader<SampleTimeoutEntity>
{
    public Task<IReadOnlyCollection<SampleTimeoutEntity>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyCollection<SampleTimeoutEntity>>(Array.Empty<SampleTimeoutEntity>());
}

internal sealed class FakePolicyLoader : IEntityLoader<SamplePolicyEntity>
{
    public Task<IReadOnlyCollection<SamplePolicyEntity>> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyCollection<SamplePolicyEntity>>(Array.Empty<SamplePolicyEntity>());
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
/// <summary>
/// 分步可控加载器：每次调用<b>先取数</b>（模拟“数据库已经读完”），再等待放行
/// （模拟慢查询/慢发布）。用于构造“取数之后、返回之前数据发生变化”的交错场景。
/// </summary>
internal sealed class StepLoader<TEntity> : IEntityLoader<TEntity>
    where TEntity : class
{
    private readonly SemaphoreSlim _gate = new(0);
    private int _loadCount;

    public Func<IReadOnlyCollection<TEntity>> DataFactory { get; set; } = () => [];

    /// <summary>获取已经“取到数并开始等待放行”的调用次数。</summary>
    public int LoadCount => Volatile.Read(ref _loadCount);

    public async Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken)
    {
        // 先取数：这样测试可以在“取数之后、返回之前”改写 DataFactory，
        // 从而模拟“本次加载拿到的是变更前的数据”。
        var items = DataFactory();

        Interlocked.Increment(ref _loadCount);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    /// <summary>放行一次等待中的调用。</summary>
    public void Release() => _gate.Release();
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
    private int _observedCancellation;

    public GateLoader(params TEntity[] items)
    {
        _items = items;
    }

    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>加载器收到的取消令牌是否真的被触发过。</summary>
    public bool ObservedCancellation => Volatile.Read(ref _observedCancellation) == 1;

    public void Release()
        => _release.TrySetResult();

    public async Task<IReadOnlyCollection<TEntity>> LoadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        try
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _observedCancellation, 1);
            throw;
        }

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
