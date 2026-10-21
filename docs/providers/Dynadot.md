# Dynadot

On the **Domains** tab, set **Provider** to **Dynadot**, then fill in:

- **Hostname**: the host.
- **DDNS password**: your Dynadot DDNS password, from the domain's Dynamic DNS settings in your Dynadot account.
- **Domain**: optional. The domain, such as example.com, used to split the hostname into domain and subdomain. Left blank, a bare two-label hostname is treated as the apex domain. Fill it in for multi-label subdomains or multi-label TLDs like .co.uk.
- **TTL**: optional, in seconds. `1` uses the provider's default.
- **Server override**: leave blank to use Dynadot's own server.

_Ported from ddclient's `nic_dynadot_update`._
