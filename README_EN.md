<div align="right">

[![中文](https://img.shields.io/badge/中文-切换-FF6B6B?style=for-the-badge)](README.md)
[![English](https://img.shields.io/badge/English-Current-1E90FF?style=for-the-badge)](README_EN.md)

</div>

# Ren'Py Live Translator

A real-time translation mod for Windows Ren'Py games. The current target game is confirmed to use Ren'Py 7.8.4.

## How It Works

- Uses Ren'Py's say/menu early filters to precisely match full source text including tags and variables; regular UI text continues through `config.replace_text`.
- Calls OpenAI-compatible `/chat/completions` endpoints in the background in batches, without blocking the game.
- Prioritizes the manually maintained per-volume translations from this project; falls back to the runtime API on misses.
- On first occurrence the English text is shown, then auto-refreshes when the translation arrives; afterwards Chinese is displayed instantly from the local cache.
- Ships with the HarmonyOS Sans SC font by default to avoid missing Chinese glyphs in the original game font.

English text baked into images is not Ren'Py text and cannot be translated by this mod; it requires OCR or image replacement.

## Installation

Run in PowerShell:

```powershell
.\Install.ps1
```

The installer interactively asks for the game directory, display font, and optionally an API Key. The default install path is:

```text
D:\Program Files\Steam\steamapps\common\Camp Buddy Scoutmaster Season
```

If your game path differs, skip the prompts and specify it directly:

```powershell
.\Install.ps1 -GamePath "D:\Games\YourRenPyGame"
```

The installer automatically:

1. Backs up the existing `zz_live_translator.rpy` in the game directory.
2. Copies the latest mod script.
3. Merges `translations/*.jsonl` into `game/live_translator/pretranslated.jsonl`.
4. Writes the font configuration; existing settings in `config.json` (base_url/model/cache) are preserved.

## API Key Configuration (DPAPI Encrypted)

**API Keys are never stored in plain text.** The installer (`Install.ps1`) or the standalone config script (`Configure-Api.ps1`) encrypts the key with Windows DPAPI and writes it to the `api_key_encrypted` field in `config.json` (bound to the current Windows user, decryptable only on this machine), and clears the old plain-text `api_key` field. Do not manually put a plain-text key in `config.json` — the mod does not read it.

The recommended way to manage your API key, provider, and model is the standalone config script:

```powershell
.\Configure-Api.ps1
```

Interactive flow: choose the game directory → choose a model provider → choose a billing method (official API / Token Plan subscription; only shown for providers with a subscription endpoint) → confirm the API base URL (prefilled, editable) → enter the API Key (saved encrypted) → confirm the model name (prefilled with the provider's recommended cost-effective default, editable).

10 built-in provider presets (in order; default models are each provider's latest cost-effective model):

| Provider | Default Model | Subscription Endpoint |
| --- | --- | --- |
| DeepSeek | deepseek-v4-flash | — |
| OpenAI | gpt-5.6-luna | — |
| Xiaomi MiMo | mimo-v2.5 | Yes |
| MiniMax | minimax-m3 | Yes |
| Tencent Hunyuan | hy3 | — |
| Google Gemini | gemini-3.6-flash | — |
| Alibaba Qwen | qwen-flash | Yes |
| Zhipu GLM | glm-4.7-flash | Yes |
| Kimi (Moonshot) | kimi-k2.6 | Yes |
| ByteDance Doubao | doubao-seed-2.0-lite | Yes |

There is also a "Custom" option to enter your own API base URL and model name (e.g. for relay services or self-hosted endpoints).

## Usage

- `F9`: toggle translation on/off temporarily.
- `F10`: show configuration, cache, or the latest request error.
- Project volume translations: `translations\interface.jsonl`, `translations\day01.jsonl`, etc.
- Game-merged translations: `game\live_translator\pretranslated.jsonl`.
- Runtime cache: `game\live_translator\cache.jsonl`.

The load order is pretranslated first, then the runtime cache, so the cache can override any pretranslated entry for manual corrections. After deleting the runtime cache, text already present in the pretranslated files still displays Chinese directly.

Files in `translations` contain only source text and Chinese translations — no API keys. The installer validates duplicates and merges them into the game directory; translation lists and extracted story source are kept only in the ignored `work` directory.

The mod performs local deterministic translations for certain dynamic text (e.g. save slot numbers, weekdays, missing-image errors), which never calls the API.

## Updating

The update script checks the releases of the GitHub repository `zhiqiangme/renpy-translator` and, when a new version is available, downloads and overwrites the project files (mod code + translations). Local private files (`config.json`, `cache.jsonl`, `work`, `backups`, `translations_bak`) are not affected.

Run it by double-clicking:

```powershell
.\Update.ps1
```

Or check only without updating (for script reuse):

```powershell
.\Update.ps1 -CheckOnly
```

`Install.ps1` also checks for updates once after installation: it finishes directly when there is no error and no update; if a new version is detected, press Enter to update immediately, or type 「不更新」(don't update) to skip. Network failures or a repository with no release yet are silently skipped without affecting installation.

The version number is taken from the GitHub release tag (e.g. `v1.0.0`) and recorded locally in the root-level `version.txt`. After updating, **run the installer once more** so the new translations are merged into the game directory's `pretranslated.jsonl`.

## Public Translation Coverage

The public repository keeps all JSONL volumes; most of the manually translated content is directly usable.

When a sentence is missing from the public translations:

- With a valid LLM API key, it will be translated in the background and written to the local runtime cache.
- Without a valid API key (or if DPAPI decryption fails), no network request is sent and the game shows the original English text.

## Uninstall

Remove only the mod, keeping configuration and cache:

```powershell
.\Uninstall.ps1
```

Remove the mod together with its configuration and cache:

```powershell
.\Uninstall.ps1 -RemoveData
```

## License

This project is released under the MIT License; see [LICENSE](LICENSE).

> **English**: This project is a translation mod created by third-party fans and is not affiliated with the game developer. The translations are provided for learning and communication purposes only. Please support the official release. Any redistribution, sale, or commercial use is solely the responsibility of the user; the original author assumes no joint or several liability. If requested by the copyright holder, the original author will cooperate in removing the relevant content.
>
> **中文**：本项目为第三方爱好者制作的翻译模组，与游戏开发商无关。译文仅供学习交流，请支持正版。任何再分发、销售或商业化行为均由使用者自行承担全部法律责任，原作者不承担任何连带责任。如版权方要求，原作者将配合移除相关内容。
