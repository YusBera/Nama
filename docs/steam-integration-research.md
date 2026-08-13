# Steam integration research

Research date: 2026-08-13

## Decision

Nama should keep direct `shortcuts.vdf` integration as its safe default. It is the
established approach used by projects such as Steam ROM Manager and other shortcut
managers, works without modifying Steam, and can be made predictable with atomic writes,
backups, unknown-field preservation, and a strict stopped-Steam requirement.

Nama must not edit `shortcuts.vdf` while Steam is running. Steam keeps the document in
memory and can replace an external edit when it exits. A message saying to restart after
the write is insufficient because the data may already have been lost. Every public
Nama write boundary now rejects the operation while Steam is running and re-checks just
before the final file mutation.

## App IDs

For a new shortcut written through the VDF backend, Nama uses the conventional
deterministic candidate:

```text
crc32(quoted executable + display name) | 0x80000000
```

This is useful for choosing artwork filenames before the entry exists. It is not a
universal promise about every Steam creation path. Steam's private live
`SteamClient.Apps.AddShortcut` API returns the ID assigned by the client. Therefore:

- a new VDF entry may start with Nama's deterministic ID;
- an ID read from Steam or returned by a Steam API is authoritative;
- updates and renames must preserve that ID;
- artwork must be keyed to the preserved ID before it is written.

## Live integration options

The modern Steam UI exposes a private JavaScript bridge in its privileged web context.
Observed methods include `SteamClient.Apps.AddShortcut`, setters for name, executable,
start directory, launch options and icon, custom artwork, and shortcut removal.
`AddShortcut` returns an app ID. The interface is documented by community type definitions
and exercised by Steam Deck plugins, but it is not a supported public Valve API.

Possible ways to reach it are:

1. A Steam-side plugin using Millennium (desktop) or Decky (Steam Deck). This is the
   cleanest optional live path, but requires the user to install a client modification.
2. Chromium DevTools Protocol against Steam's `SharedJSContext`, after enabling CEF
   remote debugging. PortProtonQt demonstrates this class of approach. It exposes a
   powerful debugging endpoint and relies on private UI internals, so Nama should not
   silently enable it or make it the default.
3. DLL injection, reverse-engineered native IPC, or binary patching. These create the
   largest security, anti-virus, maintenance, and terms-of-service surface and are out of
   scope.

The Steamworks SDK does not solve local library management. It is an SDK for a publisher's
own Steam application and account-authorized Steam features, not a public API for adding
arbitrary shortcuts to the desktop client. SteamKit2 similarly implements Steam network
protocols rather than the local modern-client shortcut UI.

## Recommended evolution

Keep a backend boundary so Nama can eventually support:

- `VdfShortcutBackend`: default, supported by Nama, Steam must be stopped;
- `SteamPluginShortcutBackend`: optional live updates through an explicitly installed
  and version-matched Millennium/Decky companion;
- no automatic CEF-debugging or injection backend.

The plugin backend should treat the ID returned by `AddShortcut` as authoritative and
feature-detect every private method because Steam updates can change the interface.

## Sources

- [Steam Homebrew Apps interface](https://docs.steambrew.app/plugins/ts/client/src/interfaces/Apps)
- [decky-romm-sync: Steam non-Steam shortcuts](https://danielcopper.github.io/decky-romm-sync/architecture/steam-non-steam-shortcuts/)
- [PortProtonQt Steam CEF integration commit](https://git.linux-gaming.ru/Boria138/PortProtonQt/commit/279f7ec36b3edb22c8fb5dd9ec3f7a595c17e893)
- [Millennium](https://github.com/SteamClientHomebrew/Millennium)
- [Steam ROM Manager](https://github.com/SteamGridDB/steam-rom-manager)
- [Steam Shortcut Manager](https://github.com/ShadowBlip/steam-shortcut-manager)
- [Steamworks API overview](https://partner.steamgames.com/doc/sdk/api)
