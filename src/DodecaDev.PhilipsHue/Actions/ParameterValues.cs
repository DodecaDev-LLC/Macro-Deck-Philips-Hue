using System.Globalization;
using System.Text.Json;

namespace DodecaDev.PhilipsHue.Actions;

/// <summary>
/// Reads action parameters. Scalars arrive as CLR values (<c>string</c>, <c>long</c>, <c>double</c>,
/// <c>bool</c>), numbers sometimes as text, and a multi-select as a <see cref="JsonElement"/> array.
/// </summary>
internal static class ParameterValues
{
	public static string? String<T>(IReadOnlyDictionary<string, T> parameters, string name)
		=> parameters.GetValueOrDefault(name) is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

	public static double? Number<T>(IReadOnlyDictionary<string, T> parameters, string name)
		=> (object?)parameters.GetValueOrDefault(name) switch
		{
			string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null,
			IConvertible convertible => convertible.ToDouble(CultureInfo.InvariantCulture),
			_ => null,
		};

	public static IReadOnlyList<string> List<T>(IReadOnlyDictionary<string, T> parameters, string name)
		=> parameters.GetValueOrDefault(name) is JsonElement { ValueKind: JsonValueKind.Array } array
			? [.. array.EnumerateArray()
				.Where(item => item.ValueKind == JsonValueKind.String)
				.Select(item => item.GetString())
				.OfType<string>()
				.Where(item => !string.IsNullOrWhiteSpace(item))]
			: [];
}
