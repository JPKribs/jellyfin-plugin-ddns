# Contributing

How to add and document parts of the Dynamic DNS plugin. Follow this so every file reads the same way.

## Naming

Reuse the shared components from the JPKribs.Jellyfin.Base package before adding anything custom. Those carry the `jpk` prefix and take priority, so reach for a base card, selector, collapsible, or status text rather than building a new element. Add a custom element only when nothing in the base covers it.

When something custom does need to exist, it uses the `ddns` prefix.

* CSS classes: `ddns-` kebab case. A component is `ddns-name`, its parts are `ddns-name-part`, its states are plain modifiers like `.active` or `.collapsed`.
* CSS variables: `--ddns-name`.
* JavaScript exports: camelCase function names.
* C# types: PascalCase in the `Jellyfin.Plugin.DynamicDns` namespace.

The only exception is a selector that styles Jellyfin's own markup. Those must match Jellyfin's class name and stay unprefixed, for example `sectionTitle`, `inputContainer`, `selectContainer`, `checkboxContainer`, and `emby-*`.

## Layout

```
API/            controller endpoints for the dashboard
Configuration/  html and js pages plus the shared asset hooks
Models/         data types and the configuration object
Providers/      one class per provider plus the shared base
Services/       detection, lookup, the update run, the scheduled task, secret protection
```

Keep a type in its own file named after it. Providers are discovered by scanning the assembly, so a new one needs no wiring. Add the value to `DnsProviderKind`, add a `{Kind}Provider` class deriving from `DnsProviderBase`, and add its fields and hint in `Configuration/ddns_domains.js`.

## How to document

### CSS

Above each component selector, add a line describing the format. Underneath, add a simple usage example for this element.

```css
/* Red helper text shown under a field when a value needs attention */
/* Example: <div class="ddns-warning">message</div> */
.ddns-warning { ... }
```

Line one describes what the style looks like. Line two is one example usage.

### JavaScript

Above each function, a short summary of what it does and, when it matters, why. Put the comment above the code, not beside it.

```js
// Credential inputs are write only. Clear the field and show a placeholder when a secret is stored, so
// the value never reaches the browser.
function setSecretField(id, stored) { ... }
```

Do not comment trivial getters or one line passthroughs.

### HTML templates

A single line comment naming each object where it starts.

```html
<!-- detection warning -->
<div id="detectionWarning" class="fieldDescription"></div>
```

### C#

A standard XML doc on every public type and member. Please try to keep the summary to two sentences or fewer, followed by every `param` and a `returns` when the member returns a value.

```csharp
/// <summary>
/// Resolves a hostname's current DNS addresses so the update cycle can compare them against the detected IP.
/// </summary>
/// <param name="hostname">The hostname to resolve.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The resolved addresses, or a failed resolution when the lookup did not answer.</returns>
public Task<DnsResolution> ResolveAsync(string hostname, CancellationToken cancellationToken) { ... }
```

For logic that results in a logic branch or isn't readily obvious, add a short line above it explaining why, not what.
