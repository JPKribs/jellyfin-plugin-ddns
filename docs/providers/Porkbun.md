# Porkbun

Set the record's **Provider** to **Porkbun**, then fill these fields.

- **Hostname**: the record. Split on the first dot when Zone is blank.
- **Login**: your Porkbun API key, starting with pk1_.
- **Password**: your Porkbun secret API key, starting with sk1_. Create both at porkbun.com/account/api and enable API access on the domain.
- **Zone**: the root domain (optional).

_Ported from ddclient's `nic_porkbun_update`._
