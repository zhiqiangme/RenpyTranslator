<div align="right">

[简体中文](README.md) | **English**

</div>

# Ren'Py Chinese Translation Manager

A Windows x64 desktop app with a graphical interface for installing, upgrading and uninstalling Chinese translations, configuring OpenAI-compatible model APIs, managing translation caches and updating the software.

## Running the App

Choose either installation method. Release packages include the .NET runtime; users do not need to install Python, PowerShell 7 or the .NET SDK.

- **Installer (recommended)**: download and run `RenpyTranslator-Setup-win-x64.exe`. It installs into `%LOCALAPPDATA%\Programs\RenpyTranslator` without administrator rights, with optional Start Menu and desktop shortcuts. Uninstalling the app does not delete configuration, caches, translations or backups under `%LOCALAPPDATA%\RenpyTranslator`.
- **Portable**: download `RenpyTranslator-win-x64.zip`, extract it completely into a directory writable by the current user, and run `RenpyTranslator.exe`. Keep `Resources` and `RenpyTranslator.Updater.exe` in the same directory.

Both methods install the same app and support in-app updates. The updater replaces files in the installation directory, so that location must be writable by the current user. After installation:

1. On the Game Management page (游戏管理), browse to the game root, or select a previously added directory and click Read / Check Status (读取 / 检查状态). The directory must contain `game` and `renpy` folders.
2. Select the resources: generic mode does not import game-specific translations and preserves the game's original confirmation screen. The bundled translations and confirmation screen are only for **Camp Buddy Scoutmaster Season**. Switching back to generic mode removes the dedicated confirmation screen and its compiled cache without deleting existing pretranslations.
3. Enter the endpoint, model and key on the Model API page (模型 API) as needed, then click Install / Upgrade / Repair Translation (安装 / 升级 / 修复汉化). A key is not required when using pretranslations alone.
4. You can close the manager after installation. In-game translation continues to run through `game/zz_live_translator.rpy`. F9 toggles translation; F10 shows its status. Restart the game after changing configuration.

The manager does not launch the game. The original mod's compatibility limits still apply; compatibility with untested Ren'Py versions is not guaranteed. English text embedded in images is outside the scope of text translation.

## API and Configuration

- Custom OpenAI-compatible `/chat/completions` APIs are supported. Enter either the full endpoint or its parent URL.
- Provider presets are inherited from the previous version. Model names, endpoints and account availability depend on the provider's actual support; subscription endpoints can also be entered manually.
- Leaving the API Key field blank preserves the stored key. To clear it, select the corresponding checkbox. Keys use Windows DPAPI CurrentUser encryption and are compatible with the legacy PowerShell configuration. Re-enter the key when switching Windows users or computers.
- Test Connection / Translation (测试连接 / 翻译) sends one Hello request and may incur a small charge. The test translation is not saved to the game's cache.
- Advanced settings include batch size, wait time, timeout, cooldown, output tokens, temperature, prompts, names and skip patterns. Restoring defaults only changes the editor; settings are written to the game when saved.
- Each game has its own configuration. Unknown configuration fields are preserved. Endpoints must use HTTPS; local loopback services may use HTTP.
- Loading configuration displays and preserves custom font paths. Installation only replaces the font when another font is selected. Relative paths are resolved from the game's `game` directory.
- Skip patterns are limited to basic regex syntax supported by both the manager and the game: literals, character classes, common escapes, anchors, alternatives, ordinary groups, non-capturing groups and quantifiers. Named groups, backreferences, lookarounds, inline options and extended escapes are unsupported. For example, `^(?:Hello|World)\s+\d{2,}$` is supported.
- When translation is disabled, confirmation dialogs preserve the source text and unsent batches are not sent. Requests already sent may finish. After translation is enabled again, untranslated text can be queued again.

## Installation, Uninstallation and Data

Before installation, translation format and duplicate source strings are checked. Files to be overwritten are backed up before writing, and their original bytes are restored on failure. Software updates do not overwrite configuration, runtime caches or unrelated user files in the installation directory.

If the installation record is damaged or lacks required checksum entries, the manager shows a repairable status while allowing valid configuration to load and installation / repair to proceed. A recognized resource mode is retained; otherwise generic mode is selected by default, so confirm the mode before repairing. Damage to the configuration file itself, file permission errors or link errors must still be resolved first.

Default uninstallation only removes the generic and dedicated mod's `.rpy` and `.rpyc` files and the desktop installation record. Configuration, caches, translations and fonts are preserved. Selecting Delete All Files Under game/live_translator During Uninstallation (卸载时删除 game/live_translator 内全部文件) recursively deletes every file in that directory, including configuration, caches, translations, fonts and files stored there by the user. Files are still backed up before deletion. Original game files, saves and other directories are outside the cleanup scope.

Manager data is stored under `%LOCALAPPDATA%/RenpyTranslator`:

| Location | Contents |
| --- | --- |
| `games.json` | Previously added game directories |
| `backups/<operation-id>/` | Files before modification; `target.txt` identifies the game root |
| `updates/<operation-id>/previous/` | Previous application version backed up before an update |
| `tests/<id>/` | Simulated game directories created by self-tests; the latest 4 are retained |
| `self-test.log`, `updater-test.log` | Automated validation results |

The Data and Logs page (数据与日志) can export caches, back up and clear caches, open the backup directory, and export the current operation log. To restore a backup, close the game first, then copy files back using their relative paths into the `game` folder of the game identified by `target.txt`. The latest 10 backups and 4 self-test directories are retained; excess directories are automatically removed during the next operation or self-test.

Software update directories are cleaned at manager startup and when preparing an update. The latest 2 are retained; directories less than one day old or currently in use are also skipped. Other old directories are removed together with their downloaded archives, extracted files and `previous` backups. Failed cleanup is retried later.

## Updates

The Updates page (更新) checks the latest GitHub Release from `zhiqiangme/RenpyTranslator`.

Downloads are offered only for versions newer than the current version. Equal or older versions show “Already up to date” without displaying old release notes. New release notes are formatted as Markdown, supporting headings, lists, code, tables and web links.

An eligible desktop release must include:

- `RenpyTranslator-win-x64.zip`
- `RenpyTranslator-win-x64.zip.sha256`
- A three-part numeric version tag such as `v26.9.11`

After downloading, the manager verifies SHA-256. The independent updater waits for the manager to exit, backs up old files, replaces release files and restarts the manager. If replacement fails, it attempts to restore the old files. Obsolete bundled resources from previous releases are removed to prevent old translations from being mixed in. The checksum detects download corruption; it is not a code signature.

The app and bundled resources are distributed in the same release package, with the manager version and resource version displayed separately. After updating, click Install / Upgrade (安装 / 升级) on the Game Management page for each game that needs the new mod and translations. No scripts need to be run manually.

## Development and Releases

Windows and the .NET 10 SDK are required. To build for development:

```powershell
dotnet build desktop/Translator/Translator.csproj -c Release
```

The app icon is generated by `packaging/make-icon.py` from `desktop/Translator/Assets/app-icon.png` into `Assets/app.ico`. The script uses only the standard library, decodes PNG itself, rounds the corners and independently antialiases seven sizes from 16 to 256 pixels. After replacing the source image, rerun the script without changing the project file. Its optional third argument is the corner-radius ratio, from 0 to 0.5, defaulting to 0.2; use 0 for square corners.

To generate a self-contained release package and run isolated tests:

```powershell
./packaging/Publish.ps1
# Specify a version (defaults to packaging/version.txt when omitted)
./packaging/Publish.ps1 -Version 26.10.1
# Keep the unpacked executable
./packaging/Publish.ps1 -KeepStage
# Generate only the ZIP, without compiling the installer
./packaging/Publish.ps1 -SkipInstaller
```

Output goes to `dist`, including the ZIP, `RenpyTranslator-Setup-win-x64.exe` installer and their respective SHA-256 checksum files. The installer is compiled with Inno Setup 6 using `packaging/installer.iss`; Inno Setup is preinstalled locally and on GitHub Actions' windows-latest runner. Installation is per user and requires no administrator rights. Build staging directories are automatically removed when the script finishes; use `-KeepStage` to retain them. Tests only operate on simulated game directories under `%LOCALAPPDATA%/RenpyTranslator/tests`. API tests use a local mock service without opening games or calling real models.

The default version comes from `packaging/version.txt`, which development builds also read. The release script injects the assembly version through `-p:Version` and synchronizes the resource version in the release staging directory. Overriding the version through a parameter or `v*` tag does not change the repository's default version file. Pushing a `v*` tag automatically builds, tests and publishes a Release. Local commits do not publish automatically; the tag must be pushed separately.

## Legacy Archives

The legacy PowerShell entry points and Chinese and English documentation have been moved unchanged to `archive/legacy-scripts`, including archive hashes, and have not been deleted. This directory preserves the historical implementation. The old scripts still depend on the original root directory layout and should not be run directly from the archive directory.

`game`, `translations` and `fonts` remain resources for the new version; `tools` is retained for development. The complete translated version, including the old installation scripts, is archived under `archive/translations_bak`. `backups` stores historical snapshots and archives. Both are excluded by `.gitignore` and are not version-controlled. `dist` retains only release ZIPs and unpacked directories; the release script removes its own staging directories at the end of each run.

Existing Camp Buddy Scoutmaster Season translations are stored under `translations/camp-buddy-scoutmaster/`. Dedicated installation and translation validation read only that game's directory. Translations for other games should use separate subdirectories.

## License

The project's [LICENSE](LICENSE) continues to apply. This is a third-party translation mod, unaffiliated with the game developers. Please support legitimate copies of the games.

Markdown parsing uses the open-source [Markdig](https://github.com/xoofx/markdig) library (BSD-2-Clause). Its license is included in release packages at `Resources/licenses/Markdig-LICENSE.txt`.
