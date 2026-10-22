# ClouDNS

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **ClouDNS**, then fill in:

- **Update URL**: the Dynamic URL (DynURL) for the record. In the ClouDNS panel, open the A or AAAA record, turn on Dynamic URL, and paste the full URL here.

The Update URL already names the record. There is no hostname field, so the record saves without one. With no name to look up, the plugin compares against the last address it sent. The proxy cannot take its address from the record, so for [Remote Access](../RemoteAccess.md) add your name under **Other addresses** in **Advanced → Addresses**.

_Ported from ddclient's `nic_cloudns_update`._
