# NS1

Set the record's **Provider** to **NS1**, then fill these fields.

- **Hostname**: the record name.
- **Login**: your NS1 API key, sent as the X-NSONE-Key header. Create it under Account Settings then API Keys.
- **Zone**: the zone name. Inferred from the hostname when left blank; set it explicitly for multi-label subdomains or multi-label TLDs like .co.uk.

A record that does not exist yet is created with the resolved TTL (300 seconds when the TTL is left at `1`).

_Ported from ddclient's `nic_ns1_update`._
