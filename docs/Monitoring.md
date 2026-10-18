# Monitoring

## Record Health

The `Domains` tab summarizes every record at a glance.

* **Healthy**: The last update succeeded within the last 24 hours.
* **Unhealthy**: A published record has not updated successfully in the last 24 hours.
* **Unpublished**: The record has never run an update yet.

## Activity Log

Notable events are written to Jellyfin's activity log under **Dashboard → Activity**, so you can see what the plugin has done without digging through the server log.

* **Record updated**: A record was pushed to its provider, with the new address.
* **Update failed**: A push was attempted and the provider rejected it or could not be reached.
* **Updates paused**: A record entered backoff after repeated failures, with one entry per streak.
* **Detection unhealthy**: No public IP could be detected for about an hour, with a recovery entry when detection returns.
* **Configuration changes**: A record was added or removed, or its credentials were changed.
* **Plaintext credentials**: Credential encryption is unavailable on the host, logged once per outage.

A run where every record is already current writes nothing, so a quiet server keeps a quiet activity feed.
