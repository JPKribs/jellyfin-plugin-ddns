# Beginner's Guide: Free

This page takes you from nothing to watching Jellyfin from your phone at `https://mymedia.duckdns.org`, in one
pass and at no cost. It uses [Duck DNS](https://www.duckdns.org) for a free name, and the plugin proves that name
to Let's Encrypt through Duck DNS, so you get a trusted certificate too. Plan on about 20 minutes.

Wherever you see `mymedia`, use the name you pick in step 1.

!!! tip "Free or your own domain"
    A Duck DNS name is free, but it always ends in `.duckdns.org` and depends on a free service. For a name of your
    own, such as `media.example.com`, follow the [Recommended guide](BeginnersGuide.md) instead. It costs about $10
    a year. Duck DNS is used here rather than No-IP, because No-IP's free names must be confirmed every month and the
    plugin cannot prove a No-IP name to Let's Encrypt without port 80.

## What you need

* Jellyfin 12.2 running at home, and an administrator account on it.
* Access to your home router's settings, to forward two ports.
* A public IP from your internet provider. Step 0 shows how to check.
* **On Linux**, permission for Jellyfin to use ports 80 and 443. Run `sudo systemctl edit jellyfin`, add
  `AmbientCapabilities=CAP_NET_BIND_SERVICE` under `[Service]`, and restart Jellyfin.
* **In Docker**, host networking, `network_mode: host`, so the proxy sees each visitor's real address.

## 0. Check you are not behind CGNAT

Some internet providers share one public address between many homes, called carrier-grade NAT or CGNAT. Behind it,
nothing from the internet can reach your home, so **this guide will not work unless you ask your provider for a
public or static IP**. Check first:

1. Open your router's status page and find its internet or WAN address.
2. On a device at home, open [ifconfig.me](https://ifconfig.me) to see the address the internet sees.

If the two match, you are fine. If they differ, or the router's address starts with `100.64` to `100.127`, you
are behind CGNAT. Ask your provider for a public IP before going further.

## 1. Get a Duck DNS name

1. Open [duckdns.org](https://www.duckdns.org) and sign in with one of the accounts it offers.
2. Under **domains**, type a name such as `mymedia` and select **add domain**. Your address is now
   `mymedia.duckdns.org`.
3. Copy the **token** shown at the top of the page. It works like a password for your names, so keep it private.

## 2. Install the plugin

1. In Jellyfin, open **Dashboard → Plugins → Repositories** and select **Add Repository**.
2. Enter this URL and select **Save**:

    ```
    https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-remoteaccess/master/manifest.json
    ```

3. Open the **Catalog** tab, find **Dynamic DNS**, and select **Install**. The catalog still lists the plugin
   under its original name.
4. Restart Jellyfin when it asks, and wait until it is fully back up.

The plugin is now the **Remote Access** entry in the dashboard's side menu.

## 3. Add the name to the plugin

1. Open **Remote Access**, then the **Domains** tab, and select **New**.
2. Fill it in:
    * **Name**: anything, such as `Home`
    * **Provider**: **Duck DNS**
    * **Labels**: `mymedia`, the name alone, without `.duckdns.org`. Use one name per record.
    * **Update IPv4**: on
    * **Update IPv6**: turn it off, unless you know your home has IPv6
    * **Token**: the token from step 1
3. Select **Save Record**.
4. On the **Address** tab, select **Update Now**.

Back on the **Domains** tab, the record's status card should say **Updated**. From now on the plugin checks every
15 minutes and updates Duck DNS whenever your address changes.

## 4. Turn on Remote Access

On the **Proxy** tab:

1. Under **Remote Access**, tick **Enabled**.
2. Under **Address**, tick `mymedia.duckdns.org`. It is listed because of the record from step 3.
3. Under **Certificate**, leave **Source** on **Let's Encrypt** and tick **I accept the Let's Encrypt terms**.
4. Select **Save**.

Within a minute or two the certificate status shows a certificate for `mymedia.duckdns.org`. The plugin proves the
name to Let's Encrypt through Duck DNS, so this works even before your router is set up.

## 5. Forward ports on your router

The **Router** section of the Proxy tab shows the address to forward to, such as `192.168.1.10`. In your router's
settings, usually under **Port forwarding**, **NAT** or **Virtual servers**, add two rules:

| External port | Internal address | Internal port | Protocol |
|---|---|---|---|
| 443 | the address from the Router section | 443 | TCP |
| 80 | the address from the Router section | 80 | TCP |

Do not forward Jellyfin's own ports, 8096 or 8920.

## 6. Trust the Proxy

On the **Proxy** tab, under **Jellyfin**, select **Trust the Proxy** and confirm. Jellyfin restarts once. Until you
do this, the proxy turns internet visitors away, since Jellyfin could not yet tell them apart from people at home.

## 7. Test it

On your phone, turn Wi-Fi off and open `https://mymedia.duckdns.org`. You should see Jellyfin's sign-in page with
a padlock in the address bar. Then add the same address as a server in the Jellyfin apps you use away from home.
At home, keep using Jellyfin's local address in your apps.

By default only visitors from your own country can connect. Before travelling, add the countries you will visit
under **Advanced → Blocking → Countries**.

## Troubleshooting

| What you see | What to do |
|---|---|
| The record's status says `server said: KO` | Duck DNS refused the name or token. Check **Labels** is the name alone, without `.duckdns.org`, and paste the token again. |
| The Address tab warns that the address is internal | The server has no public IP of its own. See step 0. |
| The Proxy tab shows no certificate | Its Certificate section shows Let's Encrypt's reason. Jellyfin's activity log has it too. |
| The phone cannot connect | Check the rules in step 5, step 6, and step 0. Some providers block ports 80 and 443 on home lines. |
| Works away from home, not at home | Normal for many routers. Use Jellyfin's local address at home. |
| Locked out after a setting change | See [Recovery](RemoteAccess.md#recovery). |
