# Beginner's Guide

From nothing to Jellyfin on your phone at a secure `https://` address. There are two paths:

| Free | Recommended |
|---|---|
| A [Duck DNS](https://www.duckdns.org) name such as `mymedia.duckdns.org`. It costs nothing and takes about 20 minutes. The name always ends in `.duckdns.org` and depends on a free service. | A domain you own, such as `media.example.com`, with its DNS at Cloudflare. It costs about $10 a year and takes about 30 minutes. |

Both paths get a trusted Let's Encrypt certificate with no extra setup, and only step 2 differs between them.
Wherever you see `mymedia` or `example.com`, use your own name.

## What you need

* Jellyfin 12.2 at home, and an administrator account on it.
* Access to your router's settings, in case it does not forward ports on its own.
* A public IP from your internet provider. Step 0 checks it.

??? note "On Linux or in Docker"
    * **Linux**: Jellyfin needs permission to use ports 80 and 443. Run `sudo systemctl edit jellyfin`, add
      `AmbientCapabilities=CAP_NET_BIND_SERVICE` under `[Service]`, and restart Jellyfin.
    * **Docker**: use host networking, `network_mode: host`, so the proxy sees each visitor's real address. See
      [Docker, tunnels and other proxies](RemoteAccess.md#docker-tunnels-and-other-proxies).

    Windows and macOS need nothing.

## 0. Check for CGNAT

Some providers share one public address between many homes, called CGNAT. Nothing from the internet can reach you
behind it, so **check before you buy anything**:

1. Find the internet or WAN address on your router's status page.
2. Open [ifconfig.me](https://ifconfig.me) on a device at home.

If they match, you are fine. If they differ, or the router's address starts with `100.64` to `100.127`, ask your
provider for a public IP first. Later, Configuration step 4 checks this for you.

## 1. Install the plugin

1. In Jellyfin, open **Dashboard > Plugins > Repositories** and add this repository:

    ```
    https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-remoteaccess/master/manifest.json
    ```

2. In **Catalog**, install **Dynamic DNS**. The catalog still uses the plugin's original name.
3. Restart Jellyfin when it asks.

A **Remote Access** entry now sits in the dashboard's side menu.

## 2. Get a name and add it

Pick your path. Everything that differs between them is here.

=== "Free"

    1. Open [duckdns.org](https://www.duckdns.org) and sign in.
    2. Under **domains**, type a name such as `mymedia` and select **add domain**.
    3. Copy the **token** at the top of the page. Keep it private, it works like a password.
    4. In Jellyfin, open **Remote Access > Configuration**, open step 2, **Address**, and fill in:
        * **Provider**: **Duck DNS**
        * **Labels**: `mymedia`, without `.duckdns.org`
        * **Token**: the token from step 3

=== "Recommended"

    1. At [dash.cloudflare.com](https://dash.cloudflare.com), open **Domain Registration > Register Domains** and
       buy a name. A domain you already own elsewhere can be added with **Add a domain** instead.
    2. Open the domain, then **DNS > Records > Add record**:
        * **Type** `A`, **Name** `media`, **IPv4 address** `192.0.2.1`. The plugin replaces this placeholder.
        * **Proxy status** off, so it shows **DNS only** with a grey cloud.
    3. Open **My Profile > API Tokens > Create Token**, use the **Edit zone DNS** template, set **Zone Resources**
       to your domain, and create it. Copy the token, Cloudflare shows it once.
    4. In Jellyfin, open **Remote Access > Configuration**, open step 2, **Address**, and fill in:
        * **Provider**: **Cloudflare**
        * **Record name**: `media.example.com`
        * **Auth email or token**: the word `token`
        * **API token or key**: the token from step 3
        * **Zone name**: `example.com`

    !!! warning "Keep the cloud grey"
        With the orange cloud on, every visitor reaches Jellyfin from a Cloudflare address, and the plugin can no
        longer ban or block the right people.

Select **Save**, then **Update Now**. The status card's **Result** should say the record was updated. From now on
the plugin checks every 15 minutes.

If your home has no IPv6, turn off **Update IPv6** under **Advanced > Dynamic DNS** and save.

## 3. Turn on Remote Access

1. Step 1, **Proxy on**: tick **Enabled**.
2. Step 3, **Certificate**: keep **Let's Encrypt** and tick **I accept the Let's Encrypt terms**.
3. Select **Save**.

Within a minute or two step 3 shows a certificate. It works before your router is set up, since the name is
proved through your DNS provider.

## 4. Router

Step 4, **Router**, opens ports 80 and 443 on your router by itself.

* **Forwarded**: step 4 names the device that answered. Check the two rules are on your router, as in
  [Checking the forward](RemoteAccess.md#checking-the-forward).
* **Shared address**: your provider shares your IP. See step 0.
* **Forward by hand**: turn on UPnP or NAT-PMP in the router and select **Try Again**, or add the two rules step 4
  shows, TCP 443 and TCP 80 to this server. Never forward Jellyfin's own ports, 8096 or 8920.

## 5. Trust the Proxy

In step 5, select **Trust the Proxy** and confirm. Jellyfin restarts once. Until then the proxy turns internet
visitors away, since Jellyfin cannot tell them apart from people at home.

## 6. Test it

In step 6, select **Test from Outside**. It shows **Reachable** once a place outside your network gets through.
Then turn off Wi-Fi on your phone and open your address. You should see Jellyfin's sign-in page with a padlock.

Add the address as a server in your Jellyfin apps for use away from home. At home, keep the local address, since
many routers cannot open your own public address from inside.

## After setup

* **Travelling**: only your own country can connect by default. Add countries under **Advanced > Blocking** before
  a trip.
* **Who connects**: see [Dashboard & Monitoring](Dashboard.md).
* **Other apps**: the plugin serves Jellyfin only. See [Other apps](RemoteAccess.md#other-apps).

## Troubleshooting

=== "Free"

    | What you see | What to do |
    |---|---|
    | Result says `server said: KO` | Check **Labels** has no `.duckdns.org`, and paste the token again. |

=== "Recommended"

    | What you see | What to do |
    |---|---|
    | `no 'A' record at Cloudflare` | Create the record from step 1, or match its name to **Record name**. |
    | `no 'AAAA' record at Cloudflare` | Turn off **Update IPv6** under **Advanced > Dynamic DNS**. |
    | `zone lookup failed`, or a permission error | Make a new token, and check **Auth email or token** is `token`. |
    | `no zone ID found for zone ...` | **Zone name** is the domain alone, such as `example.com`. |

| What you see | What to do |
|---|---|
| Configuration step 2 says the address is internal | The server has no public IP. See step 0. |
| Configuration step 2 shows **Not pointed here** | Select **Update Now** and wait a few minutes. |
| Configuration step 3 shows no certificate | Step 3 and Jellyfin's activity log show Let's Encrypt's reason. |
| Step 4 names a device that is not your router | See [Checking the forward](RemoteAccess.md#checking-the-forward). |
| **Test from Outside** shows **Not reachable** | Check step 4 and step 0. Some providers block ports 80 and 443, so ask them. |
| Works away from home, not at home | Normal for many routers. Use the local address at home. |
| Everyone shows as local on the Dashboard | See [When every visitor looks local](RemoteAccess.md#when-every-visitor-looks-local). |
| Locked out after a change | See [Recovery](RemoteAccess.md#recovery). |
