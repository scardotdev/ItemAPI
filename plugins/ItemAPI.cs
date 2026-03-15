using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;

namespace Oxide.Plugins
{
    [Info("ItemAPI", "scar.dev", "1.1.1")]
    [Description("ItemAPI downloads and caches RustHelp item list JSON and exposes it via a simple API for other plugins.")]
    public class ItemAPI : RustPlugin
    {
        private const string DefaultEndpoint = "https://rusthelp.com/downloads/admin-item-list-public.json";
        private const string CacheDataFileName = "ItemAPI.cache";

        private Configuration _config;

        private readonly object _sync = new object();
        private bool _isFetching;
        private string _lastError;
        private DateTime? _lastUpdatedUtc;

        private string _rawJson;
        private List<ItemEntry> _items = new List<ItemEntry>();
        private Dictionary<int, ItemEntry> _byId = new Dictionary<int, ItemEntry>();
        private Dictionary<string, ItemEntry> _byShortName = new Dictionary<string, ItemEntry>(StringComparer.OrdinalIgnoreCase);

        #region Models

        private class ItemEntry
        {
            [JsonProperty("shortName")] public string ShortName { get; set; }
            [JsonProperty("id")] public int Id { get; set; }
            [JsonProperty("displayName")] public string DisplayName { get; set; }
            [JsonProperty("description")] public string Description { get; set; }
            [JsonProperty("iconUrl")] public string IconUrl { get; set; }
        }

        private class CacheModel
        {
            public DateTime UpdatedUtc { get; set; }
            public string RawJson { get; set; }
        }

        private class Configuration
        {
            [JsonProperty("EndpointUrl")] public string EndpointUrl { get; set; } = DefaultEndpoint;

            // Set to 0 to disable periodic refresh (manual refresh still works).
            [JsonProperty("RefreshIntervalMinutes")] public int RefreshIntervalMinutes { get; set; } = 1440; // 24h

            [JsonProperty("StartupFetchDelaySeconds")] public int StartupFetchDelaySeconds { get; set; } = 5;

            [JsonProperty("RequestTimeoutSeconds")] public int RequestTimeoutSeconds { get; set; } = 10;

            [JsonProperty("UseDiskCache")] public bool UseDiskCache { get; set; } = true;

            [JsonProperty("DiskCacheFileName")] public string DiskCacheFileName { get; set; } = CacheDataFileName;
        }

        #endregion

        #region Oxide Lifecycle

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>() ?? new Configuration();
            }
            catch
            {
                PrintWarning("Config was invalid; regenerating defaults.");
                _config = new Configuration();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void OnServerInitialized()
        {
            if (_config.UseDiskCache)
            {
                TryLoadFromDiskCache();
            }

            timer.Once(Math.Max(0f, _config.StartupFetchDelaySeconds), () => RefreshNow("startup"));

            if (_config.RefreshIntervalMinutes > 0)
            {
                var seconds = Math.Max(60f, _config.RefreshIntervalMinutes * 60f); // at least 60s
                timer.Every(seconds, () => RefreshNow("interval"));
            }
        }

        #endregion

        #region Console Commands

        [ConsoleCommand("itemapi.refresh")]
        private void CmdRefresh(ConsoleSystem.Arg arg)
        {
            HandleRefreshCommand(arg, "itemapi.refresh");
        }

        // Backward-compatible alias; remove after one version cycle.
        [ConsoleCommand("adminitemlist.refresh")]
        private void CmdRefreshLegacy(ConsoleSystem.Arg arg)
        {
            HandleRefreshCommand(arg, "adminitemlist.refresh");
        }

        private void HandleRefreshCommand(ConsoleSystem.Arg arg, string commandName)
        {
            if (arg.Connection != null)
            {
                arg.ReplyWith("This is a server console command.");
                return;
            }

            if (!RefreshNow("manual"))
            {
                arg.ReplyWith("ItemAPI: fetch already in progress.");
                return;
            }

            var isLegacyCommand = commandName.Equals("adminitemlist.refresh", StringComparison.OrdinalIgnoreCase);
            var deprecationMessage = isLegacyCommand ? " (legacy alias; use itemapi.refresh)" : string.Empty;
            arg.ReplyWith($"ItemAPI: refresh requested via {commandName}.{deprecationMessage}");
        }

        [ConsoleCommand("itemapi.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            ReplyWithStatus(arg, "itemapi.status");
        }

        // Backward-compatible alias; remove after one version cycle.
        [ConsoleCommand("adminitemlist.status")]
        private void CmdStatusLegacy(ConsoleSystem.Arg arg)
        {
            ReplyWithStatus(arg, "adminitemlist.status");
        }

        private void ReplyWithStatus(ConsoleSystem.Arg arg, string commandName)
        {
            if (arg.Connection != null)
            {
                arg.ReplyWith("This is a server console command.");
                return;
            }

            bool ready;
            int count;
            bool fetching;
            string lastErr;
            DateTime? updated;

            lock (_sync)
            {
                ready = _items.Count > 0;
                count = _items.Count;
                fetching = _isFetching;
                lastErr = _lastError;
                updated = _lastUpdatedUtc;
            }

            arg.ReplyWith(
                "ItemAPI status:\n" +
                $"- Command: {commandName}\n" +
                $"- Ready: {ready}\n" +
                $"- Items: {count}\n" +
                $"- Fetching: {fetching}\n" +
                $"- LastUpdatedUtc: {(updated.HasValue ? updated.Value.ToString("o") : "null")}\n" +
                $"- LastError: {(string.IsNullOrEmpty(lastErr) ? "none" : lastErr)}"
            );
        }

        #endregion

        #region Fetch / Cache

        private bool RefreshNow(string reason)
        {
            string url = _config.EndpointUrl?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                SetError("EndpointUrl is empty.", reason, fireHook: true);
                return false;
            }

            lock (_sync)
            {
                if (_isFetching) return false;
                _isFetching = true;
            }

            var headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["User-Agent"] = "LoneWolfRust-ItemAPI/1.1.1"
            };

            webrequest.Enqueue(
                url,
                null,
                (code, response) => OnFetchComplete(code, response, reason),
                this,
                RequestMethod.GET,
                headers,
                _config.RequestTimeoutSeconds
            );

            return true;
        }

        private void OnFetchComplete(int code, string response, string reason)
        {
            try
            {
                if (code != 200 || string.IsNullOrWhiteSpace(response))
                {
                    SetError($"HTTP {(code == 0 ? "0 (no response)" : code.ToString())} from endpoint.", reason, fireHook: true);
                    return;
                }

                List<ItemEntry> parsed;
                try
                {
                    parsed = JsonConvert.DeserializeObject<List<ItemEntry>>(response);
                }
                catch (Exception ex)
                {
                    SetError($"JSON parse failed: {ex.Message}", reason, fireHook: true);
                    return;
                }

                if (parsed == null || parsed.Count == 0)
                {
                    SetError("Parsed item list was empty.", reason, fireHook: true);
                    return;
                }

                ApplyNewData(parsed, response, reason);
            }
            finally
            {
                lock (_sync) _isFetching = false;
            }
        }

        private void ApplyNewData(List<ItemEntry> parsed, string rawJson, string reason)
        {
            // Normalize + de-dupe from one canonical list
            var byId = new Dictionary<int, ItemEntry>(parsed.Count);

            foreach (var item in parsed)
            {
                if (item == null) continue;
                if (string.IsNullOrWhiteSpace(item.ShortName)) continue;

                // If duplicate IDs exist, "last one wins"
                byId[item.Id] = item;
            }

            var cleaned = byId.Values.ToList();
            var byShort = new Dictionary<string, ItemEntry>(cleaned.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var item in cleaned)
            {
                var trimmedShortName = item.ShortName.Trim();
                if (byShort.TryGetValue(trimmedShortName, out var existing))
                {
                    PrintWarning($"ItemAPI: shortName collision after ID de-duplication: '{trimmedShortName}' replaced id {existing.Id} with id {item.Id}.");
                }

                // If duplicate short names exist in cleaned list, "last one wins"
                byShort[trimmedShortName] = item;
            }

            lock (_sync)
            {
                _items = cleaned;
                _byId = byId;
                _byShortName = byShort;
                _rawJson = rawJson;
                _lastUpdatedUtc = DateTime.UtcNow;
                _lastError = null;
            }

            if (_config.UseDiskCache)
            {
                TryWriteDiskCache(rawJson);
            }

            Puts($"ItemAPI: loaded {_items.Count:n0} items ({reason}).");

            // Cross-plugin notification hook:
            // void OnItemApiUpdated(bool success, int count, string error, string reason, string updatedUtcIso)
            // Legacy compatibility hook retained for one version cycle.
            Interface.CallHook("OnItemApiUpdated", true, _items.Count, null, reason, DateTime.UtcNow.ToString("o"));
            Interface.CallHook("OnAdminItemListUpdated", true, _items.Count, null, reason, DateTime.UtcNow.ToString("o"));
        }

        private void SetError(string error, string reason, bool fireHook)
        {
            lock (_sync)
            {
                _lastError = error;
            }

            PrintWarning($"ItemAPI: {error} ({reason})");

            if (fireHook)
            {
                int count;
                lock (_sync) count = _items.Count;

                Interface.CallHook("OnItemApiUpdated", false, count, error, reason, DateTime.UtcNow.ToString("o"));
                Interface.CallHook("OnAdminItemListUpdated", false, count, error, reason, DateTime.UtcNow.ToString("o"));
            }
        }

        private void TryLoadFromDiskCache()
        {
            try
            {
                var fileName = string.IsNullOrWhiteSpace(_config.DiskCacheFileName) ? CacheDataFileName : _config.DiskCacheFileName.Trim();
                var cache = Interface.Oxide.DataFileSystem.ReadObject<CacheModel>(fileName);

                if (cache == null || string.IsNullOrWhiteSpace(cache.RawJson))
                    return;

                var parsed = JsonConvert.DeserializeObject<List<ItemEntry>>(cache.RawJson);
                if (parsed == null || parsed.Count == 0)
                    return;

                ApplyNewData(parsed, cache.RawJson, "cache");
                lock (_sync) _lastUpdatedUtc = cache.UpdatedUtc; // preserve cache timestamp
            }
            catch (Exception ex)
            {
                PrintWarning($"ItemAPI: failed to load disk cache: {ex.Message}");
            }
        }

        private void TryWriteDiskCache(string rawJson)
        {
            try
            {
                var fileName = string.IsNullOrWhiteSpace(_config.DiskCacheFileName) ? CacheDataFileName : _config.DiskCacheFileName.Trim();
                var cache = new CacheModel
                {
                    UpdatedUtc = DateTime.UtcNow,
                    RawJson = rawJson
                };

                Interface.Oxide.DataFileSystem.WriteObject(fileName, cache);
            }
            catch (Exception ex)
            {
                PrintWarning($"ItemAPI: failed to write disk cache: {ex.Message}");
            }
        }

        #endregion

        #region Public API (for other plugins)

        // Returns: bool
        private bool IsReady()
        {
            lock (_sync) return _items.Count > 0;
        }

        // Returns: int
        private int GetItemCount()
        {
            lock (_sync) return _items.Count;
        }

        // Returns: string (ISO-8601) or null
        private string GetLastUpdatedUtcIso()
        {
            lock (_sync) return _lastUpdatedUtc?.ToString("o");
        }

        // Returns: string (raw JSON) or null
        private string GetRawJson()
        {
            lock (_sync) return _rawJson;
        }

        // Returns: Dictionary<string, object> or null
        private Dictionary<string, object> GetItemByShortName(string shortName)
        {
            if (string.IsNullOrWhiteSpace(shortName)) return null;

            lock (_sync)
            {
                if (_byShortName.TryGetValue(shortName.Trim(), out var item))
                    return ToDto(item);
            }

            return null;
        }

        // Returns: Dictionary<string, object> or null
        private Dictionary<string, object> GetItemById(int id)
        {
            lock (_sync)
            {
                if (_byId.TryGetValue(id, out var item))
                    return ToDto(item);
            }

            return null;
        }

        // Returns: List<Dictionary<string, object>>
        // Search matches ShortName or DisplayName (case-insensitive)
        private List<Dictionary<string, object>> FindItems(string query, int maxResults = 25)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<Dictionary<string, object>>();
            if (maxResults <= 0) maxResults = 25;

            string q = query.Trim();

            List<ItemEntry> snapshot;
            lock (_sync) snapshot = _items.ToList();

            return snapshot
                .Where(i =>
                    (!string.IsNullOrEmpty(i.ShortName) && i.ShortName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(i.DisplayName) && i.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                .Take(maxResults)
                .Select(ToDto)
                .ToList();
        }

        // Returns: List<Dictionary<string, object>>
        // Use with care on large lists. You can page via skip/take.
        private List<Dictionary<string, object>> GetAllItems(int skip = 0, int take = 0)
        {
            if (skip < 0) skip = 0;
            if (take < 0) take = 0;

            List<ItemEntry> snapshot;
            lock (_sync) snapshot = _items.ToList();

            IEnumerable<ItemEntry> q = snapshot.OrderBy(i => i.ShortName, StringComparer.OrdinalIgnoreCase).Skip(skip);
            if (take > 0) q = q.Take(take);

            return q.Select(ToDto).ToList();
        }

        // Triggers a refresh; returns bool "accepted" (false if already fetching)
        private bool RequestRefresh(string reason = "api")
        {
            return RefreshNow(reason);
        }

        private Dictionary<string, object> ToDto(ItemEntry item)
        {
            return new Dictionary<string, object>
            {
                ["shortName"] = item.ShortName,
                ["id"] = item.Id,
                ["displayName"] = item.DisplayName,
                ["description"] = item.Description,
                ["iconUrl"] = item.IconUrl
            };
        }

        #endregion
    }
}
