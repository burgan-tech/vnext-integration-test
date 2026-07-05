using System.Net;
using Resilience.IntegrationTests.Infrastructure;

namespace Resilience.IntegrationTests.Tests;

/// <summary>
/// Long-polling state function ETag contract, including the early-304 change-token
/// fast path added on the resilience-hardening branch: an unchanged poll with
/// If-None-Match must answer 304 without materialising the full instance.
/// </summary>
public class StateFunctionEtagTests : IntegrationTestBase
{
    public StateFunctionEtagTests(VNextTestEnvironment environment) : base(environment) { }

    [Fact]
    public async Task StateFunction_ReturnsEtag_ThenAnswers304OnUnchangedPoll()
    {
        var id = await StartInstanceAsync();

        var first = await GetStateFunctionAsync(id);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var etag = first.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etag),
            "State function 200 response did not carry an ETag header.");

        var second = await GetStateFunctionAsync(id, new Dictionary<string, string>
        {
            ["If-None-Match"] = etag!
        });

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task StateFunction_EtagChanges_AfterTransition()
    {
        var id = await StartInstanceAsync();

        var before = await GetStateFunctionAsync(id);
        var etagBefore = before.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etagBefore));

        var (success, error) = await TryRunTransitionAsync(id, "proceed");
        Assert.True(success, $"proceed failed: {error}");
        await WaitForStateAsync(id, s => s == "reviewing");

        // A stale ETag must now yield 200 with fresh content, not a false 304.
        var after = await GetStateFunctionAsync(id, new Dictionary<string, string>
        {
            ["If-None-Match"] = etagBefore!
        });

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);

        var etagAfter = after.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrEmpty(etagAfter));
        Assert.NotEqual(etagBefore, etagAfter);
    }
}
