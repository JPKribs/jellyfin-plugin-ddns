# Monitoring

## Record status

The status card in step 2, **Address**, of the **Configuration** tab shows how the DDNS record is doing:

* **Public IP**: the address the server was last seen at.
* **Last update**: when the record was last checked.
* **Result**: what the last check did, in green when it worked and red when it failed, or **Paused** when updates
  are off under **Advanced → Dynamic DNS**.

When the proxy is off, the step's badge says **Up to date** once the last check worked within the last 24 hours,
**Failing** when it did not, and **Not updated yet** before the first one.

## Activity log

The plugin writes what it does to Jellyfin's activity log, which is part of Jellyfin's own dashboard, not the
plugin's **Dashboard** tab. Changes you make are credited to you.

![Activity log](screenshots/Activity%20Log.png)

Each entry has a level:

* **Error**: something people rely on is broken. A record is left on an old address, the proxy did not start,
  there is no working certificate, Jellyfin does not answer through the proxy, or the plugin cannot use its own
  files.
* **Warning**: something needs attention but still works. A certificate expires soon, a renewal failed while the
  current certificate is still valid, a record is paused, an address was banned, the router did not open the
  ports, or a Let's Encrypt TXT record was left at the provider.
* **Information**: everything else.

A problem is written once when it starts, and once more when it clears, not on every run. A run where every
record is already current writes nothing.

### Dynamic DNS entries

* **Record updated**: a record was sent a new address. Refreshing the same address only goes to the server log.
* **Update failed**: the provider refused the update or could not be reached. It is an error when the record is
  left on an old address, and a warning when DNS already has the current one, as after a failed refresh.
* **Update works again**: a record that was failing updated.
* **Updates paused**: a record was paused after repeated failures, once per streak.
* **Detection unhealthy**: no public IP was found for about an hour, with an entry when it is found again.
* **Configuration changes**: the record was added, changed or removed, or the settings under **Advanced → Dynamic DNS** changed.
* **Plaintext credentials**: credentials cannot be encrypted on this server, once per outage.
* **Remote Access cannot use its own files**: Jellyfin runs as an account that cannot open the plugin's folders.
  It is an error, and names the command that fixes it. See [Recovery](RemoteAccess.md#recovery).

### Remote Access entries

* **Settings changed**: a tab was saved with a change, naming what changed.
* **Proxy could not start**: once per reason, such as a port in use, with an entry when it starts. With
  **Only through the proxy** on, the plugin also puts Jellyfin's own addresses back, so Jellyfin stays reachable,
  and says why.
* **Trust the Proxy**: Jellyfin was set to trust the proxy, answered through it after the restart or did not,
  or **Undo** put its settings back.
* **Router ports**: written once when something changes, not on every hourly renewal.
    * **Remote Access opened ports 80 and 443 on your router**: the router forwards the ports, and the entry names
      the address they go to and whether UPnP or NAT-PMP was used. Before a certificate is ready, only port 80 is
      opened.
    * **Remote Access could not open ports on your router**: a warning with the router's reason, such as a port
      another device already holds.
    * **Remote Access found no router that opens ports**: a warning that no router answered over UPnP or NAT-PMP.
      Turn one of them on in the router's settings, or forward the ports by hand.
* **Certificates**: a certificate was issued, renewed, could not be, could not be loaded, or expires soon. Asking
  for a new one is logged with who asked.
* **Root certificate created**: the plugin made a new self-signed root. Every device that should trust the
  self-signed certificate needs it installed. See [Self-signed certificates](RemoteAccess.md#self-signed-certificates).
* **TXT record left over**: a Let's Encrypt TXT record could not be removed from the provider. It is a warning. The
  record does no harm, but you can delete it there.
* **Bans**: an address or network was banned. Past five bans in a minute, the rest are summed up in one entry.
  Lifting a ban is logged too.
* **Blocks**: an address was blocked or unblocked, or a country unblocked, from the Dashboard.
* **Refused connections**: an hourly summary of what the block list, bans and country rules turned away.
* **Most visitors look local**: see [When every visitor looks local](RemoteAccess.md#when-every-visitor-looks-local).
  Logged once.
* **Location database**: downloading it failed, once per reason, with an entry when it works again.
* **History settings changed**: **Recording**, **History period** or **Location** was changed.
* **History cleared**: every recorded visit, sign-in and refusal was deleted.
