# Yandex PDD

!!! warning "Retired API"
    Yandex has retired the PDD (Pochta dlya domena) API this provider talks to. Its documentation now
    redirects to the Yandex 360 API, and `pddimp.yandex.ru` no longer serves a TLS certificate for its own
    name, so updates are expected to fail. The provider is kept so existing records still load. Move the
    domain's DNS to another supported provider for new setups.

On the **Domains** tab, set **Provider** to **Yandex PDD**, then fill in:

- **FQDN**: the fully qualified host to match.
- **Domain**: your Yandex domain. It is used to list the domain's records.
- **PDD token**: your Yandex PDD API token.
- **Server override**: leave blank to use Yandex PDD's own server.

_Ported from ddclient's `nic_yandex_update`._
