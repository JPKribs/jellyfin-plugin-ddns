# Security

* **Administrators only**: Records can be authored only by administrators, and the `Update Now` endpoint is gated by Jellyfin's administrator policy.
* **Encrypted**: Credentials are encrypted in the plugin configuration with ASP.NET Core Data Protection, so they are never written to disk in plain text. The key lives in the Jellyfin data directory, **so still use the most limited token or scoped key a provider offers.** Your encrypted data is only as secure as your Jellyfin configuration!
* **Outbound only for DNS**: All provider traffic is outbound HTTP/S, and credentials are never written to logs.
* **Listeners only with Remote Access**: The plugin opens ports only when [Remote Access](RemoteAccess.md) is turned on. Its proxy refuses unknown names before the connection is encrypted, never lets the internet reach the setup wizard, and leaves your network out of every block. See [Protection](RemoteAccess.md#protection).
* **Private keys stay on the server**: Let's Encrypt and certificate keys are generated on this server and stored readable only by Jellyfin's account.
* **Private folders**: The folder holding the encryption key, and the one holding certificate keys, the Let's Encrypt account and visitor history, are restricted to Jellyfin's account at every start. On Linux and macOS they are owner-only. On Windows only Jellyfin's account, SYSTEM and Administrators can open them, and files created inside inherit that.
* **Encryption key on Windows**: The key that encrypts saved credentials is itself encrypted with Windows data protection for this machine, at every start and daily. A copy of the data folder, such as a backup, cannot decrypt the credentials anywhere else. Linux and macOS have no such store for services, so there the private folder is the protection: keep backups of Jellyfin's data folder private.
* **Account changes**: If Jellyfin later runs as another account and cannot open these folders, the plugin says so at startup in the server log, the activity log and the Proxy tab, with the command that gives the folders to the new account.

See [Storage](Storage.md) for where the encrypted credentials and their key are kept.
