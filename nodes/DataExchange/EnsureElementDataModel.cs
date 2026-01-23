using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange;
using Autodesk.DataExchange.Core;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.DataModels;
using Autodesk.DataExchange.Models;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Ensures an Exchange has an ElementDataModel. 
    /// If the exchange doesn't have one, creates a new ElementDataModel and syncs it to the exchange.
    /// This helps isolate and understand when/how ElementDataModel is created and persisted.
    /// </summary>
    public static class EnsureElementDataModel
    {
        /// <summary>
        /// Checks if the Exchange has an ElementDataModel. If not, creates one and syncs it.
        /// </summary>
        /// <param name="exchange">The Exchange to check</param>
        /// <returns>Dictionary with diagnostics, success status, and the ElementDataModel</returns>
        [MultiReturn(new[] { "log", "success", "elementDataModel", "wasCreated", "wasSynced" })]
        public static Dictionary<string, object> Ensure(Exchange exchange)
        {
            var log = new List<string>();
            var success = false;
            ElementDataModel elementDataModel = null;
            var wasCreated = false;
            var wasSynced = false;

            try
            {
                log.Add("=== EnsureElementDataModel ===");
                log.Add($"Exchange: {exchange?.ExchangeTitle ?? "N/A"} (ID: {exchange?.ExchangeId ?? "N/A"})");
                log.Add($"Collection ID: {exchange?.CollectionId ?? "N/A"}");
                log.Add($"Hub ID: {exchange?.HubId ?? "N/A"}");
                log.Add("");
                    
                if (exchange == null)
                {
                    log.Add("✗ ERROR: Exchange is null");
                    return CreateResult(log, false, null, false, false);
                }

                // Get Client instance
                var client = DataExchangeClient.GetClient();
                if (client == null)
                {
                    log.Add("✗ ERROR: Client is not initialized. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
                    return CreateResult(log, false, null, false, false);
                }

                // Create DataExchangeIdentifier
                var identifier = CreateDataExchangeIdentifier(exchange);
                log.Add($"DataExchangeIdentifier:");
                log.Add($"  ExchangeId: {identifier.ExchangeId ?? "null"}");
                log.Add($"  CollectionId: {identifier.CollectionId ?? "null"}");
                log.Add($"  HubId: {identifier.HubId ?? "null"}");
                log.Add("");

                // Step 1: Check if ElementDataModel exists
                log.Add("=== Step 1: Checking for existing ElementDataModel ===");
                var (model, isSuccess, errorMessage) = Task.Run(async () =>
                    await DataExchangeClient.GetElementDataModelWithErrorInfoAsync(identifier, CancellationToken.None)).Result;

                log.Add($"  Response.IsSuccess: {isSuccess}");
                log.Add($"  Response.Model: {(model != null ? model.GetType().FullName : "null")}");
                log.Add($"  Response.ErrorMessage: {errorMessage ?? "null"}");

                if (!isSuccess)
                {
                    log.Add($"✗ ERROR: GetElementDataModelAsync failed: {errorMessage ?? "Unknown error"}");
                    return CreateResult(log, false, null, false, false);
                }

                if (model != null)
                {
                    log.Add($"✓ ElementDataModel already exists");
                    log.Add($"  Type: {model.GetType().FullName}");

                    elementDataModel = model;
                    success = true;
                    log.Add("");
                    log.Add("=== Result: ElementDataModel already exists, no action needed ===");
                    return CreateResult(log, success, elementDataModel, false, false);
                }

                // Step 2: ElementDataModel is null - create a new one
                log.Add("");
                log.Add("=== Step 2: ElementDataModel is null - creating new one ===");
                var stopwatch = Stopwatch.StartNew();
                
                elementDataModel = ElementDataModel.Create(client);
                wasCreated = true;
                
                stopwatch.Stop();
                log.Add($"✓ Created new ElementDataModel: {elementDataModel.GetType().FullName}");
                log.Add($"⏱️  Creation time: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");

                // Step 3: Sync the new ElementDataModel to the exchange
                log.Add("");
                log.Add("=== Step 3: Syncing ElementDataModel to Exchange ===");
                stopwatch.Restart();

                var syncTask = Task.Run(() => client.SyncExchangeDataAsync(identifier, elementDataModel));
                syncTask.Wait();

                var syncResponse = syncTask.Result;
                stopwatch.Stop();
                
                log.Add($"  SyncExchangeDataAsync Response:");
                log.Add($"    IsSuccess: {syncResponse.IsSuccess}");
                log.Add($"    Value: {syncResponse.ValueOrDefault.ToString() ?? "null"}");
                
                if (syncResponse.Errors != null && syncResponse.Errors.Any())
                {
                    log.Add($"    Errors: {string.Join(", ", syncResponse.Errors.Select(e => e.ToString()))}");
                }

                if (!syncResponse.IsSuccess)
                {
                    log.Add($"✗ ERROR: SyncExchangeDataAsync failed");
                    if (syncResponse.Errors != null && syncResponse.Errors.Any())
                    {
                        log.Add($"  Error details: {string.Join(", ", syncResponse.Errors)}");
                    }
                    return CreateResult(log, false, elementDataModel, wasCreated, false);
                }

                wasSynced = true;
                log.Add($"✓ SyncExchangeDataAsync completed successfully");
                log.Add($"⏱️  Sync time: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");
            }
            catch (Exception ex)
            {
                log.Add($"✗ ERROR: {ex.GetType().Name}: {ex.Message}");
                log.Add($"Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    log.Add($"Inner: {ex.InnerException.Message}");
                }
            }

            return CreateResult(log, success, elementDataModel, wasCreated, wasSynced);
        }

        private static DataExchangeIdentifier CreateDataExchangeIdentifier(Exchange exchange)
        {
            var identifier = new DataExchangeIdentifier
            {
                ExchangeId = exchange.ExchangeId,
                CollectionId = exchange.CollectionId
            };
            if (!string.IsNullOrEmpty(exchange.HubId))
            {
                identifier.HubId = exchange.HubId;
            }
            return identifier;
        }

        private static Dictionary<string, object> CreateResult(
            List<string> log, 
            bool success, 
            ElementDataModel elementDataModel, 
            bool wasCreated, 
            bool wasSynced)
        {
            return new Dictionary<string, object>
            {
                { "log", string.Join("\n", log) },
                { "success", success },
                { "elementDataModel", elementDataModel },
                { "wasCreated", wasCreated },
                { "wasSynced", wasSynced }
            };
        }
    }
}
