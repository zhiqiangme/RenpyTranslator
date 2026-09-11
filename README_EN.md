<div align="right">

[简体中文](README.md) | **English**

</div>

# RenpyTranslator

A Windows x64 WPF desktop app for installing, repairing and removing the translation mod, configuring compatible model APIs, managing caches and downloading desktop updates.

Two install options. Either way, no separately installed Python, PowerShell 7 or .NET runtime is required.

- **Installer (recommended)**: download `RenpyTranslator-Setup-win-x64.exe` and run it. Installs into `%LOCALAPPDATA%\Programs\RenpyTranslator` per user, without administrator rights; Start Menu and desktop shortcuts are optional. Uninstalling does not remove user data (config, cache, translations, backups) under `%LOCALAPPDATA%\RenpyTranslator`.
- **Portable**: extract the complete `RenpyTranslator-win-x64.zip` into a user-writable directory and run `RenpyTranslator.exe`. Keep `Resources` and `RenpyTranslator.Updater.exe` beside it.

In-app updates work for both, because the updater replaces files inside the install directory under normal user rights, so the location must stay user-writable.

Select a game root containing `game` and `renpy`, load its configuration, choose the resource pack and install. The bundled translations are specifically for **Camp Buddy Scoutmaster Season**. Generic mode does not import them or remove existing pretranslations. The manager never launches the game. Close the manager after installation; the existing Ren'Py mod handles in-game translation.

Keys remain DPAPI-encrypted under the current Windows user, compatible with legacy settings. An empty key field preserves the stored key. The API test sends one Hello request and may incur a small charge. Provider defaults are inherited from the old scripts, not a guarantee of current account support.

Default uninstall preserves configuration, cache, translations and fonts. Explicit data removal backs up and clears the translator data files. Backups and application state live in `%LOCALAPPDATA%/RenpyTranslator`.

Updates require a GitHub Release containing `RenpyTranslator-win-x64.zip` and its `.sha256` file. The updater backs up and replaces application resources, restoring changed files if replacement fails. After updating the manager, apply the new mod to each game using the install/upgrade button.

Build on Windows with .NET 10 SDK using `packaging/Publish.ps1`. This creates the portable ZIP, the Inno Setup installer `RenpyTranslator-Setup-win-x64.exe` and their `.sha256` files, then runs isolated installation, rollback, API protocol and updater tests without launching a game or calling a real model. Pass `-SkipInstaller` to skip the installer (requires a local Inno Setup 6 otherwise). GitHub Actions builds manually or publishes tags matching `v*`.

Legacy scripts and documentation are preserved byte-for-byte in `archive/legacy-scripts`. They require the original repository layout and are reference material, not the new application entry point. See [the Chinese guide](README.md) for detailed operations and backup recovery. The existing [license](LICENSE) applies.
