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
            // Merge cloud exchanges with local storage
            _localStorage = await GetValidExchangesAsync(exchangeSearchFilter, _localStorage);
            return _localStorage;
        }

        public override async Task<Autodesk.DataExchange.Core.Models.DataExchange> GetExchangeAsync(DataExchangeIdentifier dataExchangeIdentifier)
        {
            var response = await base.GetExchangeAsync(dataExchangeIdentifier);

            if (response != null && !_localStorage.Any(item => item.ExchangeID == response.ExchangeID))
            {
                response.IsExchangeFromRead = true;
                _localStorage.Add(response);
            }

            return response;
        }

        public override List<Autodesk.DataExchange.Core.Models.DataExchange> GetCachedExchanges()
        {
            return _localStorage?.ToList() ?? new List<Autodesk.DataExchange.Core.Models.DataExchange>();
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
            Logger?.Log("========== SelectElementsAsync CALLED ==========");
            Logger?.Log($"Received {exchangeIds?.Count ?? 0} exchange IDs");
            
            try
            {
                // When an exchange is selected, capture its metadata
                if (exchangeIds != null && exchangeIds.Any() && CurrentNode != null)
                {
                    var exchangeId = exchangeIds.First();
                    var exchange = _localStorage.FirstOrDefault(e => e.ExchangeID == exchangeId);

                    if (exchange != null)
                    {
                        // ===== DETAILED LOGGING: Inspect all properties of exchange object =====
                        Logger?.Log("========== DataExchange Object Properties ==========");
                        var exchangeType = exchange.GetType();
                        foreach (var prop in exchangeType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        {
                            try
                            {
                                var value = prop.GetValue(exchange);
                                var valueStr = value?.ToString() ?? "(null)";
                                Logger?.Log($"  {prop.Name} ({prop.PropertyType.Name}): {valueStr}");
                            }
                            catch (Exception propEx)
                            {
                                Logger?.Log($"  {prop.Name}: [Error reading: {propEx.Message}]");
                            }
                        }
                        Logger?.Log("=====================================================");
                        
                        // Try to serialize the entire exchange object to JSON for inspection
                        try
                        {
                            var fullJson = JsonConvert.SerializeObject(exchange, Formatting.Indented, 
                                new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
                            Logger?.Log("========== Full Exchange JSON ==========");
                            Logger?.Log(fullJson);
                            Logger?.Log("=========================================");
                        }
                        catch (Exception jsonEx)
                        {
                            Logger?.Log($"Could not serialize full exchange to JSON: {jsonEx.Message}");
                        }

                        // Create selection data dictionary with ALL available fields
                        var selectionData = new Dictionary<string, string>
                        {
                            // Core identifiers
                            { "exchangeId", exchange.ExchangeID ?? "" },
                            { "collectionId", exchange.CollectionID ?? "" },
                            
                            // Human-readable info
                            { "exchangeTitle", exchange.ExchangeTitle ?? "" },
                            { "exchangeDescription", exchange.ExchangeDescription ?? "" },
                            { "projectName", exchange.ProjectName ?? "" },
                            { "folderPath", exchange.FolderPath ?? "" },
                            
                            // User info
                            { "createdBy", exchange.CreatedBy ?? "" },
                            { "updatedBy", exchange.UpdatedBy ?? "" },
                            
                            // URNs and IDs
                            { "projectUrn", exchange.ProjectURN ?? "" },
                            { "fileUrn", exchange.FileURN ?? "" },
                            { "folderUrn", exchange.FolderURN ?? "" },
                            { "fileVersionId", exchange.FileVersionId ?? "" },
                            { "hubId", exchange.HubId ?? "" },
                            { "hubRegion", exchange.HubRegion ?? "" },
                            
                            // Timestamps
                            { "createTime", exchange.CreateTime.ToString("o") },
                            { "updated", exchange.Updated.ToString("o") },
                            { "timestamp", DateTime.Now.ToString("o") },
                            
                            // Additional metadata
                            { "schemaNamespace", exchange.SchemaNamespace ?? "" },
                            { "exchangeThumbnail", exchange.ExchangeThumbnail ?? "" },
                            { "projectType", exchange.ProjectType.ToString() },
                            { "isUpdateAvailable", exchange.IsUpdateAvailable.ToString() }
                        };
                        
                        // Log what we're storing
                        Logger?.Log("========== Storing Selection Data ==========");
                        Logger?.Log(JsonConvert.SerializeObject(selectionData, Formatting.Indented));
                        Logger?.Log("============================================");

                        // Serialize and store in node
                        string selectionJson = JsonConvert.SerializeObject(selectionData);
                        CurrentNode.Value = selectionJson;
                        CurrentNode.OnNodeModified(forceExecute: true);
                        Logger?.Log($"ReadExchangeModel: Selection stored and node updated");
                    }
                    else
                    {
                        Logger?.Log($"ReadExchangeModel: Exchange with ID '{exchangeId}' not found in local storage");
                    }
                }
                else
                {
                    Logger?.Log($"ReadExchangeModel: Invalid input - exchangeIds: {exchangeIds?.Count ?? 0}, CurrentNode: {(CurrentNode != null ? "set" : "null")}");
                }

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger?.Log($"ReadExchangeModel: Error in SelectElementsAsync - {ex.Message}");
                Logger?.Log($"Stack trace: {ex.StackTrace}");
                return Task.FromResult(false);
            }
        }
    }
}

