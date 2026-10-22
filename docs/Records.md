# The DDNS Record

The **record** is the DNS name the plugin keeps pointed at your server, such as `media.example.com`. It is in
step 2, **Address**, of the **Configuration** tab. A server keeps one record, and its name is also the address the
proxy answers on. Every field, with its default, is in the [Settings Reference](Settings.md#ddns-record).

## Set up the record

1. In step 2, **Address**, of the **Configuration** tab, pick your DNS company in **Provider**. The note under it
   says what each field means for that provider, and only the fields it uses are shown.
   [Providers](providers/README.md) has a page for each one.
2. Enter the **Hostname** and the credentials the provider asks for.
3. Select **Save** at the bottom of the page.

![Record editor](screenshots/Record%20Editor.png)

The first update runs within 15 minutes. To run it now, select **Update Now** under the status card.

![Provider picker](screenshots/Provider%20Picker.png)

A server that kept several records from an older version shows the first enabled record. The others are removed
the next time you save, and the page says how many.

## Credentials

Credentials are encrypted when saved, and never sent back to the browser. A saved one shows as **Saved, leave
blank to keep**. Leaving the field blank keeps it, and **Clear saved value** under the field removes it. Use the
most limited token your provider offers. See [Security & Storage](Security.md).

Changing the record's **Provider** or **Server override** clears its saved credentials, so a saved password is
never sent somewhere new. Enter them again before you save.

## Finer settings

The rest of the record is under **Advanced → Dynamic DNS**, in the **Record** group: pausing it, IPv4 and IPv6,
**Server override**, **TTL**, **Treat as proxied address**, the DynDNS family options and **Remove Record**.
**Other addresses** under **Advanced → Addresses** lists more names the proxy answers on, for names whose DNS you
keep some other way. The [Settings Reference](Settings.md#ddns-record) describes each one.

![Advanced options for DynDNS v2](screenshots/Advanced%20Options.png)

## Records behind a CDN or proxy

If DNS for a name returns someone else's address, such as Cloudflare's when its orange cloud is on, the plugin
cannot tell whether your record is current. Turn on **Treat as proxied address**. The plugin then compares against
the last address it sent instead of looking the name up. Pair it with **Force update**, so the record is sent again
now and then in case it was changed elsewhere.

## How your public IP is found

* **IPv4 Detection URLs** and **IPv6 Detection URLs** under **Advanced → Dynamic DNS** list services that reply
  with your address. Leave them empty to use the built-in list. Only `https` addresses are used, since a tampered
  reply could point your DNS somewhere else.
* Two services must agree on the address before it is used. If only one answers, its answer is used. If services
  answer with different addresses and no two agree, nothing is published that run.
* If the only address found is a private one, the server most likely has no public IP of its own. Step 2 warns
  and nothing is published. Turn off **Skip update if internal** to publish such an address anyway.

![Advanced, Dynamic DNS](screenshots/Dynamic%20DNS.png)

## How updates run

A scheduled task named **Update DDNS** runs when Jellyfin starts and every 15 minutes after. Change how often under
**Dashboard → Scheduled Tasks**. Each run:

1. Finds your public IP.
2. Looks up what the record's name points at right now.
3. Sends the new address to the provider only when the name points somewhere else, or no longer resolves at all.

So a run where nothing changed sends nothing. When a name does not resolve, such as after its record was deleted
at the provider, the update is sent anyway, so a provider that creates records on update puts it back. The status
card in step 2 shows what the last run did and when. See [Record status](Dashboard.md#record-status).

![Last update status](screenshots/Last%20Update.png)

!!! tip "Run it less often than your record's TTL"
    DNS answers can be cached for as long as the record's TTL. A run before the change has spread may still see
    the old address and send the update again for nothing.

### Force update

Some providers drop a record that is not refreshed for a while. **Force update** sends the current address again
once that long has passed since the last successful update, even when DNS already matches. It is off by default.

### When a record keeps failing

A record that keeps failing is paused, so a mistake such as a wrong token does not keep hitting your provider and
risk a ban. **Back off after failures** and **Back off duration** set when and for how long. The defaults are 3
failures in a row and 24 hours. The status card says when the record is paused and when it will be tried next.

Only failures that point at the record itself count: rejected credentials, a request the provider refuses, or an
error in its reply. Timeouts, network errors, rate limits and provider outages do not. After as many of those in a
row, the record pauses briefly instead, starting at 15 minutes and doubling up to 2 hours. A problem that never
clears can look temporary too, such as a mistyped server, so once the 2 hour pause has been reached three times
the record takes the full pause.

A pause ends at once when your public IP changes, so it never holds back a new address. Editing the record's
provider, name, zone, server or credentials also starts it over.

### Names that resolve differently at home

Some home networks answer a name with a local address, through the router, an ad-blocking DNS server or a hosts
file. The plugin then cannot see your public record. It notices and compares against the last address it sent
instead, so an unchanged address is not sent every run. **Treat as proxied address** makes it always work that way.
