# Gandi LiveDNS

Set the record's **Provider** to **Gandi LiveDNS**, then fill these fields.

- **Hostname**: the record name relative to the zone.
- **Login**: the word `token` to send a Personal Access Token (Bearer), or any other value to send an Apikey header.
- **Password**: your Gandi API key or Personal Access Token, from your Gandi account under API keys.
- **Zone**: the domain, such as example.com.

_Ported from ddclient's `nic_gandi_update`._
