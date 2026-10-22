# DynDNS v1

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **DynDNS v1**, then fill in:

- **Hostname**: the host to update.
- **Username**: your account username at the DynDNS v1 compatible service.
- **Password**: your account password.
- **Server override**: the update host. Left blank, members.dyndns.org is used. Set it to your service's update host.

DynDNS v1 is a legacy protocol.

This protocol carries a single IPv4 address, so the **Update IPv6** toggle is hidden for it.

_Ported from ddclient's `nic_dyndns1_update`._
