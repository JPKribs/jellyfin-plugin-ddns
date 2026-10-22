# INWX

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **INWX**, then fill in:

- **Username**: your INWX username. A dedicated DynDNS account is recommended.
- **Password**: your INWX password.
- **Server override**: leave blank to use INWX's own server.

INWX works out which record to update from the account. There is no hostname field, so the record saves without one. Give it a **Name** to tell it apart from your other records. With no name to look up, the plugin compares against the last address it sent. The **Address** step cannot offer the record under **Address**, so for [Remote Access](../RemoteAccess.md) add your name under **Other addresses** instead.

_Ported from ddclient's `nic_inwx_update`._
