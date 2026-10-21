# Dashboard

The **Dashboard** tab shows who reaches your server through Remote Access, where from, and what was turned away.
It is the page **Plugins → Remote Access** opens on, and it refreshes itself every 30 seconds.

![Dashboard tab](screenshots/Dashboard.png)

## Locations

A world map and a table of countries.

* **Users**, **Failures** and **Blocked** above the map choose what it shows. All three are on when the page
  opens, and at least one always stays on.
* On the map, countries with users are blue, countries with failed sign-ins red, and countries with only blocked
  visitors purple. The more activity, the stronger the colour.
* The table has a row per country, with the number of users, failed sign-ins and blocked visitors, and when the
  country was last seen.

## Banned

Addresses banned for failed sign-ins: the address, its country, how many failures, when it was banned and when the
ban ends. **Unban** lifts it straight away, together with any wider ban on the same IPv6 network.

How many failures ban an address, and for how long, is set under **Advanced → Blocking**. See
[Protection](RemoteAccess.md#protection).

## Activity

Every address that reached the proxy, newest first, up to the latest 500. Filter it by **Type** and **Country**.

| Type | Meaning |
|---|---|
| **User** | Signed in, with the user and device. |
| **Failure** | A sign-in that failed. |
| **Blocked** | Turned away by a ban, the block list or the country rules. |
| **Seen** | Reached the sign-in page and did nothing else. |

Each row shows the type, user, address, device, the address it asked for, its country and when it was last seen.
With **Location** set to **Country and city**, an **Area** column adds the city.
Visitors from your own network show as **Local**. IPv6 visitors are grouped by their /64, since one device can use
many addresses in it.

The button at the end of a row acts on it:

* **Block** adds the address to the block list. Your own network and trusted networks cannot be blocked.
* **Unblock** removes it. An address blocked by a range under **Advanced** is unblocked there instead.
* **Unblock** on a country turned away by the country rules allows that country from then on.

## What is recorded

History is on by default and kept for 90 days. Change it under **Advanced → History**:

* **Recording** turns it off. The Dashboard then stays empty, but bans and blocks still work.
* **History period** is how many days are kept.
* **Location** chooses country, country and city, or none.
* **Clear History** deletes everything recorded. The block list is kept.

Locations come from the free [DB-IP](https://db-ip.com) database, downloaded to this server. Addresses are looked up
on the server itself and never sent anywhere. The history is kept in `people.db`, see [Storage](Storage.md).
