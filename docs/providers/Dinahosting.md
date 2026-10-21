# Dinahosting

On the **Domains** tab, set **Provider** to **Dinahosting**, then fill in:

- **Host and domain**: the full DNS record name to keep updated, such as home.example.com.
- **Username**: your Dinahosting username.
- **Password**: your Dinahosting password.
- **Domain**: optional. Names the domain when the hostname has more than two labels, such as deep subdomains or multi-label TLDs like .co.uk. Left blank, the hostname is split on its first dot, and a bare two-label hostname is treated as the apex domain.
- **Server override**: leave blank to use Dinahosting's own server.

_Ported from ddclient's `nic_dinahosting_update`._
