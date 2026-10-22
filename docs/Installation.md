# Installation

The plugin needs **Jellyfin 12.2**. Servers on Jellyfin 10.11, 12.0 or 12.1 are offered an earlier version
without Remote Access.

## Add the repository

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Select **Add Repository** and enter this URL:

    ```
    https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-remoteaccess/master/manifest.json
    ```

3. Select **Save**.

## Install the plugin

1. Open the **Catalog** tab under **Plugins**.
2. Find **Dynamic DNS** and select **Install**. The catalog still lists it under its original name.
3. When it says to, restart Jellyfin, and wait until it is fully back up.

## Find it

After the restart, the plugin has a **Remote Access** entry in the dashboard's side menu. It opens on the
**Dashboard** tab. The other tabs are **Configuration** and **Advanced**.

Next: [The DDNS Record](Records.md).
