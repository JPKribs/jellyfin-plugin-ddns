# Porkbun

On the **Domains** tab, set **Provider** to **Porkbun**, then fill in:

- **Hostname**: the record.
- **API key**: your Porkbun API key, starting with pk1_.
- **Secret API key**: your Porkbun secret API key, starting with sk1_. Create both at porkbun.com/account/api and turn on API access for the domain.
- **Root domain**: optional. The root domain, such as example.com. Left blank, the hostname is split on its first dot, and a bare two-label hostname is treated as the apex domain. Fill it in for multi-label subdomains or multi-label TLDs like .co.uk.
- **Server override**: leave blank to use Porkbun's own server.

_Ported from ddclient's `nic_porkbun_update`._
