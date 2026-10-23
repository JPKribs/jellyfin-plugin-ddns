# Settings Reference

Remote Access adds one entry, **Remote Access**, to the dashboard's side menu. Its pages share a tab bar along the
top: **Dashboard**, **Configuration** and **Advanced**. This page tours the three tabs, then lists every setting
with its default and range.

## The three tabs

### Dashboard

Who reaches the server, from where, and what was turned away: the share card with **Copy URL**, **Copy Image** and
**Share**, a map and table of **Locations**, the **Banned** list and the **Activity** list. See
[Dashboard & Monitoring](Dashboard.md).

### Configuration

Everything needed to reach Jellyfin from outside, as six numbered steps. Each step shows its state on the right,
such as **Ready** or **Forward by hand**, and opens to show its settings. Steps that are not done yet open on their
own. The summary at the top counts the steps that are done, with a bar for each step in its colour. It explains
the first step that needs attention, with **Open step** to jump to it, or says **Ready to share** once all six are
done. Under it, a note names the countries that can connect, with a link to change them in **Advanced > Blocking**.

![Configuration tab](screenshots/Configuration.png)

1. **Proxy on**: tick **Enabled** to start the proxy.
2. **Address**: the one DDNS record, with only **Provider**, **Hostname** and the credentials that provider asks
   for. A card shows the public IP, the last update and its result, with **Update Now**. See
   [The DDNS Record](Records.md).
3. **Certificate**: Let's Encrypt, your own files, or a self-signed certificate, with its status.
4. **Router**: the ports to forward by hand when the router does not, **Try Again**, and a warning when your
   provider shares your public address.
5. **Trust the Proxy**: lets Jellyfin see each visitor's real address. **Undo** puts it back.
6. **Reachable from outside**: **Test from Outside** checks that the internet can reach your address.

**Save** at the bottom keeps every step, the record in step 2 included. Steps 1 and 3 to 6 are explained in
[Remote Access](RemoteAccess.md).

![Step 2, Address](screenshots/Address%20Step.png)

### Advanced

Every setting not on the other tabs, each starting on its default, in seven sections: **Dynamic DNS** with the
record's **Record** group, **Addresses**, **Security**, **Blocking**, **History**, **Performance** and
**Timeouts**. **Save** on that tab keeps them. Each section has its own table below.

![Advanced tab](screenshots/Advanced.png)

## Defaults worth knowing

The defaults favour safety. Three of them can surprise you, and each is one change under **Advanced**.

* **Country rule: Server country only.** Only visitors from the country this server is in can connect from the
  internet. The **Configuration** tab names the countries that can connect, in green. Family abroad or a phone on a
  VPN in another country is refused, so add their countries in the **Countries** picker under
  **Advanced > Blocking**, or set **Country rule** to **Off**. Until the server's own country is known, shown in
  yellow, everyone can connect.
* **HTTPS memory: 180 days.** A browser that visited once keeps using HTTPS for your address for 180 days, which
  stops anyone downgrading the connection. If you later turn the proxy off, those browsers refuse `http://` for
  your address until it runs out, so open Jellyfin by its local IP instead.
* **Back off duration: 24 hours.** After 3 failed updates in a row the record waits a day, so a wrong password
  does not get your account blocked at the provider. A change of public IP still tries again at once.

## DDNS record

The fields shown, and their labels, depend on the **Provider**. Each [provider page](providers/README.md) lists
exactly what it shows. See [The DDNS Record](Records.md) for how the record works.

In step 2, **Address**, of the **Configuration** tab:

| Setting | What it does | Default |
|---|---|---|
| **Provider** | The DNS company, out of 45. | Cloudflare |
| Hostname, login, password and zone fields | What the provider needs. Their labels change per provider. | Empty |

Under **Advanced > Dynamic DNS**, in the **Record** group:

| Setting | What it does | Default |
|---|---|---|
| **Enabled** | Whether the record is updated. A paused record keeps its settings. | On |
| **Update IPv4** | Keeps the A record current. | On |
| **Update IPv6** | Keeps the AAAA record current. Hidden for providers that only take IPv4. | On |
| **Server override** | Sends updates to another server, for providers with compatible services. | The provider's own |
| **TTL** | How long DNS may cache the record, in seconds, for providers that take one. `1` uses the provider's default. | `1` |
| **Treat as proxied address** | Compares against the last address sent instead of looking up DNS. For a record behind a proxy or CDN. | Off |
| Wildcard, static DNS and mail settings | For the DynDNS family only. | Off |
| **Remove Record** | Deletes the record and takes its names off the proxy's addresses. | |

## Configuration

The proxy's settings in the numbered steps of the **Configuration** tab. The proxy answers on the record's names
and the **Other addresses** under Advanced.

| Setting | What it does | Default |
|---|---|---|
| **Enabled** | Step 1. Starts the proxy on ports 80 and 443. | Off |
| **Source** | Step 3. Where the certificate comes from: Let's Encrypt, PEM files, a PKCS#12 file, self-signed, or none. | Let's Encrypt |
| **I accept the Let's Encrypt terms** | Step 3. Nothing is requested from Let's Encrypt until this is ticked. | Off |
| **Certificate file**, **Private key file**, **Password** | Step 3. The files for PEM or PKCS#12. The password is stored encrypted. | Empty |
| **Trust the Proxy** | Step 5. Lets Jellyfin see each visitor's real address. Until it is done, internet visitors are turned away. Jellyfin restarts once. **Undo** reverses it. | Not set |

## Advanced

### Dynamic DNS

How the DDNS record finds your public IP and when it is sent. The record's own settings are in the **Record**
group of this section, listed under [DDNS record](#ddns-record).

| Setting | What it does | Default | Range |
|---|---|---|---|
| **IPv4 Detection URLs** | Services that reply with your IPv4 address, one per line. Only `https` is used. | A built-in list | Any number |
| **IPv6 Detection URLs** | The same, for IPv6. | A built-in list | Any number |
| **Skip update if internal** | Never publishes a private, loopback or CGNAT address. | On | |
| **Force update** | Sends the address again after this long, even when DNS matches. | Off | 1 hour to weekly |
| **Back off after failures** | Failures in a row before the record is paused. | 3 | Off, 3, 5 or 10 |
| **Back off duration** | How long a paused record waits. | 24 hours | 1 to 48 hours |
| **Request timeout** | How long each request may take, in seconds. | 15 | 1 to 300 |

How often updates run is set on the **Update DDNS** scheduled task, every 15 minutes by default. See
[How updates run](Records.md#how-updates-run).

### Addresses

| Setting | What it does | Default | Range |
|---|---|---|---|
| **Other addresses** | More names the proxy answers on besides the DDNS record's names, one per line. | None | |
| **Automatic port forwarding** | Asks the router to forward ports 80 and 443 to this server through NAT-PMP or UPnP. See [Automatic port forwarding](RemoteAccess.md#automatic-port-forwarding). | On | |

### Security

| Setting | What it does | Default | Range |
|---|---|---|---|
| **Only through the proxy** | Jellyfin only listens on this server while it trusts the proxy. See [Only through the proxy](RemoteAccess.md#only-through-the-proxy). | Off | |
| **Always use HTTPS** | Browsers that have visited once refuse plain HTTP, known as HSTS. | On | |
| **HTTPS memory** | How long browsers keep using HTTPS after a visit, in days. | 180 | 1 to 730 |
| **Block embedding** | Other websites cannot frame Jellyfin's pages. | On | |
| **Trusted networks** | Addresses or ranges treated like your own network, such as a VPN, one per line. | None | |

**Request a New Certificate** replaces the certificate early. See [Renewal](RemoteAccess.md#renewal).

### Blocking

These only ever apply to the internet, never to your own or trusted networks.

| Setting | What it does | Default | Range |
|---|---|---|---|
| **Blocked addresses** | Addresses or ranges always refused, one per line. | None | |
| **Failed sign-ins before a ban** | Failures within the window that ban an address. 0 turns bans off. | 5 | 0 to 1000 |
| **Ban window** | The minutes failures are counted over. | 10 | 1 to 1440 |
| **Ban length** | How long a ban lasts in minutes. 0 bans until you lift it. | 60 | 0 to 525600 |
| **Country rule** | **Server country only**, **Block these countries**, **Allow only these countries**, or **Off**. | Server country only | |
| **Countries** | The countries the rule names. | None | |
| **Unknown addresses** | Allow or block addresses the location database has no country for. | Allow | |
| **Without the location database** | Allow or block everyone while the database is missing or downloading. | Allow | |

How bans work for IPv6 is under [Protection](RemoteAccess.md#protection).

### History

| Setting | What it does | Default | Range |
|---|---|---|---|
| **Recording** | Records who connects and what they did, for the Dashboard. | On | |
| **History period** | How long history is kept, in days. | 90 | 1 to 3650 |
| **Location** | **Country**, **Country and city**, which is a 130 MB download, or **Off**. | Country | |

**Refresh Locations** downloads the newest location database now, and **Clear History** deletes everything
recorded. The **Refresh Locations** scheduled task also does this weekly.

### Performance

| Setting | What it does | Default | Range |
|---|---|---|---|
| **HTTP/2** | Lets browsers load pages faster over one connection. | On | |
| **Largest upload from the internet** | In MB, with 0 for unlimited. | 100 | 0 or more |
| **Largest upload from your network** | In MB, with 0 for unlimited. | Unlimited | 0 or more |
| **Connections per internet address** | Connections one internet address may hold open, with 0 for unlimited. | Auto: 64, or 32 on low memory | 0 or more |
| **Total connections** | Connections the proxy holds open in all, with 0 for unlimited. | Auto: 2000, or 500 on low memory | 0 or more |

Jellyfin compresses its own pages, through the proxy too, so the proxy leaves them as they are.

### Timeouts

| Setting | What it does | Default | Range |
|---|---|---|---|
| **Idle connections** | How long an unused connection stays open, in seconds. | Auto: 130, or 60 on low memory | 5 to 3600 |
| **Request headers** | How long a visitor may take to send a request's headers, in seconds. | 15 | 1 to 300 |
| **Header size** | The largest set of request headers accepted, in KB. | 32 | 8 to 1024 |
| **Minimum read rate** | The slowest a visitor may read Jellyfin's pages, in bytes per second. Media is exempt. 0 is off. | 240 | 0 or more |
| **Jellyfin response** | How long Jellyfin may go silent before a request is dropped, in seconds. | 300 | 61 to 86400 |

**Auto** settings follow the server's memory. Below 3 GB available to Jellyfin they use the lower value. See
[Low-memory servers](RemoteAccess.md#low-memory-servers). **Reset to Defaults** puts every limit and security
setting back, **Unknown addresses** and **Without the location database** included. It keeps trusted networks,
the block list, **Country rule**, **Countries**, **Other addresses**, history and the **Dynamic DNS** settings. Nothing changes until you save. **Restart Proxy** restarts
the listener without restarting Jellyfin.

## Scheduled tasks

Under **Dashboard > Scheduled Tasks**:

| Task | What it does | Runs |
|---|---|---|
| **Update DDNS** | Checks and updates the DDNS record. | At startup and every 15 minutes |
| **Refresh Certificate** | Requests a Let's Encrypt certificate when one is missing, no longer covers your addresses, or expires within 30 days. Reloads certificate files and replaces a self-signed one before it expires. | Daily at 3:00 |
| **Refresh Locations** | Downloads the newest DB-IP location database. | Weekly |
| **Verify External Access** | Runs step 6's outside check on your public address and port 443, and writes an error to the activity log when no place can connect. Skipped while the proxy is off. | Every hour |
