using DodecaDev.PhilipsHue.Bridges;
using MacroDeck.Plugin.Protocol.Capabilities.Issues;
using MacroDeck.Plugin.Testing;
using NUnit.Framework;

namespace DodecaDev.PhilipsHue.Tests;

[TestFixture]
public sealed class BridgeHealthTests
{
	[Test]
	public async Task A_healthy_bridge_has_no_issues()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);
		await WaitForProbeAsync(bridge);

		var issues = (await harness.Issues.GetIssuesAsync()).DataAs<IssueListResult>();

		Assert.That(issues!.Issues, Is.Empty);
	}

	[Test]
	public async Task A_bridge_that_moved_is_found_again_and_its_new_address_saved()
	{
		var bridge = new FakeHueBridge { CurrentHost = "192.168.1.77" };
		await using var harness = await Harness.ConfiguredAsync(bridge);

		await Harness.UntilAsync(async () =>
			await harness.Context.Config.GetStringAsync(harness.Context.Config.Entries[0].Id, BridgeConfigKeys.Host) == "192.168.1.77",
			because: "the probe should rediscover the bridge and persist its address");

		var outcome = await harness.Actions.ExecuteAsync("set-scene", new Dictionary<string, object?> { ["scene"] = "abc" });
		Assert.That(outcome.Succeeded, Is.True);
	}

	[Test]
	public async Task A_migrated_entry_without_an_address_is_located_by_discovery()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge, host: null);

		await Harness.UntilAsync(async () =>
			await harness.Context.Config.GetStringAsync(harness.Context.Config.Entries[0].Id, BridgeConfigKeys.Host) == FakeHueBridge.Host,
			because: "discovery should supply the address Macro Deck 2 never stored");
	}

	[Test]
	public async Task A_revoked_app_key_becomes_an_issue_that_reopens_setup()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge, appKey: "revoked-key");

		IssueListResult? issues = null;
		await Harness.UntilAsync(async () =>
		{
			issues = (await harness.Issues.GetIssuesAsync()).DataAs<IssueListResult>();
			return issues!.Issues.Count > 0;
		}, because: "the probe should notice the rejected key");

		var issue = issues!.Issues.Single();
		Assert.That(issue.Id, Is.EqualTo($"unauthorized-{FakeHueBridge.BridgeId}"));
		Assert.That(issue.Severity, Is.EqualTo("Error"));

		var resolution = (await harness.Issues.ResolveAsync(new IssueResolveArguments { IssueId = issue.Id })).DataAs<IssueResolveResult>();
		Assert.That(resolution!.FollowUp, Is.EqualTo("StartConfigFlow"));

		var outcome = await harness.Actions.ExecuteAsync("set-scene", new Dictionary<string, object?> { ["scene"] = "abc" });
		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task An_unreachable_bridge_is_a_warning_and_retry_reports_the_truth()
	{
		var bridge = new FakeHueBridge { CurrentHost = "10.0.0.1", DiscoveryAvailable = false };
		await using var harness = await Harness.ConfiguredAsync(bridge);

		IssueListResult? issues = null;
		await Harness.UntilAsync(async () =>
		{
			issues = (await harness.Issues.GetIssuesAsync()).DataAs<IssueListResult>();
			return issues!.Issues.Count > 0;
		}, because: "the probe should give up on the bridge");

		var issue = issues!.Issues.Single();
		Assert.That(issue.Severity, Is.EqualTo("Warning"));

		var stillDown = (await harness.Issues.ResolveAsync(new IssueResolveArguments { IssueId = issue.Id })).DataAs<IssueResolveResult>();
		Assert.That(stillDown!.Success, Is.False);

		bridge.CurrentHost = FakeHueBridge.Host;
		var recovered = (await harness.Issues.ResolveAsync(new IssueResolveArguments { IssueId = issue.Id })).DataAs<IssueResolveResult>();
		Assert.That(recovered!.Success, Is.True);
		Assert.That((await harness.Issues.GetIssuesAsync()).DataAs<IssueListResult>()!.Issues, Is.Empty);
	}

	private static Task WaitForProbeAsync(FakeHueBridge bridge)
		=> Wait.UntilAsync(() => bridge.Requests.Any(request => request.Path.EndsWith("/groups/0", StringComparison.Ordinal)),
			because: "the initial probe should run");
}
