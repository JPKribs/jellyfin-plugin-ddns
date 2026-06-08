# Directnic

Set the record's **Provider** to **Directnic**, then fill these fields.

- **Hostname**: the record to update.
- **Login**: your Directnic IPv4 gateway update URL, from the record's dynamic DNS settings (ddclient's urlv4).
- **Password**: your Directnic IPv6 gateway update URL (ddclient's urlv6). Leave blank if you only update IPv4.

Directnic has no username or password. It issues a per record gateway URL for each family, which you place in the Login (IPv4) and Password (IPv6) fields.

_Ported from ddclient's `nic_directnic_update`._
