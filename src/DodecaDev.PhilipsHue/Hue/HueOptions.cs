namespace DodecaDev.PhilipsHue.Hue;

public sealed class HueOptions
{
	/// <summary>Signify's N-UPnP endpoint: lists the bridges registered from the caller's public IP.</summary>
	public Uri DiscoveryUri { get; set; } = new("https://discovery.meethue.com/");

	public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>How long to listen for mDNS answers. Bridges usually answer within a few hundred milliseconds.</summary>
	public TimeSpan MdnsTimeout { get; set; } = TimeSpan.FromSeconds(2);

	/// <summary>How long a subnet scan waits for each address. A bridge on the LAN answers in milliseconds.</summary>
	public TimeSpan ScanHostTimeout { get; set; } = TimeSpan.FromSeconds(1);

	/// <summary>How long pairing keeps asking after the user says the link button is pressed. The bridge
	/// itself accepts a registration for 30 seconds after the press.</summary>
	public TimeSpan LinkButtonWindow { get; set; } = TimeSpan.FromSeconds(10);

	public TimeSpan LinkRetryInterval { get; set; } = TimeSpan.FromSeconds(1);
}
