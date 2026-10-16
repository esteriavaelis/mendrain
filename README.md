<p align="center">
  <img src="assets/logo.png" alt="Mendrain logo" width="112" height="112">
</p>

<h1 align="center">Mendrain</h1>

<p align="center"><strong>A modern archive utility for Windows</strong></p>

<p align="center">
  A cleaner way to create, extract, and manage archives on Windows.
</p>

<p align="center">
  <a href="https://github.com/esteriavaelis/mendrain/releases/latest">Download</a> ·
  <a href="#features">Features</a> ·
  <a href="#supported-formats-and-limitations">Formats</a> ·
  <a href="#building-from-source">Build from source</a> ·
  <a href="https://github.com/esteriavaelis/mendrain/issues">Report an issue</a>
</p>

<p align="center">
  <img src="docs/images/home.png" alt="Mendrain home screen" width="820">
</p>

## Features

- **Open archives** — browse the folders inside ZIP, 7Z and RAR files, with sizes and dates.
- **Extract** — into a new folder named after the archive (default) or any folder you pick, with progress and Cancel.
- **Batch Extract** — drop many archives and extract them one after another, each into its own folder.
  Folder-name clashes are detected before anything is written, and one broken archive doesn't stop the rest.
- **Create ZIP** — from any mix of files and folders, with four compression levels.
- **Passwords** — asks when an archive is encrypted and tells you clearly when a password is wrong.
- **Safe by design** — files can't be written outside the folder you chose, links are never extracted,
  and existing files are never replaced without your confirmation.
- Drag and drop, recent archives, and light, dark or system theme.

## Screenshots

| Archive viewer | Batch Extract |
|---|---|
| <img src="docs/images/viewer.png" alt="Browsing a ZIP archive" width="400"> | <img src="docs/images/batch.png" alt="Batch Extract with three archives" width="400"> |
| **Create ZIP** | **Light theme** |
| <img src="docs/images/create-zip.png" alt="Creating a ZIP file" width="400"> | <img src="docs/images/viewer-light.png" alt="Archive viewer in light theme" width="400"> |

## Download and installation

Get the latest version from the
[Releases page](https://github.com/esteriavaelis/mendrain/releases/latest). Two downloads are offered:

| File | What it is |
|---|---|
| `Mendrain-Setup-x64.exe` | **Installer** (recommended). Installs for your user account only, without administrator rights, into `%LOCALAPPDATA%\Programs\Mendrain`. Adds a Start menu shortcut (and optionally a desktop shortcut) and can be removed from *Settings → Apps*. |
| `Mendrain-Portable-x64.zip` | **Portable version.** Unzip anywhere and run `Mendrain\Mendrain.exe`. Nothing is installed. |

Neither download needs .NET or any other runtime installed: everything Mendrain needs is included.

**Notes**
- The downloads are **not code-signed**, so Windows SmartScreen may show "Windows protected your PC".
  Choose *More info → Run anyway* if you trust the source. `SHA256SUMS.txt` on each release lets you verify the files.
- The portable version still stores its preferences and recent-archives list in `%LOCALAPPDATA%\Mendrain`,
  not next to the program.

## System requirements

- Windows 10 version 1809 (build 17763) or later, or Windows 11
- 64-bit (x64) PC
- About 175 MB of disk space

Mendrain has been tested on Windows 10 22H2 (x64). Windows 11 should work but hasn't been tested yet,
and the Mica window background only appears there.

## Usage

- **Open an archive:** click *Open archive*, drop one archive on the home screen, or open it with
  `Mendrain.exe <archive>`. Double-click folders to browse them, press Backspace to go up.
  Choose *Extract to* and a folder name, then click **Extract**.
- **Extract many archives:** drop several archives on the home screen (or open *Batch Extract* and click
  *Add archives*), choose a destination, then click **Extract all**. Each archive goes into
  `<destination>\<archive name>`. If a folder name is already taken, Mendrain asks whether to use a new name
  (for example `Project (2)`) or skip that archive.
- **Create a ZIP:** drop files and folders on the home screen (or open *Create ZIP*), pick a name and
  location, then click **Create ZIP**. Selected files go at the top of the ZIP; selected folders keep their
  name and everything inside them.

## Supported formats and limitations

| Format | Extract | Create |
|---|---|---|
| ZIP (incl. ZipCrypto and AES encryption, ZIP64) | Yes | Yes |
| 7Z (incl. solid and AES-encrypted, also with encrypted file names) | Yes | No |
| RAR 5 (incl. solid, encrypted, multi-volume `name.part1.rar` sets) | Yes | No |
| RAR 4 | Expected to work, not tested | No |

Limitations:
- The whole archive is extracted; extracting only selected files isn't supported yet.
- Only RAR `name.partN.rar` multi-volume sets are supported. Old-style `.r00` volumes, split 7Z (`.7z.001`)
  and split ZIP archives are not.
- No RAR/7Z creation, no password-protected ZIP creation, no self-extracting archives, no archive repair.
- File contents, folders and modification dates are restored. NTFS permissions and alternate data streams are not.
- For 7Z archives, a wrong password can't be told apart from damaged data; Mendrain says so in the message.

## How extraction stays safe

- Entry names inside archives are treated as untrusted. Names containing `..`, absolute paths, drive letters
  or network paths are blocked, and every final path is checked to be inside the destination folder.
- Names Windows can't use are adjusted (invalid characters, trailing dots, reserved names such as `CON`), and
  names that differ only by upper/lower case get `name (2)`. Every change is listed in the result.
- Symbolic links, hard links and device files inside archives are never extracted, and Mendrain never writes
  through existing links in the destination.
- Files are first extracted into a hidden temporary folder and checked against the sizes and checksums in
  the archive. They're moved into place only if the whole archive succeeded. On failure or Cancel, only that
  temporary folder is removed — nothing that existed before is deleted.
- Existing files are never replaced unless you choose *Replace* and confirm.

## Building from source

Requirements: Windows 10/11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).
Visual Studio isn't required.

```powershell
git clone https://github.com/esteriavaelis/mendrain.git
cd mendrain
dotnet run --project src/Mendrain         # start the app (Debug)
```

To build the release packages (portable ZIP, plus the installer if
[Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed):

```powershell
powershell -ExecutionPolicy Bypass -File tools/package.ps1
```

Output goes to `artifacts/`. See [installer/README.md](installer/README.md) for installer details.
Releases are built by GitHub Actions when a version tag such as `v1.0.0` is pushed; the workflow creates a
draft release for review.

## Reporting issues

Please open an issue on the
[issue tracker](https://github.com/esteriavaelis/mendrain/issues) with your Windows version, the
Mendrain version (*Settings* page), and steps to reproduce. If Mendrain crashed, the file
`%LOCALAPPDATA%\Mendrain\mendrain.log` helps. Please don't attach archives that contain private data.

## License

Mendrain is released under the [MIT License](LICENSE).

Third-party components included in the release packages are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

<p align="center">© 2026 Esteria Vaelis</p>
