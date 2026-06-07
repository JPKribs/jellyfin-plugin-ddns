namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// The DNS provider/protocol a record is updated against. Names mirror ddclient's
/// <c>protocol=</c> values (see https://github.com/ddclient/ddclient).
/// </summary>
public enum DnsProviderKind
{
    /// <summary>ddclient <c>1984</c> — 1984 Hosting (api.1984.is).</summary>
    Hosting1984,

    /// <summary>ddclient <c>changeip</c> — ChangeIP (nic.changeip.com).</summary>
    ChangeIp,

    /// <summary>ddclient <c>cloudflare</c> — Cloudflare v4 API.</summary>
    Cloudflare,

    /// <summary>ddclient <c>cloudns</c> — ClouDNS dynamic URL API.</summary>
    CloudNs,

    /// <summary>ddclient <c>ddnsfm</c> — DDNS.fm.</summary>
    DdnsFm,

    /// <summary>ddclient <c>ddnss</c> — DDNSS.de.</summary>
    Ddnss,

    /// <summary>ddclient <c>digitalocean</c> — DigitalOcean DNS API.</summary>
    DigitalOcean,

    /// <summary>ddclient <c>dinahosting</c> — Dinahosting.</summary>
    Dinahosting,

    /// <summary>ddclient <c>directnic</c> — Directnic.</summary>
    Directnic,

    /// <summary>ddclient <c>dnsexit2</c> — DNSExit v2 API.</summary>
    DnsExit2,

    /// <summary>ddclient <c>dnsmadeeasy</c> — DNS Made Easy.</summary>
    DnsMadeEasy,

    /// <summary>ddclient <c>domeneshop</c> — Domeneshop.</summary>
    Domeneshop,

    /// <summary>ddclient <c>dondominio</c> — DonDominio.</summary>
    DonDominio,

    /// <summary>ddclient <c>dslreports1</c> — DSLReports.</summary>
    DslReports1,

    /// <summary>ddclient <c>duckdns</c> — DuckDNS.</summary>
    DuckDns,

    /// <summary>ddclient <c>dynadot</c> — Dynadot.</summary>
    Dynadot,

    /// <summary>ddclient <c>dyndns1</c> — legacy DynDNS v1 protocol.</summary>
    DynDns1,

    /// <summary>ddclient <c>dyndns2</c> — DynDNS v2 protocol (members.dyndns.org and compatibles).</summary>
    DynDns2,

    /// <summary>ddclient <c>dynu</c> — Dynu.</summary>
    Dynu,

    /// <summary>ddclient <c>easydns</c> — easyDNS.</summary>
    EasyDns,

    /// <summary>ddclient <c>enom</c> — eNom.</summary>
    Enom,

    /// <summary>ddclient <c>freedns</c> — FreeDNS (afraid.org).</summary>
    FreeDns,

    /// <summary>ddclient <c>freemyip</c> — freemyip.com.</summary>
    FreeMyIp,

    /// <summary>ddclient <c>gandi</c> — Gandi LiveDNS.</summary>
    Gandi,

    /// <summary>ddclient <c>godaddy</c> — GoDaddy.</summary>
    GoDaddy,

    /// <summary>ddclient <c>henet</c> — Hurricane Electric (dyn.dns.he.net).</summary>
    HeNet,

    /// <summary>ddclient <c>hetzner</c> — Hetzner DNS.</summary>
    Hetzner,

    /// <summary>ddclient <c>infomaniak</c> — Infomaniak.</summary>
    Infomaniak,

    /// <summary>ddclient <c>inwx</c> — INWX.</summary>
    Inwx,

    /// <summary>ddclient <c>ionos</c> — IONOS.</summary>
    Ionos,

    /// <summary>ddclient <c>keysystems</c> — Key-Systems.</summary>
    KeySystems,

    /// <summary>ddclient <c>mythicdyn</c> — Mythic Beasts.</summary>
    MythicDyn,

    /// <summary>ddclient <c>namecheap</c> — Namecheap.</summary>
    Namecheap,

    /// <summary>ddclient <c>nfsn</c> — NearlyFreeSpeech.NET.</summary>
    Nfsn,

    /// <summary>ddclient <c>njalla</c> — Njalla.</summary>
    Njalla,

    /// <summary>ddclient <c>noip</c> — No-IP.</summary>
    NoIp,

    /// <summary>ddclient <c>ns1</c> — NS1.</summary>
    Ns1,

    /// <summary>ddclient <c>ovh</c> — OVH.</summary>
    Ovh,

    /// <summary>ddclient <c>porkbun</c> — Porkbun.</summary>
    Porkbun,

    /// <summary>ddclient <c>regfishde</c> — regfish.de.</summary>
    RegfishDe,

    /// <summary>ddclient <c>simplycom</c> — Simply.com.</summary>
    SimplyCom,

    /// <summary>ddclient <c>sitelutions</c> — Sitelutions.</summary>
    Sitelutions,

    /// <summary>ddclient <c>spaceship</c> — Spaceship.</summary>
    Spaceship,

    /// <summary>ddclient <c>yandex</c> — Yandex 360 / PDD.</summary>
    Yandex,

    /// <summary>ddclient <c>zoneedit1</c> — ZoneEdit.</summary>
    ZoneEdit1
}
