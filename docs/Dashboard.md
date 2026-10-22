# Dashboard & Monitoring

The **Dashboard** tab shows who reaches your server through Remote Access, where from, and what was turned away.
It is the page the **Remote Access** entry in the dashboard's side menu opens on, and it refreshes itself every 30 seconds.

![Dashboard tab](screenshots/Dashboard.png)

## Share your server

Once Remote Access has an address, the **Share Your Server** card at the top shows it with a QR code. It is the
proxy's first public name, the DDNS record's hostname in step 2 of the **Configuration** tab. A **Public** badge means
anyone can open it, and **Home only** means only your home network can.

* Scan the code with a phone camera to open the address.
* **Copy URL** puts the address on the clipboard, and **Copy Image** the QR code, ready to paste into a message.
  A browser that cannot copy images, such as one on a plain `http://` page, saves the code as a file instead.
* **Share** opens the device's share sheet, on browsers that have one.
* When the only address is one your home network alone knows, such as `nas.lan`, the card shows **Home only** and a note,
  since phones outside cannot open it. Choose a public address in step 2 to share it outside.
* The card stays hidden while the proxy is off, or has no address other than a wildcard.

![Share card](screenshots/Share%20Card.png)

## Locations

A world map and a table of countries.

* **Users**, **Failures** and **Blocked** above the map choose what it shows. All three are on when the page
  opens, and at least one always stays on.
* On the map, countries with users are blue, countries with failed sign-ins red, and countries with only blocked
  visitors purple. The more activity, the stronger the colour.
* The table has a row per country, with the number of users, failed sign-ins and blocked visitors, and when the
  country was last seen.

## Banned

Addresses banned for failed sign-ins: the **IP**, its country, how many failures, when it was banned and when the
ban ends. **Unban** lifts it straight away, together with any wider ban on the same IPv6 network.

How many failures ban an address, and for how long, is set under **Advanced > Blocking**. See
[Protection](RemoteAccess.md#protection).

## Activity

Every address that reached the proxy, newest first, up to the latest 500. Filter it by **Type** and **Country**.
The **Country** filter also has **Local**, for visitors from your own network.

| Type | Meaning |
|---|---|
| **User** | Signed in, with the user and device. |
| **Failure** | A sign-in that failed. |
| **Blocked** | Turned away by a ban, the block list or the country rules. |
| **Seen** | Reached the sign-in page and did nothing else. |

Each row shows the **Type**, **User**, **IP**, **Device**, the **Address** it asked for, its **Country** and when it
was **Last Seen**.
With **Location** set to **Country and city**, an **Area** column adds the city.
Visitors from your own network show as **Local**. IPv6 visitors are grouped by their /64, since one device can use
many addresses in it.

The button at the end of a row acts on it:

* **Unban** lifts the ban on an address that is banned, as on the Banned table.
* **Block** adds the address to the block list. Your own network and trusted networks cannot be blocked.
* **Unblock** removes it. An address blocked by a range under **Advanced** is unblocked there instead.
* **Unblock** on a country turned away by the country rules allows that country from then on.

## What is recorded

History is on by default and kept for 90 days. Change it under **Advanced > History**, listed in the
[Settings Reference](Settings.md#history). With **Recording** off the Dashboard stays empty, but bans and blocks
still work. **Clear History** deletes everything recorded and keeps the block list.

Locations come from the free [DB-IP](https://db-ip.com) database, downloaded to this server. Addresses are looked up
on the server itself and never sent anywhere. The history is kept in `people.db`. See [Storage](Security.md#storage).

## Monitoring

The Dashboard tab covers visitors. The DDNS record and the plugin itself report in two other places.

### Record status

The status card in step 2, **Address**, of the **Configuration** tab shows how the DDNS record is doing:

* **Public IP**: the address the server was last seen at.
* **Last update**: when the record was last checked.
* **Result**: what the last check did, in green when it worked and red when it failed, or **Paused** when updates
  are off under **Advanced > Dynamic DNS**.

When the proxy is off, the step's badge says **Up to date** once the last check worked within the last 24 hours,
**Failing** when it did not, and **Not updated yet** before the first one.

### Activity log

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

#### Dynamic DNS entries

* **Record updated**: a record was sent a new address. Refreshing the same address only goes to the server log.
* **Update failed**: the provider refused the update or could not be reached. It is an error when the record is
  left on an old address, and a warning when DNS already has the current one, as after a failed refresh.
* **Update works again**: a record that was failing updated.
* **Updates paused**: a record was paused after repeated failures, once per streak.
* **Detection unhealthy**: no public IP was found for about an hour, with an entry when it is found again.
* **Configuration changes**: the record was added, changed or removed, or the settings under **Advanced > Dynamic DNS** changed.
* **Credentials cannot be encrypted**: an error when the plugin could not set up its encryption keys, once per
  outage. No credential can be saved until it can. Reinstall the plugin, then check the permissions on its folders.
* **Remote Access cannot use its own files**: an error when Jellyfin runs as an account that cannot open the
  plugin's folders, naming the command that fixes it. See [Recovery](RemoteAccess.md#recovery).

#### Remote Access entries

* **Settings changed**: a tab was saved with a change, naming what changed.
* **Proxy could not start**: once per reason, such as a port in use, with an entry when it starts. With
  **Only through the proxy** on, Jellyfin stays on this server alone, and the entry says how to turn the proxy off
  by hand. See [If the proxy stops working](RemoteAccess.md#if-the-proxy-stops-working).
* **Trust the Proxy**: Jellyfin was set to trust the proxy, answered through it after the restart or did not,
  or **Undo** put its settings back.
* **Router ports**: written once when something changes, not on every hourly renewal.
    * **Remote Access opened ports 80 and 443 on your router**: names the device that answered, the address the
      ports go to and whether UPnP or NAT-PMP was used, and says when that device is not this server's default
      gateway. Before a certificate is ready, only port 80 is opened. See
      [Checking the forward](RemoteAccess.md#checking-the-forward).
    * **Remote Access could not open ports on your router**: a warning with the router's reason, such as a port
      another device already holds.
    * **Remote Access found no router that opens ports**: a warning that no router answered over UPnP or NAT-PMP.
* **Certificates**: a certificate was issued, renewed, could not be, could not be loaded, or expires soon. Asking
  for a new one is logged with who asked.
* **Root certificate created**: the plugin made a new self-signed root, naming what it may vouch for. Every device
  that should trust the self-signed certificate needs it installed, in place of any earlier one. See [Self-signed certificates](RemoteAccess.md#self-signed-certificates).
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
