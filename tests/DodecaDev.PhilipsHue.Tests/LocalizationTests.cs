using DodecaDev.PhilipsHue.Hue;
using NUnit.Framework;

namespace DodecaDev.PhilipsHue.Tests;

[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
	{
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.dodecadev.philips-hue"));
	}

	[Test]
	public void Every_key_the_default_culture_declares_resolves_to_text()
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True);
			Assert.That(text, Is.Not.Empty);
		}
	}
}

[TestFixture]
public sealed class HueColorTests
{
	[TestCase("#FF0000", 0.7006, 0.2993)]
	[TestCase("#00FF00", 0.1724, 0.7468)]
	[TestCase("#FFFFFF", 0.3227, 0.3290)]
	public void Rgb_converts_to_hue_xy(string hex, double x, double y)
	{
		Assert.That(HueColor.TryParseHex(hex, out var rgb), Is.True);
		var xy = HueColor.ToXy(rgb.R, rgb.G, rgb.B);
		Assert.That(xy.X, Is.EqualTo(x).Within(0.001));
		Assert.That(xy.Y, Is.EqualTo(y).Within(0.001));
	}

	[TestCase("#F00", true)]
	[TestCase("FF0000", true)]
	[TestCase("#FF0000CC", true)]
	[TestCase("#FF00", false)]
	[TestCase("red", false)]
	[TestCase("", false)]
	public void Hex_parsing_accepts_the_usual_forms(string text, bool valid)
	{
		Assert.That(HueColor.TryParseHex(text, out _), Is.EqualTo(valid));
	}
}
