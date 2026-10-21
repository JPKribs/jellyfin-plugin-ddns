# Remote Access

![Remote Access](assets/logo.png){ width="160" }

Remote Access is a Jellyfin plugin for reaching your server from anywhere. Its Dynamic DNS keeps your DNS records pointed at your server's current public IP, running inside Jellyfin as a scheduled task that updates your DNS provider over outbound HTTP/S. Its proxy can put HTTPS in front of Jellyfin, described under [Remote Access](RemoteAccess.md).

!!! danger "Actively being tested"
    Remote Access is being actively tested. The proxy, certificates and blocking are new and still changing between releases. Keep another way into your server while you try it, and please report anything that looks wrong in [Issues](https://github.com/JPKribs/jellyfin-plugin-remoteaccess/issues).

!!! warning "Considerations before using"
    This plugin exists to simplify some of the networking around Jellyfin and to minimize the number of external applications that you need to run this. Please keep in mind that **this centralizes your setup around Jellyfin**. If you run into issues with Jellyfin, your DDNS will be offline as well. If you rely on your DDNS for administrative access to your system, I would recommend an external solution so your access isn't lost if Jellyfin goes down.

## How It Works

Each record has a name, a provider, a hostname, and the credentials that provider needs. A scheduled task named `Update Dynamic DNS` detects your public IP, looks up what each hostname currently resolves to in DNS, and pushes the detected IP to any record whose DNS does not already serve it. See [Adding Records](Records.md) and [How Updates Run](HowItWorks.md).

The `Domains` tab lists every record with a health summary and the editor for the selected record.

![Domains tab](screenshots/Domains.png)

Dynamic DNS supports 45 providers based on the great work done by [ddclient](https://github.com/ddclient/ddclient). They are the recommended tool for an external, standalone DDNS solution! See [Providers](providers/README.md) for the fields each one needs.

[Remote Access](RemoteAccess.md) can also put a secure HTTPS proxy in front of Jellyfin, with automatic Let's Encrypt certificates and protection against unwanted visitors.

Every tab is shown in [The Plugin's Pages](Pages.md).

Next: [Installation](Installation.md).
