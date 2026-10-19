# How Updates Run

The scheduled task named `Update Dynamic DNS` runs every 15 minutes and on startup by default, but you can set the interval under `Scheduled Tasks`. On each run the plugin detects the public IP, looks up what each hostname currently resolves to in DNS, and pushes the detected IP to any record whose DNS does not already serve it. Each record shows its last action, updated, unchanged, or failed, and when it was last checked.

The status card at the bottom of each record shows the result of its last run.

![Last update status](screenshots/Last%20Update.png)

## Force Update

If you want a record refreshed even when DNS already matches, choose an interval under `Force Update` in the `Update Behavior` section of the `Address` tab. When that period has passed since the last successful push, the plugin re-sends the current IP, which keeps the record alive on providers that age out stale entries. `Force Update` is `Off` by default, so records are pushed only when DNS does not already serve your detected IP.

## Backoff

If a record keeps failing, for example because a token is wrong, the plugin pauses it after a set number of failures in a row and then retries it only once each backoff window, so a misconfiguration does not keep spamming your provider and risk a rate limit or ban. Both the failure count and the window are set under `Update Behavior` and default to three failures and twenty four hours. The record card shows when a record is backing off and when it will next be tried.

Only failures that point at the record itself count toward that pause: rejected credentials, a request the provider refuses, or an error the provider reports in its reply. Temporary failures (a timeout, a network error, a rate limit, or a provider server error) never count toward it. After the same number of temporary failures in a row the record instead pauses briefly, starting at fifteen minutes and doubling up to two hours. A failure that never clears can look temporary too (a mistyped server override, a provider host that no longer resolves), so once the two hour pause has been reached three times the record takes the full backoff window. Either pause is lifted as soon as your public IP changes, so a paused record never holds back a new address.

Editing a record's provider, hostname, zone, server, or credentials starts its status over, clearing any pause and the last pushed address.

!!! tip "Set the scheduled interval longer than your record TTL"
    If a run happens before the change has propagated, your resolver may still answer with the old address and the record can be pushed again needlessly.

## Split Horizon DNS

If your server resolves its own hostname through a local resolver that answers with an internal address, which is common in a split horizon setup using a router or an ad blocking resolver or a hosts file, the live DNS check cannot see your public record. The plugin handles this by comparing the IP against the last IP it pushed instead of that answer, so an unchanged IP is not re-pushed every run.

You can still turn on `Treat as Proxied Address` to force that comparison for a record at all times (E.G. Cloudflare's Proxy).

Next: [Monitoring](Monitoring.md).
