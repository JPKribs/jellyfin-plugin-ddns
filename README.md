<img src="docs/assets/icon.png" alt="Dynamic DNS" width="96" align="left">

A simple Jellyfin plugin that keeps your DNS records pointed at your server's current public IP. It runs entirely inside Jellyfin as a scheduled task and updates your DNS provider over outbound HTTP/S.

<br clear="left">

## Documentation

Installation, record setup, every provider, and security are covered in the **[Dynamic DNS documentation](https://jpkribs.github.io/jellyfin-plugin-ddns/)**.

Plugin repository URL for Jellyfin:

```
https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-ddns/master/manifest.json
```

## Considerations Before Using

This plugin exists to simplify some of the networking around Jellyfin and to minimize the number of external applications that you need to run this. Please keep in mind that **this centralizes your setup around Jellyfin**. If you run into issues with Jellyfin, your DDNS will be offline as well. If you rely on your DDNS for administrative access to your system, I would recommend an external solution so your access isn't lost if Jellyfin goes down.

## AI Disclaimer

Anthropic's Claude Code is utilized in the development and maintenance of this project.
