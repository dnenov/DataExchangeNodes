using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange.DataModels;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Ensures an Exchange has an ElementDataModel.
    /// If the exchange doesn't have one, creates a new ElementDataModel and syncs it to the exchange.
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
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            ElementDataModel elementDataModel = null;
            var wasCreated = false;
            var wasSynced = false;

            try
            {
                if (exchange == null)
                {
                    log.Error("Exchange is null");
                    return BuildResult(log, false, null, false, false);
                }

                // Validate client
                var (client, isValid, errorMessage) = DataExchangeUtils.GetValidatedClient();
                if (!isValid)
                {
                    log.Error(errorMessage);
                    return BuildResult(log, false, null, false, false);
                }

                // Create identifier using shared utility
                var identifier = DataExchangeUtils.CreateIdentifier(exchange);

                // Check if ElementDataModel exists
                var (model, isSuccess, getError) = DataExchangeUtils.RunSync(async () =>
                    await DataExchangeClient.GetElementDataModelWithErrorInfoAsync(identifier, CancellationToken.None));

                if (!isSuccess)
                {
                    log.Error($"GetElementDataModelAsync failed: {getError ?? "Unknown error"}");
                    return BuildResult(log, false, null, false, false);
                }

                if (model != null)
                {
                    // ElementDataModel already exists
                    return BuildResult(log, true, model, false, false);
                }

                // ElementDataModel is null - create a new one
                elementDataModel = ElementDataModel.Create(client);
                wasCreated = true;

                // Sync the new ElementDataModel to the exchange
                var syncResponse = DataExchangeUtils.RunSync(() =>
                    client.SyncExchangeDataAsync(identifier, elementDataModel));

                if (!syncResponse.IsSuccess)
                {
                    var syncErrors = syncResponse.Errors != null && syncResponse.Errors.Any()
                        ? string.Join(", ", syncResponse.Errors.Select(e => e.ToString()))
                        : "Unknown error";
                    log.Error($"SyncExchangeDataAsync failed: {syncErrors}");
                    return BuildResult(log, false, elementDataModel, wasCreated, false);
                }

                wasSynced = true;
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
                return BuildResult(log, false, elementDataModel, wasCreated, wasSynced);
            }

            return BuildResult(log, true, elementDataModel, wasCreated, wasSynced);
        }

        private static Dictionary<string, object> BuildResult(
            DiagnosticsLogger log,
            bool success,
            ElementDataModel elementDataModel,
            bool wasCreated,
            bool wasSynced)
        {
            return new NodeResultBuilder()
                .WithLog(log)
                .WithSuccess(success)
                .WithProperty("elementDataModel", elementDataModel)
                .WithProperty("wasCreated", wasCreated)
                .WithProperty("wasSynced", wasSynced)
                .Build();
        }
    }
}
