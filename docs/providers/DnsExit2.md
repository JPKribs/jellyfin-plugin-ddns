# DNSExit

Set the record's **Provider** to **DNSExit**, then fill these fields.

- **Hostname**: the record name.
- **Password**: your DNSExit API key, from the Dynamic DNS or API settings.
- **Zone**: the domain, such as example.com. Defaults to the hostname when left blank.

A TTL of `1` (the default) sends `5`, ddclient's default for this protocol.

_Ported from ddclient's `nic_dnsexit2_update`._
