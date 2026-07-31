# DynDNS v1 (legacy)

Set the record's **Provider** to **DynDNS v1 (legacy)**, then fill these fields.

- **Hostname**: the host to update.
- **Login**: your account username at the DynDNS v1 compatible service.
- **Password**: your account password.
- **Server**: the update endpoint host. Defaults to members.dyndns.org. Set it to your provider's update host.

This protocol carries a single IPv4 address, so the IPv6 (AAAA) toggle is hidden for it.

_Ported from ddclient's `nic_dyndns1_update`._
