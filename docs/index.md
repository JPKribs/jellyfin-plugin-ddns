# Dynamic DNS

![Dynamic DNS](assets/logo.png){ width="160" }

Dynamic DNS is a simple Jellyfin plugin that keeps your DNS records pointed at your server's current public IP. It runs entirely inside Jellyfin as a scheduled task and updates your DNS provider over outbound HTTP/S.

!!! warning "Considerations before using"
    This plugin exists to simplify some of the networking around Jellyfin and to minimize the number of external applications that you need to run this. Please keep in mind that **this centralizes your setup around Jellyfin**. If you run into issues with Jellyfin, your DDNS will be offline as well. If you rely on your DDNS for administrative access to your system, I would recommend an external solution so your access isn't lost if Jellyfin goes down.

## How It Works

Each record has a name, a provider, a hostname, and the credentials that provider needs. A scheduled task named `Update Dynamic DNS` detects your public IP, looks up what each hostname currently resolves to in DNS, and pushes the detected IP to any record whose DNS does not already serve it. See [Adding Records](Records.md) and [How Updates Run](HowItWorks.md).

Dynamic DNS supports 45 providers based on the great work done by [ddclient](https://github.com/ddclient/ddclient). They are the recommended tool for an external, standalone DDNS solution! See [Providers](providers/README.md) for the fields each one needs.

Next: [Installation](Installation.md).
