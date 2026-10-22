# Sitelutions

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **Sitelutions**, then fill in:

- **Record ID**: the numeric record ID from your Sitelutions Recordbook. It is a number, not a host name, so for [Remote Access](../RemoteAccess.md) add your name under **Other addresses** in **Advanced → Addresses**.
- **Account email**: your Sitelutions account email.
- **Account password**: your Sitelutions password.
- **TTL**: optional, in seconds. `1` uses the provider's default.
- **Server override**: leave blank to use Sitelutions's own server.

_Ported from ddclient's `nic_sitelutions_update`._
