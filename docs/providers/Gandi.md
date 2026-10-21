# Gandi LiveDNS

On the **Domains** tab, set **Provider** to **Gandi LiveDNS**, then fill in:

- **Record name**: the record name, relative to the zone.
- **Auth keyword**: the word `token` to use a Personal Access Token. Any other value uses an API key.
- **API key or token**: your Gandi API key or Personal Access Token, from your Gandi account under API keys.
- **Domain**: the domain, such as example.com.
- **TTL**: optional, in seconds. `1`, the default, sends Gandi's 300 second minimum. Higher values are sent as entered.
- **Server override**: leave blank to use Gandi LiveDNS's own server.

_Ported from ddclient's `nic_gandi_update`._
