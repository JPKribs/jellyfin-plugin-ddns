# Security

* **Administrators only**: Records can be authored only by administrators, and the `Update Now` endpoint is gated by Jellyfin's administrator policy.
* **Encrypted**: Credentials are encrypted in the plugin configuration with ASP.NET Core Data Protection, so they are never written to disk in plain text. The key lives in the Jellyfin data directory, **so still use the most limited token or scoped key a provider offers.** Your encrypted data is only as secure as your Jellyfin configuration!
* **Outbound only**: All provider traffic is outbound HTTP/S. The plugin opens no listeners and needs no special permissions, and credentials are never written to logs.

See [Storage](Storage.md) for where the encrypted credentials and their key are kept.
