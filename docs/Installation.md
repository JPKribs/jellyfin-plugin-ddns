# Installation

The plugin needs **Jellyfin 12.2**. Servers on Jellyfin 10.11, 12.0 or 12.1 are offered an earlier version
without Remote Access.

1. In Jellyfin, open **Dashboard > Plugins > Repositories**, select **Add Repository**, enter this URL and select
   **Save**:

    ```
    https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-remoteaccess/master/manifest.json
    ```

2. Open the **Catalog** tab, find **Dynamic DNS** and select **Install**. The catalog still lists it under its
   original name.
3. Restart Jellyfin when it asks, and wait until it is fully back up.

The plugin then has a **Remote Access** entry in the dashboard's side menu, with the tabs **Dashboard**,
**Configuration** and **Advanced**. It opens on **Dashboard**. Next, set up [the DDNS record](Records.md), or follow the
[Beginner's Guide](BeginnersGuide.md) for the whole setup.
