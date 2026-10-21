# Adding Records

A **record** is one DNS name the plugin keeps pointed at your server, such as `media.example.com`. Records are on
the **Domains** tab.

## Add a record

1. On the **Domains** tab, select **New**.
2. Give it a **Name**, for your own reference.
3. Pick your DNS company in **Provider**. The note under it says what each field means for that provider, and only
   the fields it uses are shown. [Providers](providers/README.md) has a page for each one.
4. Fill in the fields and choose **Update IPv4**, **Update IPv6** or both. IPv4 keeps the A record current, IPv6
   the AAAA record.
5. Select **Save Record**.

![Record editor](screenshots/Record%20Editor.png)

The first update runs within 15 minutes. To run it now, select **Update Now** on the **Address** tab. The status
card at the bottom of the record then shows how it went.

![Provider picker](screenshots/Provider%20Picker.png)

## Credentials

Credentials are encrypted when saved, and never sent back to the browser. A saved one shows as dots. Leaving the
field blank keeps it, and **Clear saved value** under the field removes it. Use the most limited token your
provider offers. See [Security](Security.md).

## Advanced options

Providers in the DynDNS family add an **Advanced options** section, for a wildcard record, a static DNS service
and mail settings.

![Advanced options for DynDNS v2](screenshots/Advanced%20Options.png)

## Records behind a CDN or proxy

If DNS for a name returns someone else's address, such as Cloudflare's when its orange cloud is on, the plugin
cannot tell whether your record is current. Turn on **Treat as proxied address** on that record. The plugin then
compares against the last address it sent instead of looking the name up. Pair it with **Force update** on the
**Address** tab, so the record is sent again now and then in case it was changed elsewhere.

## How your public IP is found

The **Address** tab shows the public IP the server was last seen at, and how it is found:

* **IPv4 Detection URLs** and **IPv6 Detection URLs** list services that reply with your address, one per line.
  Leave them empty to use the built-in list. Only `https` addresses are used, since a tampered reply could point
  your DNS somewhere else.
* Two services must agree on the address before it is used. If only one answers, its answer is used.
* If the only address found is a private one, the server most likely has no public IP of its own. The tab warns
  and nothing is published. Turn off **Skip update if internal** to
  publish such an address anyway.

![Address tab](screenshots/Address.png)

Next: [How Updates Run](HowItWorks.md).
