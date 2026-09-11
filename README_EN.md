# RenpyTranslator

A Windows x64 WPF desktop app for installing, repairing and removing the translation mod, configuring compatible model APIs, managing caches and downloading desktop updates.

Extract the complete `RenpyTranslator-win-x64.zip` into a writable directory and run `RenpyTranslator.exe`. Keep `Resources` and `RenpyTranslator.Updater.exe` beside it. No separately installed Python, PowerShell 7 or .NET runtime is required.

Select a game root containing `game` and `renpy`, load its configuration, choose the resource pack and install. The bundled translations are specifically for **Camp Buddy Scoutmaster Season**. Generic mode does not import them or remove existing pretranslations. The manager never launches the game. Close the manager after installation; the existing Ren'Py mod handles in-game translation.

Keys remain DPAPI-encrypted under the current Windows user, compatible with legacy settings. An empty key field preserves the stored key. The API test sends one Hello request and may incur a small charge. Provider defaults are inherited from the old scripts, not a guarantee of current account support.

Default uninstall preserves configuration, cache, translations and fonts. Explicit data removal backs up and clears the translator data files. Backups and application state live in `%LOCALAPPDATA%/RenpyTranslator`.

Updates require a GitHub Release containing `RenpyTranslator-win-x64.zip` and its `.sha256` file. The updater backs up and replaces application resources, restoring changed files if replacement fails. After updating the manager, apply the new mod to each game using the install/upgrade button.

Build on Windows with .NET 10 SDK using `packaging/Publish.ps1`. This creates a self-contained package and runs isolated installation, rollback, API protocol and updater tests without launching a game or calling a real model. GitHub Actions builds manually or publishes tags matching `desktop-v*`.

Legacy scripts and documentation are preserved byte-for-byte in `archive/legacy-scripts`. They require the original repository layout and are reference material, not the new application entry point. See [the Chinese guide](README.md) for detailed operations and backup recovery. The existing [license](LICENSE) applies.
