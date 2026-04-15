# ItemAPI Developer Integration Guide

Author: **scar.dev**  
Plugin: **ItemAPI**  
Current plugin version: **1.2.0**

## Purpose

`ItemAPI` is a shared service plugin for Rust servers that provides normalized item metadata to other plugins.

It fetches item JSON from configured endpoints, normalizes/merges it, caches it, and exposes safe plugin-call methods for lookup/search/access.

## Quick Start

1. Ensure `plugins/ItemAPI.cs` is loaded before your plugin performs lookups.
2. Resolve the plugin with `plugins.Find("ItemAPI")`.
3. Null-check plugin references and defensively cast all `Plugin.Call` results.
4. Expect startup windows where `IsReady()` is `false`.
5. Optionally consume update hooks to refresh your plugin-side cache.

## API Surface (`Plugin.Call`)

- `IsReady()` → `bool`
- `GetItemCount()` → `int`
- `GetLastUpdatedUtcIso()` → `string | null`
- `GetRawJson()` → `string | null`
- `GetItemByShortName(string shortName)` → `Dictionary<string, object> | null`
- `GetItemById(int id)` → `Dictionary<string, object> | null`
- `FindItems(string query, int maxResults = 25)` → `List<Dictionary<string, object>>`
- `GetAllItems(int skip = 0, int take = 0)` → `List<Dictionary<string, object>>`
- `RequestRefresh(string reason = "api")` → `bool` (`false` when a fetch is already in progress)

Behavior notes:

- `GetItemByShortName` trims input and uses case-insensitive matching.
- `FindItems` matches on `shortName` and `displayName` with case-insensitive substring logic.
- `FindItems` returns empty list for blank query and coerces `maxResults <= 0` to `25`.
- `GetAllItems` sorts by `shortName` case-insensitively.
- `GetAllItems` coerces negative `skip`/`take` to `0`; `take = 0` means unlimited after skip.

## DTO Contract

Returned dictionaries include:

- `shortName` (`string`)
- `id` (`int`)
- `displayName` (`string`)
- `description` (`string`)
- `iconUrl` (`string`)
- `rarity` (`string`)
- `category` (`string`)

Example parser:

```csharp
private bool TryReadItem(Dictionary<string, object> row, out string shortName, out int id, out string displayName)
{
    shortName = null;
    id = 0;
    displayName = null;

    if (row == null) return false;

    if (!row.TryGetValue("shortName", out var snObj) || snObj == null) return false;
    if (!row.TryGetValue("id", out var idObj) || idObj == null) return false;

    shortName = snObj.ToString();

    if (idObj is long l) id = (int)l;
    else if (idObj is int i) id = i;
    else if (!int.TryParse(idObj.ToString(), out id)) return false;

    row.TryGetValue("displayName", out var dnObj);
    displayName = dnObj?.ToString() ?? string.Empty;

    return true;
}
```

## Refresh + Hook Semantics

ItemAPI may refresh via startup timer, periodic timer, console command, or API call.

After each refresh attempt, it emits:

```csharp
private void OnItemApiUpdated(bool success, int count, string error, string reason, string updatedUtcIso)
{
    // Primary hook.
}

private void OnAdminItemListUpdated(bool success, int count, string error, string reason, string updatedUtcIso)
{
    // Legacy compatibility hook alias.
}
```

Use these hooks to rehydrate your own caches and log failures without hard-crashing dependent features.

## Merge Behavior (Important for Consumers)

Normalization pipeline in `ItemAPI`:

1. Discards rows with blank `shortName`.
2. Deduplicates by `id`.
   - First usable item for an ID is retained.
   - Later duplicate ID can replace only when it improves missing `iconUrl` or missing `displayName`.
3. Rebuilds short-name index (trimmed, case-insensitive).
4. Logs short-name collisions and merges colliding records field-by-field, preferring already-populated values.

Implication: for duplicate IDs and short names, results are deterministic but not strictly "last writer wins".

## Configuration Defaults (from `configs/ItemAPI.json`)

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

## Console Commands (Server Console Only)

- `itemapi.refresh`
- `itemapi.status`

Legacy aliases (still registered):

- `adminitemlist.refresh`
- `adminitemlist.status`
