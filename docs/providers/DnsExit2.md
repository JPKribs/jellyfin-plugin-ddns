# DNSExit

On the **Domains** tab, set **Provider** to **DNSExit**, then fill in:

- **Record name**: the record name.
- **API key**: your DNSExit API key, from the Dynamic DNS or API settings.
- **Domain**: the domain, such as example.com. Left blank, the record name is used.
- **TTL**: optional, in seconds. `1`, the default, sends `5`, which is ddclient's default for this protocol.
- **Server override**: leave blank to use DNSExit's own server.

_Ported from ddclient's `nic_dnsexit2_update`._
