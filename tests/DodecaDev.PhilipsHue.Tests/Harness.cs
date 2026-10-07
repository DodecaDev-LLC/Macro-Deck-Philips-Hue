using DodecaDev.PhilipsHue.Bridges;
using DodecaDev.PhilipsHue.Hue;
using MacroDeck.Plugin.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DodecaDev.PhilipsHue.Tests;

internal static class Harness
{
	/// <summary>The plugin as Program.cs wires it, with the fake bridge as the HTTP handler and no pairing wait.</summary>
	internal static PluginTestHarness Create(FakeHueBridge bridge, FakeMdnsBrowser? mdns = null, FakeSubnetScanner? scanner = null) =>
		PluginTestHarness.Create(builder =>
		{
			builder.UseLocalization(Strings.LocalizationCatalog).RegisterIntegration<PhilipsHueIntegration>();
			builder.Services.AddPhilipsHue().ConfigurePrimaryHttpMessageHandler(() => bridge);
			builder.Services.AddSingleton<IHueMdnsBrowser>(mdns ?? new FakeMdnsBrowser());
			builder.Services.AddSingleton<IHueSubnetScanner>(scanner ?? new FakeSubnetScanner());
			builder.Services.Configure<HueOptions>(options => options.LinkButtonWindow = TimeSpan.Zero);
		});

	/// <summary>A harness with one paired bridge, initialized.</summary>
	internal static async Task<PluginTestHarness> ConfiguredAsync(
		FakeHueBridge bridge, string appKey = FakeHueBridge.AppKey, string? host = FakeHueBridge.Host)
	{
		var harness = Create(bridge);
		Seed(harness, FakeHueBridge.BridgeId, appKey, host, FakeHueBridge.Name);
		await harness.InitializeIntegrationsAsync();
		return harness;
	}

	/// <summary>Polls a condition that needs an await; <c>Wait.UntilAsync</c> only takes a synchronous one.</summary>
	internal static async Task UntilAsync(Func<Task<bool>> condition, string because)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
		while (!await condition())
		{
			if (DateTime.UtcNow > deadline)
			{
				throw new TimeoutException($"Timed out waiting: {because}");
			}

			await Task.Delay(20);
		}
	}

	internal static Guid Seed(PluginTestHarness harness, string bridgeId, string appKey, string? host, string title)
	{
		var entry = harness.Context.Config.AddEntry(title);
		harness.Context.Config.SeedString(entry, BridgeConfigKeys.BridgeId, bridgeId);
		harness.Context.Config.SeedSecret(entry, BridgeConfigKeys.AppKey, appKey);
		if (host is not null)
		{
			harness.Context.Config.SeedString(entry, BridgeConfigKeys.Host, host);
		}

		return entry;
	}
}
