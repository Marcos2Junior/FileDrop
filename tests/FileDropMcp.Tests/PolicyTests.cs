using FileDropMcp;
using Xunit;

namespace FileDropMcp.Tests;

public sealed class PolicyTests
{
    private static CancellationToken CT => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Native_first_then_first_technical_failure_pins_fallback_across_restart()
    {
        using var fixture = new Fixture();
        Assert.True((await fixture.Policy.Get("chat", "image-delivery", "task", CT)).MayTryNative);
        Assert.False((await fixture.Policy.Get("chat", "image-delivery", "task", CT)).MayTryNative);
        var failure = await fixture.Policy.Record("chat", "image-delivery", "task", DeliveryOutcome.TechnicalFailure, CT);
        Assert.True(failure.MayPublish);
        Assert.Equal(fixture.Clock.Now.AddHours(24), failure.FallbackUntil);
        var restarted = new PolicyStore(fixture.Options, fixture.Clock);
        Assert.True((await restarted.Get("chat", "image-delivery", "new-task", CT)).MayPublish);
        Assert.True((await restarted.Get("other-channel", "image-delivery", "task", CT)).MayTryNative);
        Assert.True((await restarted.Get("chat", "unrelated-capability", "task", CT)).MayTryNative);
        var disk = await File.ReadAllTextAsync(Path.Combine(fixture.Options.StateDirectory, "policy.json"), CT);
        Assert.DoesNotContain("image-delivery", disk);
        Assert.DoesNotContain("new-task", disk);
        Assert.DoesNotContain("https", disk);
        Assert.DoesNotContain(Fixture.FakeKey, disk);
    }
    [Fact]
    public async Task Active_task_stays_pinned_past_24h_new_task_gets_one_native_probe()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        fixture.Clock.Now += TimeSpan.FromHours(24);
        Assert.True((await fixture.Policy.Get("chat", "image-delivery", "task", CT)).MayPublish);
        Assert.True((await fixture.Policy.Get("chat", "image-delivery", "new-task", CT)).MayTryNative);
        Assert.False((await fixture.Policy.Get("chat", "image-delivery", "other-new-task", CT)).MayTryNative);
        var renewed = await fixture.Policy.Record("chat", "image-delivery", "new-task", DeliveryOutcome.TechnicalFailure, CT);
        Assert.Equal(fixture.Clock.Now.AddHours(24), renewed.FallbackUntil);
    }
    [Theory]
    [InlineData(DeliveryOutcome.Denied, "permission_denied")]
    [InlineData(DeliveryOutcome.Cancelled, "cancelled")]
    [InlineData(DeliveryOutcome.PendingConfirmation, "confirmation_pending")]
    [InlineData(DeliveryOutcome.Uncertain, "delivery_uncertain")]
    public async Task Nontechnical_outcomes_never_activate_fallback(DeliveryOutcome outcome, string expected)
    {
        using var fixture = new Fixture();
        await fixture.Policy.Get("chat", "image-delivery", "task", CT);
        var result = await fixture.Policy.Record("chat", "image-delivery", "task", outcome, CT);
        Assert.False(result.MayPublish);
        Assert.Null(result.FallbackUntil);
        Assert.Equal(expected, result.Code);
        var attemptedBypass = await fixture.Policy.Record("chat", "image-delivery", "task", DeliveryOutcome.TechnicalFailure, CT);
        Assert.False(attemptedBypass.MayPublish);
        Assert.Equal(expected, attemptedBypass.Code);
        Assert.False((await fixture.Policy.BeginPublish("chat", "image-delivery", "task", CT)).MayPublish);
    }
    [Fact]
    public async Task Uncertainty_requires_verified_non_delivery_before_fallback()
    {
        using var fixture = new Fixture();
        await fixture.Policy.Get("chat", "image-delivery", "task", CT);
        await fixture.Policy.Record("chat", "image-delivery", "task", DeliveryOutcome.Uncertain, CT);
        var resolution = await fixture.Policy.Record("chat", "image-delivery", "task", DeliveryOutcome.VerifiedNotDelivered, CT);
        Assert.True(resolution.MayPublish);
    }
    [Fact]
    public async Task Denial_cannot_be_cleared_by_claiming_delivered()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        await fixture.Policy.Record("chat", "image-delivery", "task", DeliveryOutcome.Denied, CT);
        var result = await fixture.Policy.Record("chat", "image-delivery", "task", DeliveryOutcome.Delivered, CT);
        Assert.False(result.MayPublish);
        Assert.Equal("permission_denied", result.Code);
    }
    [Fact]
    public async Task Concurrent_process_stores_grant_only_one_native_probe()
    {
        using var fixture = new Fixture();
        var stores = Enumerable.Range(0, 12).Select(_ => new PolicyStore(fixture.Options, fixture.Clock)).ToArray();
        var choices = await Task.WhenAll(stores.Select((store, index) => store.Get("chat", "image-delivery", "task-" + index, CT)));
        Assert.Single(choices, choice => choice.MayTryNative);
    }
    [Fact]
    public async Task Publishing_is_persisted_and_cannot_be_repeated_after_process_loss()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        Assert.True((await fixture.Policy.BeginPublish("chat", "image-delivery", "task", CT)).MayPublish);
        var restarted = new PolicyStore(fixture.Options, fixture.Clock);
        Assert.False((await restarted.BeginPublish("chat", "image-delivery", "task", CT)).MayPublish);
        Assert.Equal("verify_result", (await restarted.Get("chat", "image-delivery", "task", CT)).Action);
    }
    [Fact]
    public async Task Closed_task_cannot_be_reused_to_escape_its_state()
    {
        using var fixture = new Fixture();
        await fixture.Activate();
        await fixture.Policy.End("chat", "image-delivery", "task", CT);
        Assert.Equal("task_closed", (await fixture.Policy.Get("chat", "image-delivery", "task", CT)).Code);
        fixture.Clock.Now += TimeSpan.FromHours(25);
        Assert.True((await fixture.Policy.Get("chat", "image-delivery", "new-task", CT)).MayTryNative);
    }
    [Theory]
    [InlineData("https://sensitive.example", "image-delivery", "task")]
    [InlineData("chat", "image delivery", "task")]
    public async Task Scope_rejects_free_text_urls_and_potential_secret_payloads(string channel, string capability, string task)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<DeliveryException>(() => fixture.Policy.Get(channel, capability, task, CT));
    }
    [Fact]
    public async Task Corrupt_state_fails_closed_and_is_preserved()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Options.StateDirectory);
        var path = Path.Combine(fixture.Options.StateDirectory, "policy.json");
        await File.WriteAllTextAsync(path, "{invalid", CT);
        await Assert.ThrowsAsync<DeliveryException>(() => fixture.Policy.Get("chat", "image-delivery", "task", CT));
        Assert.Equal("{invalid", await File.ReadAllTextAsync(path, CT));
    }
}
