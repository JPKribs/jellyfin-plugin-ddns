# ![Dynamic DNS](Jellyfin.Plugin.DynamicDns/Assets/Logo.png)

A simple Jellyfin plugin that keeps your DNS records pointed at your server's current public IP. It runs entirely inside Jellyfin as a scheduled task and updates your DNS provider over outbound HTTP/S.

## Considerations Before Using

This plugin exists to simplify some of the networking around Jellyfin and to minimize the number of external applications that you need to run this. Please keep in mind that **this centralizes your setup around Jellyfin**. If you run into issues with Jellyfin, your DDNS will be offline as well. If you rely on your DDNS for administrative access to your system, I would recommend an external solution so your access isn't lost if Jellyfin goes down.

## How It Works

### Adding a record

Each record has a name, a provider, a hostname, and the credentials that provider needs. The `Domains` tab shows only the fields a provider actually uses to try and minimize confusion. Records live in the plugin configuration, so they are captured by your normal Jellyfin config backups.

If a record's hostname sits behind a proxy or CDN, turn on `Treat as Proxied Address`. The plugin then compares against the last IP it pushed rather than live DNS, since the DNS will return the proxied address. It's recommended to pair that with `Force Update` on the `Address` tab, which re-sends the record periodically in case it was changed from elsewhere.

### Address families and detection

Each record chooses its address families with `Update IPv4 (A)` and `Update IPv6 (AAAA)` on the `Domains` tab. Detection reads your public IP from endpoints that return it as plain text. The `IPv4 Detection URLs` and `IPv6 Detection URLs` fields list every endpoint, one per line. Only `https` endpoints are used, since a tampered reply could point your DNS at the wrong address. This process attempts to find two endpoints that agree on the address before submitting that IP. It falls back to only a single provider if there is only one active provider available.

The `Address` tab also shows the current public IP and an `Update Now` button that runs the task immediately. If detection finds only an internal address, which usually means the server has no public IP, the `Address` tab shows a warning so you know nothing was published. An address counts as internal when it falls in a private, loopback, link local, CGNAT, or other reserved range, and you can turn off `Skip Update if Internal` to publish such an address anyway.

### How updates run

The scheduled task named `Update Dynamic DNS` runs every 15 minutes and on startup by default, but you can set the interval under `Scheduled Tasks`. On each run the plugin detects the public IP, looks up what each hostname currently resolves to in DNS, and pushes the detected IP to any record whose DNS does not already serve it. Each record shows its last action, updated, unchanged, or failed, and when it was last checked.

If you want a record refreshed even when DNS already matches, choose an interval under `Force Update` in the `Update Behavior` section of the `Address` tab. When that period has passed since the last successful push, the plugin re-sends the current IP, which keeps the record alive on providers that age out stale entries. `Force Update` is `Off` by default, so records are pushed only when DNS does not already serve your detected IP.

If a record keeps failing, for example because a token is wrong, the plugin pauses it after a set number of failures in a row and then retries it only once each backoff window, so a misconfiguration does not keep spamming your provider and risk a rate limit or ban. Both the failure count and the window are set under `Update Behavior` and default to three failures and twenty four hours. The record card shows when a record is backing off and when it will next be tried.

**Set the scheduled interval longer than your record TTL.** If a run happens before the change has propagated, your resolver may still answer with the old address and the record can be pushed again needlessly.

If your server resolves its own hostname through a local resolver that answers with an internal address, which is common in a split horizon setup using a router or an ad blocking resolver or a hosts file, the live DNS check cannot see your public record. The plugin handles this by comparing the IP against the last IP it pushed instead of that answer, so an unchanged IP is not re-pushed every run. 

You can still turn on `Treat as Proxied Address` to force that comparison for a record at all times (E.G. Cloudflare's Proxy).

### Record health

The `Domains` tab summarizes every record at a glance.

* **Healthy** - The last update succeeded within the last 24 hours.
* **Unhealthy** - A published record has not updated successfully in the last 24 hours.
* **Unpublished** - The record has never run an update yet.

### Providers

Dynamic DNS supports 45 providers based on the great work done by [ddclient](https://github.com/ddclient/ddclient). They are the recommdended tool for an external, standlone DDNS solution! See [CONTRIBUTING.md](docs/contributors/CONTRIBUTING.md) & [PROVIDERS.md](docs/contributors/PROVIDERS.md) for how to add more providers.

*I have only personally tested Cloudflare so please let me know if any of these do not work as appropriately.*

## Security

* **Administrators only** - Records can be authored only by administrators, and the `Update Now` endpoint is gated by Jellyfin's administrator policy.
* **Encrypted** - Credentials are encrypted in the plugin configuration with ASP.NET Core Data Protection, so they are never written to disk in plain text. The key lives in the Jellyfin data directory, **so still use the most limited token or scoped key a provider offers.** Your encrypted data is only as secure as your Jellyfin configuration!
* **Outbound only** - All provider traffic is outbound HTTP/S. The plugin opens no listeners and needs no special permissions, and credentials are never written to logs.

## Storage

Everything the plugin keeps lives inside your Jellyfin data directory, so a normal Jellyfin backup captures all of it.

* Plugin files: `plugins/Dynamic DNS_10.11.1.0/`
* Records, settings, and encrypted credentials: `plugins/configurations/Jellyfin.Plugin.DynamicDns.xml`
* The encryption key for those credentials: `plugins/configurations/Jellyfin.Plugin.DynamicDns.Keys/`

Those paths are relative to your Jellyfin data directory.

The encryption key sits beside the configuration it protects, both inside the data volume, so they persist together across container restarts and image rebuilds. Resetting this folder will require that you re-authenticate your tokens, usernames, and passwords.

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

* After restart, open the Dynamic DNS page from the dashboard, add a record, choose its provider, and use `Update Now` to confirm it reaches your provider.

---

## AI Disclaimer

Claude Code was utilized in the initial structure of this project and first drafts of documentation. All code has been manually reviewed, tested, and revised after its generation. This disclaimer exists in the interest of transparency.

**All code was written, or code reviewed and tested, by humans.**
