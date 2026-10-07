# Philips Hue Plugin for Macro Deck 3

Control Philips Hue lights from Macro Deck: recall scenes, switch lights on or off, set brightness and
color, and nudge brightness, saturation, hue and color temperature up or down.

This is the Macro Deck 3 version of the Macro Deck 2 Philips Hue Plugin. It is a new, out-of-process plugin
and can take over the bridges and buttons you set up in Macro Deck 2.

## Requirements

- Macro Deck 3
- A Philips Hue bridge on the same local network as the computer running Macro Deck

## Setting up a bridge

1. In Macro Deck, open **Integrations** and set up **Philips Hue Plugin**.
2. Pick your bridge from the list. If it is not listed, choose **Enter an address** and type the
   bridge's IP address (the Hue app shows it under **Settings > Bridges**).
3. Press the round link button on top of the bridge, then select **Continue** within 30 seconds.

Repeat for each additional bridge. When more than one bridge is set up, each action asks which bridge to use.

If a bridge's IP address changes, the plugin finds it again automatically.

## Actions

| Action | What it does |
| --- | --- |
| Set scene | Recalls a scene stored on the bridge, either on the scene's own lights or on a room you pick. |
| Update light | Sets one or more lights to a fixed power state, brightness, color and fade time. |
| Adjust light | Raises or lowers brightness, saturation, hue and color temperature relative to the current state. |

## Coming from Macro Deck 2

Macro Deck 3's migration wizard hands your Macro Deck 2 setup to this plugin:

- **Paired bridges** become integration entries, so no re-pairing is needed. (This needs the
  migration wizard to read Macro Deck 2's stored credentials.)
- **Set scene**, **Update light** and **Adjust light** buttons become the matching Macro Deck 3 actions,
  with the same bridge, lights, scene and settings. Brightness is now a percentage instead of 0-255.

## Network use and privacy

- The plugin talks to your bridge over the bridge's local HTTP API (Hue API v1).
- To find bridges, it uses, in this order:
  1. **mDNS** on your local network (the bridge announces itself as `_hue._tcp`), and at the same time
  2. **a scan of your local subnet**: one `GET /api/config` request to each address next to your computer
     (at most 254 per network adapter), and
  3. only if both find nothing, **Signify's discovery service** (`https://discovery.meethue.com`), which
     lists the Hue bridges registered from your public IP address. The plugin sends nothing else there.
- Discovery runs when you set up a bridge, and when a configured bridge stops answering at its last
  address.
- The app key the bridge issues during pairing is stored in Macro Deck's encrypted secret store.

## Known limitations

- Uses the Hue API v1 over plain HTTP on your local network, as the Macro Deck 2 plugin did.
- No variables or button states yet. Lights are controlled, not monitored.
- Rooms and zones can be targeted by **Set scene**; **Update light** and **Adjust light** work on individual lights.

## Development

```bash
dotnet build
dotnet test
macrodeck-plugin test --project src/DodecaDev.PhilipsHue
```

To debug against the desktop app, enable Developer Mode in Macro Deck and start the
**Macro Deck - Real Host** launch profile (first run needs a one-time enrollment token in the project's .NET
User Secrets under `MacroDeck:Plugin:EnrollmentToken`).

Releases are published by creating a GitHub release; `.github/workflows/release.yml` runs the official
Macro Deck publishing workflow. The release tag sets the version.

## License

MIT. Based on the original Macro Deck 2 plugin by RecklessBoon.
