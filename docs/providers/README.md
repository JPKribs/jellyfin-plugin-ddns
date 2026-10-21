# Provider setup

Pick your DNS provider for the fields it needs and where to find each value. Set the record's
Provider to the matching value on the Domains tab, then fill the fields below from its page here.

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
* [DynDNS v1 (legacy)](DynDns1.md)
* [DynDNS v2](DynDns2.md)
* [Dynu](Dynu.md)
* [easyDNS](EasyDns.md)
* [eNom](Enom.md)
* [FreeDNS (afraid.org)](FreeDns.md)
* [freemyip.com](FreeMyIp.md)
* [Gandi LiveDNS](Gandi.md)
* [GoDaddy](GoDaddy.md)
* [Hetzner DNS](Hetzner.md)
* [Hurricane Electric (dns.he.net)](HeNet.md)
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
* [Yandex PDD (legacy)](Yandex.md)
* [ZoneEdit](ZoneEdit1.md)

## Provider notes

* **TTL**: a TTL of `1` (the default) means "use the provider's default". Each provider resolves it to a sensible value its API accepts (for example Gandi's 300 second minimum or GoDaddy's 600 second minimum) instead of sending a literal one second TTL.
* **IPv4 only protocols**: OVH DynHost, DynDNS v1, DSLReports, and NearlyFreeSpeech.NET carry a single IPv4 address, so the `Update IPv6` toggle is hidden for them and no AAAA update is ever recorded.
* **Zones**: when a provider's `Zone` field is left blank the hostname is split on its first dot, and a bare two label hostname (like `example.com`) is treated as the apex domain. Set the zone explicitly for multi label subdomains or multi label TLDs like `.co.uk`.

*I have only personally tested Cloudflare so please let me know if any of these do not work as appropriately.*

See [Contributing](../contributors/CONTRIBUTING.md) and [Creating a provider](../contributors/PROVIDERS.md) for how to add more providers.
