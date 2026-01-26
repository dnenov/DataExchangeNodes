using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DataExchange;
using Autodesk.DataExchange.Core;
using Autodesk.DataExchange.Core.Interface;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.DataModels;
using Autodesk.DataExchange.Models;
using Autodesk.DesignScript.Runtime;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Centralized DataExchange Client management.
    /// Provides a consistent way to initialize and access the DataExchange Client instance
    /// across all nodes in the DataExchangeNodes project.
    ///
    /// Pattern matches the grasshopper-connector's ReadExchangesData.InitializeClient approach.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    public static class DataExchangeClient
    {
        private static Client _client;
        private static SDKOptions _sdkOptions;

        /// <summary>
        /// Initializes the Autodesk.DataExchange client with the specified SDK options
        /// and sets the singleton instance. This method must be called before using the Client.
        /// 
        /// Pattern matches: ReadExchangesData.InitializeClient (grasshopper-connector)
        /// </summary>
        /// <param name="sdkOptions">The SDK options for configuring the DataExchange client</param>
        public static void InitializeClient(SDKOptions sdkOptions)
        {
            if (sdkOptions == null)
            {
                throw new ArgumentNullException(nameof(sdkOptions), "SDKOptions cannot be null");
            }

            Client client = null;
            Task.Run(() =>
            {
                client = new Client(sdkOptions);
            }).GetAwaiter().GetResult();

            _client = client;
            _sdkOptions = sdkOptions;
        }

        /// <summary>
        /// Gets the DataExchange Client instance.
        /// Returns null if InitializeClient has not been called yet.
        /// </summary>
        /// <returns>The Client instance, or null if not initialized</returns>
        public static Client GetClient()
        {
            return _client;
        }

        /// <summary>
        /// Gets the SDK options that were used to initialize the client.
        /// Returns null if InitializeClient has not been called yet.
        /// </summary>
        /// <returns>The SDKOptions instance, or null if not initialized</returns>
        public static SDKOptions GetSDKOptions()
        {
            return _sdkOptions;
        }

        /// <summary>
        /// Checks if the Client has been initialized.
        /// </summary>
        /// <returns>True if the Client has been initialized, false otherwise</returns>
        public static bool IsInitialized()
        {
            return _client != null;
        }

        /// <summary>
        /// Resets the client instance. Useful for testing or when re-initialization is needed.
        /// </summary>
        public static void Reset()
        {
            _client = null;
            _sdkOptions = null;
        }

        /// <summary>
        /// Gets the latest revision ID for the specified exchange.
        /// Returns null if no revisions are found or if the client is not initialized.
        /// 
        /// Pattern matches: ReadExchangesData.GetCurrentExchangeDataAndStepFile (grasshopper-connector line 236-237)
        /// Uses .First() to get the first revision from the response (revisions are typically ordered with latest first)
        /// </summary>
        /// <param name="exchangeIdentifier">The exchange identifier</param>
        /// <returns>The latest revision ID, or null if not found</returns>
        public static async Task<string> GetLatestRevisionIdAsync(DataExchangeIdentifier exchangeIdentifier)
        {
            if (_client == null)
            {
                return null;
            }

            try
            {
                // Pattern matches: ReadExchangesData.GetCurrentExchangeDataAndStepFile (grasshopper-connector line 236)
                var revisionsResponse = await _client.GetExchangeRevisionsAsync(exchangeIdentifier);
                if (revisionsResponse == null || revisionsResponse.Value == null || !revisionsResponse.Value.Any())
                {
                    return null;
                }

                // Pattern matches: ReadExchangesData.GetCurrentExchangeDataAndStepFile (grasshopper-connector line 237)
                // Use .First() - revisions are typically ordered with latest first
                string firstRev = revisionsResponse.Value.First().Id;
                return firstRev;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the ElementDataModel for the specified exchange.
        /// This method follows the exact pattern from grasshopper-connector's ReadExchangesData.GetCurrentExchangeDataAndStepFile.
        /// 
        /// Pattern matches: ReadExchangesData.GetCurrentExchangeDataAndStepFile (grasshopper-connector line 240)
        /// - Gets ElementDataModel using just the identifier, without revision parameter
        /// - The SDK automatically uses the latest revision when no revision parameter is specified
        /// 
        /// Note: This matches grasshopper-connector exactly - they call GetElementDataModelAsync with JUST the identifier.
        /// </summary>
        /// <param name="exchangeIdentifier">The exchange identifier</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>The ElementDataModel response, or null if not available</returns>
        public static async Task<IResponse<ElementDataModel>> GetElementDataModelAsync(DataExchangeIdentifier exchangeIdentifier, CancellationToken cancellationToken = default)
        {
            if (_client == null)
            {
                return null;
            }

            try
            {
                // Match grasshopper-connector exactly: call GetElementDataModelAsync with JUST the identifier
                // This uses the overload without revision parameter, which automatically gets the latest revision
                // Pattern matches: ReadExchangesData.GetCurrentExchangeDataAndStepFile (grasshopper-connector line 240)
                var exchangeDataResponse = Task.Run(async () => await _client.GetElementDataModelAsync(exchangeIdentifier)).Result;
                return exchangeDataResponse;
            }
            catch (Exception ex)
            {
                // Re-throw to preserve error information - GetElementDataModelWithErrorInfoAsync will catch it
                throw;
            }
        }

        /// <summary>
        /// Gets the ElementDataModel for the specified exchange and revision with full error information.
        /// Returns both the model (if successful) and error details (if failed).
        /// </summary>
        /// <param name="exchangeIdentifier">The exchange identifier</param>
        /// <param name="fromRevision">The revision ID to fetch the ElementDataModel from</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A tuple with the ElementDataModel (or null), IsSuccess flag, and error messages</returns>
        public static async Task<(ElementDataModel model, bool isSuccess, string errorMessage)> GetElementDataModelWithErrorInfoAsync(
            DataExchangeIdentifier exchangeIdentifier, 
            string fromRevision,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (_client == null)
                {
                    return (null, false, "Client is not initialized");
                }

                var response = Task.Run(async () => await _client.GetElementDataModelAsync(exchangeIdentifier, fromRevision, cancellationToken)).Result;
                if (response == null)
                {
                    return (null, false, "GetElementDataModelAsync returned null response (client may not be initialized)");
                }

                var isSuccess = response.IsSuccess;
                var model = response.ValueOrDefault;
                
                string errorMessage = null;
                if (!isSuccess)
                {
                    if (response.Errors != null && response.Errors.Any())
                    {
                        var errorMessages = response.Errors.Select(e => e.Message ?? e.GetType().Name).ToList();
                        errorMessage = string.Join("; ", errorMessages);
                    }
                    else
                    {
                        errorMessage = "GetElementDataModelAsync failed but no error details were provided";
                    }
                }
                // Note: If IsSuccess is true but model is null, that means it's a new/empty exchange
                // This is expected and not an error - the caller should create a new ElementDataModel

                return (model, isSuccess, errorMessage);
            }
            catch (Exception ex)
            {
                return (null, false, $"Exception calling GetElementDataModelAsync: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the ElementDataModel for the specified exchange with full error information.
        /// Returns both the model (if successful) and error details (if failed).
        /// Uses the latest revision automatically.
        /// </summary>
        /// <param name="exchangeIdentifier">The exchange identifier</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>A tuple with the ElementDataModel (or null), IsSuccess flag, and error messages</returns>
        public static async Task<(ElementDataModel model, bool isSuccess, string errorMessage)> GetElementDataModelWithErrorInfoAsync(
            DataExchangeIdentifier exchangeIdentifier, 
            CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await GetElementDataModelAsync(exchangeIdentifier, cancellationToken);
                if (response == null)
                {
                    return (null, false, "GetElementDataModelAsync returned null response (client may not be initialized)");
                }

                var isSuccess = response.IsSuccess;
                var model = response.ValueOrDefault;
                
                string errorMessage = null;
                if (!isSuccess)
                {
                    // Actual error - get error details
                    if (response.Errors != null && response.Errors.Any())
                    {
                        var errorMessages = response.Errors.Select(e => e.Message ?? e.GetType().Name).ToList();
                        errorMessage = string.Join("; ", errorMessages);
                    }
                    else
                    {
                        errorMessage = "GetElementDataModelAsync failed but no error details were provided";
                    }
                }
                // Note: If IsSuccess is true but model is null, that means it's a new/empty exchange
                // This is expected and not an error - the caller should create a new ElementDataModel

                return (model, isSuccess, errorMessage);
            }
            catch (Exception ex)
            {
                return (null, false, $"Exception calling GetElementDataModelAsync: {ex.Message}");
            }
        }
    }
}
