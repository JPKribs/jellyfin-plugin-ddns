# Duck DNS

On the **Domains** tab, set **Provider** to **Duck DNS**, then fill in:

- **Labels**: your Duck DNS subdomain label, such as myhost. Separate several with commas.
- **Token**: your Duck DNS token, shown at the top of duckdns.org after you sign in.
- **Server override**: leave blank to use Duck DNS's own server.

This record can also prove your address to Let's Encrypt for [Remote Access](../RemoteAccess.md#how-lets-encrypt-checks-your-address), so no port has to be open and wildcards are covered.

_Ported from ddclient's `nic_duckdns_update`._
