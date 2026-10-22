# DNS Made Easy

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **DNS Made Easy**, then fill in:

- **Record ID**: the numeric ID of the Dynamic DNS record, shown on that record in the DNS Made Easy panel. It is a number, not a host name, so for [Remote Access](../RemoteAccess.md) add your name under **Other addresses** in **Advanced → Addresses**.
- **Account email**: your account email address.
- **Record password**: the dynamic DNS password generated for the record.
- **Server override**: leave blank to use DNS Made Easy's own server.

_Ported from ddclient's `nic_dnsmadeeasy_update`._
