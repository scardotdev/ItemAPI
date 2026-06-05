using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;

namespace Oxide.Plugins
{
    [Info("ItemAPI", "scar.dev", "1.2.3")]
    [Description("ItemAPI downloads and merges Rust item list JSON from RustHelp + Carbon and exposes it via a simple API for other plugins.")]
    public class ItemAPI : RustPlugin
    {
        private const string DefaultPrimaryEndpoint = "https://rusthelp.com/downloads/admin-item-list-public.json";
        private const string DefaultSecondaryEndpoint = "https://api.carbonmod.gg/meta/rust/items.json";
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
            [JsonProperty("rarity")] public string Rarity { get; set; }
            [JsonProperty("category")] public string Category { get; set; }
        }

        private class CacheModel
        {
            public DateTime UpdatedUtc { get; set; }
            public string RawJson { get; set; }
        }

        private class Configuration
        {
            [JsonProperty("PrimaryEndpointUrl")] public string PrimaryEndpointUrl { get; set; } = DefaultPrimaryEndpoint;

            [JsonProperty("SecondaryEndpointUrl")] public string SecondaryEndpointUrl { get; set; } = DefaultSecondaryEndpoint;

            [JsonProperty("EnableSecondarySource")] public bool EnableSecondarySource { get; set; } = true;

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

        [ConsoleCommand("itemapi.lookup")]
        private void CmdLookup(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                arg.ReplyWith("This is a server console command.");
                return;
            }

            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith("Usage: itemapi.lookup <shortName>");
                return;
            }

            var shortName = arg.Args[0].ToString().Trim();
            if (string.IsNullOrWhiteSpace(shortName))
            {
                arg.ReplyWith("Usage: itemapi.lookup <shortName>");
                return;
            }

            var dto = GetItemByShortName(shortName);
            if (dto == null)
            {
                arg.ReplyWith($"ItemAPI: no item found for shortName '{shortName}'.");
                return;
            }

            arg.ReplyWith($"ItemAPI lookup for '{shortName}': {JsonConvert.SerializeObject(dto)}");
        }

        #endregion

        #region Fetch / Cache

        private bool RefreshNow(string reason)
        {
            string primaryUrl = _config.PrimaryEndpointUrl?.Trim();
            if (string.IsNullOrEmpty(primaryUrl))
            {
                SetError("PrimaryEndpointUrl is empty.", reason, fireHook: true);
                return false;
            }

            lock (_sync)
            {
                if (_isFetching) return false;
                _isFetching = true;
            }

            var endpoints = new List<EndpointRequest>
            {
                new EndpointRequest(primaryUrl, "rusthelp")
            };

            if (_config.EnableSecondarySource)
            {
                var secondaryUrl = _config.SecondaryEndpointUrl?.Trim();
                if (!string.IsNullOrEmpty(secondaryUrl))
                    endpoints.Add(new EndpointRequest(secondaryUrl, "carbon"));
                else
                    PrintWarning("ItemAPI: EnableSecondarySource is true but SecondaryEndpointUrl is empty; skipping secondary source.");
            }

            FetchEndpointChain(endpoints, reason);

            return true;
        }

        private class EndpointRequest
        {
            public string Url { get; }
            public string SourceName { get; }

            public EndpointRequest(string url, string sourceName)
            {
                Url = url;
                SourceName = sourceName;
            }
        }

        private class FetchState
        {
            public readonly List<EndpointRequest> Endpoints;
            public readonly string Reason;
            public readonly List<ItemEntry> CombinedItems = new List<ItemEntry>();
            public readonly List<string> Errors = new List<string>();
            public int Index;

            public FetchState(List<EndpointRequest> endpoints, string reason)
            {
                Endpoints = endpoints;
                Reason = reason;
            }
        }

        private void FetchEndpointChain(List<EndpointRequest> endpoints, string reason)
        {
            if (endpoints == null || endpoints.Count == 0)
            {
                SetError("No endpoints configured.", reason, fireHook: true);
                lock (_sync) _isFetching = false;
                return;
            }

            FetchNextEndpoint(new FetchState(endpoints, reason));
        }

        private void FetchNextEndpoint(FetchState state)
        {
            if (state.Index >= state.Endpoints.Count)
            {
                FinalizeFetch(state);
                return;
            }

            var endpoint = state.Endpoints[state.Index++];
            var headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["User-Agent"] = "LoneWolfRust-ItemAPI/1.2.0"
            };

            webrequest.Enqueue(
                endpoint.Url,
                null,
                (code, response) =>
                {
                    if (code != 200 || string.IsNullOrWhiteSpace(response))
                    {
                        state.Errors.Add($"{endpoint.SourceName}: HTTP {(code == 0 ? "0 (no response)" : code.ToString())}");
                    }
                    else
                    {
                        try
                        {
                            var parsed = ParseItemsFromJson(response);
                            if (parsed.Count == 0)
                                state.Errors.Add($"{endpoint.SourceName}: parsed item list was empty");
                            else
                                state.CombinedItems.AddRange(parsed);
                        }
                        catch (Exception ex)
                        {
                            state.Errors.Add($"{endpoint.SourceName}: JSON parse failed ({ex.Message})");
                        }
                    }

                    FetchNextEndpoint(state);
                },
                this,
                RequestMethod.GET,
                headers,
                _config.RequestTimeoutSeconds
            );
        }

        private List<ItemEntry> ParseItemsFromJson(string rawJson)
        {
            var array = JArray.Parse(rawJson);
            var items = new List<ItemEntry>(array.Count);

            foreach (var token in array)
            {
                var obj = token as JObject;
                if (obj == null) continue;

                var shortName = ReadString(obj, "shortName", "ShortName");
                var displayName = ReadString(obj, "displayName", "DisplayName");
                var description = ReadString(obj, "description", "Description");
                var iconUrl = ReadString(obj, "iconUrl", "IconUrl");
                var rarity = ReadString(obj, "rarity", "Rarity");
                var category = ReadString(obj, "category", "Category");

                int id;
                if (!TryReadInt(obj, out id, "id", "Id"))
                    continue;

                items.Add(new ItemEntry
                {
                    ShortName = shortName,
                    Id = id,
                    DisplayName = displayName,
                    Description = description,
                    IconUrl = iconUrl,
                    Rarity = rarity,
                    Category = category
                });
            }

            return items;
        }

        private string ReadString(JObject obj, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = obj[key];
                if (token == null || token.Type == JTokenType.Null) continue;
                return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            }

            return null;
        }

        private bool TryReadInt(JObject obj, out int value, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = obj[key];
                if (token == null || token.Type == JTokenType.Null) continue;

                if (token.Type == JTokenType.Integer)
                {
                    value = token.Value<int>();
                    return true;
                }

                if (int.TryParse(token.ToString(), out value))
                    return true;
            }

            value = 0;
            return false;
        }

        private void FinalizeFetch(FetchState state)
        {
            try
            {
                if (state.CombinedItems.Count == 0)
                {
                    var details = state.Errors.Count > 0 ? string.Join("; ", state.Errors) : "all sources returned empty data";
                    SetError($"No data loaded from configured sources: {details}", state.Reason, fireHook: true);
                    return;
                }

                ApplyNewData(state.CombinedItems, state.Reason);

                if (state.Errors.Count > 0)
                    PrintWarning($"ItemAPI: refresh completed with partial source failures: {string.Join("; ", state.Errors)}");
            }
            finally
            {
                lock (_sync) _isFetching = false;
            }
        }

        private void ApplyNewData(List<ItemEntry> parsed, string reason)
        {
            // Normalize + de-dupe from one canonical list
            var byId = new Dictionary<int, ItemEntry>(parsed.Count);

            foreach (var item in parsed)
            {
                if (item == null) continue;
                if (string.IsNullOrWhiteSpace(item.ShortName)) continue;

                if (byId.TryGetValue(item.Id, out var existing))
                {
                    byId[item.Id] = MergeItem(existing, item);
                    continue;
                }

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

                var merged = item;
                if (existing != null)
                {
                    merged = MergeItem(existing, item);
                    byId[merged.Id] = merged;
                }

                byShort[trimmedShortName] = merged;
            }

            var normalized = byId.Values.ToList();
            var normalizedRawJson = JsonConvert.SerializeObject(normalized);

            lock (_sync)
            {
                _items = normalized;
                _byId = byId;
                _byShortName = byShort;
                _rawJson = normalizedRawJson;
                _lastUpdatedUtc = DateTime.UtcNow;
                _lastError = null;
            }

            if (_config.UseDiskCache)
            {
                TryWriteDiskCache(normalizedRawJson);
            }

            Puts($"ItemAPI: loaded {_items.Count:n0} items ({reason}).");

            // Cross-plugin notification hook:
            // void OnItemApiUpdated(bool success, int count, string error, string reason, string updatedUtcIso)
            // Legacy compatibility hook retained for one version cycle.
            Interface.CallHook("OnItemApiUpdated", true, _items.Count, null, reason, DateTime.UtcNow.ToString("o"));
            Interface.CallHook("OnAdminItemListUpdated", true, _items.Count, null, reason, DateTime.UtcNow.ToString("o"));
        }

        private ItemEntry MergeItem(ItemEntry existing, ItemEntry incoming)
        {
            if (existing == null) return incoming;
            if (incoming == null) return existing;

            return new ItemEntry
            {
                Id = existing.Id,
                ShortName = !string.IsNullOrWhiteSpace(existing.ShortName) ? existing.ShortName : incoming.ShortName,
                DisplayName = !string.IsNullOrWhiteSpace(existing.DisplayName) ? existing.DisplayName : incoming.DisplayName,
                Description = !string.IsNullOrWhiteSpace(existing.Description) ? existing.Description : incoming.Description,
                IconUrl = !string.IsNullOrWhiteSpace(existing.IconUrl) ? existing.IconUrl : incoming.IconUrl,
                Rarity = !string.IsNullOrWhiteSpace(existing.Rarity) ? existing.Rarity : incoming.Rarity,
                Category = !string.IsNullOrWhiteSpace(existing.Category) ? existing.Category : incoming.Category
            };
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

                ApplyNewData(parsed, "cache");
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
                ["iconUrl"] = item.IconUrl,
                ["rarity"] = item.Rarity,
                ["category"] = item.Category
            };
        }

        #endregion
    }
}
