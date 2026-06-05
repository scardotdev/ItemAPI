# ItemAPI

Author: **scar.dev**  
Plugin: **ItemAPI** (`plugins/ItemAPI.cs`)  
Framework target: **Oxide/uMod and Carbon-compatible Rust server plugins**

## Overview

`ItemAPI` downloads Rust item metadata from RustHelp and (optionally) Carbon, merges it into a normalized in-memory index, caches the normalized payload, and exposes lookup/search APIs for other plugins.

## Plugin metadata

- Name: `ItemAPI`
- Author: `scar.dev`
- Version: `1.2.3`
- Description: `ItemAPI downloads and merges Rust item list JSON from RustHelp + Carbon and exposes it via a simple API for other plugins.`

## Data sources

Default endpoints:

- Primary (RustHelp): `https://rusthelp.com/downloads/admin-item-list-public.json`
- Secondary (Carbon): `https://api.carbonmod.gg/meta/rust/items.json`

## Configuration

Default config file in this repo: `configs/ItemAPI.json`.

```json
{
  "PrimaryEndpointUrl": "https://rusthelp.com/downloads/admin-item-list-public.json",
  "SecondaryEndpointUrl": "https://api.carbonmod.gg/meta/rust/items.json",
  "EnableSecondarySource": true,
  "RefreshIntervalMinutes": 1440,
  "StartupFetchDelaySeconds": 5,
  "RequestTimeoutSeconds": 10,
  "UseDiskCache": true,
  "DiskCacheFileName": "ItemAPI.cache"
}
```

- `PrimaryEndpointUrl` (`string`): required primary source URL.
- `SecondaryEndpointUrl` (`string`): secondary source URL, used when `EnableSecondarySource = true`.
- `EnableSecondarySource` (`bool`): enables/disables secondary fetch.
- `RefreshIntervalMinutes` (`int`): periodic refresh interval; `0` disables periodic refresh. If enabled, interval is clamped to at least 60 seconds.
- `StartupFetchDelaySeconds` (`int`): delay before startup fetch; negative values are clamped to immediate execution.
- `RequestTimeoutSeconds` (`int`): per-request timeout.
- `UseDiskCache` (`bool`): enables cache load/save via Oxide data filesystem.
- `DiskCacheFileName` (`string`): cache data file name; blank falls back to `ItemAPI.cache`.

## Lifecycle behavior

On `OnServerInitialized`:

1. Loads disk cache when enabled.
2. Schedules one startup refresh after `StartupFetchDelaySeconds`.
3. Schedules periodic refresh when `RefreshIntervalMinutes > 0`.

## Fetch / parse behavior

Refresh can be triggered by startup timer, interval timer, console command, or API.

For each enabled endpoint:

- Executes HTTP GET with headers:
  - `Accept: application/json`
  - `User-Agent: LoneWolfRust-ItemAPI/1.2.0`
- Requires HTTP `200` and non-empty body.
- Parses JSON as an array of item objects.
- Accepts both camelCase and PascalCase keys for fields.
- Requires a valid integer `id`; records without parseable ID are skipped.

Supported item fields:

- `shortName`
- `id`
- `displayName`
- `description`
- `iconUrl`
- `rarity`
- `category`

If all endpoints fail or produce no usable records, refresh fails and emits failure hooks with detailed error text.

## Merge / normalization behavior

When applying parsed records:

1. Null rows and rows with blank `shortName` are discarded.
2. Records are deduplicated by `id`.
   - First valid record for an ID is retained.
   - A later record with the same ID only replaces the retained one if the retained one is missing `iconUrl` and the incoming one has it, or if the retained one is missing `displayName` and the incoming one has it.
3. A short-name index is rebuilt from ID-deduplicated records using case-insensitive trimmed keys.
4. If multiple records share the same short name after ID dedupe, the plugin logs a warning and merges field-by-field, preferring existing non-empty values.
5. Normalized list is serialized and stored as raw JSON.

## Disk cache model

When cache is written, it stores:

- `UpdatedUtc` (`DateTime`)
- `RawJson` (`string`)

On cache load:

- If cache `RawJson` parses into a non-empty list, it is applied.
- `LastUpdatedUtc` is then restored from cache metadata.

## Server console commands

Server-console-only commands:

- `itemapi.refresh` → requests refresh (`reason = "manual"`).
- `itemapi.status` → prints readiness, item count, fetching flag, `LastUpdatedUtc`, and `LastError`.
- `itemapi.lookup <shortName>` → prints matching item details as JSON (or a not-found message).

Legacy aliases (for one version cycle):

- `adminitemlist.refresh`
- `adminitemlist.status`

## Public API methods

`Plugin.Call(...)` methods exposed by `ItemAPI`:

- `IsReady()` → `bool`
- `GetItemCount()` → `int`
- `GetLastUpdatedUtcIso()` → `string | null`
- `GetRawJson()` → `string | null`
- `GetItemByShortName(string shortName)` → `Dictionary<string, object> | null`
- `GetItemById(int id)` → `Dictionary<string, object> | null`
- `FindItems(string query, int maxResults = 25)` → `List<Dictionary<string, object>>`
- `GetAllItems(int skip = 0, int take = 0)` → `List<Dictionary<string, object>>`
- `RequestRefresh(string reason = "api")` → `bool`

Behavior details:

- `FindItems` matches `shortName` and `displayName` via case-insensitive substring search.
- `FindItems` returns empty list for blank query.
- `FindItems` coerces `maxResults <= 0` to `25`.
- `GetAllItems` sorts by `shortName` (case-insensitive).
- `GetAllItems` coerces negative `skip`/`take` to `0`; `take = 0` means no limit.

## DTO shape returned by lookup/search/list methods

```json
{
  "shortName": "string",
  "id": 0,
  "displayName": "string",
  "description": "string",
  "iconUrl": "string",
  "rarity": "string",
  "category": "string"
}
```

## Update hooks emitted

`ItemAPI` emits on success and failure:

```csharp
void OnItemApiUpdated(bool success, int count, string error, string reason, string updatedUtcIso)
void OnAdminItemListUpdated(bool success, int count, string error, string reason, string updatedUtcIso) // legacy alias
```
