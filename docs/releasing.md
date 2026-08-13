# Releasing Nama

Nama's public artifact is a per-user Windows installer attached to a GitHub Release.
The application is published self-contained for `win-x64`, so the target machine does
not need a separate .NET installation.

## One-time repository setup

GitHub Actions must be enabled for the repository. The release workflow uses the standard
repository `GITHUB_TOKEN` with `contents: write`; it needs no personal access token.

The current installer is not Authenticode-signed. Windows may therefore show an
unrecognized-publisher warning until a signing certificate and CI signing step are added.

## Release checklist

1. Merge the intended release commit into `main` and verify CI is green.
2. Ensure `Directory.Build.props` has a sensible development version.
3. Review the accumulated changes and choose a semantic version.
4. Create an annotated tag on the exact `main` commit and push it:

   ```powershell
   git switch main
   git pull --ff-only
   git tag -a v0.1.0 -m "Nama 0.1.0"
   git push origin v0.1.0
   ```

5. Watch the **Release** workflow. It will:

   - validate the tag;
   - restore and run the complete test suite;
   - publish a self-contained `win-x64` application with the tag version embedded;
   - compile `installer/Nama.iss` with Inno Setup;
   - create a SHA-256 checksum;
   - create a GitHub Release with generated notes and attach both files.

Tags such as `v0.2.0-beta.1` create prereleases. A failed workflow does not publish a
partial release. Fix the cause, delete the failed remote tag only if it points at the
wrong commit, and create a new version rather than silently replacing a published build.

## Local packaging

Publish the application:

```powershell
$env:NAMA_VERSION = "0.1.0"
dotnet publish src/Nama.App/Nama.App.csproj -c Release -r win-x64 `
  --self-contained true -p:Version=$env:NAMA_VERSION `
  -p:InformationalVersion=$env:NAMA_VERSION `
  -p:PublishSingleFile=false -p:PublishTrimmed=false `
  -o artifacts/publish/win-x64
```

With Inno Setup 6 installed, compile the installer:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" `
  "/DMyAppVersion=$env:NAMA_VERSION" installer/Nama.iss
```

The output is `artifacts/installer/Nama-Setup-<version>.exe`.

## Installer behavior

- Installs without elevation under `%LOCALAPPDATA%\Programs\Nama`.
- Adds a Start menu shortcut.
- Offers optional desktop and Explorer right-click shortcuts.
- Uses Nama's own maintenance command to create Explorer entries so installer and app
  settings cannot drift apart.
- Removes Explorer entries during uninstall, including entries enabled later from Nama's
  Settings screen.
