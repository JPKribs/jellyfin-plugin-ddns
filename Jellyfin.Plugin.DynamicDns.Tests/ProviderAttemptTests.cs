using System.Threading.Tasks;
using Jellyfin.Plugin.DynamicDns.Providers;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers how one update attempt's HTTP responses classify its failure: the final response decides
/// whether it was temporary, and a rejected credential anywhere makes it definitive.
/// </summary>
public class ProviderAttemptTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(200, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    public void SingleResponse(int status, bool transient)
    {
        var attempt = new ProviderAttempt();
        attempt.Observe(status);
        Assert.Equal(transient, attempt.FailureIsTransient);
    }

    [Fact]
    public void NoRequest_IsDefinitive() => Assert.False(new ProviderAttempt().FailureIsTransient);

    [Fact]
    public void LookupMissThenServerError_IsTransient()
    {
        // A 404 lookup that leads to a create is routine; the create failing with a 5xx is temporary.
        var attempt = new ProviderAttempt();
        attempt.Observe(404);
        attempt.Observe(503);
        Assert.True(attempt.FailureIsTransient);
    }

    [Fact]
    public void AuthFailureAnywhere_IsDefinitive()
    {
        var attempt = new ProviderAttempt();
        attempt.Observe(401);
        attempt.Observe(503);
        Assert.False(attempt.FailureIsTransient);
    }

    [Fact]
    public async Task Begin_SetsTheCurrentAttemptForTheFlowOnly()
    {
        ProviderAttempt? inner = null;
        await Task.Run(async () =>
        {
            var attempt = ProviderAttempt.Begin();
            await Task.Yield();
            inner = ProviderAttempt.Current;
            Assert.Same(attempt, inner);
        });

        Assert.NotNull(inner);
        Assert.Null(ProviderAttempt.Current);
    }

    [Fact]
    public void DefinitiveFamily_ThenServerErrorFamily_IsDefinitive()
    {
        // A rejected (404) then AAAA failing with a server error: the rejection wins.
        var attempt = new ProviderAttempt();
        attempt.Observe(404);
        attempt.CompleteFamily(false);
        attempt.Observe(503);
        attempt.CompleteFamily(false);
        Assert.False(attempt.FailureIsTransient);
    }

    [Fact]
    public void BodyErrorFamily_ThenNetworkErrorFamily_IsDefinitive()
    {
        // A answered 200 with an error in the body, AAAA never got a response.
        var attempt = new ProviderAttempt();
        attempt.Observe(200);
        attempt.CompleteFamily(false);
        attempt.Observe(0);
        attempt.CompleteFamily(false);
        Assert.False(attempt.FailureIsTransient);
    }

    [Fact]
    public void FamilyFailingBeforeAnyRequest_IsDefinitive()
    {
        var attempt = new ProviderAttempt();
        attempt.CompleteFamily(false);
        attempt.Observe(503);
        attempt.CompleteFamily(false);
        Assert.False(attempt.FailureIsTransient);
    }

    [Fact]
    public void BothFamiliesTemporary_IsTransient()
    {
        var attempt = new ProviderAttempt();
        attempt.Observe(404);
        attempt.Observe(503);
        attempt.CompleteFamily(false);
        attempt.FamilyTimedOut();
        attempt.CompleteFamily(false);
        Assert.True(attempt.FailureIsTransient);
    }

    [Fact]
    public void SucceededFamily_DoesNotMakeAFailedOneDefinitive()
    {
        var attempt = new ProviderAttempt();
        attempt.Observe(200);
        attempt.CompleteFamily(true);
        attempt.Observe(502);
        attempt.CompleteFamily(false);
        Assert.True(attempt.FailureIsTransient);
    }
}
