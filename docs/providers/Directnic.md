# Directnic

On the **Domains** tab, set **Provider** to **Directnic**, then fill in:

- **IPv4 gateway URL**: the IPv4 update URL from the record's dynamic DNS settings at Directnic.
- **IPv6 gateway URL**: the IPv6 update URL from the same place. Leave it blank if you only update IPv4.
- **Server override**: leave blank to use Directnic's own server.

Directnic has no username or password. It issues a gateway URL per record for each address family, and those URLs are all the plugin needs.

_Ported from ddclient's `nic_directnic_update`._
