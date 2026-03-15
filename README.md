# ItemAPI

Author: **scar.dev**  
Plugin: **ItemAPI** (`plugins/ItemAPI.cs`)  
Framework target: **Oxide/uMod and Carbon-compatible Rust server plugins**

## Overview

`ItemAPI` downloads RustHelp's public admin item list JSON, validates/parses it, caches it in memory, optionally persists the raw payload to disk, and exposes item lookup/search methods that other plugins can call.

In short, this plugin acts as a **shared item metadata service** for your server-side plugin ecosystem.

## What this plugin does

- Fetches item data from a configurable JSON endpoint (`EndpointUrl`).
- Parses each item into a normalized internal model:
  - `shortName`
  - `id`
  - `displayName`
  - `description`
  - `iconUrl`
- Maintains multiple fast lookup indexes:
  - by integer item ID
  - by case-insensitive short name
- Keeps the original raw JSON payload available to other plugins.
- Optionally stores/loads a disk cache for faster warm start and resilience.
- Performs delayed startup fetch and optional periodic refresh.
- Exposes API methods other plugins can call (lookup, search, enumerate, refresh, status-style helpers).
- Emits a cross-plugin hook whenever refresh succeeds or fails.

## Data source

Default endpoint:

- `https://rusthelp.com/downloads/admin-item-list-public.json`

You can override this via config.

## Plugin metadata

From `plugins/ItemAPI.cs`:

- Name: `ItemAPI`
- Author: `scar.dev`
- Version: `1.1.1`
- Description: ItemAPI downloads and caches RustHelp item list JSON and exposes it via a simple API for other plugins.

## Lifecycle behavior

On `OnServerInitialized` the plugin:

1. Loads from disk cache (if `UseDiskCache = true` and cache exists/valid).
2. Schedules a startup refresh after `StartupFetchDelaySeconds`.
3. Schedules periodic refresh if `RefreshIntervalMinutes > 0`.

### Refresh flow

When refresh is requested (startup / interval / manual / API):

1. Validates configured endpoint URL.
2. Avoids overlapping requests using an `_isFetching` gate.
3. Sends HTTP GET with headers:
   - `Accept: application/json`
   - `User-Agent: LoneWolfRust-ItemAPI/1.1.1`
4. Handles response:
   - requires HTTP 200 and non-empty payload
   - deserializes JSON into `List<ItemEntry>`
   - rejects empty parsed list
5. Applies data:
   - drops null/invalid records (missing/blank short name)
   - de-duplicates by `id` first (`last id wins`)
   - builds a canonical cleaned list from the deduped ID dictionary
   - rebuilds short-name index from that canonical list (`last short-name in cleaned list wins`)
   - logs short-name collisions found during short-name index rebuild
   - rebuilds in-memory indexes
   - updates timestamps/state
6. Writes disk cache (if enabled)
7. Fires update hooks (`OnItemApiUpdated` and legacy `OnAdminItemListUpdated`) with success/failure details.

## De-duplication and normalization details

During apply:

- Entries with null item object are skipped.
- Entries with empty/whitespace `shortName` are skipped.
- Duplicate IDs are overwritten by later entries in payload order.
- `_items` (canonical list) is created from deduplicated ID dictionary values.
- Duplicate short names are resolved only after ID dedupe, while rebuilding `_byShortName` from the canonical list.
- Short-name keys are trimmed and matched case-insensitively.
- If multiple canonical entries share the same short name, the later canonical entry overwrites the earlier one and a warning is logged.

This ensures consumers query a consistent and fast in-memory representation.

## Configuration

Default config file location in this repository: `config/ItemAPI.json`.

The plugin creates/uses a config object with these fields:

```json
{
  "EndpointUrl": "https://rusthelp.com/downloads/admin-item-list-public.json",
  "RefreshIntervalMinutes": 1440,
  "StartupFetchDelaySeconds": 5,
  "RequestTimeoutSeconds": 10,
  "UseDiskCache": true,
  "DiskCacheFileName": "ItemAPI.cache"
}
```

### Config reference

- `EndpointUrl` (`string`)
  - Source URL for item JSON.
  - Must be non-empty or refresh will fail.

- `RefreshIntervalMinutes` (`int`)
  - Periodic auto-refresh cadence.
  - `0` disables interval refresh (manual/API refresh still works).
  - Internally clamped to at least 60 seconds if enabled.

- `StartupFetchDelaySeconds` (`int`)
  - Delay before first startup refresh request.
  - Negative values effectively become immediate (`0`) due to clamping.

- `RequestTimeoutSeconds` (`int`)
  - HTTP timeout for the fetch request.

- `UseDiskCache` (`bool`)
  - Enables load/save of cached payload via Oxide data filesystem.

- `DiskCacheFileName` (`string`)
  - Data file name for cache model.
  - Falls back to `ItemAPI.cache` if blank.

## Disk cache model

When enabled, the plugin writes a cache object containing:

- `UpdatedUtc` (`DateTime`)
- `RawJson` (`string`)

On startup cache load:

- If cache payload parses to valid non-empty list, it is applied.
- `LastUpdatedUtc` is then restored to cached timestamp.
- Invalid cache data is ignored with warning (plugin continues running).

## Server console commands

> These are **server console only** commands. If run by a player connection, the plugin replies that it is server-console-only.

- `itemapi.refresh`
  - Triggers manual refresh (`reason = "manual"`).
  - Reply includes command context and confirms refresh request.

- `itemapi.status`
  - Prints:
    - ready state
    - item count
    - fetching state
    - last updated UTC ISO string or null
    - last error or `none`

Legacy aliases (kept for one version cycle):

- `adminitemlist.refresh` (alias to `itemapi.refresh`)
- `adminitemlist.status` (alias to `itemapi.status`)

## Public API for other plugins

The following methods are exposed as plugin-callable API methods (via `Plugin.Call(...)`).

### `IsReady()`

- Returns: `bool`
- Meaning: whether at least one item is loaded.

### `GetItemCount()`

- Returns: `int`
- Meaning: current in-memory item count.

### `GetLastUpdatedUtcIso()`

- Returns: `string` (ISO-8601) or `null`
- Meaning: timestamp of last successful apply.

### `GetRawJson()`

- Returns: `string` or `null`
- Meaning: raw JSON payload most recently applied.

### `GetItemByShortName(string shortName)`

- Returns: `Dictionary<string, object>` or `null`
- Behavior:
  - trims input
  - case-insensitive short name lookup

### `GetItemById(int id)`

- Returns: `Dictionary<string, object>` or `null`

### `FindItems(string query, int maxResults = 25)`

- Returns: `List<Dictionary<string, object>>`
- Behavior:
  - case-insensitive contains-match against `shortName` and `displayName`
  - returns up to `maxResults`
  - if `maxResults <= 0`, resets to `25`
  - empty query returns empty list

### `GetAllItems(int skip = 0, int take = 0)`

- Returns: `List<Dictionary<string, object>>`
- Behavior:
  - sorted by `shortName` (case-insensitive)
  - supports paging (`skip` then optional `take`)
  - `take <= 0` means "no upper limit"

### `RequestRefresh(string reason = "api")`

- Returns: `bool`
- Meaning:
  - `true` if refresh request accepted
  - `false` if a fetch is already in progress

### DTO shape returned by lookup/search/list methods

```json
{
  "shortName": "string",
  "id": 0,
  "displayName": "string",
  "description": "string",
  "iconUrl": "string"
}
```

## Cross-plugin hook emitted by ItemAPI

The plugin calls:

```csharp
void OnItemApiUpdated(bool success, int count, string error, string reason, string updatedUtcIso)
void OnAdminItemListUpdated(bool success, int count, string error, string reason, string updatedUtcIso) // legacy alias for one version cycle
```

Parameters:

- `success`: `true` on successful refresh/apply, otherwise `false`
- `count`: number of currently loaded items
- `error`: null on success, message on failure
- `reason`: refresh origin (`startup`, `interval`, `manual`, `api`, `cache`, etc.)
- `updatedUtcIso`: current UTC timestamp when hook fired

## Integration example (another plugin)

```csharp
var itemApi = plugins.Find("ItemAPI");
if (itemApi == null) return;

bool isReady = (bool)(itemApi.Call("IsReady") ?? false);
if (!isReady) return;

var row = itemApi.Call("GetItemByShortName", "rifle.ak") as Dictionary<string, object>;
if (row != null)
{
    Puts($"AK id = {row["id"]}, displayName = {row["displayName"]}");
}

var found = itemApi.Call("FindItems", "med", 10) as List<Dictionary<string, object>>;
Puts($"Matches: {found?.Count ?? 0}");
```

## Logging and diagnostics

On success, plugin logs loaded count with reason.

On failures, plugin logs warning messages such as:

- empty endpoint
- non-200/no-response HTTP status
- JSON parse failure
- empty parsed list
- cache read/write exceptions

## Thread-safety and state management

The plugin uses a private `_sync` lock to protect mutable shared state including:

- fetch gate (`_isFetching`)
- item collections and indexes
- last error and update timestamp
- raw JSON payload

Read APIs generally snapshot under lock before LINQ operations where appropriate.

## Operational notes

- Designed for production-friendly periodic background refresh.
- Survives transient endpoint failures by preserving last successful in-memory data.
- Disk cache can improve startup resilience if endpoint is temporarily unavailable.
- If endpoint schema changes incompatibly, parse errors will be reported through logs and update hook error payload.

## Compatibility notes

- Built against Rust plugin conventions (`RustPlugin`) and Oxide APIs.
- Carbon environments that preserve Oxide compatibility should work as expected.
- Command and log naming uses `ItemAPI` as the canonical namespace. Legacy console commands (`adminitemlist.refresh`/`adminitemlist.status`) and legacy hook (`OnAdminItemListUpdated`) are still emitted for one version cycle to preserve compatibility.
