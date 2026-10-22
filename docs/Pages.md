# The Plugin's Pages

Remote Access adds one entry, **Remote Access**, to the dashboard's side menu. Its pages share a
tab bar along the top: **Dashboard**, **Setup**, **Domains**, **Address**, **Proxy** and **Advanced**. Every
setting, with its default, is listed in the [Settings Reference](Settings.md).

## Dashboard

Who reaches the server, from where, and what was turned away.

* **Locations**: a world map and a table by country. **Users**, **Failures** and **Blocked** pick what the map
  shows, one or more at once. All three are on when the page opens.
* **Banned**: addresses banned for failed sign-ins, with **Unban** on each.
* **Activity**: every recorded visit, sign-in, failure and refusal, filtered by type and country. A row can be
  blocked or unblocked from here.

See [Dashboard](Dashboard.md) for every part of it.

![Dashboard tab](screenshots/Dashboard.png)

## Domains

The Dynamic DNS records that keep your hostnames pointed at this server. The cards at the top count healthy,
unhealthy and unpublished records. Pick a record to edit it, or **New** to add one. The status card at the bottom
shows its last run. See [Adding Records](Records.md) and [Providers](providers/README.md).

![Domains tab](screenshots/Domains.png)

## Address

The public IP the server was last seen at, with **Update Now** to run every record's update straight away. Below it
are how the address is found and how records are updated: the detection URLs for IPv4 and IPv6, **Skip update if
internal**, the forced update, the backoff after repeated failures, and the request timeout. See
[How Updates Run](HowItWorks.md).

![Address tab](screenshots/Address.png)

## Proxy

Remote access itself. The cards at the top show what is left to do, and each section below is one step:

* **Remote Access**: turns the proxy on.
* **Address**: the names people use to reach Jellyfin. Names from your Domains records are offered here.
* **Certificate**: Let's Encrypt, your own files, or a self-signed certificate, with its status.
* **Router**: the ports to forward and where to.
* **Jellyfin**: **Trust the Proxy**, which lets Jellyfin see each visitor's real address, and **Undo**.

See [Remote Access](RemoteAccess.md).

![Proxy tab](screenshots/Proxy.png)

## Advanced

Every proxy setting not on the other tabs, each starting on its default.

* **Security**: Only through the proxy, Always use HTTPS, HTTPS memory, Block embedding, and Trusted networks.
* **Blocking**: the block list, bans for failed sign-ins, and country rules.
* **History**: whether visits are recorded, for how long, and how precise locations are.
* **Performance**: HTTP/2, upload sizes and connection limits.
* **Timeouts**: idle connections, request header limits, the minimum read rate and how long Jellyfin may take to
  answer.

![Advanced tab](screenshots/Advanced.png)
