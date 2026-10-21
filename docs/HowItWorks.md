# How Updates Run

A scheduled task named **Update Dynamic DNS** runs when Jellyfin starts and every 15 minutes after. Change how
often under **Dashboard → Scheduled Tasks**. Each run:

1. Finds your public IP.
2. Looks up what each record's name points at right now.
3. Sends the new address to the provider only for records that point somewhere else.

So a run where nothing changed sends nothing. The status card at the bottom of each record shows what its last run
did and when.

![Last update status](screenshots/Last%20Update.png)

!!! tip "Run it less often than your record's TTL"
    DNS answers can be cached for as long as the record's TTL. A run before the change has spread may still see
    the old address and send the update again for nothing.

## Force update

Some providers drop a record that is not refreshed for a while. **Force update**, under **Update Behavior** on the
**Address** tab, sends the current address again once that long has passed since the last successful update,
even when DNS already matches. It is off by default.

## When a record keeps failing

A record that keeps failing is paused, so a mistake such as a wrong token does not keep hitting your provider and
risk a ban. **Back off after failures** and **Back off duration**, under **Update Behavior**, set when and for how
long. The defaults are 3 failures in a row and 24 hours. The record's status card says when it is paused and when
it will be tried next.

Only failures that point at the record itself count: rejected credentials, a request the provider refuses, or an
error in its reply. Timeouts, network errors, rate limits and provider outages do not. After as many of those in a
row, the record pauses briefly instead, starting at 15 minutes and doubling up to 2 hours. A problem that never
clears can look temporary too, such as a mistyped server, so once the 2 hour pause has been reached three times
the record takes the full pause.

A pause ends at once when your public IP changes, so it never holds back a new address. Editing a record's
provider, name, zone, server or credentials also starts it over.

## Names that resolve differently at home

Some home networks answer a name with a local address, through the router, an ad-blocking DNS server or a hosts
file. The plugin then cannot see your public record. It notices and compares against the last address it sent
instead, so an unchanged address is not sent every run. **Treat as proxied address** on a record makes it always
work that way.

Next: [Monitoring](Monitoring.md).
