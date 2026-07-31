# Porkbun

Set the record's **Provider** to **Porkbun**, then fill these fields.

- **Hostname**: the record. Split on the first dot when Zone is blank; a bare two-label hostname is treated as the apex domain. Set Zone explicitly for multi-label subdomains or multi-label TLDs like .co.uk.
- **Login**: your Porkbun API key, starting with pk1_.
- **Password**: your Porkbun secret API key, starting with sk1_. Create both at porkbun.com/account/api and enable API access on the domain.
- **Zone**: the root domain (optional).

_Ported from ddclient's `nic_porkbun_update`._
