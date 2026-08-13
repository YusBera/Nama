# Nama

> I have a game on my computer. Make it a proper Steam library entry.

Nama is a small Windows utility that takes a local game executable or folder, works out
what game it is, lets you pick artwork, and adds it to Steam as a Non-Steam Game with
proper library art.

```
Right-click EXE → Nama → Confirm game → Pick artwork → Add to Steam
```

**Status:** early but functional. The MVP flow works end to end — identification,
artwork selection, shortcut creation, duplicate handling and artwork application have all
been exercised against the live Steam, SteamGridDB and VNDB services and against a real
`shortcuts.vdf`. Requires Windows 10/11 and the .NET 10 runtime.

## The flow

1. Point Nama at a game (context menu, drag and drop, or a file picker).
2. Nama reads name hints from the executable, its folder, sibling files and the
   executable's version resources.
3. It normalizes the name — stripping repack tags, versions, scene groups and noise.
4. It searches Steam, SteamGridDB and VNDB in parallel and ranks the matches.
5. You confirm the game.
6. Artwork from every enabled provider — including the icon read out of the game's own
   executable — is merged into one picker, five recommended results per category,
   `Show more ↓` to see the rest.
7. You optionally edit the Steam display name.
8. Nama writes the shortcut and the artwork, and tells you if Steam needs a restart.

## Name normalization

Identification quality is bounded by this step, so it is treated as a feature rather
than a helper:

| Raw | Normalized |
| --- | --- |
| `ELDEN-RING-v1.12.2-FITGIRL` | Elden Ring |
| `White_Album_2_Closing_Chapter` | White Album 2 Closing Chapter |
| `SubaHibiEN.exe` | Subarashiki Hibi |
| `Cyberpunk.2077.v2.1.REPACK-DODI` | Cyberpunk 2077 |
| `EldenRing-Win64-Shipping` | Elden Ring |
| `Danganronpa_V3_Killing_Harmony-CODEX` | Danganronpa V3 Killing Harmony |
| `There Is No Game` | There Is No Game |

Every stage is kept — raw input, normalized title, ranked candidate list, and the tokens
that were removed — because identification searches the candidates in order and the UI
shows you what it detected. Japanese titles are preserved rather than transliterated, and
matching is fuzzy (Jaro-Winkler plus a weighted token-set score) rather than exact.

Words like `Game`, `HD`, `Complete` and `Remastered` describe packaging *and* appear in
real titles, so they are kept in the canonical name and only removed to build an
alternate search candidate — `There Is No Game` stays intact while `There Is No` is still
searched as a fallback. Likewise a bare `V3` mid-title is a title word, not a build
number; only dotted or trailing versions are stripped.

Nama never renames your files. Normalization is for identification and display only.

## Providers

Providers sit behind `IGameProvider` and `IArtworkProvider`. Nothing in the UI or the
identification core knows which ones exist.

| Provider | Games | Artwork | Credentials |
| --- | --- | --- | --- |
| Game files (local) | — | icon | none |
| Steam | yes | grid, cover, hero, logo, background | none |
| SteamGridDB | yes | grid, cover, hero, logo, icon | free API key |
| VNDB | yes | cover, background | none |
| IGDB | yes | cover, background | Twitch client id + secret |

Steam and VNDB work with no configuration. SteamGridDB is where most of the artwork
comes from, so adding its free key is the single highest-value setting. A provider that
is unreachable or unconfigured is skipped — it never blocks the others.

**Game files** is the local fallback: it reads the largest icon straight out of the
executable's PE resources, so the Icon slot is filled instantly, offline, and without any
API key. That matters most for Japanese visual novels, which frequently have no
SteamGridDB entry at all. The parser is pure managed code with no Win32 interop, and it
is fuzz-tested to fail cleanly rather than throw on a malformed executable.

Grid, Hero, Logo and Icon are **Steam-invented formats**. No game database holds them,
because publishers never produced them — only SteamGridDB does, since it is a community
that makes them by hand. Adding more metadata databases will not fill those slots.

## Visual design

Nama borrows the Steam client's visual language so it reads as a companion to Steam
rather than a foreign window: a desaturated blue-grey surface stack (`#1b2838` pages on
`#171a21` chrome), one light-blue accent (`#66c0f4`) for links, focus and selection,
near-square 2-3px corners, small letter-spaced uppercase section labels, and a green
button reserved for the single committing action.

Nothing is taken from Valve. Colour values and layout conventions are not protectable;
the things that are — the Steam logo, wordmark, icons, and the proprietary **Motiva Sans**
typeface — are deliberately absent. Type falls back to the system UI font, exactly as
Steam itself does when Motiva Sans is unavailable. Settings carries an explicit notice
that Nama is unofficial and unaffiliated with Valve Corporation.

## Steam integration

All Steam knowledge lives in `Nama.SteamIntegration`; the rest of the app never touches a
VDF file. Nama:

- finds Steam via the registry, falling back to the usual install paths;
- picks the most recently used local account;
- reads and writes `shortcuts.vdf` (Valve's binary KeyValues format) atomically, keeping
  a one-generation backup and preserving fields written by other tools;
- derives the shortcut app id the same way Steam does — `crc32(exe + name) | 0x80000000` —
  so artwork can be written under the exact filenames Steam looks for;
- detects an existing entry by target, app id or name and asks what to do rather than
  ever creating a duplicate silently.

Artwork is written to `userdata\<user>\config\grid\` as `<id>.png`, `<id>p.png`,
`<id>_hero.png`, `<id>_logo.png` and `<id>_icon.png`.

**Steam holds `shortcuts.vdf` in memory and rewrites it on exit.** If Steam is running
when you add a game, Nama tells you to restart it.

## Building

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build
dotnet test
dotnet run --project src/Nama.App
```

Launch straight into identification with a path:

```powershell
dotnet run --project src/Nama.App -- "D:\Games\Elden Ring\eldenring.exe"
```

Publish a self-contained executable:

```powershell
dotnet publish src/Nama.App -c Release -r win-x64 --self-contained
```

## Explorer integration

Settings → **Add to right-click menu** writes three `HKEY_CURRENT_USER` verbs (executables,
folders, and folder backgrounds). No elevation is needed and nothing outside your user
account is touched. On Windows 11 the entry lives under **Show more options** (Shift+F10),
because that is where Windows puts third-party verbs.

## Project layout

```
src/
  Nama.Core               models, normalization, identification, provider interfaces
  Nama.Providers          Steam, SteamGridDB, VNDB, IGDB, local PE icon reader
  Nama.SteamIntegration   binary/text VDF, app id derivation, SteamManager
  Nama.Storage            settings, search cache, image cache
  Nama.App                WPF UI (views, view models, Explorer integration)
tests/
  Nama.Tests              131 tests over normalization, matching, VDF, Steam writes,
                          local metadata extraction and PE icon parsing
```

`Nama.Core` has no dependency on any provider, on Steam, or on WPF, so identification and
ranking are testable without a network or a Steam install.

## Scope

Nama is a utility, not a library manager. It deliberately does not do game launching,
cloud sync, file organization, mod management, or automatic artwork selection without
confirmation.

## Known limitations

- Artwork is applied for the detected Steam account only; multi-account selection is not
  exposed in the UI yet.
- IGDB is implemented but off by default, since it needs Twitch credentials.
- Animated (webm/apng) SteamGridDB artwork is ranked down because Steam does not render
  it for non-Steam shortcuts.
- Grid and Hero still depend on SteamGridDB or a Steam store page; they are not yet
  synthesized from a cover plus a screenshot.
- Logo is intentionally left empty when no provider has one — it needs real transparency,
  and Steam falls back to drawing the game's name over the hero.
- ErogameScape is not wired up; it holds no artwork, though its metadata and furigana
  would improve Japanese title matching.
- The window caption buttons expose no UI Automation names, so screen readers cannot
  identify them. Mouse and keyboard use is unaffected.
- No license file yet, so default copyright applies.

---

Nama is an independent, unofficial tool. It is not affiliated with, endorsed by, or
sponsored by Valve Corporation. Steam is a trademark of Valve Corporation. Nama ships no
Valve logos, icons, wordmarks or fonts; its interface deliberately follows Steam's
unprotectable colour and layout conventions only.
