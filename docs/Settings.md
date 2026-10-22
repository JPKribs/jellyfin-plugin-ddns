# Settings Reference

Every setting on every tab, with its default and range. For what each tab is for, see
[The Plugin's Pages](Pages.md).

## Domains

One record per DNS name. The fields shown, and their labels, depend on the **Provider**. Each
[provider page](providers/README.md) lists exactly what it shows.

| Setting | What it does | Default |
|---|---|---|
| **Name** | A label for your own reference. | Empty |
| **Provider** | The DNS company, out of 45. | Cloudflare |
| **Enabled** | Whether the record is updated. Disabled records keep their settings. | On |
| **Update IPv4** | Keeps the A record current. | On |
| **Update IPv6** | Keeps the AAAA record current. Hidden for providers that only take IPv4. | On |
| Name, login, password and zone fields | What the provider needs. Their labels change per provider. | Empty |
| **Server override** | Sends updates to another server, for providers with compatible services. | The provider's own |
| **TTL** | How long DNS may cache the record, in seconds, for providers that take one. `1` uses the provider's default. | `1` |
| **Treat as proxied address** | Compares against the last address sent instead of looking up DNS. For records behind a proxy or CDN. | Off |
| **Advanced options** | Wildcard, static DNS and mail settings, for the DynDNS family only. | Off |

## Address

| Setting | What it does | Default | Range |
|---|---|---|---|
| **IPv4 Detection URLs** | Services that reply with your IPv4 address, one per line. Only `https` is used. | A built-in list | Any number |
| **IPv6 Detection URLs** | The same, for IPv6. | A built-in list | Any number |
| **Skip update if internal** | Never publishes a private, loopback or CGNAT address. | On | |
| **Force update** | Sends the address again after this long, even when DNS matches. | Off | 1 hour to weekly |
| **Back off after failures** | Failures in a row before a record is paused. | 3 | Off, 3, 5 or 10 |
| **Back off duration** | How long a paused record waits. | 24 hours | 1 to 48 hours |
| **Request timeout** | How long each request may take, in seconds. | 15 | 1 to 300 |

How often updates run is set on the **Update DDNS** scheduled task, every 15 minutes by default. See
[How Updates Run](HowItWorks.md).

## Proxy

| Setting | What it does | Default |
|---|---|---|
| **Enabled** | Starts the proxy on ports 80 and 443. | Off |
| **Address** | The names people use to reach Jellyfin. Names from Domains records are listed, others go under **Other addresses**. | None |
| **Source** | Where the certificate comes from: Let's Encrypt, PEM files, a PKCS#12 file, self-signed, or none. | Let's Encrypt |
| **I accept the Let's Encrypt terms** | Nothing is requested from Let's Encrypt until this is ticked. | Off |
| **Certificate file**, **Private key file**, **Password** | The files for PEM or PKCS#12. The password is stored encrypted. | Empty |
| **Trust the Proxy** | Lets Jellyfin see each visitor's real address. Until it is done, internet visitors are turned away. Jellyfin restarts once. **Undo** reverses it. | Not set |

See [Remote Access](RemoteAccess.md).

## Services

Each Service is another app behind the proxy. The settings marked **Default** follow the Advanced tab unless set,
and the page says what that currently is.

| Setting | What it does | Default | Range |
|---|---|---|---|
| **Name** | A label for your own reference. | Empty | |
| **Enabled** | Whether the proxy serves it. | On | |
| **Addresses**, **Other addresses** | The names people use for this app. Each name belongs to one app only. | None | |
| **Target** | Where the app listens on your network, such as `http://192.168.1.20:5055`. | Empty | |
| **Reachable from** | **Anywhere**, or **Your network only**. | Anywhere | |
| **Largest upload from the internet** | In MB, with 0 for unlimited. | Default, 100 MB | 0 or more |
| **Largest upload from your network** | In MB, with 0 for unlimited. | Default, unlimited | 0 or more |
| **App timeout** | How long the app may go silent before a request is dropped, in seconds. | Default, 300 | 61 to 86400 |
| **Minimum read rate** | The slowest a visitor may read the app's pages, in bytes per second. | Off | 0 or more |
| **Always use HTTPS** | Browsers that have visited once refuse plain HTTP. | Default, on | |
| **HSTS duration** | How long browsers keep using HTTPS, in days. The same as **HTTPS memory** on the Advanced tab. | Default, 180 | 1 to 730 |
| **Framing** | **Same site only**, or **Any site** may embed the app. The same as **Block embedding** on the Advanced tab. | Default, same site only | |
| **Compression** | Shrinks pages and scripts. Turn it on only for apps that sign in with headers rather than cookies, since compressed pages can leak secrets. | Off | |
| **Send the original address to the app** | Passes the name the visitor asked for, for apps that need it. | Off | |
| **App certificate** | For an `https://` target: **Verify normally**, **Accept one pinned certificate** by its SHA-256 thumbprint, or **Accept any certificate**. | Verify normally | |

## Advanced

### Security

| Setting | What it does | Default | Range |
|---|---|---|---|
| **Only through the proxy** | Jellyfin only listens on this server while it trusts the proxy. See [Only through the proxy](RemoteAccess.md#only-through-the-proxy). | Off | |
| **Always use HTTPS** | Browsers that have visited once refuse plain HTTP, known as HSTS. | On | |
| **HTTPS memory** | How long browsers keep using HTTPS after a visit, in days. | 180 | 1 to 730 |
| **Block embedding** | Other websites cannot frame Jellyfin's pages. | On | |
| **Trusted networks** | Addresses or ranges treated like your own network, such as a VPN, one per line. | None | |

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

IPv6 bans cover the visitor's whole /64, and widen to the /56 and /48 when failures spread across them. See
[Protection](RemoteAccess.md#protection).

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

Jellyfin compresses its own pages, through the proxy too, so the proxy leaves them as they are. Compression for
other apps is set on each Service.

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
the block list, **Country rule**, **Countries** and history. Nothing changes until you save. **Restart Proxy** restarts
the listener without restarting Jellyfin.

## Scheduled tasks

Under **Dashboard → Scheduled Tasks**:

| Task | What it does | Runs |
|---|---|---|
| **Update DDNS** | Checks and updates every record. | At startup and every 15 minutes |
| **Refresh Certificate** | Requests a Let's Encrypt certificate when one is missing, no longer covers your addresses, or expires within 30 days. Reloads certificate files and replaces a self-signed one before it expires. | Daily at 3:00 |
| **Refresh Locations** | Downloads the newest DB-IP location database. | Weekly |

To replace a certificate now, see [Renewal](RemoteAccess.md#renewal).
