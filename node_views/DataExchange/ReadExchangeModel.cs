using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DataExchange.BaseModels;
using Autodesk.DataExchange.Core;
using Autodesk.DataExchange.Core.Enums;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.Interface;
using DataExchangeNodes.NodeModels.DataExchange;
using Dynamo.Logging;
using Newtonsoft.Json;
using DynamoLogLevel = Dynamo.Logging.LogLevel;

namespace DataExchangeNodes.NodeViews.DataExchange
{
    /// <summary>
    /// Read-only DataExchange model for element selection
    /// Extends BaseReadWriteExchangeModel but only implements read functionality
    /// </summary>
    public class ReadExchangeModel : BaseReadWriteExchangeModel
    {
        private static ReadExchangeModel _instance;
        private List<Autodesk.DataExchange.Core.Models.DataExchange> _localStorage = new List<Autodesk.DataExchange.Core.Models.DataExchange>();
        
        /// <summary>
        /// The current node that is using this exchange model
        /// </summary>
        public static SelectExchangeElements CurrentNode { get; set; }
        
        /// <summary>
        /// Logger for diagnostic output
        /// </summary>
        public static ILogger Logger { get; set; }

        private ReadExchangeModel(IClient client) : base(client)
        {
        }

        public static ReadExchangeModel GetInstance(IClient client)
        {
            if (_instance == null || _instance.Client != client)
            {
                _instance = new ReadExchangeModel(client);
            }
            return _instance;
        }

        public override async Task<List<Autodesk.DataExchange.Core.Models.DataExchange>> GetExchangesAsync(ExchangeSearchFilter exchangeSearchFilter)
        {
            Logger?.Log("[ReadExchangeModel] GetExchangesAsync called - fetching exchanges from cloud");
            // Merge cloud exchanges with local storage
            _localStorage = await GetValidExchangesAsync(exchangeSearchFilter, _localStorage);
            Logger?.Log($"[ReadExchangeModel] GetExchangesAsync returned {_localStorage?.Count ?? 0} exchange(s)");
            foreach (var ex in _localStorage ?? Enumerable.Empty<Autodesk.DataExchange.Core.Models.DataExchange>())
            {
                Logger?.Log($"  - {ex.ExchangeID}: {ex.ExchangeTitle}");
            }
            return _localStorage;
        }

        public override async Task<Autodesk.DataExchange.Core.Models.DataExchange> GetExchangeAsync(DataExchangeIdentifier dataExchangeIdentifier)
        {
            Logger?.Log($"[ReadExchangeModel] GetExchangeAsync called for ExchangeId: {dataExchangeIdentifier?.ExchangeId}");
            var response = await base.GetExchangeAsync(dataExchangeIdentifier);

            if (response != null && !_localStorage.Any(item => item.ExchangeID == response.ExchangeID))
            {
                Logger?.Log($"[ReadExchangeModel] Adding exchange to localStorage: {response.ExchangeTitle}");
                response.IsExchangeFromRead = true;
                _localStorage.Add(response);
            }

            Logger?.Log($"[ReadExchangeModel] GetExchangeAsync result: {response?.ExchangeTitle ?? "null"}");

            // Auto-select when user clicks on an exchange (improves UX - no need for 3-dot menu)
            if (response != null && CurrentNode != null)
            {
                Logger?.Log($"[ReadExchangeModel] Auto-triggering selection for clicked exchange: {response.ExchangeTitle}");
                var exchangeIds = new List<string> { response.ExchangeID };
                await SelectElementsAsync(exchangeIds);
            }

            return response;
        }

        public override List<Autodesk.DataExchange.Core.Models.DataExchange> GetCachedExchanges()
        {
            return _localStorage?.ToList() ?? new List<Autodesk.DataExchange.Core.Models.DataExchange>();
        }

        /// <summary>
        /// Pre-loads an exchange into localStorage from saved selection JSON.
        /// This allows the UI to show the previously selected exchange when opened after deserialization.
        /// </summary>
        /// <param name="selectionJson">The saved selection JSON from node.Value</param>
        /// <returns>True if exchange was successfully pre-loaded</returns>
        public bool PreloadExchangeFromSelection(string selectionJson)
        {
            try
            {
                if (string.IsNullOrEmpty(selectionJson))
                    return false;

                var selectionData = JsonConvert.DeserializeObject<Dictionary<string, string>>(selectionJson);
                if (selectionData == null || !selectionData.ContainsKey("exchangeId"))
                    return false;

                var exchangeId = selectionData["exchangeId"];

                // Check if already in localStorage
                if (_localStorage.Any(e => e.ExchangeID == exchangeId))
                {
                    Logger?.Log($"[ReadExchangeModel] Exchange '{exchangeId}' already in localStorage");
                    return true;
                }

                // Create a DataExchange object from the saved data
                var exchange = new Autodesk.DataExchange.Core.Models.DataExchange
                {
                    ExchangeID = exchangeId,
                    CollectionID = selectionData.GetValueOrDefault("collectionId", ""),
                    ExchangeTitle = selectionData.GetValueOrDefault("exchangeTitle", ""),
                    ExchangeDescription = selectionData.GetValueOrDefault("exchangeDescription", ""),
                    ProjectName = selectionData.GetValueOrDefault("projectName", ""),
                    FolderPath = selectionData.GetValueOrDefault("folderPath", ""),
                    CreatedBy = selectionData.GetValueOrDefault("createdBy", ""),
                    UpdatedBy = selectionData.GetValueOrDefault("updatedBy", ""),
                    ProjectURN = selectionData.GetValueOrDefault("projectUrn", ""),
                    FileURN = selectionData.GetValueOrDefault("fileUrn", ""),
                    FolderURN = selectionData.GetValueOrDefault("folderUrn", ""),
                    FileVersionId = selectionData.GetValueOrDefault("fileVersionId", ""),
                    HubId = selectionData.GetValueOrDefault("hubId", ""),
                    HubRegion = selectionData.GetValueOrDefault("hubRegion", ""),
                    SchemaNamespace = selectionData.GetValueOrDefault("schemaNamespace", ""),
                    ExchangeThumbnail = selectionData.GetValueOrDefault("exchangeThumbnail", ""),
                    IsExchangeFromRead = true
                };

                // Parse dates if available
                if (selectionData.TryGetValue("createTime", out var createTimeStr) &&
                    DateTime.TryParse(createTimeStr, out var createTime))
                {
                    exchange.CreateTime = createTime;
                }

                if (selectionData.TryGetValue("updated", out var updatedStr) &&
                    DateTime.TryParse(updatedStr, out var updated))
                {
                    exchange.Updated = updated;
                }

                _localStorage.Add(exchange);
                Logger?.Log($"[ReadExchangeModel] Pre-loaded exchange from saved selection: {exchange.ExchangeTitle}");
                return true;
            }
            catch (Exception ex)
            {
                Logger?.Log($"[ReadExchangeModel] Failed to pre-load exchange: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Validates that a saved exchange still exists by checking if we can get its revisions.
        /// This is a lightweight validation that doesn't download the full exchange data.
        /// </summary>
        /// <param name="exchangeId">The exchange ID to validate</param>
        /// <param name="collectionId">The collection ID</param>
        /// <returns>True if the exchange exists and is accessible</returns>
        public async Task<bool> ValidateExchangeExistsAsync(string exchangeId, string collectionId)
        {
            try
            {
                if (string.IsNullOrEmpty(exchangeId) || string.IsNullOrEmpty(collectionId))
                    return false;

                var identifier = new DataExchangeIdentifier
                {
                    ExchangeId = exchangeId,
                    CollectionId = collectionId
                };

                // Try to get revisions - this is a lightweight call that validates the exchange exists
                var revisionsResponse = await Client.GetExchangeRevisionsAsync(identifier);

                var exists = revisionsResponse != null &&
                            revisionsResponse.Value != null &&
                            revisionsResponse.Value.Any();

                Logger?.Log($"[ReadExchangeModel] Exchange validation for '{exchangeId}': {(exists ? "EXISTS" : "NOT FOUND")}");
                return exists;
            }
            catch (Exception ex)
            {
                Logger?.Log($"[ReadExchangeModel] Exchange validation failed: {ex.Message}");
                return false;
            }
        }

        public override Task UpdateExchangeAsync(ExchangeItem exchangeItem, CancellationToken cancellationToken = default)
        {
            // Read-only mode - no updates allowed
            return Task.CompletedTask;
        }

        public override Task<IEnumerable<string>> UnloadExchangesAsync(List<ExchangeItem> exchanges)
        {
            return Task.FromResult(exchanges.Select(n => n.ExchangeID));
        }

        public override Task<bool> SelectElementsAsync(List<string> exchangeIds)
        {
            Logger?.Log($"[ReadExchangeModel] SelectElementsAsync called with {exchangeIds?.Count ?? 0} exchange ID(s)");

            try
            {
                if (exchangeIds == null || !exchangeIds.Any())
                {
                    Logger?.Log("[ReadExchangeModel] ERROR: exchangeIds is null or empty");
                    return Task.FromResult(false);
                }

                if (CurrentNode == null)
                {
                    Logger?.Log("[ReadExchangeModel] ERROR: CurrentNode is null");
                    return Task.FromResult(false);
                }

                var exchangeId = exchangeIds.First();
                Logger?.Log($"[ReadExchangeModel] Looking for exchange ID: {exchangeId}");
                Logger?.Log($"[ReadExchangeModel] LocalStorage has {_localStorage?.Count ?? 0} exchanges");

                var exchange = _localStorage.FirstOrDefault(e => e.ExchangeID == exchangeId);

                if (exchange == null)
                {
                    Logger?.Log($"[ReadExchangeModel] ERROR: Exchange '{exchangeId}' not found in localStorage");
                    // List what we have
                    foreach (var e in _localStorage ?? Enumerable.Empty<Autodesk.DataExchange.Core.Models.DataExchange>())
                    {
                        Logger?.Log($"  - Available: {e.ExchangeID} ({e.ExchangeTitle})");
                    }
                    return Task.FromResult(false);
                }

                Logger?.Log($"[ReadExchangeModel] Found exchange: {exchange.ExchangeTitle}");

                // Create selection data dictionary
                var selectionData = new Dictionary<string, string>
                {
                    { "exchangeId", exchange.ExchangeID ?? "" },
                    { "collectionId", exchange.CollectionID ?? "" },
                    { "exchangeTitle", exchange.ExchangeTitle ?? "" },
                    { "exchangeDescription", exchange.ExchangeDescription ?? "" },
                    { "projectName", exchange.ProjectName ?? "" },
                    { "folderPath", exchange.FolderPath ?? "" },
                    { "createdBy", exchange.CreatedBy ?? "" },
                    { "updatedBy", exchange.UpdatedBy ?? "" },
                    { "projectUrn", exchange.ProjectURN ?? "" },
                    { "fileUrn", exchange.FileURN ?? "" },
                    { "folderUrn", exchange.FolderURN ?? "" },
                    { "fileVersionId", exchange.FileVersionId ?? "" },
                    { "hubId", exchange.HubId ?? "" },
                    { "hubRegion", exchange.HubRegion ?? "" },
                    { "createTime", exchange.CreateTime.ToString("o") },
                    { "updated", exchange.Updated.ToString("o") },
                    { "timestamp", DateTime.Now.ToString("o") },
                    { "schemaNamespace", exchange.SchemaNamespace ?? "" },
                    { "exchangeThumbnail", exchange.ExchangeThumbnail ?? "" },
                    { "projectType", exchange.ProjectType.ToString() },
                    { "isUpdateAvailable", exchange.IsUpdateAvailable.ToString() }
                };

                // Serialize and store in node
                string selectionJson = JsonConvert.SerializeObject(selectionData);
                Logger?.Log($"[ReadExchangeModel] Storing selection in node (JSON length: {selectionJson.Length})");
                CurrentNode.Value = selectionJson;
                CurrentNode.OnNodeModified(forceExecute: true);

                Logger?.Log("[ReadExchangeModel] Selection stored successfully!");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger?.Log($"[ReadExchangeModel] ERROR: {ex.GetType().Name}: {ex.Message}");
                return Task.FromResult(false);
            }
        }
    }
}

