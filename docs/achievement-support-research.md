# Achievement support research

Research date: 2026-08-13

Status: exploratory reference for a possible separate project. Achievement tracking is
not part of Nama's immediate release scope.

## Short answer

Nama can aggregate achievement catalogs and progress and can show its own local
notifications. It cannot generically detect achievements from an arbitrary DRM-free game,
and it cannot convert a GOG unlock into an official Steam-profile achievement.

For the specific case where a user owns the GOG version, deletes GOG Galaxy, keeps only
the game files, and adds the executable to Steam through Nama: the game will usually keep
running, but its GOG achievement service is unavailable. There is no Windows-wide
"achievement unlocked" event for Nama to receive. Automatic detection is possible only
when that particular game writes readable achievement state to a save, log, database, or
other documented local file.

## Why the Steam copy on the store does not help

A non-Steam shortcut is not the store game's Steam AppID and has no Steam achievement
schema. The Steamworks API requires a known AppID, a running Steam client, and a license
for that AppID on the active account. `SetAchievement` operates on the current published
AppID's schema and `StoreStats` persists it.

The Web API can read a game's schema and a user's public/authorized progress. Writing
another game's stats requires the publisher authentication key that owns that AppID and
must be performed by the publisher's secure server. Nama cannot and should not write
official achievements for another publisher.

## What happens to GOG achievements without Galaxy

GOG's SDK asks the game to retrieve the signed-in user's stats, call `SetAchievement`,
then store the result through Galaxy. Offline support means "signed in before losing the
internet," not "Galaxy uninstalled." GOG's operational-state table marks achievements,
stats, and leaderboards unavailable when there is no Galaxy client or no user.

If Galaxy remains installed, running, and signed in, a legitimately owned game launched
externally can use achievements. Nama could then aggregate official progress or watch
Galaxy's local gameplay databases for changes. Existing projects demonstrate database
watching, but this is a provider-specific implementation, not a universal game event.

## Feasible product scope

### Safe first version

- Identify the game's real Steam AppID and/or GOG product ID.
- Fetch achievement names, descriptions, hidden flags, and icons from an authorized or
  public provider.
- Import official historical progress where the user's provider privacy/authentication
  permits it.
- Store and display progress in Nama, clearly labeled by source.
- Support manual local achievements and notifications.

### Provider and game-specific detection

- When GOG Galaxy is present: read or monitor its supported/local gameplay data and sync
official GOG progress.

## Steam compatibility wrappers and "online fixes"

Some unofficial wrappers do connect the game to the real Steam client. That connection
is commonly made under Valve's public Steamworks sample application, Spacewar (AppID
480), to reuse Steam networking, lobbies, invitations, overlay, or presence. Seeing the
user as "in Steam" therefore does not prove that the wrapper is reporting achievements
for the commercial game's real AppID.

Steam achievements are scoped to an AppID and must match API names published in that
AppID's Steamworks configuration. A call made in the AppID 480 context can address only
Spacewar's schema; an achievement key from a different commercial game is not thereby
written to that game's Steam profile. The active account also needs a license for the
actual AppID before the normal Steamworks API initializes in that context.

Wrappers and Steam API emulators may instead intercept the game's `SetAchievement` call
and store the result in a local file. That is how local achievement watchers can show an
unlock without Steam accepting it. This is a technically detectable source, but it is
local emulator state, not an official Steam achievement event.

Nama should keep any future local-detector interface format-neutral and clearly label
such progress as local. It should not install, modify, or depend on unofficial online-fix
DLLs, spoofed AppIDs, client unlockers, or publisher-license bypasses. Besides the legal
and account concerns, those mechanisms are untrusted executable code and can change or
disappear without a stable API.
- For games without a launcher: use opt-in detector manifests for known structured logs,
  JSON/INI state, or stable save-file rules.
- Add specialized providers such as RetroAchievements where an official API exists.

The detector UI must report its confidence and provenance. A save-file detector can say
"detected locally"; it must not imply that Steam or GOG accepted the unlock.

### Do not implement

- Injecting or proxying `Galaxy64.dll` / `steam_api64.dll` to intercept calls.
- Process memory scanning or generic DLL hooks.
- Achievement-emulator or cracked-game data formats as a core integration.
- Any attempt to set another publisher's Steam achievements.

Those approaches are brittle, trigger anti-cheat and antivirus concerns, create a large
security surface, and blur the distinction between a local tracker and official platform
progress.

## Suggested architecture

```text
IAchievementCatalogProvider   definitions, descriptions, icons
IAchievementProgressProvider  official account/provider progress
IAchievementDetector          optional local, explicitly per game or format
```

The three concerns should remain separate. One game may use a Steam catalog, GOG account
progress, and a local save detector, and the UI should show exactly which source supplied
each fact.

## Sources

- [Steamworks API initialization and ownership requirements](https://partner.steamgames.com/doc/sdk/api)
- [Steam ISteamUserStats](https://partner.steamgames.com/doc/api/isteamuserstats)
- [Steam ISteamUserStats Web API](https://partner.steamgames.com/doc/webapi/isteamuserstats)
- [Steamworks Spacewar example application (AppID 480)](https://partner.steamgames.com/doc/sdk/api/example)
- [Steamworks step-by-step achievements guide](https://partner.steamgames.com/doc/features/achievements/ach_guide)
- [GOG SDK statistics and achievements](https://docs.gog.com/sdk-stats-and-achievements/)
- [GOG SDK operational states and feature availability](https://docs.gog.com/sdk-galaxy-feats-and-states/)
- [Playnite Achievements](https://github.com/justin-delano/PlayniteAchievements)
- [Achievements: GOG Galaxy database monitor](https://github.com/PSerban93/Achievements)
