using DodecaDev.PhilipsHue.Hue;

namespace DodecaDev.PhilipsHue.Tests;

/// <summary>Stands in for scanning, which tests must not do on the real network.</summary>
internal sealed class FakeSubnetScanner : IHueSubnetScanner
{
	internal List<DiscoveredBridge> Found { get; } = [];

	public Task<IReadOnlyList<DiscoveredBridge>> ScanAsync(CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<DiscoveredBridge>>([.. Found]);
}
