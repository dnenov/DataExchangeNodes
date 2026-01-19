using System;
using System.Threading.Tasks;
using Autodesk.DataExchange;
using Autodesk.DataExchange.Core;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Centralized DataExchange Client management.
    /// Provides a consistent way to initialize and access the DataExchange Client instance
    /// across all nodes in the DataExchangeNodes project.
    /// 
    /// Pattern matches the grasshopper-connector's ReadExchangesData.InitializeClient approach.
    /// </summary>
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
    }
}
