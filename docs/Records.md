# The DDNS Record

The **record** is the DNS name the plugin keeps pointed at your server, such as `media.example.com`. It is in
step 2, **Address**, of the **Configuration** tab. A server keeps one record, and its name is also the address the
proxy answers on.

## Set up the record

1. In step 2, **Address**, of the **Configuration** tab, pick your DNS company in **Provider**. The note under it
   says what each field means for that provider, and only the fields it uses are shown.
   [Providers](providers/README.md) has a page for each one.
2. Enter the **Hostname** and the credentials the provider asks for.
3. Select **Save** at the bottom of the page.

![Record editor](screenshots/Record%20Editor.png)

The first update runs within 15 minutes. To run it now, select **Update Now** under the status card. The card
shows the public IP, when the record was last updated, and how it went.

![Provider picker](screenshots/Provider%20Picker.png)

A server that kept several records from an older version shows the first enabled record. The others are removed
the next time you save, and the page says how many.

## Credentials

Credentials are encrypted when saved, and never sent back to the browser. A saved one shows as **Saved, leave
blank to keep**. Leaving the field blank keeps it, and **Clear saved value** under the field removes it. Use the
most limited token your provider offers. See [Security](Security.md).

Changing the record's **Provider** or **Server override** clears its saved credentials, so a saved password is
never sent somewhere new. Enter them again before you save.

## Finer settings

**Advanced → Dynamic DNS**, under **Record**, holds the rest of the record:

* **Enabled**: turn it off to pause updates without removing the record.
* **Update IPv4** and **Update IPv6**: IPv4 keeps the A record current, IPv6 the AAAA record.
* **Server override** and **TTL**, for providers that take them.
* **Treat as proxied address**, described below.
* For the DynDNS family, a wildcard record, a static DNS service and mail settings.
* **Remove Record** deletes the record and takes its name off the proxy's addresses.

**Other addresses** under **Advanced → Addresses** lists more names the proxy answers on, one per line, for names
whose DNS you keep some other way.

![Advanced options for DynDNS v2](screenshots/Advanced%20Options.png)

## Records behind a CDN or proxy

If DNS for a name returns someone else's address, such as Cloudflare's when its orange cloud is on, the plugin
cannot tell whether your record is current. Turn on **Treat as proxied address** under **Advanced → Dynamic DNS**.
The plugin then compares against the last address it sent instead of looking the name up. Pair it with **Force
update** in the same section, so the record is sent again now and then in case it was changed elsewhere.

## How your public IP is found

Step 2 of the **Configuration** tab shows the public IP the server was last seen at. How it is found is set under
**Advanced → Dynamic DNS**:

* **IPv4 Detection URLs** and **IPv6 Detection URLs** list services that reply with your address, one per line.
  Leave them empty to use the built-in list. Only `https` addresses are used, since a tampered reply could point
  your DNS somewhere else.
* Two services must agree on the address before it is used. If only one answers, its answer is used. If services
  answer with different addresses and no two agree, nothing is published that run.
* If the only address found is a private one, the server most likely has no public IP of its own. Step 2 warns
  and nothing is published. Turn off **Skip update if internal** to publish such an address anyway.

![Advanced, Dynamic DNS](screenshots/Dynamic%20DNS.png)

Next: [How Updates Run](HowItWorks.md).
