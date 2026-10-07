using MacroDeck.Localization;

namespace DodecaDev.PhilipsHue.Hue;

/// <summary>
/// One failure type for everything that talks to a bridge. <see cref="Exception.Message"/> is the English
/// diagnostic for the log; <see cref="UserMessage"/> is what a user is shown, resolved in their language.
/// </summary>
public sealed class HueException : Exception
{
	public HueException(HueFailure reason, string message, LocalizedText userMessage, Exception? innerException = null)
		: base(message, innerException)
	{
		Reason = reason;
		UserMessage = userMessage;
	}

	public HueFailure Reason { get; }

	public LocalizedText UserMessage { get; }

	internal static HueException Unreachable(string host, Exception innerException) => new(
		HueFailure.Unreachable,
		$"The Hue bridge at {host} is unreachable.",
		Strings.Failures.Unreachable(),
		innerException);

	internal static HueException Timeout(string host, Exception innerException) => new(
		HueFailure.Timeout,
		$"The Hue bridge at {host} did not answer in time.",
		Strings.Failures.Timeout(),
		innerException);

	internal static HueException Unauthorized() => new(
		HueFailure.Unauthorized,
		"The Hue bridge rejected the stored app key.",
		Strings.Failures.Unauthorized());

	internal static HueException LinkButtonNotPressed() => new(
		HueFailure.LinkButtonNotPressed,
		"The link button on the Hue bridge was not pressed.",
		Strings.Failures.LinkButtonNotPressed());

	internal static HueException InvalidResponse(string detail, Exception? innerException = null) => new(
		HueFailure.InvalidResponse,
		$"The Hue bridge sent a response this plugin could not read: {detail}",
		Strings.Failures.InvalidResponse(),
		innerException);

	internal static HueException Rejected(int errorType, string description) => new(
		HueFailure.Rejected,
		$"The Hue bridge rejected the request (error {errorType}: {description}).",
		Strings.Failures.Rejected());

	internal static HueException DiscoveryFailed(Exception? innerException = null) => new(
		HueFailure.Unreachable,
		"The Hue discovery service could not be reached.",
		Strings.Failures.DiscoveryFailed(),
		innerException);
}

public enum HueFailure
{
	Unreachable,
	Timeout,
	Unauthorized,
	LinkButtonNotPressed,
	Rejected,
	InvalidResponse,
}
