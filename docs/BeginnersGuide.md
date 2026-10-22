# Beginner's Guide

This page takes you from nothing to watching Jellyfin from your phone at a secure `https://` address, in one pass.
It has two paths. They share most steps and only differ where you get your name.

| | Free | Recommended |
|---|---|---|
| Your address | `mymedia.duckdns.org` | `media.example.com` |
| Cost | Nothing | A domain, usually around $10 a year |
| Time | About 20 minutes | About 30 minutes |
| The name | Always ends in `.duckdns.org` and depends on a free service | A name you own, with DNS at Cloudflare |

Both paths get a trusted Let's Encrypt certificate with no extra setup, because the plugin can prove a
[Duck DNS](https://www.duckdns.org) or Cloudflare name to Let's Encrypt through DNS. The free path uses Duck DNS
rather than No-IP, because No-IP's free names must be confirmed every month and the plugin cannot prove a No-IP name
to Let's Encrypt without port 80.

Wherever you see `mymedia` or `example.com`, use your own name. Sections marked **Free** or **Recommended** only
apply to that path.

## What you need

* Jellyfin 12.2 running at home, and an administrator account on it.
* Access to your home router's settings, in case it does not forward two ports on its own.
* A public IP from your internet provider. Step 0 shows how to check.
* **On Linux**, permission for Jellyfin to use ports 80 and 443. Run `sudo systemctl edit jellyfin`, add
  `AmbientCapabilities=CAP_NET_BIND_SERVICE` under `[Service]`, and restart Jellyfin. Windows and macOS need
  nothing.
* **In Docker**, host networking, `network_mode: host`, so the proxy sees each visitor's real address. See
  [Docker, tunnels and other proxies](RemoteAccess.md#docker-tunnels-and-other-proxies).

## 0. Check you are not behind CGNAT

Some internet providers share one public address between many homes, called carrier-grade NAT or CGNAT. Behind it,
nothing from the internet can reach your home, so **this guide will not work unless you ask your provider for a
public or static IP**. Many give one for free or a small monthly fee. Check before you buy anything:

1. Open your router's status page and find its internet or WAN address.
2. On a device at home, open [ifconfig.me](https://ifconfig.me) to see the address the internet sees.

If the two match, you are fine. If they differ, or the router's address starts with `100.64` to `100.127`, you
are behind CGNAT. Ask your provider for a public IP before going further.

Once the plugin is set up, Configuration step 4, **Router**, checks this too and shows **Shared address** when
your provider shares your public IP.

## 1. Get a name

### Free: a Duck DNS name

1. Open [duckdns.org](https://www.duckdns.org) and sign in with one of the accounts it offers.
2. Under **domains**, type a name such as `mymedia` and select **add domain**. Your address is now
   `mymedia.duckdns.org`.
3. Copy the **token** shown at the top of the page. It works like a password for your names, so keep it private.

### Recommended: a domain with Cloudflare

**Buy the domain.** Sign up or sign in at [dash.cloudflare.com](https://dash.cloudflare.com), open
**Domain Registration → Register Domains**, search for a name, and buy it. A domain bought from Cloudflare uses
Cloudflare's DNS straight away. If you already own a domain elsewhere, add it to Cloudflare with **Add a domain**
and change its nameservers at your registrar as Cloudflare shows.

**Create the DNS record.** The plugin updates a record that exists. It does not create one.

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
    Jellyfin from a Cloudflare address. The plugin can then no longer ban or block the right people.

If your internet provider gives you IPv6, you can also add an `AAAA` record for `media` with the placeholder
`2001:db8::1`. Skip it if you are not sure.

**Create an API token.** It lets the plugin change DNS records in this domain, and nothing else.

1. Open **My Profile → API Tokens** and select **Create Token**.
2. Next to **Edit zone DNS**, select **Use template**.
3. Under **Zone Resources**, choose **Include → Specific zone → example.com**.
4. Select **Continue to summary**, then **Create Token**.
5. Copy the token. Cloudflare only shows it once.

## 2. Install the plugin

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and select **Add Repository**.
2. Enter this URL and select **Save**:

    ```
    https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-remoteaccess/master/manifest.json
    ```

3. Open the **Catalog** tab, find **Dynamic DNS**, and select **Install**. The catalog still lists the plugin
   under its original name.
4. Restart Jellyfin when it asks, and wait until it is fully back up.

The plugin now has a **Remote Access** entry in the dashboard's side menu.

## 3. Add the name to the plugin

Open **Remote Access**, then the **Configuration** tab, and open step 2, **Address**. Fill it in for your path.

### Free

* **Provider**: **Duck DNS**
* **Labels**: `mymedia`, the name alone, without `.duckdns.org`. Use one name.
* **Token**: the token from step 1

### Recommended

* **Provider**: **Cloudflare**
* **Record name**: `media.example.com`, the full name
* **Auth email or token**: the word `token`
* **API token or key**: the token from step 1
* **Zone name**: `example.com`

### Then, for both

1. Select **Save** at the bottom of the page.
2. Select **Update Now** under the record's status card. Its **Result** should say the record was updated. On the
   Recommended path, Cloudflare's record now shows your real address instead of `192.0.2.1`.
3. **Update IPv6** starts on. Unless your home has IPv6, and on the Recommended path an `AAAA` record, turn it off
   under **Advanced → Dynamic DNS**, in the **Record** group, and save.

From now on the plugin checks every 15 minutes and updates the record whenever your address changes.

## 4. Turn on Remote Access

Check the Linux or Docker item in [What you need](#what-you-need) first. Without it the proxy cannot use ports 80
and 443, and no certificate arrives. Then, on the **Configuration** tab:

1. In step 1, **Proxy on**, tick **Enabled**.
2. Step 2, **Address**, already uses your name from step 3 as the proxy's address. Nothing to do here.
3. In step 3, **Certificate**, leave **Source** on **Let's Encrypt** and tick **I accept the Let's Encrypt terms**.
4. Select **Save** at the bottom of the page.

Step 2 then shows **Ready** once your name points to this server. Within a minute or two step 3 shows a
certificate for it. The plugin proves the name to Let's Encrypt through Duck DNS or Cloudflare, so this works even
before your router is set up.

## 5. Forward ports on your router

Look at Configuration step 4, **Router**. **Automatic port forwarding** is on by default, so the plugin has already
asked your router to forward ports 80 and 443 through UPnP or NAT-PMP.

* **Forwarded**: the router did it. Go on to step 6.
* **Shared address**: your provider shares your public IP. See step 0.
* **Forward by hand**: the router did not answer, or has UPnP and NAT-PMP turned off. Either turn one on in the
  router's settings and select **Try Again**, or add the rules yourself as below.

Step 4 shows the address to forward to, such as `192.168.1.10`. In your router's settings, usually under
**Port forwarding**, **NAT** or **Virtual servers**, add two rules:

| External port | Internal address | Internal port | Protocol |
|---|---|---|---|
| 443 | the address from step 4 | 443 | TCP |
| 80 | the address from step 4 | 80 | TCP |

Port 443 carries everything. Port 80 only sends `http://` links on to HTTPS. Do not forward Jellyfin's own ports,
8096 or 8920.

## 6. Trust the Proxy

Until this is done, the proxy turns every internet visitor away, since Jellyfin cannot yet tell them apart from
people at home. In Configuration step 5, **Trust the Proxy**,
select **Trust the Proxy** and confirm. Jellyfin restarts once. After that it sees each visitor's real address, so
its own remote-access rules and bitrate limits work, and the proxy's bans and country rules apply.

## 7. Test it

In Configuration step 6, **Reachable from outside**, select **Test from Outside**. check-host.net connects to your
address from up to three places in the world, and the step shows **Reachable** when one gets through. Only the
hostname and port are sent. See [Test from Outside](RemoteAccess.md#test-from-outside).

Then, on your phone, turn Wi-Fi off and open your address, such as `https://mymedia.duckdns.org` or
`https://media.example.com`. You should see Jellyfin's sign-in page with a padlock in the address bar. Add the same
address as a server in the Jellyfin apps you use away from home.

At home, keep using Jellyfin's local address in your apps. Many routers cannot open your own public address from
inside your home.

## After setup

* **Travelling abroad**: by default only visitors from your own country can connect. Before you go, add the
  countries you will visit in the **Countries** picker under **Advanced → Blocking**.
* **Who is connecting**: the **Dashboard** tab shows visitors, failed sign-ins and bans. See
  [Dashboard & Monitoring](Dashboard.md).
* **Other apps**: the plugin serves Jellyfin only. See [Other apps](RemoteAccess.md#other-apps).

## Troubleshooting

| What you see | What to do |
|---|---|
| **Free**: the record's result says `server said: KO` | Duck DNS refused the name or token. Check **Labels** is the name alone, without `.duckdns.org`, and paste the token again. |
| **Recommended**: `no 'A' record at Cloudflare` | The record from step 1 is missing, or its name differs from **Record name**. Create it, or fix the name. |
| **Recommended**: `no 'AAAA' record at Cloudflare` | Turn off **Update IPv6** under **Advanced → Dynamic DNS**, or add an `AAAA` record as in step 1. |
| **Recommended**: `zone lookup failed`, or another authentication or permission error | The token is wrong. Make a new one as in step 1, and check **Auth email or token** is `token`. |
| **Recommended**: `no zone ID found for zone ...` | **Zone name** must be the domain alone, such as `example.com`, not `media.example.com`. It can also mean the token is limited to a different zone. |
| Configuration step 2 warns that the address is internal | The server has no public IP of its own. See step 0. |
| Configuration step 2 shows **Not pointed here** | The record has not updated yet, or its name differs. Select **Update Now** and wait a few minutes. |
| Configuration step 3 shows no certificate | It shows Let's Encrypt's reason. Jellyfin's activity log has it too. |
| Configuration step 4 shows **Shared address** | Your provider shares your public IP. See step 0. |
| **Test from Outside** shows **Not reachable**, or the phone cannot connect | Check the forwarding in step 5, and step 0. Some providers block ports 80 and 443 on home lines. Ask them. |
| Works away from home, not at home | Normal for many routers. Use Jellyfin's local address at home. |
| The sign-in page loads, but everyone shows as local on the Dashboard | Something in front of the proxy hides visitors' addresses. See [When every visitor looks local](RemoteAccess.md#when-every-visitor-looks-local). |
| Locked out after a setting change | See [Recovery](RemoteAccess.md#recovery). |
