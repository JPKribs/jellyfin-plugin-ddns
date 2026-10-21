namespace Jellyfin.Plugin.DynamicDns.Models;

/// <summary>
/// The DNS provider/protocol a record is updated against. Names mirror ddclient's
/// <c>protocol=</c> values (see https://github.com/ddclient/ddclient).
/// </summary>
public enum DNSProviderKind
{
    /// <summary>ddclient <c>1984</c> for 1984 Hosting (api.1984.is).</summary>
    Hosting1984,

    /// <summary>ddclient <c>changeip</c> for ChangeIP (nic.changeip.com).</summary>
    ChangeIp,

    /// <summary>ddclient <c>cloudflare</c> for Cloudflare v4 API.</summary>
    Cloudflare,

    /// <summary>ddclient <c>cloudns</c> for ClouDNS dynamic URL API.</summary>
    CloudNs,

    /// <summary>ddclient <c>ddnsfm</c> for DDNS.fm.</summary>
    DdnsFm,

    /// <summary>ddclient <c>ddnss</c> for DDNSS.de.</summary>
    Ddnss,

    /// <summary>ddclient <c>digitalocean</c> for DigitalOcean DNS API.</summary>
    DigitalOcean,

    /// <summary>ddclient <c>dinahosting</c> for Dinahosting.</summary>
    Dinahosting,

    /// <summary>ddclient <c>directnic</c> for Directnic.</summary>
    Directnic,

    /// <summary>ddclient <c>dnsexit2</c> for DNSExit v2 API.</summary>
    DnsExit2,

    /// <summary>ddclient <c>dnsmadeeasy</c> for DNS Made Easy.</summary>
    DnsMadeEasy,

    /// <summary>ddclient <c>domeneshop</c> for Domeneshop.</summary>
    Domeneshop,

    /// <summary>ddclient <c>dondominio</c> for DonDominio.</summary>
    DonDominio,

    /// <summary>ddclient <c>dslreports1</c> for DSLReports.</summary>
    DslReports1,

    /// <summary>ddclient <c>duckdns</c> for DuckDNS.</summary>
    DuckDns,

    /// <summary>ddclient <c>dynadot</c> for Dynadot.</summary>
    Dynadot,

    /// <summary>ddclient <c>dyndns1</c> for the legacy DynDNS v1 protocol.</summary>
    DynDns1,

    /// <summary>ddclient <c>dyndns2</c> for the DynDNS v2 protocol (members.dyndns.org and compatibles).</summary>
    DynDns2,

    /// <summary>ddclient <c>dynu</c> for Dynu.</summary>
    Dynu,

    /// <summary>ddclient <c>easydns</c> for easyDNS.</summary>
    EasyDns,

    /// <summary>ddclient <c>enom</c> for eNom.</summary>
    Enom,

    /// <summary>ddclient <c>freedns</c> for FreeDNS (afraid.org).</summary>
    FreeDns,

    /// <summary>ddclient <c>freemyip</c> for freemyip.com.</summary>
    FreeMyIp,

    /// <summary>ddclient <c>gandi</c> for Gandi LiveDNS.</summary>
    Gandi,

    /// <summary>ddclient <c>godaddy</c> for GoDaddy.</summary>
    GoDaddy,

    /// <summary>ddclient <c>henet</c> for Hurricane Electric (dyn.dns.he.net).</summary>
    HeNet,

    /// <summary>ddclient <c>hetzner</c> for Hetzner DNS.</summary>
    Hetzner,

    /// <summary>ddclient <c>infomaniak</c> for Infomaniak.</summary>
    Infomaniak,

    /// <summary>ddclient <c>inwx</c> for INWX.</summary>
    Inwx,

    /// <summary>ddclient <c>ionos</c> for IONOS.</summary>
    Ionos,

    /// <summary>ddclient <c>keysystems</c> for Key Systems.</summary>
    KeySystems,

    /// <summary>ddclient <c>mythicdyn</c> for Mythic Beasts.</summary>
    MythicDyn,

    /// <summary>ddclient <c>namecheap</c> for Namecheap.</summary>
    Namecheap,

    /// <summary>ddclient <c>nfsn</c> for NearlyFreeSpeech.NET.</summary>
    Nfsn,

    /// <summary>ddclient <c>njalla</c> for Njalla.</summary>
    Njalla,

    /// <summary>ddclient <c>noip</c> for No IP.</summary>
    NoIp,

    /// <summary>ddclient <c>ns1</c> for NS1.</summary>
    Ns1,

    /// <summary>ddclient <c>ovh</c> for OVH.</summary>
    Ovh,

    /// <summary>ddclient <c>porkbun</c> for Porkbun.</summary>
    Porkbun,

    /// <summary>ddclient <c>regfishde</c> for regfish.de.</summary>
    RegfishDe,

    /// <summary>ddclient <c>simplycom</c> for Simply.com.</summary>
    SimplyCom,

    /// <summary>ddclient <c>sitelutions</c> for Sitelutions.</summary>
    Sitelutions,

    /// <summary>ddclient <c>spaceship</c> for Spaceship.</summary>
    Spaceship,

    /// <summary>ddclient <c>yandex</c> for Yandex 360 and PDD.</summary>
    Yandex,

    /// <summary>ddclient <c>zoneedit1</c> for ZoneEdit.</summary>
    ZoneEdit1
}
