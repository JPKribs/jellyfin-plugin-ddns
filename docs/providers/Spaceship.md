# Spaceship

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **Spaceship**, then fill in:

- **Hostname**: the record. The subdomain is worked out from it, with @ for the apex.
- **API key**: your Spaceship API key.
- **API secret**: your Spaceship API secret. Create both in the Spaceship API Manager.
- **Domain**: the domain, such as example.com. A bare two-label hostname is treated as the apex. Fill it in for multi-label subdomains or multi-label TLDs like .co.uk.
- **TTL**: optional, in seconds. `1`, the default, sends 1800 seconds, which is ddclient's default for Spaceship.
- **Server override**: leave blank to use Spaceship's own server.

The new record is written before stale ones are removed, so a failed update leaves the old address serving rather than deleting the record.

_Ported from ddclient's `nic_spaceship_update`._
