# Dynadot

Set the record's **Provider** to **Dynadot**, then fill these fields.

- **Hostname**: the host. With Zone set, it splits into domain and subdomain. Without a Zone, a bare two-label hostname is treated as the apex domain; set Zone explicitly for multi-label subdomains or multi-label TLDs like .co.uk.
- **Password**: your Dynadot DDNS password, from the domain's Dynamic DNS settings in your Dynadot account.
- **Zone**: the domain, such as example.com.

_Ported from ddclient's `nic_dynadot_update`._
