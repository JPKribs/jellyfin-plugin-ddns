# Yandex PDD (legacy)

!!! warning "Retired API"
    Yandex has retired the PDD (Pochta dlya domena) API this provider talks to. Its documentation now
    redirects to the Yandex 360 API, and `pddimp.yandex.ru` no longer serves a TLS certificate for its own
    name, so updates are expected to fail. The provider is kept so existing records still load. Move the
    domain's DNS to another supported provider for new setups.

Set the record's **Provider** to **Yandex PDD (legacy, retired API)**, then fill these fields.

- **Hostname**: the fully qualified host to match.
- **Login**: your Yandex domain. It is used to list the domain's records.
- **Password**: your Yandex PDD API token, sent as the PddToken header.

_Ported from ddclient's `nic_yandex_update`._
