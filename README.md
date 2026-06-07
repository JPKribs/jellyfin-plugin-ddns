# ![Dynamic DNS](Jellyfin.Plugin.DynamicDns/Assets/Logo.png)

A simple Jellyfin plugin that keeps your DNS records pointed at your server's current public IP. It runs entirely inside Jellyfin as a scheduled task and updates your DNS provider over outbound HTTP/S.

---

**All plugins are made for my personal use cases. I've made these publicly available for anyone who has the same use cases and can benefit from this work. I have no desire to advertise or market for these plugins as these are for personal usage only.**

**Thank you,**

*Joe Kribs*

---

## Considerations Before Using

This plugin exists to simplify some of the networking around Jellyfin and to minimize the number of external applications that you need to run this. Please keep in mind that **this centralizes your setup around Jellyfin**. If you run into issues with Jellyfin, your DDNS will be offline as well. If you rely on your DDNS for administrative access to your system, I would recommend an external solution so your access isn't lost if Jellyfin goes down.

---

## How It Works

Dynamic DNS runs entirely inside Jellyfin using the same runtime and resources as the server. When the plugin loads, Jellyfin registers a scheduled task that detects the server's public IP on an interval and updates each configured record over outbound HTTP/S.

### Adding a record

Each record has a name, a provider, a hostname, and the credentials that provider needs. The Domains tab shows only the fields a provider actually uses and labels them in that provider's own terms, so you enter exactly what is required and nothing more. Records live in the plugin configuration, so they are captured by your normal Jellyfin config backups.

### IP versions and detection

The Address tab controls addressing for every record. IPv4 A records and IPv6 AAAA records can each be turned on or off, and turning a version off stops it being detected or updated. Detection reads your public IP from an endpoint that returns it as plain text, with a separate URL for each version. The Address tab also shows the current public IP and an Update now button that runs the task immediately.

### How updates run

The scheduled task named Update Dynamic DNS runs every 15 minutes and on startup by default, and you set the interval under Scheduled Tasks. On each run the plugin detects the public IP and pushes it to every enabled record. An update is skipped when the IP has not changed and the previous run succeeded. Each record shows its last action, updated, unchanged, or failed, and when it was last checked.

### Record health

The Domains tab summarizes every record at a glance:

* **Healthy** - The last update succeeded within the last 24 hours.
* **Unhealthy** - A published record has not updated successfully in the last 24 hours.
* **Unpublished** - The record has never run an update yet.

### Providers

Dynamic DNS supports 45 providers, from the common dyndns2 services such as No IP and DuckDNS through to full APIs such as Cloudflare, GoDaddy, Gandi, Hetzner, Porkbun, and NS1. Choose a provider and the form adapts to it. Adding a provider is a single class that the plugin discovers automatically at startup, so there is no list to maintain.

Provider implementations are based on [ddclient](https://github.com/ddclient/ddclient) and port its protocol logic for each service. Please report any provider that misbehaves.

## Security model

I am no security expert. While I personally advise pointing this only at providers and networks you trust, I have attempted to handle credentials sensibly.

* **Administrators only.** Records can be authored only by administrators, and the Update now endpoint is gated by Jellyfin's administrator policy.
* **Encrypted at rest.** Credentials are encrypted in the plugin configuration with ASP.NET Core Data Protection, so backups do not hold them in plain text. The key lives in the Jellyfin data directory, so still use the most limited token or scoped key a provider offers.
* **Write-only fields.** Saved credentials are never sent back to the browser; a stored value shows as dots. Leave a field blank to keep it, or type to replace it.
* **Outbound only.** All provider traffic is outbound HTTP/S. The plugin opens no listeners and needs no special permissions, and credentials are never written to logs.

These steps alone cannot prevent all issues, so HTTPS, TLS, and a trusted network are always recommended.

*I am always interested in doing this better. Please feel free to reach out to me directly if you believe there are ways I can be doing this more securely.*

---

## Versioning

Releases use a four part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature and bug count:

```
10.11.1.0
└───┘ └┬┘
  │    └── 1 = Plugin feature release
  │        0 = Plugin bug/patch release within that feature
  │
  └─── 10.11 = Jellyfin version this build was tested/released for
```

Targets **Jellyfin 10.11.x** on `net9.0` with ABI `10.11.0.0`.

## Installation

### Step 1: Add Plugin Repository

* Open Jellyfin and navigate to Dashboard → Plugins → Repositories
* Click Add Repository
* Enter the following repository URL: `https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-ddns/master/manifest.json`
* Click Save

### Step 2: Install Plugin

* Go to the Catalog tab in the Plugins section
* Find Dynamic DNS in the catalog
* Click Install
* Wait for installation to complete

### Step 3: Restart Jellyfin

* Restart your Jellyfin server completely
* Wait for Jellyfin to fully start up

### Verification Check

* After restart, open the Dynamic DNS page from the dashboard, add a record, choose its provider, and use Update now to confirm it reaches your provider.

---

## AI Disclaimer

Claude Code was utilized in the initial structure of this project and first drafts of documentation. All code has been manually reviewed, tested, and revised after its generation. This disclaimer exists in the interest of transparency.

**All code was written, or code reviewed and tested, by humans.**
