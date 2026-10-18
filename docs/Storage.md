# Storage

Everything the plugin keeps lives inside your Jellyfin data directory, so a normal Jellyfin backup captures all of it.

| What | Path |
| :--- | :--- |
| Plugin files | `plugins/Dynamic DNS_<version>/` |
| Records, settings, and encrypted credentials | `plugins/configurations/Jellyfin.Plugin.DynamicDns.xml` |
| The encryption key for those credentials | `plugins/configurations/Jellyfin.Plugin.DynamicDns.Keys/` |

Those paths are relative to your Jellyfin data directory.

The encryption key sits beside the configuration it protects, both inside the data volume, so they persist together across container restarts and image rebuilds. Resetting this folder will require that you re-authenticate your tokens, usernames, and passwords.
