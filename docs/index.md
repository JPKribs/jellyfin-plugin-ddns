# Remote Access

![Remote Access](assets/logo.png){ width="160" }

Remote Access is a Jellyfin plugin for reaching your server from anywhere. It does two things, both inside
Jellyfin:

* **Dynamic DNS** keeps a domain name, such as `media.example.com`, pointed at your home's public IP, even when
  your internet provider changes it. It supports [45 DNS providers](providers/README.md).
* **The proxy** puts a secure HTTPS address in front of Jellyfin, with a free certificate from Let's Encrypt and
  protection against people trying to break in. See [Remote Access](RemoteAccess.md).

## Where to start

* **New to all of this?** Follow the [Beginner's Guide](BeginnersGuide.md). It goes from nothing to watching from
  your phone, with a free path using Duck DNS and a recommended path using your own domain.
* **Already have a domain?** [Install the plugin](Installation.md), [set up the record](Records.md), then set up
  [Remote Access](RemoteAccess.md).
* **Want a tour first?** The [Settings Reference](Settings.md) shows every tab.

![Configuration tab](screenshots/Configuration.png)

The DNS providers are ported from [ddclient](https://github.com/ddclient/ddclient), which is the recommended tool
if you want Dynamic DNS outside Jellyfin.
