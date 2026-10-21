# Monitoring

## Record health

The cards at the top of the **Domains** tab count your records:

* **Healthy**: the last update succeeded within 24 hours.
* **Unhealthy**: it has not succeeded within 24 hours.
* **Unpublished**: it has not run yet.

![Record health summary](screenshots/Record%20Health.png)

## Activity log

The plugin writes what it does to Jellyfin's activity log, under **Dashboard → Activity**. Changes you make are
credited to you.

![Activity log](screenshots/Activity%20Log.png)

Each entry has a level:

* **Error**: something people rely on is broken. A record is left on an old address, the proxy did not start,
  there is no working certificate, or Jellyfin does not answer through the proxy.
* **Warning**: something needs attention but still works. A certificate expires soon, a renewal failed while the
  current certificate is still valid, a record is paused, or an address was banned.
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
* **Configuration changes**: a record was added, changed or removed, or the Address tab's settings changed.
* **Plaintext credentials**: credentials cannot be encrypted on this server, once per outage.

### Remote Access entries

* **Settings changed**: a tab was saved with a change, naming what changed.
* **Proxy could not start**: once per reason, such as a port in use, with an entry when it starts.
* **Trust the Proxy**: Jellyfin was set to trust the proxy, answered through it after the restart or did not,
  or **Undo** put its settings back.
* **Certificates**: a certificate was issued, renewed, could not be, could not be loaded, or expires soon. Asking
  for a new one is logged with who asked.
* **Bans**: an address or network was banned. Past five bans in a minute, the rest are summed up in one entry.
  Lifting a ban is logged too.
* **Blocks**: an address or country was blocked or unblocked from the Dashboard.
* **Refused connections**: an hourly summary of what the block list, bans and country rules turned away.
* **Every visitor looks local**: see [When every visitor looks local](RemoteAccess.md#when-every-visitor-looks-local).
  Logged once.
* **Location database**: downloading it failed, once per reason, with an entry when it works again.
* **History cleared**: every recorded visit, sign-in and refusal was deleted.
