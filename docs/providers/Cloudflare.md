# Cloudflare

On the **Domains** tab, set **Provider** to **Cloudflare**, then fill in:

- **Record name**: the full DNS record name to keep updated, such as home.example.com.
- **Auth email or token**: the word `token`, in lowercase, when you use a scoped API token, or your account email when you use the Global API Key. An empty field is treated as `token`.
- **API token or key**: the API token, or the Global API Key. To make a token, go to My Profile, then API Tokens, then Create Token, and use the Edit zone DNS template.
- **Zone name**: the zone name, such as example.com.
- **Server override**: leave blank to use Cloudflare's own server.

If the record is proxied, with the orange cloud on, Cloudflare's public DNS returns Cloudflare's address, not yours. Turn on **Treat as proxied address** on the record so the plugin compares against the last address it pushed rather than DNS.

This record can also prove your address to Let's Encrypt for [Remote Access](../RemoteAccess.md#how-lets-encrypt-checks-your-address), so no port has to be open and wildcards are covered. The Edit zone DNS template already allows it.

_Ported from ddclient's `nic_cloudflare_update`._
