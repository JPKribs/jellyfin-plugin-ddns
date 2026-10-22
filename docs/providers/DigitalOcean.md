# DigitalOcean

In step 2, **Address**, of the **Configuration** tab, set **Provider** to **DigitalOcean**, then fill in:

- **Record name**: the record name, such as home.example.com.
- **API token**: a DigitalOcean API token with read and write scope. Create one under API, then Tokens, then Generate New Token.
- **Apex domain**: the apex domain, such as example.com.
- **Server override**: leave blank to use DigitalOcean's own server.

_Ported from ddclient's `nic_digitalocean_update`._
