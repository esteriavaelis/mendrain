# Changelog

All notable changes to Mendrain are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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
