using DodecaDev.PhilipsHue;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;

// Identity, description and icon come from manifest.json at the content root.
var builder = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.RegisterIntegration<PhilipsHueIntegration>();

builder.Services.AddPhilipsHue();

var plugin = builder.Build();

await plugin.RunAsync();
