# The Plugin's Pages

Remote Access adds one entry, **Remote Access**, to the dashboard's side menu. Its pages share a
tab bar along the top: **Dashboard**, **Configuration** and **Advanced**. Every setting, with its default, is
listed in the [Settings Reference](Settings.md).

## Dashboard

Who reaches the server, from where, and what was turned away.

* **Share card**: your server's address with a QR code. **Copy URL**, **Copy Image** and **Share** send it to family.
* **Locations**: a world map and a table by country. **Users**, **Failures** and **Blocked** pick what the map
  shows, one or more at once. All three are on when the page opens.
* **Banned**: addresses banned for failed sign-ins, with **Unban** on each.
* **Activity**: every recorded visit, sign-in, failure and refusal, filtered by type and country. A row can be
  blocked or unblocked from here.

See [Dashboard](Dashboard.md) for every part of it.

![Dashboard tab](screenshots/Dashboard.png)

## Configuration

Everything needed to reach Jellyfin from outside, as six numbered steps. Each step shows its state on the right,
such as **Ready** or **Forward by hand**, and opens to show its settings. Steps that are not done yet open on their
own. The summary at the top counts the steps that are done, with a bar for each step in its colour. It explains
the first step that needs attention, with **Open step** to jump to it, or says **Ready to share** once all six are done.
Under it, a note names the countries that can connect, with a link to change them in **Advanced → Blocking**.

![Configuration tab](screenshots/Configuration.png)

1. **Proxy on**: tick **Enabled** to start the proxy.
2. **Address**: the one DDNS record, with only **Provider**, **Hostname** and the credentials that provider asks
   for. Its hostname is also the proxy's address. A small card shows the public IP, the last update and its
   result, with **Update Now** to send it straight away. The step checks that the name points at this server's
   public IP.
3. **Certificate**: Let's Encrypt, your own files, or a self-signed certificate, with its status.
4. **Router**: the ports to forward by hand when the router does not, **Try Again**, and a warning when your
   provider shares your public address.
5. **Trust the Proxy**: lets Jellyfin see each visitor's real address. **Undo** puts it back.
6. **Reachable from outside**: **Test from Outside** checks that the internet can reach your address. The
   **Outside check** card lists each place it was tried from as **Reached** or **Failed**.

**Save** at the bottom keeps every step: the record in step 2 and the proxy settings. A server that kept several
records from an older version keeps only the one shown, the first enabled record, once you save. The page warns
how many will be removed.

See [The DDNS Record](Records.md) for step 2 and [Remote Access](RemoteAccess.md) for the rest.

![Step 2, Address](screenshots/Address%20Step.png)

## Advanced

Every setting not on the other tabs, each starting on its default.

* **Dynamic DNS**: IPv4 Detection URLs, IPv6 Detection URLs, Skip update if internal, Force update, Back off
  after failures, Back off duration, and Request timeout. See [How Updates Run](HowItWorks.md). Under **Record**
  are the record's finer settings: Enabled, Update IPv4, Update IPv6, Server override, TTL, Treat as proxied
  address, the DynDNS family options, and **Remove Record**.
* **Addresses**: **Other addresses**, more names the proxy answers on besides the record's name, and **Automatic
  port forwarding**.
* **Security**: Only through the proxy, Always use HTTPS, HTTPS memory, Block embedding, Trusted networks, and
  **Request a New Certificate**.
* **Blocking**: the block list, bans for failed sign-ins, and country rules.
* **History**: whether visits are recorded, for how long, and how precise locations are.
* **Performance**: HTTP/2, upload sizes and connection limits.
* **Timeouts**: idle connections, request header limits, the minimum read rate and how long Jellyfin may take to
  answer.

![Advanced tab](screenshots/Advanced.png)
