using DodecaDev.PhilipsHue.Hue;

namespace DodecaDev.PhilipsHue.Tests;

/// <summary>Stands in for multicast, which tests must not send onto the real network.</summary>
internal sealed class FakeMdnsBrowser : IHueMdnsBrowser
{
	internal List<MdnsBridge> Answers { get; } = [];

	public Task<IReadOnlyList<MdnsBridge>> BrowseAsync(CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<MdnsBridge>>([.. Answers]);
}
