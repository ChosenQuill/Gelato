using System.Net.Http;
using System.Reflection;
using Gelato.Filters;
using Xunit;

namespace Gelato.Tests;

public class SearchActionFilterTests
{
    [Theory]
    [InlineData(typeof(HttpRequestException), "upstream-http")]
    [InlineData(typeof(TaskCanceledException), "upstream-timeout")]
    [InlineData(typeof(TimeoutException), "upstream-timeout")]
    public async Task ExpectedVirtualSearchFailureFallsBackToNativeExactlyOnce(
        Type exceptionType,
        string expectedReason
    )
    {
        var method = GetFallbackMethod();
        var nativeCalls = 0;
        var loggedReasons = new List<string>();
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Func<Task<List<StremioMeta>>> virtualSearch = () =>
            Task.FromException<List<StremioMeta>>(exception);
        Func<Task> nativeSearch = () =>
        {
            nativeCalls++;
            return Task.CompletedTask;
        };

        var fallback = (Task<List<StremioMeta>?>)method.Invoke(
            null,
            [virtualSearch, nativeSearch, (Action<string>)loggedReasons.Add]
        )!;
        var result = await fallback;

        Assert.Null(result);
        Assert.Equal(1, nativeCalls);
        Assert.Equal([expectedReason], loggedReasons);
    }

    [Fact]
    public async Task UnexpectedVirtualSearchFailureDoesNotInvokeNativeSearch()
    {
        var method = GetFallbackMethod();
        var nativeCalls = 0;
        var loggedReasons = new List<string>();

        Func<Task<List<StremioMeta>>> virtualSearch = () =>
            Task.FromException<List<StremioMeta>>(new InvalidOperationException("unexpected"));
        Func<Task> nativeSearch = () =>
        {
            nativeCalls++;
            return Task.CompletedTask;
        };

        var fallback = (Task<List<StremioMeta>?>)method.Invoke(
            null,
            [virtualSearch, nativeSearch, (Action<string>)loggedReasons.Add]
        )!;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fallback);
        Assert.Equal(0, nativeCalls);
        Assert.Empty(loggedReasons);
    }

    private static MethodInfo GetFallbackMethod()
    {
        var method = typeof(SearchActionFilter).GetMethod(
            "SearchOrFallBackToNativeAsync",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.NotNull(method);
        return method!;
    }
}
