# Hetzner DNS

Set the record's **Provider** to **Hetzner DNS**, then fill these fields.

- **Hostname**: the record name.
- **Password**: a Hetzner DNS API token (DNS Console then API Tokens then New token).
- **Zone**: the domain, such as example.com.

A TTL of `1` (the default) leaves the TTL to the zone default when a record set is created. Hetzner applies changes asynchronously; an update it has accepted but not yet finished applying is reported as success.

_Ported from ddclient's `nic_hetzner_update`._
