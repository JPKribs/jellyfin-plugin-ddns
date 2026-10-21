# OVH

On the **Domains** tab, set **Provider** to **OVH**, then fill in:

- **Hostname**: the record to update.
- **Username**: your OVH DynHost username. Create a DynHost user in the OVH control panel.
- **Password**: the DynHost password.
- **Server override**: leave blank to use OVH's own server.

OVH uses DynHost credentials, which you create per record in the OVH panel, not your main OVH API keys.

DynHost carries a single IPv4 address, so the **Update IPv6** toggle is hidden for it.

_Ported from ddclient's `nic_ovh_update`._
