# Installer

`Mendrain.iss` is an [Inno Setup 6](https://jrsoftware.org/isinfo.php) script. It produces
`Mendrain-Setup-x64.exe`, which:

- installs per user into `%LOCALAPPDATA%\Programs\Mendrain` (no administrator rights needed; an
  "install for all users" choice is offered and then asks for elevation),
- offers a Start menu shortcut (on by default) and a desktop shortcut (off by default),
- registers Mendrain in *Settings → Apps* so it can be uninstalled normally,
- closes a running Mendrain before upgrading.

Uninstalling removes the installed files and shortcuts. Preferences and the recent-archives list in
`%LOCALAPPDATA%\Mendrain` are left behind.

## Building it

You do not normally run Inno Setup by hand: `tools/package.ps1` publishes the app and calls it.

1. Install Inno Setup 6: `winget install JRSoftware.InnoSetup` (or download it from jrsoftware.org).
2. From the repository root: `pwsh tools/package.ps1`

Without Inno Setup the script still builds the portable ZIP and prints a warning that the installer
was not built. On GitHub Actions the release workflow installs Inno Setup automatically.

## Signing

The installer and `Mendrain.exe` are **not code-signed**, so Windows SmartScreen may warn on first run.
To sign, add a signing step to `tools/package.ps1` and keep the certificate in GitHub Actions secrets,
never in the repository.
