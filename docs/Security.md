# Security & Storage

## Credentials

* **Administrators only**: only Jellyfin administrators can see or change records and settings.
* **Encrypted at rest**: DNS credentials and certificate passwords are encrypted before they are saved. The key
  that decrypts them is kept in Jellyfin's data folder, so anyone with a copy of that folder could read them.
  Use the most limited token your provider offers, so a leak can only change DNS.
* **Key protected on Windows**: on Windows the key itself is encrypted for this machine, so a copy of the folder,
  such as a backup, cannot decrypt anything elsewhere. Linux and macOS have no such store for services. There,
  keep backups of Jellyfin's data folder private.
* **Keep the key with the settings**: the credentials can only be read with the key beside them. If the key
  folder is lost or reset, enter your tokens and passwords again.
* **Never stored unencrypted**: if the plugin cannot set up its encryption keys, it refuses to save credentials
  and asks you to reinstall it and check the permissions on its folders. A credential it finds unencrypted while
  the keys are missing is removed rather than used.
* **Never logged**: credentials, and anything that looks like one in an address, a provider's reply or an error,
  are masked in the server log, the activity log and the status the plugin keeps. A provider's reply is also
  checked for the very credentials it was sent with. IP detection endpoints are logged by their host alone.
* **Keys stay here**: certificate keys are made on this server and never leave it.
* **Self-signed root**: it can only vouch for your proxy's names, `localhost` and private addresses, and lasts
  three years. Its key is in `selfsigned-root.pfx`, readable only by Jellyfin's account. A copy of that file could
  make certificates for your names that your devices trust, so keep backups private. See
  [Self-signed certificates](RemoteAccess.md#self-signed-certificates).

## Network

* **Dynamic DNS only connects out**, to your DNS provider and the IP detection services. It uses HTTPS unless
  you set a **Server override** that starts with `http://`. Record names are looked up with the server's own DNS
  settings.
* **Credentials stay with their server**: a request to a provider follows a redirect only on the same server. A
  redirect to another server, or from HTTPS to HTTP, is never followed, so a key is only ever sent where you
  entered it for.
* **Ports open only with Remote Access**: nothing listens until the proxy is turned on. Then it refuses names you
  did not add before encrypting anything, keeps the setup wizard off the internet, and never blocks your own
  network. See [Protection](RemoteAccess.md#protection).
* **The router**: with **Automatic port forwarding** on, the plugin asks the router on your own network to forward
  ports 80 and 443, and nothing else. See [Automatic port forwarding](RemoteAccess.md#automatic-port-forwarding).
* **Test from Outside** sends the hostname and port 443 to check-host.net, and only when you select it.

## Storage

Everything the plugin keeps is in Jellyfin's data folder, so a normal Jellyfin backup includes it. These paths are
relative to that folder.

| What | Where |
| :--- | :--- |
| The plugin itself | `plugins/Dynamic DNS_<version>/` |
| Records, settings and encrypted credentials | `plugins/configurations/Jellyfin.Plugin.DynamicDns.xml` |
| The key for those credentials | `plugins/configurations/Jellyfin.Plugin.DynamicDns.Keys/` |
| Each record's last status, its backoff state, and the last detected public IP | `plugins/configurations/Jellyfin.Plugin.DynamicDns.Status.json` |
| Remote Access data | `plugins/configurations/Jellyfin.Plugin.DynamicDns.Proxy/` |

The Remote Access folder holds:

| File | What |
| :--- | :--- |
| `acme/` | The Let's Encrypt account, certificate and key |
| `selfsigned-root.pfx`, `selfsigned.pfx` | The self-signed root and the certificate issued from it |
| `people.db` | Visitor history, bans and refusals |
| `GeoIP/` | The DB-IP location database, and the one it last replaced |
| `network-state.json` | What **Trust the Proxy** changed, for **Undo** |
| `DISABLE` | Only when you create it, to turn the proxy off. See [Recovery](RemoteAccess.md#recovery). |

Jellyfin's own config folder also gets `network.before-remote-access.xml`, a copy of its network settings from
before the plugin first changed them.

### Private folders

The key folder and the Remote Access folder are made readable only by Jellyfin's account at every start. On
Windows, SYSTEM and Administrators keep access too. If Jellyfin later runs as a different account that cannot open
these folders, the Configuration tab, the activity log and the server log say so, with the command that fixes it.
