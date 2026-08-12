# Nama

Nama turns a local Windows game into a polished Steam library entry.

Select an executable or game folder, confirm the detected title, choose artwork, and Nama creates or updates a Steam Non-Steam shortcut. It is intentionally a focused utility rather than a full game-library manager.

> Status: early development. Nama modifies Steam's local `shortcuts.vdf`; the write path creates backups and refuses to run while Steam is open, but you should still treat pre-release builds cautiously.

## Features

- Select or drag in a game executable/folder
- Extract title candidates from filenames, folders, nearby files, and executable metadata
- Normalize release names without renaming files
- Fuzzy identification across English, Japanese, romaji, and aliases
- Search Steam and VNDB without user accounts or API keys
- Optional SteamGridDB artwork with a personal API key
- Experimental exact-code DLsite lookup for `RJ`, `VJ`, and `BJ` products
- Combined artwork picker with Cover, Banner, Hero, Logo, and Icon slots
- Import local artwork when providers do not have the game
- Editable Steam display name
- Duplicate detection with update/replace choices
- Safe Steam shortcut writes with backup, atomic replacement, verification, and rollback
- Explorer right-click integration without administrator privileges

## Requirements

- Windows 10 or Windows 11
- Steam desktop client
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) when building from source

## Build and run

```powershell
dotnet build Nama.slnx
dotnet run --project src/Nama.App
```

Start directly with a game path:

```powershell
dotnet run --project src/Nama.App -- "C:\Games\Example\Game.exe"
```

Run the offline test suite:

```powershell
dotnet test tests/Nama.Tests/Nama.Tests.csproj --filter "Category!=Network"
```

Network tests call live provider endpoints and are excluded by the command above.

## Typical flow

1. Select or drop an executable/folder.
2. Confirm the detected game or correct the search.
3. Pick provider artwork or choose local images.
4. Edit the final Steam display name if needed.
5. Add the shortcut. If Steam is running, Nama asks before closing it.

After enabling the Explorer integration in Settings:

```text
Right-click game executable or folder
→ Show more options (Windows 11)
→ Add to Steam with Nama
```

## Artwork and metadata sources

| Source | Authentication | Purpose |
|---|---|---|
| Steam | None | PC game matching and official store artwork |
| VNDB | None | Visual novel titles, aliases, covers, release artwork, and screenshots |
| DLsite | None | Experimental exact-product-code metadata and artwork |
| SteamGridDB | Personal API key | Community grids, heroes, logos, icons, and covers |

DLsite support uses an undocumented public product endpoint. It is deliberately limited to exact product-code lookups, cached locally, and designed to fail without interrupting the rest of the flow.

## Settings and privacy

- Settings and response caches are stored under `%APPDATA%\Nama`.
- The SteamGridDB key is encrypted with Windows DPAPI for the current user.
- Nama does not upload local executables or hash game files for identification.
- Provider searches send normalized title candidates or an exact store product code.
- Experimental DLsite and VNDB integrations can be disabled in Settings.

## Steam write safety

Nama does not use an official API to register arbitrary local games. It updates Steam's per-account Non-Steam shortcut file and artwork directory.

Before writing, Nama:

- refuses to continue while Steam is running;
- detects existing entries instead of silently creating duplicates;
- verifies that the original VDF can round-trip byte-for-byte;
- creates a backup and retains recent backups;
- writes through a temporary file;
- re-reads and verifies the result;
- rolls back automatically when verification fails.

## Project structure

```text
src/
├── Nama.App        WPF desktop UI and Windows integration
├── Nama.Cli        Diagnostic and development CLI
├── Nama.Core       Models, normalization, matching, and aggregation
├── Nama.Providers  Steam, SteamGridDB, VNDB, and DLsite adapters
├── Nama.Steam      VDF parsing, shortcut management, and artwork writes
└── Nama.Storage    Settings and local response cache

tests/Nama.Tests    Offline mapping, behavior, and write-safety tests
```

## Current limitations

- Windows 11 places the registry-based context-menu command under **Show more options**.
- Retro ROM support is not implemented yet because a correct Steam entry also needs emulator/profile configuration.
- SteamGridDB requires users to supply their own API key.
- There are no packaged releases yet; build from source.

## Contributing

Issues and focused pull requests are welcome. Provider implementations must map responses into Nama's normalized models, remain independent of the UI, use caching where appropriate, and fail without blocking other providers.

Please run the offline tests before submitting changes.
