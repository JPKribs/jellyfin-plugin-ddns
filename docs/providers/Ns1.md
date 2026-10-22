# NS1

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **NS1**, then fill in:

- **Record name**: the record name.
- **API key**: your NS1 API key. Create it under Account Settings, then API Keys.
- **Zone**: optional. The zone name. Left blank, it is taken from the record name. Fill it in for multi-label subdomains or multi-label TLDs like .co.uk.
- **TTL**: optional, in seconds. A record that does not exist yet is created with this TTL. `1`, the default, uses 300 seconds.
- **Server override**: leave blank to use NS1's own server.

_Ported from ddclient's `nic_ns1_update`._
