# Storage

Everything the plugin keeps is in Jellyfin's data folder, so a normal Jellyfin backup includes it. These paths are
relative to that folder.

| What | Where |
| :--- | :--- |
| The plugin itself | `plugins/Dynamic DNS_<version>/` |
| Records, settings and encrypted credentials | `plugins/configurations/Jellyfin.Plugin.DynamicDns.xml` |
| The key for those credentials | `plugins/configurations/Jellyfin.Plugin.DynamicDns.Keys/` |
| Remote Access data | `plugins/configurations/Jellyfin.Plugin.DynamicDns.Proxy/` |

The Remote Access folder holds:

| File | What |
| :--- | :--- |
| `acme/` | The Let's Encrypt account, certificate and key |
| `selfsigned-root.pfx`, `selfsigned.pfx` | The self-signed root and the certificate issued from it |
| `people.db` | Visitor history, bans and refusals |
| `GeoIP/` | The DB-IP location database |
| `network-state.json` | What **Trust the Proxy** changed, for **Undo** |
| `DISABLE` | Only when you create it, to turn the proxy off. See [Recovery](RemoteAccess.md#recovery). |

Jellyfin's own config folder also gets `network.before-remote-access.xml`, a copy of its network settings from
before the plugin first changed them.

!!! note "Keep the key with the settings"
    The credentials can only be read with the key beside them. If the key folder is lost or reset, enter your
    tokens and passwords again.
