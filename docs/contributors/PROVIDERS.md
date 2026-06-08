# Creating a provider

How to add a new DNS provider to the plugin and document it. Follow it exactly so every provider reads the
same way. The providers are ports of ddclient's protocols, so the source of truth for behavior is the
matching `nic_{name}_update` in ddclient.

## The pieces

A provider is one C# class plus two small touch points. Nothing else is wired by hand, since providers are
discovered by scanning the assembly at startup.

```
Models/DNSProviderKind.cs                      the enum value for the provider
Providers/IDNSProvider.cs                      the contract (Kind plus UpdateAsync)
Providers/DNSProviderBase.cs                   the shared base with the helpers below
Providers/Implementations/{Kind}Provider.cs    the provider itself, including its label, hint, and fields
docs/providers/{Kind}.md                       the user facing setup doc
```

The dashboard reads the provider list, hints, and fields from the `Providers` endpoint, which is built from
each provider class, so the JS needs no per provider edit.

## Steps

1. Add the value to `DNSProviderKind` in `Models/`. The name you choose is the provider's identity: the
   class must be `{Kind}Provider` and the reflection in `DNSProviderKindExtensions` resolves the class from
   the enum name, so they must match.
2. Add `Providers/Implementations/{Kind}Provider.cs` deriving from `DNSProviderBase`. Override `Kind` to
   return your enum value and implement `UpdateAsync`. Put the per family request logic behind
   `ApplyPerFamilyAsync` so IPv4 and IPv6 are handled the same way.
3. Map the record fields to the provider's credentials in the class summary (see below), and read them from
   the `DNSRecord` passed to `UpdateAsync`.
4. Override `Label`, `Hint`, and `Fields` on the provider. `Label` is the friendly name in the dropdown,
   `Hint` is the one line note under it, and `Fields` is the label for each used field with a null for any
   field the provider does not use. The dashboard renders itself from these, so no JS edit is needed.
5. Add `docs/providers/{Kind}.md` describing where the user gets each field.
6. Add a test under `Jellyfin.Plugin.DynamicDns.Tests` that exercises the request shape and the success and
   failure detection, following the existing provider tests.

## Base class helpers

`DNSProviderBase` carries everything a provider needs, so a provider stays small and consistent.

* `ApplyPerFamilyAsync(record, ip, updater)`: runs your per family `updater` for each enabled and detected
  family, then folds the per family results into one `DNSUpdateResult`. Use it rather than branching on
  IPv4 and IPv6 by hand.
* `ServerBase(record, defaultServer)`: returns the record's `Server` override when set, otherwise the
  default. Use it so every provider honors a `Server` override the same way.
* `SendAsync(...)`: sends the HTTP request over Jellyfin's client and never throws on a network failure,
  returning an `HttpResult` with the status and body. It logs a redacted warning on failure.
* `FirstToken(body)` and `FirstLine(body)`: parse a plain text reply, used for the dyndns style protocols
  whose body is a short status line such as `good` or `nochg`.
* `Redact(url)`: strips credentials from a URL before it is logged.

## Documenting the field mapping

The class summary is the canonical record of what each field means, so write it precisely. Name the
ddclient protocol it ports, then state what `Login`, `Password`, `Zone`, and `Server` map to. The user
setup doc and the dashboard hint are derived from this.

```csharp
/// <summary>
/// Example (port of ddclient's <c>nic_example_update</c>). <see cref="DNSRecord.Login"/> is the API key id
/// and <see cref="DNSRecord.Password"/> is the secret. <see cref="DNSRecord.Zone"/> is the domain and
/// <see cref="DNSRecord.Server"/> overrides the default endpoint.
/// </summary>
```

## The user setup doc

Every provider has a `docs/providers/{Kind}.md`. Keep it to the fields the provider actually reads, one
line each, in the form `Field: where to get the value`. Note any field that is not what its name suggests,
for example a hostname field that actually takes a numeric record id. When a provider proxies the record so
its public DNS hides the origin, like Cloudflare's orange cloud, tell the user to turn on Treat as proxied
address on the record so the plugin compares against the last pushed IP instead of DNS.

## C# documentation

A standard XML doc on every public type and member, the summary two sentences or fewer, followed by every
`param` and a `returns` when the member returns a value. For a branch or a non obvious step, add a short
line above it explaining why, not what. Prose carries no dashes or semicolons.
