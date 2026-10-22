# Hetzner DNS

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **Hetzner DNS**, then fill in:

- **Record name**: the record name.
- **API token**: a Hetzner DNS API token. Create one in the DNS Console under API Tokens, then New token.
- **DNS zone**: the domain, such as example.com.
- **TTL**: optional, in seconds. `1`, the default, leaves the TTL to the zone default when a record set is created.
- **Server override**: leave blank to use Hetzner DNS's own server.

Hetzner applies changes in the background. An update it has accepted but not finished applying is reported as success.

_Ported from ddclient's `nic_hetzner_update`._
