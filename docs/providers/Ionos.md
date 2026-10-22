# IONOS

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **IONOS**, then fill in:

- **Hostname**: the record name.
- **API key**: your IONOS API key, in the form prefix.secret. Create it in the IONOS Developer DNS API.
- **TTL**: optional, in seconds. `1` uses the provider's default.
- **Server override**: leave blank to use IONOS's own server.

_Ported from ddclient's `nic_ionos_update`._
