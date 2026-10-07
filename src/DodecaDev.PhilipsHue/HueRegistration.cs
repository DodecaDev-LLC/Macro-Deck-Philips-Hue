using DodecaDev.PhilipsHue.Actions;
using DodecaDev.PhilipsHue.Bridges;
using DodecaDev.PhilipsHue.Hue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DodecaDev.PhilipsHue;

/// <summary>
/// The plugin's services in one place, so tests start from the same wiring Program.cs uses and only
/// replace the HTTP handler.
/// </summary>
public static class HueRegistration
{
	public static IHttpClientBuilder AddPhilipsHue(this IServiceCollection services)
	{
		services.AddOptions<HueOptions>();
		services.AddSingleton<HueClient>();
		services.AddSingleton<IHueMdnsBrowser, MdnsBrowser>();
		services.AddSingleton<IHueSubnetScanner, SubnetScanner>();
		services.AddSingleton<HueDiscovery>();
		services.AddSingleton<BridgeRegistry>();
		services.AddSingleton<BridgeProbe>();
		services.AddSingleton<BridgeMonitor>();
		services.AddSingleton<HueActionSupport>();

		return services.AddHttpClient(HueClient.HttpClientName)
			.ConfigureHttpClient((provider, client) =>
				client.Timeout = provider.GetRequiredService<IOptions<HueOptions>>().Value.RequestTimeout);
	}
}
