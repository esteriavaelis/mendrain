# Installer, updates and releases

Mendrain is packaged with [Velopack](https://velopack.io). `tools/package.ps1` produces, in `artifacts/`:

| File | Purpose |
|---|---|
| `Mendrain-Setup-x64.exe` | One-click per-user installer (no administrator rights). |
| `Mendrain-Portable-x64.zip` | Portable copy; unzip anywhere. It can update itself in its folder. |
| `releases/releases.win.json`, `releases/*.nupkg` | The update feed the app reads from GitHub Releases. |
| `SHA256SUMS.txt` | Checksums of the installer, portable ZIP and packages. |

## What the installer does

- Installs to `%LOCALAPPDATA%\MendrainApp` (pack ID `MendrainApp`): `Update.exe`, `current\` (the program) and
  `packages\`. Adds a Start menu shortcut and an entry in *Settings → Apps*.
- Adds the File Explorer menu (Velopack install hook → `ShellMenu.Register`), pointing at
  `%LOCALAPPDATA%\MendrainApp\current\Mendrain.exe`. That path stays the same across updates; the update hook
  only repairs the menu if it is still registered for this copy, so a user's *Remove* is respected.
- Registers the file types (install hook → `FileAssociations.Register`): ProgIDs `MendrainApp.zip/.rar/.7z`,
  `HKCU\Software\MendrainApp\Capabilities` + a `Mendrain` value under `RegisteredApplications` (Default apps),
  named values in `.zip/.rar/.7z\OpenWithProgids`, and `Applications\Mendrain.exe`. The extensions' default
  values and UserChoice are never written, so the user's default app is unchanged. The update hook repairs this
  like the menu (only if still registered for this copy, or for a deleted exe); an update from 0.2.0 doesn't add it.
- Uninstalling removes `%LOCALAPPDATA%\MendrainApp`, the shortcut, Mendrain's menu keys and its file-type
  registration (uninstall hook). Other apps' values in the shared keys stay.
- User data (`settings.json`, `recent.json`, `passwords.json`, `mendrain.log`) lives in `%LOCALAPPDATA%\Mendrain`,
  outside the install folder, so updates and uninstalling never touch it. **Never change the pack ID to
  `Mendrain`**: Velopack deletes the whole install folder on uninstall, which would then be the data folder.

## How updates work

- The app checks `https://github.com/esteriavaelis/mendrain` releases with Velopack's `GithubSource` (HTTPS,
  no token), once per session a few seconds after start, and on *Settings → Check for updates*. Stable releases
  only, unless *Include preview versions* is ticked. Draft releases are invisible to the app.
- Nothing is downloaded until the user clicks **Update**; nothing is applied until **Restart now** (auto-apply
  at startup is turned off). Restart is refused while an extraction or ZIP creation is running.
- Velopack checks each package's SHA256/size against the feed before applying it.
- Development builds and 0.1.0 copies report that they can't update themselves.

## Migration from 0.1.0 (Inno Setup)

0.1.0 was installed by Inno Setup into `%LOCALAPPDATA%\Programs\Mendrain` and has no updater, so those users must
run the new Setup once. The new app detects the old installation (Inno uninstall key for AppId
`{6B0E2F3A-8C4D-4E1B-9A57-3F2D8E6C1A90}`) and shows *Remove old version*, which runs the old uninstaller with
`/SILENT` and then restores the Start menu shortcut and Explorer menu if the old uninstaller removed them.
The old installer script stays in git history (`installer/Mendrain.iss` before this change).

## Release procedure (maintainers)

1. Update `<Version>` in `src/Mendrain/Mendrain.csproj` and move the *Unreleased* notes in `CHANGELOG.md` into a
   `## [x.y.z] - date` section. The first lines of that section become the in-app update summary.
2. Commit, then push a tag `vX.Y.Z` (or `vX.Y.Z-preview.N` for a pre-release that only opted-in users get).
3. `.github/workflows/release.yml` runs the tests, publishes, downloads the previous release for a delta
   package, runs `vpk pack`, then `vpk upload github` into a **draft** release, adds the friendly-named Setup,
   portable ZIP and `SHA256SUMS.txt`, and sets the release notes.
4. Check the draft: it must contain `releases.win.json`, the `MendrainApp-<version>-full.nupkg` (and a
   `-delta.nupkg` from the second Velopack release on), `Mendrain-Setup-x64.exe`, `Mendrain-Portable-x64.zip`
   and `SHA256SUMS.txt`. Install the Setup on a test machine if possible.
5. Click *Publish release*. Only then do installed copies see the update. Never delete or edit the assets of a
   published release: installed copies download packages from it.

Locally, `powershell -ExecutionPolicy Bypass -File tools/package.ps1` builds the same packages without uploading.
`-TestFeed` builds a test-only app whose update source can be redirected with `MENDRAIN_UPDATE_FEED` (a local
folder or URL) to try updates without GitHub; never release such a build.

## Signing

Nothing is code-signed yet, so Windows SmartScreen may warn. To sign, pass Velopack's signing options to
`vpk pack` in `tools/package.ps1` and keep the certificate in GitHub Actions secrets, never in the repository.
