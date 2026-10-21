# Remote Access

Remote Access puts a secure HTTPS proxy in front of Jellyfin, inside the plugin. Point a domain at your server
with a Domains record, forward a port on your router, and the plugin handles the certificate, the ports and the
protection. There is no separate reverse proxy to run.

!!! warning "Everything runs inside Jellyfin"
    If Jellyfin is down, remote access is down too. See [Recovery](#recovery) for getting back in if a setting
    locks you out.

## Setup

Everything is on the **Proxy** tab. The cards at the top show what is left to do.

1. **Remote Access**: tick **Enabled** and save.
2. **Address**: the names people use to reach Jellyfin. Names your Domains records keep pointed at this server
   are offered and ticked for you. Add any others underneath.
3. **Certificate**: leave **Let's Encrypt** selected and tick **I accept the Let's Encrypt terms**. See
   [Certificates](#certificates).
4. **Router**: forward TCP ports **80** and **443** to this server. The Router section shows its address.
5. **Jellyfin**: select **Trust the Proxy**. Jellyfin adds this machine to its known proxies and restarts once.
   Its own ports are not changed. If Jellyfin cannot be reached through the proxy afterwards, the Proxy tab says
   so; **Undo** puts its settings back.

Then open your address on a phone with Wi-Fi off.

### Ports

The proxy uses the standard web ports, and Jellyfin keeps its own:

| Port | What |
|---|---|
| 443 | HTTPS for everyone outside your network. Forward it on your router. |
| 80 | Sends `http://` links on to HTTPS, and lets Let's Encrypt check your address. Forward it too. |
| Jellyfin's own | Unchanged, usually 8096 and 8920. Your network keeps using them directly. |

Do not forward Jellyfin's own ports on your router; internet traffic should only reach the proxy. **Trust the
Proxy** turns on Jellyfin's remote access, so anyone who can reach Jellyfin's own ports directly gets past the
proxy. If this server has a public address of its own, such as a VPS or a public IPv6 address, block Jellyfin's
ports for the internet in its firewall; the Proxy tab warns when it sees one.

### Only through the proxy

**Advanced → Security → Only through the proxy** closes that gap from the plugin's side. While Jellyfin trusts
the proxy, it listens on this server alone (127.0.0.1, and ::1 when IPv6 is on), so the proxy is the only way
in, even with Jellyfin's port still forwarded on the router or the server on a public address. It is off by
default because it changes how things at home reach Jellyfin:

| Before | With the option on |
|---|---|
| Local apps connect to `http://192.168.1.10:8096` | Local apps connect by your address, such as `https://media.example.com`, through the proxy. Jellyfin tells them this address. |
| Your address may only work from outside | Your address has to reach this server from home too: through your router (hairpin NAT) or local DNS that points it at the server |
| Apps find Jellyfin by searching the network | Network search, which uses Jellyfin's own port, finds nothing; add the server by its address |
| Chromecast and other casting devices fetch from the server's local address | Casting devices fetch through the proxy too, so they also need your address to resolve at home |
| DLNA devices see Jellyfin | DLNA does not reach it |

Local clients keep their local status: they come through the proxy from your network, so bans, the block list
and country rules never apply to them, and Jellyfin still treats them as local.

It needs an address that is not a wildcard, so there is one to send local apps to. Turning it on or off while
Jellyfin trusts the proxy restarts Jellyfin once; otherwise it takes effect at **Trust the Proxy**. A change
that cannot be applied, such as turning it on while the proxy cannot reach Jellyfin, is refused and not saved.
**Undo**, the `DISABLE` file and uninstalling all put Jellyfin's own addresses back.

!!! warning "Disabling the plugin"
    Disabling the plugin on Jellyfin's Plugins page stops the proxy but leaves Jellyfin listening on this server
    alone, so nobody can reach it. Use **Undo** before disabling. If you are already locked out, either create the
    `DISABLE` file and restart Jellyfin with the plugin enabled, or stop Jellyfin and copy
    `network.before-remote-access.xml` over `network.xml` in Jellyfin's config folder. The copy is saved the
    first time the plugin changes Jellyfin's network settings and is never replaced.

!!! note "Linux"
    Ports below 1024 need permission on Linux. Run `sudo systemctl edit jellyfin`, add
    `AmbientCapabilities=CAP_NET_BIND_SERVICE` under `[Service]`, and restart Jellyfin. In Docker, publish ports
    80 and 443 to the container instead. Windows and macOS need nothing.

!!! warning "Docker"
    The proxy needs to see each visitor's real address. Rootless Docker and rootless Podman, and Docker's
    userland proxy for IPv6 when ip6tables is off, hand every connection over from an internal address instead.
    Then every visitor looks local: bans, the block list and country rules never apply, and the dashboard shows
    only local visitors. The Proxy tab warns, and the activity log notes it once, when an hour brings 100 or more
    requests for your public names from a single local address and none from the internet. Use host networking (`network_mode: host`) so addresses reach the plugin unchanged.
    Do not also publish Jellyfin's own port with `-p 8096:8096`: Docker opens published ports past host
    firewalls such as ufw, which the Proxy tab cannot see.

### When every visitor looks local

Passing the real addresses through is the fix. When something in front of the proxy cannot, such as a tunnel or
another proxy, **Advanced → Security → Public names follow internet rules** gives requests for your public names
the internet's rules even from a local address: the user list is hidden, setup is blocked, Services set to your
network only are refused, and the internet's upload limit applies. Plain HTTP is still answered rather than
redirected, so a tunnel that speaks HTTP to the proxy does not loop.

Bans, the block list, country rules and sign-in limits still go by the real address. One shared address stands
for every visitor, so banning it would lock everyone out, and Jellyfin itself still treats those visitors as
local. People at home who use your public name get the same rules. The option is off by default.

## Certificates

| Source | When to use it |
|---|---|
| **Let's Encrypt** | The default. Issued and renewed for you. |
| **PEM files** | You already run certbot, acme.sh or similar. Renewals are picked up within a minute. |
| **PKCS#12 file** | A single `.pfx` or `.p12` file. |
| **Self-signed** | No outside service at all. Each device trusts it once you install its root certificate; TVs cannot. |

Let's Encrypt is only contacted once **I accept the Let's Encrypt terms** is ticked. The terms are the [Let's Encrypt Subscriber Agreement](https://letsencrypt.org/repository/).

### Self-signed certificates

Self-signed certificates are made on this server. The plugin creates its own root certificate once, valid for
ten years, and issues the proxy's certificate from it. That certificate renews itself about every 13 months and
whenever your addresses change. A device that trusts the root keeps trusting every renewal, so you install the
root once per device.

1. On the Proxy tab, in the Certificate section, select **Download Root Certificate**. It saves
   `remote-access-root.crt`, which holds no private key.
2. Install it on each device, as below. Before trusting it, check that the device shows the same SHA-256
   fingerprint as the Proxy tab.

| Device | How to install |
|---|---|
| Windows | Open the file, select **Install Certificate**, choose **Place all certificates in the following store**, and pick **Trusted Root Certification Authorities**. Edge and Chrome use this store. |
| macOS | Open the file to add it to Keychain Access, open it there, and set **When using this certificate** to **Always Trust**. |
| iPhone and iPad | Send it to the device and open it, then install it under **Settings → Profile Downloaded**. Then turn it on under **Settings → General → About → Certificate Trust Settings**. |
| Android | Install it under **Settings → Security → Encryption & credentials → Install a certificate → CA certificate**. Browsers trust it, but since Android 7 most apps ignore certificates a person installs, so check the Jellyfin app on your device. |
| Firefox | Firefox may keep its own list: **Settings → Privacy & Security → Certificates → View Certificates → Authorities → Import**. |
| TVs and streaming boxes | Roku, Fire TV, Android TV and most smart TVs cannot install a certificate, so their Jellyfin apps will refuse the connection. Use Let's Encrypt for those. |

The root is replaced only if its file is lost or damaged, or within a year of its end. When that happens the
activity log says so, and every device needs the new root installed. A self-signed certificate from an older
version of the plugin is replaced by one issued from the root on the next start.

### How Let's Encrypt checks your address

Let's Encrypt only issues a certificate once it has checked that the address really points at you. The plugin
picks the check for you:

* **DNS**: used when every address falls under a **Cloudflare** or **Duck DNS** record on the Domains tab. The
  plugin publishes a short-lived TXT record through that provider. Port 80 is not needed for it, and wildcard
  addresses such as `*.example.com` are covered. A Cloudflare API token needs **Edit zone DNS** permission.
* **Port 80**: used otherwise. Let's Encrypt fetches a token from `http://your-address/` on port 80, so port 80
  must be forwarded. Wildcard addresses are left off the certificate.

The Certificate section says which one is in use.

### Renewal

Certificates last 90 days. The **Refresh Certificate** scheduled task runs daily and renews within 30 days of
expiry; saving the Proxy tab checks too. A new certificate is used straight away, without a restart.

If Let's Encrypt refuses, the reason shows on the Proxy tab and in the activity log, and the next try waits an
hour, doubling up to a day, so Let's Encrypt's rate limits are not hit. Changing the address retries at once.
Renewal is measured from when the certificate was issued, so a server clock that runs fast or slow still renews
at the right time.

To replace a certificate now, for example after its key may have been exposed, use **Request a New
Certificate** in the Certificate section, or run the **Request a New Certificate** scheduled task. Neither has a
schedule of its own. Let's Encrypt allows five of the same certificate a week.

With DNS checks, the TXT record is removed once Let's Encrypt has looked at it. If that fails, or Jellyfin stops
in between, it is removed on a later check; one that still cannot be removed after a week is reported in the
activity log.

!!! tip "Testing first"
    Set the environment variable `DDNS_ACME_DIRECTORY` to
    `https://acme-staging-v02.api.letsencrypt.org/directory` to use Let's Encrypt's test server. It issues
    untrusted certificates without counting against the real limits.

## Dashboard

The **Dashboard** tab, also in the sidebar as **Remote Access**, shows who connects:

* **Locations**: a map and country table of users, failed sign-ins and blocked addresses.
* **Banned**: addresses banned for failed sign-ins, with **Unban**.
* **Activity**: every address and what it did: **User**, **Failure**, **Blocked**, or **Seen** for an address
  that reached the sign-in page and did nothing else. Each row can be blocked or unblocked.

Locations come from the free DB-IP database, downloaded to this server; addresses are never sent anywhere. What
is recorded, and for how long, is under **Advanced → History**.

## Protection

These are on by default. Each can be changed under **Advanced**.

* **Your network is never blocked.** Bans, the block list and country rules apply to the internet only.
* **Country rule**: only connections from the country this server is in are allowed. Add countries, or turn it
  off, under **Advanced → Blocking**. Unblocking a country from the Dashboard adds it.
* **Bans**: 5 failed sign-ins within 10 minutes bans the address for 60 minutes. An IPv6 client is banned with
  its whole /64, since it can pick any address in it, and failures spread over many /64s ban the whole /56
  (after 15) or /48 (after 30). Those wider bans last at most 15 minutes, since they also shut out everyone
  else in that network; an attacker who carries on is banned again. An address can have two sign-ins in progress at once, a /56 four and a /48
  eight, and each must arrive within 10 seconds. A ban, a block or a new country rule also cuts off
  connections that are already open, streams and WebSockets included. A ban on a /56 or /48 shows on every
  address in it, and Unban by any of those addresses lifts it.
* **Hidden user list**: sign-in screens on the internet show no user picker.
* **Setup wizard**: never reachable from the internet.
* **Unknown names**: requests for any name you did not add, including your bare IP, are dropped before the
  connection is encrypted.
* **HTTPS only**: browsers are told to use HTTPS for your addresses, and TLS 1.2 or 1.3 is required.
* **Tokens stay out of logs**: API keys in addresses are removed from everything the proxy logs.

### Low-memory servers

On a server with under 3 GB of memory available to Jellyfin, such as a small single-board computer or a
container with a memory limit (.NET counts three quarters of a container's limit, so a 4 GB container
counts as 3 GB), the proxy holds fewer connections open: 500 in total instead of 2000, 32 per
internet address instead of 64, and idle connections close after 60 seconds instead of 130. This only changes
settings you have not set yourself, and switches back on its own if the server gets more memory. **Advanced**
shows both defaults and which one applies.

## Services

The **Services** tab puts other apps on your network, such as a request manager, behind the same proxy on their
own address. Only add apps that have their own sign-in. The certificate has to cover each app's address; with
Let's Encrypt it is added automatically. Services set to your network only, and names that only your own DNS
knows, such as `nas.lan`, are left off a Let's Encrypt certificate, since it can never check them.

## Recovery

* **Undo** on the Proxy tab puts Jellyfin's network settings back as they were before **Trust the Proxy**.
* If you cannot reach the dashboard, create an empty file named `DISABLE` in
  `plugins/configurations/Jellyfin.Plugin.DynamicDns.Proxy/` under Jellyfin's data folder and restart Jellyfin.
  The proxy stays off and Jellyfin's network settings go back as they were. Delete the file to turn the proxy
  on again. Jellyfin itself stays reachable on its own ports throughout.
* If the Proxy tab says the plugin cannot use its own files, Jellyfin is running as a different account than the
  one that made them. Run the command it shows, then restart Jellyfin.
* Uninstalling the plugin restores Jellyfin's network settings first. Disabling the plugin from Jellyfin's
  plugin list does not: Jellyfin keeps trusting the proxy until you use **Undo** or uninstall.
