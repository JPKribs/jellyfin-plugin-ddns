# Cloudflare

Set the record's **Provider** to **Cloudflare**, then fill these fields.

- **Hostname**: the full DNS record name you want to keep updated, such as home.example.com.
- **Login**: the word `token` when using a scoped API token, or your account email when using the Global API Key.
- **Password**: the API token (My Profile then API Tokens then Create Token, using the Edit zone DNS template), or the Global API Key.
- **Zone**: the zone name, such as example.com.

If the record is proxied (the orange cloud is on), Cloudflare's public DNS returns Cloudflare's address, not yours. Turn on **Treat as proxied address** on the record so the plugin compares against the last IP it pushed rather than DNS.

_Ported from ddclient's `nic_cloudflare_update`._
