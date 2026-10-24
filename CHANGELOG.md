# Changelog

All notable changes to Mendrain are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.2.0] - 2026-10-25

Saved passwords, a File Explorer right-click menu, and automatic updates.

### Upgrading from 0.1.0
Version 0.1.0 can't update itself. Download and run the new `Mendrain-Setup-x64.exe` once; Mendrain then
offers to remove the old copy. Settings and recent archives are kept.

### Added
- Saved passwords: a "Save password" option and a dropdown of saved passwords built into the password field,
  with show/hide. Passwords are saved only after they worked, encrypted with Windows DPAPI for the current user.
  Batch Extract tries a password saved for an archive of the same name before asking, and never retries a
  password that already failed for that archive. *Settings → Saved passwords* can remove them all.
- File Explorer context menu for `.zip`, `.rar` and `.7z`: a *Mendrain* submenu with Open with Mendrain,
  Extract Here, Extract to Folder and Batch Extract. Per-user registration (no admin, default apps unchanged),
  added by the installer, kept up to date by updates, and manageable in *Settings → File Explorer menu*.
  Multi-file selections are merged into one request, and requests go to the running Mendrain window if there is one.
- Automatic updates from GitHub Releases (Velopack): a check shortly after start (once per session), a small
  notification with **Update** / **Later**, download progress with Cancel, and **Restart now** to apply.
  *Settings → Updates* adds *Check for updates* and an opt-in for preview versions.
- After upgrading from 0.1.0, an offer to remove the old installation.

### Changed
- The installer is now a one-click per-user Velopack installer (`%LOCALAPPDATA%\MendrainApp`), and the portable
  ZIP can update itself. The Inno Setup installer is no longer built.

## [0.1.0] - 2026-10-16

First public version.

### Added
- Open ZIP, 7Z and RAR archives and browse their folders (name, size, packed size, date).
- Extract an archive into a new folder named after it, or into a chosen folder, with progress and Cancel.
- "Files already exist" handling: keep both (`name (2)`), skip, or replace after a second confirmation.
- Batch Extract: queue archives and extract them one at a time, each into its own folder; detects
  conflicting folder names before extracting; a failed archive does not stop the batch; final summary.
- Password prompts for encrypted archives, including archives with encrypted file names.
- Multi-volume RAR sets (`name.part1.rar`, `name.part2.rar`, …).
- Create ZIP from files and folders with four compression levels.
- Recent archives, drag and drop, light/dark/system theme, opening files passed on the command line.
- Safe extraction: path-traversal protection, Windows name rules, links never extracted, staged
  extraction with size/CRC checks, no silent overwrites.
- Released under the MIT License.
- Portable ZIP and per-user Inno Setup installer; GitHub Actions release workflow (draft releases).
