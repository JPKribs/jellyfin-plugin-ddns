<img src="docs/assets/icon.png" alt="Remote Access" width="96" align="left">

A Jellyfin plugin for reaching your server from anywhere. It keeps your DNS records pointed at your server's current public IP, and it can put a secure HTTPS proxy in front of Jellyfin, with automatic Let's Encrypt certificates and protection against unwanted visitors. It runs entirely inside Jellyfin.

<br clear="left">

## Documentation

Installation, record setup, every provider, and security are covered in the **[Remote Access documentation](https://jpkribs.github.io/jellyfin-plugin-remoteaccess/)**.

View the **[Beginner's Guide](https://jpkribs.github.io/jellyfin-plugin-remoteaccess/BeginnersGuide/)** if you are new to domains and port forwarding.

Plugin repository URL for Jellyfin:

```
https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-remoteaccess/master/manifest.json
```

## Considerations Before Using

This plugin exists to simplify external networking for Jellyfin with no additional programs to manage. Please keep in mind that **this centralizes your setup around Jellyfin**. If you run into issues with Jellyfin, your DDNS will be offline as well. If you rely on your DDNS for administrative access to your system, I would recommend an external solution so your access isn't lost if Jellyfin goes down.

## AI Disclaimer

Anthropic's Claude Code is utilized in the development and maintenance of this project.
