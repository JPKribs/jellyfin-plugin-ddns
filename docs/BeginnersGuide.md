# Beginner's Guide: Recommended

This page takes you from nothing to watching Jellyfin from your phone at `https://media.example.com`, in one pass:
buying a domain, installing the plugin, keeping the domain pointed at your home, and turning on secure remote
access. It uses Cloudflare for the domain, because the plugin can then also prove the domain to Let's Encrypt
with no extra setup. Plan on about 30 minutes, and the price of a domain, usually around $10 a year.

!!! tip "Rather not pay?"
    The [free guide](BeginnersGuideFree.md) uses a free Duck DNS name, such as `mymedia.duckdns.org`, instead of a
    domain of your own.

Wherever you see `example.com`, use your own domain. `media` is the name this guide gives the server. Any name
works.

## What you need

* Jellyfin 12.2 running at home, and an administrator account on it.
* Access to your home router's settings, to forward two ports.
* A public IP from your internet provider. Step 0 shows how to check.
* **On Linux**, permission for Jellyfin to use ports 80 and 443. Run `sudo systemctl edit jellyfin`, add
  `AmbientCapabilities=CAP_NET_BIND_SERVICE` under `[Service]`, and restart Jellyfin. Windows and macOS need
  nothing.
* **In Docker**, host networking, `network_mode: host`. See
  [Docker, tunnels and other proxies](RemoteAccess.md#docker-tunnels-and-other-proxies).

## 0. Check you are not behind CGNAT

Some internet providers share one public address between many homes, called carrier-grade NAT or CGNAT. Behind it,
nothing from the internet can reach your home, so **this guide will not work unless you ask your provider for a
public or static IP**. Many give one for free or a small monthly fee. Check before you buy anything:

1. Open your router's status page and find its internet or WAN address.
2. On a device at home, open [ifconfig.me](https://ifconfig.me) to see the address the internet sees.

If the two match, you are fine. If they differ, or the router's address starts with `100.64` to `100.127`, you
are behind CGNAT. Ask your provider for a public IP before going further.

## 1. Buy a domain

1. Sign up at [dash.cloudflare.com](https://dash.cloudflare.com), or sign in.
2. Open **Domain Registration → Register Domains**, search for a name, and buy it.

A domain bought from Cloudflare uses Cloudflare's DNS straight away. If you already own a domain elsewhere, add it
to Cloudflare with **Add a domain** and change its nameservers at your registrar as Cloudflare shows. Then carry
on from step 2.

## 2. Create the DNS record

The plugin updates a record that exists. It does not create one, so make it now.

1. In Cloudflare, open your domain, then **DNS → Records**, and select **Add record**.
2. Fill it in:
    * **Type**: `A`
    * **Name**: `media`
    * **IPv4 address**: `192.0.2.1`. This is a placeholder, and the plugin replaces it with your real address.
    * **Proxy status**: turn it **off**, so it shows **DNS only** with a grey cloud.
    * **TTL**: **Auto**
3. Select **Save**.

!!! warning "Keep the cloud grey"
    With the orange cloud on, Cloudflare stands between your visitors and your server, so every visitor reaches
    Jellyfin from a Cloudflare address. The plugin can then no longer ban or block the right people. Keep the
    record **DNS only**.

If your internet provider gives you IPv6, you can also add an `AAAA` record for `media` with the placeholder
`2001:db8::1`. Skip it if you are not sure. The plugin only updates the record types you turn on in step 5.

## 3. Create an API token

The token lets the plugin change DNS records in this domain, and nothing else.

1. In Cloudflare, open **My Profile → API Tokens** and select **Create Token**.
2. Next to **Edit zone DNS**, select **Use template**.
3. Under **Zone Resources**, choose **Include → Specific zone → example.com**.
4. Select **Continue to summary**, then **Create Token**.
5. Copy the token. Cloudflare only shows it once.

## 4. Install the plugin

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and select **Add Repository**.
2. Enter this URL and select **Save**:

    ```
    https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-remoteaccess/master/manifest.json
    ```

3. Open the **Catalog** tab, find **Dynamic DNS**, and select **Install**. The catalog still lists the plugin
   under its original name.
4. Restart Jellyfin when it asks, and wait until it is fully back up.

The plugin now has a **Remote Access** entry in the dashboard's side menu.

## 5. Add the record to the plugin

1. Open the **Remote Access** entry in the dashboard's side menu, then the **Domains** tab, and select **New**.
2. Fill it in:
    * **Name**: anything, such as `Home`
    * **Provider**: **Cloudflare**
    * **Record name**: `media.example.com`, the full name
    * **Update IPv4**: on
    * **Update IPv6**: it starts on. Turn it off unless you made the `AAAA` record in step 2
    * **Auth email or token**: the word `token`
    * **API token or key**: the token from step 3
    * **Zone name**: `example.com`
3. Select **Save Record**.
4. On the **Address** tab, select **Update Now**.

Back on the **Domains** tab, the record's status card should say **Updated**, and Cloudflare's record should now
show your real address instead of `192.0.2.1`. From now on the plugin checks every 15 minutes and updates the
record whenever your address changes.

## 6. Turn on Remote Access

Check the Linux or Docker item in [What you need](#what-you-need) first. Without it the proxy cannot use ports 80
and 443, and no certificate arrives.

On the **Proxy** tab:

1. Under **Remote Access**, tick **Enabled**.
2. Under **Address**, tick `media.example.com`. It is listed because of the record from step 5.
3. Under **Certificate**, leave **Source** on **Let's Encrypt** and tick **I accept the Let's Encrypt terms**.
4. Select **Save**.

Within a minute or two the certificate status shows a certificate for `media.example.com`. Because the address is
a Cloudflare record, the plugin proves it to Let's Encrypt through DNS, so this works even before your router is
set up.

## 7. Forward ports on your router

The **Router** section of the Proxy tab shows the address to forward to, such as `192.168.1.10`. In your router's
settings, usually under **Port forwarding**, **NAT** or **Virtual servers**, add two rules:

| External port | Internal address | Internal port | Protocol |
|---|---|---|---|
| 443 | the address from the Router section | 443 | TCP |
| 80 | the address from the Router section | 80 | TCP |

Port 443 carries everything. Port 80 only sends `http://` links on to HTTPS. Do not forward Jellyfin's own ports,
8096 or 8920.

## 8. Trust the Proxy

Until this step is done, the proxy turns every internet visitor away. On the **Proxy** tab, under **Jellyfin**,
select **Trust the Proxy** and confirm. Jellyfin restarts once. After that it sees each visitor's real address, so
its own remote-access rules and bitrate limits work, and the proxy's bans and country rules apply.

## 9. Test it

On your phone, turn Wi-Fi off and open `https://media.example.com`. You should see Jellyfin's sign-in page with a
padlock in the address bar. Then add the same address as a server in the Jellyfin apps you use away from home.

At home, keep using Jellyfin's local address in your apps. Many routers cannot open your own public address from
inside your home.

## After setup

* **Travelling abroad**: by default only visitors from your own country can connect. Before you go, add the
  countries you will visit in the **Countries** picker under **Advanced → Blocking**.
* **Who is connecting**: the **Dashboard** tab shows visitors, failed sign-ins and bans.
* **Other apps**: the plugin serves Jellyfin only. See [Other apps](RemoteAccess.md#other-apps) for reaching
  other apps from outside.

## Troubleshooting

| What you see | What to do |
|---|---|
| The record's status says `no 'A' record at Cloudflare` | The record from step 2 is missing, or its name differs from **Record name**. Create it, or fix the name. |
| `no 'AAAA' record at Cloudflare` | Turn off **Update IPv6**, or add an `AAAA` record as in step 2. |
| `zone lookup failed`, or another authentication or permission error | The token is wrong. Make a new one as in step 3, and check **Auth email or token** is `token`. |
| `no zone ID found for zone ...` | **Zone name** must be the domain alone, such as `example.com`, not `media.example.com`. It can also mean the token is limited to a different zone. |
| The Address tab warns that the address is internal | The server has no public IP of its own. See step 0. |
| The Proxy tab shows no certificate | Its Certificate section shows Let's Encrypt's reason. Jellyfin's activity log has it too. |
| The phone cannot connect | Check the forwarding rules in step 7, and step 0. Some providers block ports 80 and 443 on home lines. Ask them. |
| Works away from home, not at home | Normal for many routers. Use Jellyfin's local address at home. |
| The sign-in page loads, but everyone shows as local on the Dashboard | Something in front of the proxy hides visitors' addresses. See [When every visitor looks local](RemoteAccess.md#when-every-visitor-looks-local). |
| Locked out after a setting change | See [Recovery](RemoteAccess.md#recovery). |
