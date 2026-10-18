# Installation

## Add the Plugin Repository

1. Open Jellyfin and go to **Dashboard → Plugins → Repositories**.
2. Click **Add Repository**.
3. Enter this repository URL and click **Save**:

```
https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-ddns/master/manifest.json
```

## Install the Plugin

1. Open the **Catalog** tab in the Plugins section.
2. Find **Dynamic DNS** and click **Install**.
3. Wait for the installation to complete.

## Restart Jellyfin

Restart your Jellyfin server completely and wait for it to fully start up.

## Check It Works

After the restart, open the Dynamic DNS page from the dashboard, add a record, choose its provider, and use `Update Now` to confirm it reaches your provider.

Next: [Adding Records](Records.md).
