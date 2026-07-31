# Dinahosting

Set the record's **Provider** to **Dinahosting**, then fill these fields.

- **Hostname**: the full DNS record name you want to keep updated, such as home.example.com.
- **Login**: your dinahosting username.
- **Password**: your dinahosting password.
- **Zone**: optional. Names the domain when the hostname has more than two labels (deep subdomains, multi-label TLDs like .co.uk). Left blank, the hostname is split on its first dot and a bare two-label hostname is treated as the apex domain.

_Ported from ddclient's `nic_dinahosting_update`._
