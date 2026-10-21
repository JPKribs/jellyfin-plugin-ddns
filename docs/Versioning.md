# Versioning

Releases use a four part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature and bug count:

```
12.2.3.0
└──┘ └┬┘
 │    └── 3 = Plugin feature release
 │        0 = Plugin bug/patch release within that feature
 │
 └─── 12.2 = Jellyfin version this build was tested/released for
```

Targets **Jellyfin 12.2.x** on `net10.0` with ABI `12.2.0.0`.
