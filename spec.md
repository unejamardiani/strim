# Strim – Functional & Technical Spec

## 1. Overview

**Strim** is a tool for managing and cleaning IPTV playlists (primarily M3U/M3U8).
The goal is to turn large, cluttered lists into clean, performant, and user-tailored playlists.

**Main Objectives**

* Automatically remove unwanted channel groups (e.g. "XXX", "Arab", "Test", etc.).
* Keep, rename, or move relevant channels into custom groups based on rules.
* Provide repeatable, versionable configuration (rulesets) instead of one-off manual editing.
* Optional: Web UI / CLI for easy usage and integration into existing setups (Docker, NAS, home server).

**Non-Goals (v1)**

* No dedicated EPG server.
* No full IPTV frontend (no stream playback).
* No integrated account management for providers.

---

## 2. Use Cases / User Stories

### 2.1 Core Use Cases

1. **Clean a raw M3U**

   * *As* a user
   * *I want* to import a raw M3U from my IPTV provider (URL or file),
   * *so that* I get a new, "clean" playlist with only the channels that are relevant to me.

2. **Remove unwanted groups**

   * *As* a user
   * *I want* to remove groups based on `group-title` rules (e.g. via whitelist/blacklist),
   * *so that* I can get rid of all XXX, test, or irrelevant country groups.

3. **Filter channels by name patterns**

   * *As* a user
   * *I want* to filter channels via regular expressions or simple contains rules,
   * *so that* I can, for example, keep only FHD/HD versions or remove duplicates.

4. **Define favorites / custom groups**

   * *As* a user
   * *I want* to define my own groups and favorites lists,
   * *so that* I end up with a compact "daily use" playlist.

5. **Save output as M3U**

   * *As* a user
   * *I want* to save the cleaned playlist as an M3U file or expose it via URL,
   * *so that* I can load it into players like IPTV Smarters, TiviMate, Kodi, Plex, etc.

### 2.2 Advanced Use Cases (optional / future)

6. **Rule profiles / presets**

   * Manage multiple rule sets (e.g. "Living Room", "Bedroom", "Parents").

7. **Scheduled updates**

   * Automatically regenerate the playlist at intervals (e.g. daily) and publish it.

8. **Multi-provider support**

   * Merge multiple input playlists (e.g. Provider A + Provider B) and clean them together.

---

## 3. Functional Requirements

### 3.1 Input

* **Formats:** M3U / M3U8 (UTF-8, optionally other encodings).
* **Sources:**

  * HTTP(S) URL
  * Local file
* **Validation:**

  * Detect invalid lines.
  * Minimal check: EXTINF lines + stream URL must appear in pairs.

### 3.2 Parsing & Domain Model

**Entities:**

* `Playlist`

  * `id`
  * `name`
  * `source` (url | file path)
  * `channels: Channel[]`

* `Channel`

  * `id`
  * `name` (from `tvg-name` or from EXTINF)
  * `groupTitle` (from `group-title`)
  * `tvgId`
  * `tvgLogo`
  * `url` (stream URL)
  * `rawExtinf` (for debugging)

* `RuleSet`

  * `id`
  * `name`
  * `rules: Rule[]`

* `Rule`

  * `id`
  * `type` (`Include`, `Exclude`, `Transform`, `GroupRemap`, `Rename`)
  * `scope` (`ChannelName`, `GroupTitle`, `TvgId`, `TvgName`, `Url`)
  * `pattern` (string, optional regex)
  * `action` (e.g. `Keep`, `Drop`, `MoveToGroup`, `RenameChannel`)
  * `parameters` (e.g. target group, new name)

### 3.3 Rule Engine (Cleaner)

* Rules are executed in a defined order (configurable priority).
* **Basic principle:**

  * Start with all channels.
  * Apply exclude rules → channels are marked/removed.
  * Apply include rules → channels are explicitly kept.
  * Apply transform/rename/group rules → channels are modified.
* Option for **dry run**:

  * Shows how many channels would be removed / kept without writing output.
* Support for:

  * Simple contains (case-insensitive)
  * StartsWith/EndsWith
  * Regex (optionally enabled)

### 3.4 Output

* Generate a new M3U(M3U8) file:

  * Update all EXTINF attributes accordingly.
  * `group-title` according to the final group assignment.
  * Optional: normalization of logos/EPG IDs (if added later).
* Output targets:

  * Local file (configurable path).
  * Optional: HTTP endpoint (e.g. embedded mini web server or via reverse proxy).

---

## 4. Cleaner Manager (Module Spec)

The **Cleaner Manager** is the component responsible for managing rule sets and applying them to playlists.

### 4.1 Responsibilities

* CRUD operations for rule sets
* Versioning of rule sets (optional)
* Assigning rule sets to playlists/profiles
* Triggering clean runs (manual / scheduled)
* Providing preview functions (e.g. "before/after" statistics)

### 4.2 Functions

1. **Create rule set**

   * Name, description
   * Empty rule set or based on a template (e.g. "Standard blacklist: adult/test/country X/Y")

2. **Add/edit/delete rules**

   * Form-based or JSON editor (depending on UI)
   * Validation (no invalid regex, valid types, etc.)

3. **Assign rule set**

   * Assign a rule set to an input (playlist source) or a "profile".

4. **Run preview**

   * Number of channels before/after
   * Show sample channels that will be removed / kept

5. **Execute cleaning**

   * Based on the selected rule set
   * Define output location (file or URL)

6. **Audit / logging (basic)**

   * Log entry per run: timestamp, source, rule set, number of channels before/after.

---

## 5. Interfaces / API (Proposal)

### 5.1 REST API (Example)

* `GET /api/playlists` – List known playlists
* `POST /api/playlists/parse` – Read and parse playlist from URL/file
* `GET /api/rulesets` – List all rule sets
* `POST /api/rulesets` – Create new rule set
* `PUT /api/rulesets/{id}` – Update rule set
* `DELETE /api/rulesets/{id}` – Delete rule set
* `POST /api/clean` – Execute cleaning
  Payload: `playlistId`, `rulesetId`, `outputConfig`

---

## 6. Non-Functional Requirements

* **Performance:**

  * 50k+ channels should be processed within seconds.
* **Configurability:**

  * Rule sets as files (e.g. YAML/JSON) and versioned via Git.
* **Portability:**

  * Runs in a Docker container
  * Optimized for lightweight cloud services (Azure App Service, etc.)
* **Logging & Monitoring:**

  * Structured logs (JSON), levels: Info, Warn, Error.

---

## 7. Tech Stack (Placeholder – adapt to actual project)

> Adapt this section to your concrete setup (e.g. .NET, Node, Go, …).

* Backend: `.NET 10` 
* Interfaces: REST API
* UI: SPA with React
* Database: Postgres
* Deployment: Docker image, docker-compose / Kubernetes / Multi-Cloud compatibility start with Azure

---

## 8. Open Points / TODO

* EPG mapping (optional, separate spec).
* Define multi-provider merge in more detail.
* User management (if the Web UI is exposed).
* Document integration into existing home server setups.
