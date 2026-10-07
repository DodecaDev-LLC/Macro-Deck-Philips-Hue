using System.Text.Json;
using MacroDeck.Plugin.Protocol.Capabilities.ConfigFlow;
using MacroDeck.Plugin.Testing;
using NUnit.Framework;

namespace DodecaDev.PhilipsHue.Tests;

[TestFixture]
public sealed class ConfigFlowTests
{
	private const string SessionId = "session-1";

	[Test]
	public async Task The_flow_offers_discovered_bridges_by_name_and_a_manual_address()
	{
		await using var harness = await StartedAsync(new FakeHueBridge());

		var result = (await harness.ConfigFlow.StartAsync(Start())).DataAs<ConfigFlowResultDto>();

		var field = result!.NextStep!.Fields.Single(f => f.Name == "bridgeAddress");
		Assert.That(field.Options!.Select(option => option.Value), Is.EqualTo(new[] { FakeHueBridge.Host, "manual" }));
		Assert.That(field.Options![0].Label!.Value.Literal, Does.Contain(FakeHueBridge.Name));
	}

	[Test]
	public async Task Without_discovery_the_flow_still_offers_a_manual_address()
	{
		await using var harness = await StartedAsync(new FakeHueBridge { DiscoveryAvailable = false });

		var result = (await harness.ConfigFlow.StartAsync(Start())).DataAs<ConfigFlowResultDto>();

		Assert.That(result!.NextStep!.Fields.Single(f => f.Name == "bridgeAddress").Options!.Single().Value, Is.EqualTo("manual"));
	}

	[Test]
	public async Task An_address_without_a_bridge_is_a_field_error()
	{
		await using var harness = await StartedAsync(new FakeHueBridge());
		await harness.ConfigFlow.StartAsync(Start());

		var result = (await harness.ConfigFlow.SubmitAsync(Submit("bridge",
			("bridgeAddress", "manual"), ("manualAddress", "192.168.1.250")))).DataAs<ConfigFlowResultDto>();

		Assert.That(result!.Kind, Is.EqualTo("Error"));
		Assert.That(result.FieldErrors, Does.ContainKey("manualAddress"));
	}

	[Test]
	public async Task Continuing_without_pressing_the_link_button_asks_again()
	{
		await using var harness = await StartedAsync(new FakeHueBridge());
		await harness.ConfigFlow.StartAsync(Start());
		await harness.ConfigFlow.SubmitAsync(Submit("bridge", ("bridgeAddress", FakeHueBridge.Host)));

		var result = (await harness.ConfigFlow.SubmitAsync(Submit("link"))).DataAs<ConfigFlowResultDto>();

		Assert.That(result!.Kind, Is.EqualTo("Error"));
		Assert.That(result.NextStep!.StepId, Is.EqualTo("link"));
	}

	[Test]
	public async Task Pressing_the_link_button_completes_with_the_app_key_as_a_secret()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await StartedAsync(bridge);
		await harness.ConfigFlow.StartAsync(Start());
		var linkStep = (await harness.ConfigFlow.SubmitAsync(Submit("bridge", ("bridgeAddress", FakeHueBridge.Host))))
			.DataAs<ConfigFlowResultDto>();
		Assert.That(linkStep!.NextStep!.StepId, Is.EqualTo("link"));

		bridge.LinkButtonPressed = true;
		var result = (await harness.ConfigFlow.SubmitAsync(Submit("link"))).DataAs<ConfigFlowResultDto>();

		Assert.That(result!.Kind, Is.EqualTo("Complete"));
		Assert.That(result.EntryTitle, Is.EqualTo(FakeHueBridge.Name));
		Assert.That(result.Values!["app_key"].IsSecret, Is.True);
		Assert.That(result.Values["app_key"].Value, Is.EqualTo(FakeHueBridge.AppKey));
		Assert.That(result.Values["bridge_id"].Value, Is.EqualTo(FakeHueBridge.BridgeId));
		Assert.That(result.Values["host"].Value, Is.EqualTo(FakeHueBridge.Host));
		Assert.That(bridge.Writes.Single().Body!["devicetype"]!.GetValue<string>(), Does.StartWith("macro_deck#"));
	}

	[Test]
	public async Task A_bridge_that_is_already_set_up_is_refused()
	{
		var bridge = new FakeHueBridge();
		await using var harness = await Harness.ConfiguredAsync(bridge);
		await harness.ConfigFlow.StartAsync(Start());

		var result = (await harness.ConfigFlow.SubmitAsync(Submit("bridge", ("bridgeAddress", FakeHueBridge.Host))))
			.DataAs<ConfigFlowResultDto>();

		Assert.That(result!.Kind, Is.EqualTo("Error"));
	}

	private static async Task<PluginTestHarness> StartedAsync(FakeHueBridge bridge)
	{
		var harness = Harness.Create(bridge);
		await harness.InitializeIntegrationsAsync();
		return harness;
	}

	private static FlowStartArguments Start() => new() { SessionId = SessionId, OAuth = OAuth() };

	private static FlowSubmitArguments Submit(string stepId, params (string Name, object? Value)[] input) => new()
	{
		SessionId = SessionId,
		StepId = stepId,
		Input = input.ToDictionary(pair => pair.Name, pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal),
		OAuth = OAuth(),
	};

	private static ConfigFlowOAuthContextDto OAuth() => new() { RedirectUri = "http://localhost/callback", State = "state-1" };
}
