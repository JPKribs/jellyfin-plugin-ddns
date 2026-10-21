# Hurricane Electric

On the **Domains** tab, set **Provider** to **Hurricane Electric**, then fill in:

- **Record**: the record name. It is also used as the sign-in user.
- **DDNS key**: the DDNS key generated for that record. In the dns.he.net panel, turn on dynamic DNS for the A or AAAA record and copy the generated key.
- **Server override**: leave blank to use Hurricane Electric's own server.

This is the DNS service at dns.he.net.

_Ported from ddclient's `nic_henet_update`._
