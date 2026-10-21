# Provider setup

Each page below lists the fields a provider shows on the **Domains** tab and where to find each value.
Set **Provider** to your DNS provider, then fill in the fields from its page.

* [1984 Hosting](Hosting1984.md)
* [ChangeIP](ChangeIp.md)
* [Cloudflare](Cloudflare.md)
* [ClouDNS](CloudNs.md)
* [DDNS.fm](DdnsFm.md)
* [DDNSS.de](Ddnss.md)
* [DigitalOcean](DigitalOcean.md)
* [Dinahosting](Dinahosting.md)
* [Directnic](Directnic.md)
* [DNS Made Easy](DnsMadeEasy.md)
* [DNSExit](DnsExit2.md)
* [Domeneshop](Domeneshop.md)
* [DonDominio](DonDominio.md)
* [DSLReports](DslReports1.md)
* [Duck DNS](DuckDns.md)
* [Dynadot](Dynadot.md)
* [DynDNS v1](DynDns1.md)
* [DynDNS v2](DynDns2.md)
* [Dynu](Dynu.md)
* [easyDNS](EasyDns.md)
* [eNom](Enom.md)
* [FreeDNS](FreeDns.md)
* [freemyip.com](FreeMyIp.md)
* [Gandi LiveDNS](Gandi.md)
* [GoDaddy](GoDaddy.md)
* [Hetzner DNS](Hetzner.md)
* [Hurricane Electric](HeNet.md)
* [Infomaniak](Infomaniak.md)
* [INWX](Inwx.md)
* [IONOS](Ionos.md)
* [Key-Systems](KeySystems.md)
* [Mythic Beasts](MythicDyn.md)
* [Namecheap](Namecheap.md)
* [NearlyFreeSpeech.NET](Nfsn.md)
* [Njalla](Njalla.md)
* [No-IP](NoIp.md)
* [NS1](Ns1.md)
* [OVH](Ovh.md)
* [Porkbun](Porkbun.md)
* [regfish.de](RegfishDe.md)
* [Simply.com](SimplyCom.md)
* [Sitelutions](Sitelutions.md)
* [Spaceship](Spaceship.md)
* [Yandex PDD](Yandex.md)
* [ZoneEdit](ZoneEdit1.md)

## Provider notes

* **TTL**: a TTL of `1`, the default, uses the provider's default. Each provider turns it into a value its API accepts, such as Gandi's 300 second minimum or GoDaddy's 600 second minimum. It never sends a literal one second TTL.
* **IPv4 only**: OVH, DynDNS v1, DSLReports and NearlyFreeSpeech.NET carry a single IPv4 address. The **Update IPv6** toggle is hidden for them, and no AAAA update is ever recorded.
* **Zones**: when an optional domain or zone field is left blank, the hostname is split on its first dot. A bare two-label hostname such as `example.com` is treated as the apex domain. Fill the field in for multi-label subdomains or multi-label TLDs such as `.co.uk`.
* **Legacy**: DynDNS v1 is a legacy protocol, and Yandex PDD is kept only so existing records still load.

Only Cloudflare has been tested end to end. Please open an issue if another provider does not work.

See [Contributing](../contributors/CONTRIBUTING.md) and [Creating a provider](../contributors/PROVIDERS.md) for how to add more providers.
