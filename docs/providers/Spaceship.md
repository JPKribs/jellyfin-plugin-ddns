# Spaceship

Set the record's **Provider** to **Spaceship**, then fill these fields.

- **Hostname**: the record. The subdomain is derived, using @ for the apex.
- **Login**: your Spaceship API key, sent as X-Api-Key.
- **Password**: your Spaceship API secret, sent as X-Api-Secret. Create both in the Spaceship API Manager.
- **Zone**: the domain, such as example.com. Set it explicitly for multi-label subdomains or multi-label TLDs like .co.uk; a bare two-label hostname is treated as the apex.

A TTL of `1` (the default) sends 1800 seconds (ddclient's default for Spaceship). The new record is written before stale ones are removed, so a failed update leaves the old address serving rather than deleting the record.

_Ported from ddclient's `nic_spaceship_update`._
