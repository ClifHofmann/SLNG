# [FEAT-AI-02] Built-in Local Chat Translation (Zero-Install)

- **Feature ID:** `FEAT-AI-02`
- **Track:** `ui` / `core` / `net`
- **Status:** `⏸️ Pending`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Real-time chat translation is critical for the international Second Life and OpenSim community. Relying on external cloud APIs (like Google Translate or DeepL) raises privacy concerns and costs API credits. Requiring users to install and run massive 8GB LLMs locally (via Ollama) consumes too much VRAM, degrading viewer performance.

This feature introduces a **100% native, offline, zero-install** translation layer directly within the SLNG viewer. By embedding `Microsoft.ML.OnnxRuntime`, the viewer can execute highly optimized, tiny translation models (e.g., Opus-MT or MarianMT) entirely on the CPU or with minimal GPU acceleration.

## Core Advantages
- **Zero Configuration**: Users do not need to install Ollama, LM Studio, or Python.
- **Low VRAM Impact**: These models are typically 80 MB - 150 MB per language pair. They will not crash the Godot PBR renderer.
- **Privacy & Licensing**: Opus-MT/MarianMT are released under permissive licenses (MIT) and run 100% locally. No chat data is sent to external servers.

---

## Acceptance Criteria

### 1. ONNX Runtime Integration (`SLNG.Core.AI.Translation`)
- [ ] Add `Microsoft.ML.OnnxRuntime` NuGet package to `SLNG.Core`.
- [ ] Implement `ITranslationService` wrapping an ONNX inference session.
- [ ] Provide an asset downloader that fetches the required `*.onnx` model weights (e.g., `opus-mt-en-de`) on-demand from a trusted source (or bundles the most common pairs) to keep the initial viewer download small.

### 2. UI & Scope (`ChatConsole` & `PreferencesWindow`)
- [ ] **Master Toggle (Global)**: A main "Enable Local Translation" toggle in Preferences (or a quick-toggle in the Toolbar/Menu) that instantly activates/deactivates the entire translation engine.
- [ ] **Preferences (Per-Channel)**: Specific toggles to enable/disable translation for Nearby Chat and Group Chat.
- [ ] **IM Tabs (Per-Session)**: Direct messages (IMs) have a local dropdown in the tab header to set the target language specifically for that chat partner.
- [ ] **Incoming (Display)**: Translated text is shown directly in the chat. The original text is accessible via a hover-tooltip over an `[A->A]` icon to keep the UI clean.
- [ ] **Outgoing (Auto-Translate)**: A toggle next to the chat input bar. When active, typing in native language translates the message *before* it is sent to the grid.
- [ ] **Language Detection**: Automatic source-language detection (built into the model) to prevent translating text that is already in the target language.

### 3. Chat Logging (`FileLogger`)
- [ ] **Gold Standard Logging**: Logs written to disk (e.g. `chat.txt`) must ALWAYS contain both the translation and the original text to prevent context loss.
  - *Incoming Example:* `[10:15] Avatar: Translated text. (Original: Original text)`
  - *Outgoing Example:* `[10:16] You: Translated text sent. (Original: Native text typed)`

### 4. Localization & Self-Test
- [ ] UI labels localized in `en-US.json` and `de-DE.json`.
- [ ] `--selftest` covers ONNX model loading and a dummy translation inference.
- [ ] `AppVersion` bumped upon implementation.

---

## Technical Specs & Affected Files
- `src/SLNG.Core/AI/Translation/ITranslationService.cs`
- `src/SLNG.Core/AI/Translation/OnnxTranslationService.cs`
- `src/SLNG.Core/AI/Translation/LanguagePackManager.cs`
- `app/scripts/UI/Preferences/TranslationPreferencesPage.cs`
- `app/scripts/UI/Chat/ChatConsole.cs`
- `app/scripts/UI/Chat/ChatInputBar.cs`
- `app/i18n/en-US.json`
- `app/i18n/de-DE.json`
- `tests/SLNG.Core.Tests/TranslationServiceTests.cs`

---

## Sub-tasks / Progress
- [ ] `[Phase 1]` Evaluate and select the precise ONNX export of Opus-MT/MarianMT to ensure MIT license compliance.
- [ ] `[Phase 2]` Integrate `Microsoft.ML.OnnxRuntime` and build `OnnxTranslationService`.
- [ ] `[Phase 3]` Implement `LanguagePackManager` to download and cache `*.onnx` files in the user directory.
- [ ] `[Phase 4]` Hook into the incoming/outgoing chat event stream.
- [ ] `[Phase 5]` Build `TranslationPreferencesPage` and Chat UI toggles.
- [ ] `[Phase 6]` Unit tests and `--selftest` coverage.
