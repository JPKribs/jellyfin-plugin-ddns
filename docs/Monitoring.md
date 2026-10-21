# Monitoring

## Record Health

The `Domains` tab summarizes every record at a glance.

![Record health summary](screenshots/Record%20Health.png)

* **Healthy**: The last update succeeded within the last 24 hours.
* **Unhealthy**: A published record has not updated successfully in the last 24 hours.
* **Unpublished**: The record has never run an update yet.

## Activity Log

Notable events are written to Jellyfin's activity log under **Dashboard → Activity**, so you can see what the plugin has done without digging through the server log.

Here is the activity log after records were added and their first updates failed.

![Activity log](screenshots/Activity%20Log.png)

Changes you make are credited to you, so the feed shows who did what. Something broken that people rely on is an error: a record left on an old address, the proxy not starting, no working certificate, or Jellyfin not answering through the proxy. Something that needs attention while things still work is a warning, such as a certificate expiring soon, a failed renewal while the current certificate is still valid, a paused record or a ban. Everything else is information. Each kind of problem is written once when it starts, with a second entry when it clears, rather than on every run.

**Dynamic DNS**

* **Record updated**: A record was pushed to its provider with a new address. A periodic refresh of the same address only goes to the server log.
* **Update failed**: A push was attempted and the provider rejected it or could not be reached. It is an error when the record is left on an old address, and a warning when DNS already serves the current one, as after a failed refresh.
* **Update works again**: A record that had been failing updated successfully.
* **Updates paused**: A record entered backoff after repeated failures, with one entry per streak.
* **Detection unhealthy**: No public IP could be detected for about an hour, with a recovery entry when detection returns.
* **Configuration changes**: A record was added, changed or removed, its credentials were changed, or the detection, refresh or backoff settings were changed.
* **Plaintext credentials**: Credential encryption is unavailable on the host, logged once per outage.

**Remote Access**

* **Settings changed**: The proxy, its addresses, certificate, Services or Advanced settings were saved with a change, naming what changed. History settings get their own entry.
* **Proxy could not start**: Once per distinct reason, such as a port in use, with an entry when it starts again.
* **Trust the Proxy**: When Jellyfin is set to trust the proxy, when it answers through it after the restart (or does not), and when Undo puts its settings back.
* **Certificates**: Let's Encrypt issued or renewed a certificate, could not, a certificate could not be loaded, or one expires soon. Asking for a new certificate from the Proxy tab is logged with who asked, and so is a DNS check record that could not be removed after a week.
* **Bans**: An address or network was banned for failed sign-ins. Past five bans in a minute, the rest are summed up in one entry. Lifting a ban is logged too.
* **Blocks**: An address or country was blocked or unblocked from the Remote Access page.
* **Every visitor looks local**: An hour brought many requests for your public names, all from one local address and none from the internet. Logged once. See [When every visitor looks local](RemoteAccess.md#when-every-visitor-looks-local).
* **Refused connections**: One summary an hour of the connections the block list, bans and country rules turned away, while any were. The server log has the count each minute, and the Remote Access page lists the addresses.
* **Location database**: Downloading it failed, logged once per distinct reason, with an entry when it works again.
* **History cleared**: Every recorded visit, sign-in and refusal was deleted.

A run where every record is already current writes nothing, so a quiet server keeps a quiet activity feed.
