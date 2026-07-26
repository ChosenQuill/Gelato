using Xunit;

namespace Gelato.Tests;

public sealed class KeyLockTests
{
    [Fact]
    public async Task ConcurrentCallsForTheSameKeyShareOneInflightAction()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var invocationCount = 0;

        async Task Action(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invocationCount);
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        var first = keyLock.RunSingleFlightAsync(key, Action);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = keyLock.RunSingleFlightAsync(key, Action);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, invocationCount);
    }
}
