# [FEAT-AI-01] In-Viewer AI Assistant & MCP Server (Inventory & Settings)

- **Feature ID:** `FEAT-AI-01`
- **Track:** `ui` / `core` / `net`
- **Status:** `⏸️ Pending`
- **Owner:** `gemini`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
For Second Life and OpenSim residents, managing massive inventories and finding specific viewer preferences can be overwhelming. This feature introduces an **AI Help Bot / Assistant** directly into SLNG, combined with an optional **Model Context Protocol (MCP)** server:
1. **Configurable AI Providers (Cloud vs. Local)**: Users can configure their preferred provider (OpenAI, Anthropic, Gemini) via API keys to save local VRAM, or connect a local model via Ollama/LM Studio if their hardware permits.
2. **In-Viewer Assistant Window (`AiAssistantWindow`)**: A floating chat floater providing conversational help, intelligent inventory assistance (duplicate analysis, auto-sorting), and **direct viewer settings control** (e.g., "enable PBR shadows").
3. **Structured Tool Calling & Plan Execution**: The assistant uses structured tools to analyze inventory hierarchies, identify patterns, and propose concrete cleanup plans.
4. **Human-in-the-Loop Confirmation**: Destructive or batch modifications (moving items, creating folders, moving duplicates to Trash) are NEVER executed automatically. The viewer presents a clear action preview dialog where the user reviews and confirms each change.
5. **MCP Server Integration**: An optional local MCP server interface allowing external desktop agents (Claude Desktop, Cursor, Antigravity) to inspect the viewer's live inventory and propose actions using standardized MCP tools.

---

## Linden Lab TPV Policy & Safety Rules
- **Non-Negotiables**:
  - Full adherence to the Linden Lab Third-Party Viewer Policy.
  - Honor item permissions strictly: Never bypass `no-copy`, `no-transfer`, or `no-modify` restrictions.
  - Destructive operations (moving to Trash, emptying Trash) always require explicit user confirmation.
  - User API keys are stored locally in `user://preferences.cfg` (or encrypted credential store) and are never transmitted to any third party other than the configured provider endpoint.

---

## Acceptance Criteria

### 1. Preferences Configuration (`PreferencesWindow`)
- [ ] New preferences page / section **AI Assistant** (`preferences.cfg [ai_assistant]`):
  - `Enabled` (bool, default `false`)
  - `Provider` (enum: `openai`, `anthropic`, `gemini`, `local_ollama`, `custom_openai_compatible`)
  - `EndpointUrl` (string, e.g. `http://localhost:11434/v1` or custom API proxy)
  - `ApiKey` (masked password field, stored securely per account/user)
  - `ModelName` (string, e.g. `gpt-4o`, `claude-3-7-sonnet`, `gemini-2.5-flash`, `llama3.2`)
  - `Temperature` (float slider, default 0.3 for precision)
  - `EnableMcpServer` (bool, default `false`, port configuration `127.0.0.1:port`)

### 2. In-Viewer AI Assistant (`AiAssistantWindow` - `SLNGWindow`)
- [ ] Floating, draggable `SLNGWindow` accessible via top menu bar and shortcut.
- [ ] Conversational chat UI: message history, user input bar, streaming or token-buffered response display, status indicators (thinking / analyzing).
- [ ] Quick-action suggestion pills:
  - "Doppelte Landmarken aufräumen" / "Clean duplicate landmarks"
  - "Leere oder unbenutzte Ordner finden" / "Find empty folders"
  - "Hilfe zu Viewer-Einstellungen" / "Help with viewer settings"
  - "Inventar-Übersicht erstellen" / "Summarize inventory structure"
- [ ] **Action Plan Card**:
  - When the AI generates batch operations, it emits a structured plan (e.g. `[ActionPlan] 12 items to move, 3 to trash`).
  - The UI renders an interactive confirmation card listing source, target, item names, and checkboxes.
  - Changes are applied ONLY when the user clicks "Änderungen anwenden" / "Apply Changes".

### 3. Core AI & Tool Engine (`SLNG.Core.AI`)
- [ ] Engine-agnostic tool definitions (`IAiTool`) for querying and mutation:
  - `get_inventory_tree(folder_id, max_depth)`
  - `search_inventory(query, item_type)`
  - `find_duplicate_landmarks()`
  - `find_empty_folders()`
  - `create_inventory_folder(name, parent_id)`
  - `move_inventory_items(item_ids[], target_folder_id)`
  - `move_items_to_trash(item_ids[])`
  - `search_viewer_settings(query)` (e.g. searching for "shadows" or "PBR")
  - `update_viewer_setting(key, value)` (requires confirmation card)
- [ ] Provider adapters in `SLNG.Core.AI.Providers`:
  - `OpenAiCompatibleProvider` (OpenAI, Ollama, LM Studio, Groq)
  - `AnthropicProvider` (Claude API)
  - `GeminiProvider` (Google Gemini REST API)
- [ ] Unit tests for prompt formation, tool argument serialization, plan verification, and permission checks.

### 4. Model Context Protocol (MCP) Server Integration
- [ ] Embedded local MCP server (JSON-RPC over stdio or local HTTP/SSE on localhost only).
- [ ] Exposes standardized MCP tools matching the viewer's inventory actions:
  - `slng_list_inventory`
  - `slng_search_inventory`
  - `slng_detect_duplicates`
  - `slng_propose_organization_plan`
  - `slng_apply_approved_plan`
- [ ] External agents (Claude Desktop, Cursor, Antigravity) can connect and manage the inventory collaboratively.

### 5. Localization & Self-Test
- [ ] All UI labels, options, placeholders, and tool confirmation texts localized in `en-US.json` and `de-DE.json`.
- [ ] Integration with `--selftest` verifying window opening, provider switching, and plan parsing.
- [ ] `AppVersion` bumped upon implementation.

---

## Technical Specs & Affected Files
- `src/SLNG.Core/AI/AiProviderType.cs`
- `src/SLNG.Core/AI/AiAssistantConfig.cs`
- `src/SLNG.Core/AI/IAiProvider.cs`
- `src/SLNG.Core/AI/AiToolDefinitions.cs`
- `src/SLNG.Core/AI/AiActionPlan.cs`
- `src/SLNG.Core/AI/Mcp/SlngMcpServer.cs`
- `src/SLNG.Net/GridSession.Inventory.cs` (existing inventory move, folder create, trash operations)
- `app/scripts/UI/Preferences/AiPreferencesPage.cs`
- `app/scripts/UI/AiAssistantWindow.cs`
- `app/scripts/UI/AiActionPlanDialog.cs`
- `app/i18n/en-US.json`
- `app/i18n/de-DE.json`
- `tests/SLNG.Core.Tests/AiAssistantTests.cs`
- `tests/SLNG.Core.Tests/McpServerTests.cs`

---

## Sub-tasks / Progress
- [ ] `[Phase 1]` Core data models, `AiAssistantConfig`, and provider abstraction in `SLNG.Core.AI`.
- [ ] `[Phase 2]` Implement OpenAI-compatible & Ollama REST adapter with tool calling support.
- [ ] `[Phase 3]` Inventory analysis tool dispatchers connecting to `GridSession` methods.
- [ ] `[Phase 4]` `AiPreferencesPage` in `PreferencesWindow` with provider, endpoint, API key, and model settings.
- [ ] `[Phase 5]` `AiAssistantWindow` chat interface and `AiActionPlanDialog` confirmation modal.
- [ ] `[Phase 6]` Embedded local MCP server for external AI tools.
- [ ] `[Phase 7]` Unit test suite in `SLNG.Core.Tests` and `--selftest` integration.
