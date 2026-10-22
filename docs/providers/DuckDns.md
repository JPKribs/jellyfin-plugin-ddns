# Duck DNS

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **Duck DNS**, then fill in:

- **Labels**: your Duck DNS subdomain label, such as myhost. Separate several with commas, but use one label if you use it for Remote Access. With several labels the record cannot be the proxy's address or prove the address to Let's Encrypt.
- **Token**: your Duck DNS token, shown at the top of duckdns.org after you sign in.
- **Server override**: leave blank to use Duck DNS's own server.

This record can also prove your address to Let's Encrypt for [Remote Access](../RemoteAccess.md#how-lets-encrypt-checks-your-address), so no port has to be open and wildcards are covered.

_Ported from ddclient's `nic_duckdns_update`._
