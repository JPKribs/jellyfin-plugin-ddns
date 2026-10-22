# Security

## Credentials

* **Administrators only**: only Jellyfin administrators can see or change records and settings.
* **Encrypted at rest**: DNS credentials and certificate passwords are encrypted before they are saved. The key
  that decrypts them is kept in Jellyfin's data folder, so anyone with a copy of that folder could read them.
  Use the most limited token your provider offers, so a leak can only change DNS.
* **Key protected on Windows**: on Windows the key itself is encrypted for this machine, so a copy of the folder,
  such as a backup, cannot decrypt anything elsewhere. Linux and macOS have no such store for services. There,
  keep backups of Jellyfin's data folder private.
* **Never logged**: credentials, and tokens inside addresses, are removed from everything written to the logs.

## Files

* **Private folders**: the folders holding the encryption key, certificate keys, the Let's Encrypt account and
  visitor history are made readable only by Jellyfin's account at every start. On Windows, SYSTEM and
  Administrators keep access too.
* **Keys stay here**: certificate keys are made on this server and never leave it.
* **Account changes**: if Jellyfin later runs as a different account that cannot open these folders, the
  Configuration tab, the activity log and the server log say so, with the command that fixes it.

## Network

* **Dynamic DNS only connects out**, to your DNS provider and the IP detection services. It uses HTTPS unless
  you set a **Server override** that starts with `http://`. Record names are looked up with the server's own DNS
  settings.
* **Ports open only with Remote Access**: nothing listens until the proxy is turned on. Then it refuses names you
  did not add before encrypting anything, keeps the setup wizard off the internet, and never blocks your own
  network. See [Protection](RemoteAccess.md#protection).
* **The router**: with **Automatic port forwarding** on, the plugin asks the router on your own network to forward
  ports 80 and 443, and nothing else. A port the router already forwards to another device is left alone, and the
  forwards are removed when Jellyfin stops or the plugin is uninstalled. See
  [Automatic port forwarding](RemoteAccess.md#automatic-port-forwarding).
* **Test from Outside** sends the hostname and port 443 to check-host.net, and only when you select it.

See [Storage](Storage.md) for where everything is kept.
