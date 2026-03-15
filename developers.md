# ItemAPI Developer Integration Guide

Author: **scar.dev**  
Plugin: **ItemAPI**  
Current plugin version: **1.1.1**

---

## Purpose

`ItemAPI` is a shared service plugin for Rust servers running Oxide/uMod-compatible plugins (including Carbon-compatible environments). It downloads and caches Rust item metadata, then exposes a small API surface for other plugins to query by short name, by ID, or by search.

Use this document when building another plugin that **depends on ItemAPI** for item metadata.

---

## Quick Start (for other plugin authors)

1. Ensure `plugins/ItemAPI.cs` is installed and loaded before your plugin attempts lookups.
2. Resolve `ItemAPI` by name with `plugins.Find("ItemAPI")`.
3. Guard every API call with null checks and safe casts.
4. Expect temporary startup periods where `IsReady()` is false.
5. Optionally listen for update hooks to rehydrate your own plugin cache when item data changes.

---

## Dependency & Availability Pattern

Use this defensive pattern in your plugin:

```csharp
private Plugin _itemApi;

private void OnServerInitialized()
{
    _itemApi = plugins.Find("ItemAPI");

    if (_itemApi == null)
    {
        PrintWarning("ItemAPI not found. Features depending on item metadata are disabled.");
        return;
    }

    var ready = (bool?)_itemApi.Call("IsReady") ?? false;
    if (!ready)
    {
        Puts("ItemAPI found but not ready yet; waiting for first successful data load.");
    }
}
```

Recommended fallback behavior:

- If `ItemAPI` is missing: disable only dependent features, not your whole plugin.
- If `ItemAPI` is present but not ready: retry later or defer the operation.

---

## Public API Methods (Plugin.Call)

All methods below are invoked on the `Plugin` instance returned by `plugins.Find("ItemAPI")`.

### `IsReady()`

- **Call:** `itemApi.Call("IsReady")`
- **Returns:** `bool`
- **Use for:** readiness gate before expensive / user-facing metadata operations.

### `GetItemCount()`

- **Call:** `itemApi.Call("GetItemCount")`
- **Returns:** `int`
- **Use for:** diagnostics, health checks, or telemetry.

### `GetLastUpdatedUtcIso()`

- **Call:** `itemApi.Call("GetLastUpdatedUtcIso")`
- **Returns:** `string` (ISO-8601) or `null`
- **Use for:** displaying staleness age or logging.

### `GetRawJson()`

- **Call:** `itemApi.Call("GetRawJson")`
- **Returns:** `string` raw JSON or `null`
- **Use for:** advanced/custom parsing workflows (prefer DTO APIs first).

### `GetItemByShortName(string shortName)`

- **Call:** `itemApi.Call("GetItemByShortName", "rifle.ak")`
- **Returns:** `Dictionary<string, object>` or `null`
- **Behavior:** trims input and uses case-insensitive short-name matching.

### `GetItemById(int id)`

- **Call:** `itemApi.Call("GetItemById", -1461508848)`
- **Returns:** `Dictionary<string, object>` or `null`

### `FindItems(string query, int maxResults = 25)`

- **Call:** `itemApi.Call("FindItems", "med", 10)`
- **Returns:** `List<Dictionary<string, object>>`
- **Behavior:** case-insensitive substring match on `shortName` and `displayName`.

### `GetAllItems(int skip = 0, int take = 0)`

- **Call:** `itemApi.Call("GetAllItems", 0, 100)`
- **Returns:** `List<Dictionary<string, object>>`
- **Behavior:** sorted by short name, supports paging.

### `RequestRefresh(string reason = "api")`

- **Call:** `itemApi.Call("RequestRefresh", "myplugin-manual")`
- **Returns:** `bool`
  - `true` = accepted
  - `false` = already fetching

---

## DTO Contract (returned item object)

For lookup/search/list methods, each item dictionary uses these keys:

- `shortName` (`string`)
- `id` (`int`)
- `displayName` (`string`)
- `description` (`string`)
- `iconUrl` (`string`)

Helper parser:

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

---

## Cross-Plugin Update Hooks You Can Implement

`ItemAPI` emits these hooks after refresh attempts:

```csharp
private void OnItemApiUpdated(bool success, int count, string error, string reason, string updatedUtcIso)
{
    // Your plugin receives update results here.
}

private void OnAdminItemListUpdated(bool success, int count, string error, string reason, string updatedUtcIso)
{
    // Legacy compatibility alias (still emitted for one version cycle).
}
```

Practical use cases:

- Rebuild your own item cache when `success == true`.
- Degrade gracefully (without hard failure) when `success == false`.
- Log `reason` to differentiate startup, interval, manual, cache, or API-triggered refresh paths.

---

## End-to-End Example (Safe Integration)

```csharp
private Plugin _itemApi;

private void OnServerInitialized()
{
    _itemApi = plugins.Find("ItemAPI");
}

private bool TryResolveItemByShortName(string shortName, out int id, out string displayName)
{
    id = 0;
    displayName = string.Empty;

    if (_itemApi == null)
        _itemApi = plugins.Find("ItemAPI");

    if (_itemApi == null)
        return false;

    var isReady = (bool?)_itemApi.Call("IsReady") ?? false;
    if (!isReady)
        return false;

    var row = _itemApi.Call("GetItemByShortName", shortName) as Dictionary<string, object>;
    if (row == null)
        return false;

    if (!row.TryGetValue("id", out var idObj))
        return false;

    if (idObj is long l) id = (int)l;
    else if (idObj is int i) id = i;
    else if (!int.TryParse(idObj?.ToString(), out id))
        return false;

    row.TryGetValue("displayName", out var dnObj);
    displayName = dnObj?.ToString() ?? shortName;

    return true;
}
```

---

## Best Practices for Plugin Authors

- Prefer `GetItemByShortName` or `GetItemById` for targeted lookups.
- Use `FindItems` for user-driven fuzzy searches.
- Avoid calling `GetAllItems` repeatedly in hot paths; cache results in your plugin when needed.
- Never assume `ItemAPI` is always loaded or ready.
- Treat all plugin-call return values as untrusted dynamic objects; validate and cast defensively.
- If you call `RequestRefresh`, avoid spamming; it can return `false` while a fetch is already in progress.

---

## Troubleshooting Checklist

If your integration is not working:

1. Confirm ItemAPI is loaded (`o.plugins` / plugin list command in your framework).
2. Confirm readiness via `IsReady()`.
3. Check server logs for ItemAPI warnings (endpoint issues, parse failures, empty payload).
4. Verify your key names match DTO contract exactly (`shortName`, `id`, `displayName`, `description`, `iconUrl`).
5. Verify your casts handle both `int` and `long` numeric representations.

---

## Support Notes

When requesting support, include:

- Your plugin name and version.
- ItemAPI version.
- The exact API method and argument(s) you called.
- Any relevant console/log output.
- Whether issue occurs at startup only or persists during runtime.

This helps isolate whether the issue is dependency loading, readiness timing, data contract handling, or endpoint refresh state.
