using System;
using System.Collections.Generic;
using Autodesk.DesignScript.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Utility functions for DataExchange operations (ZeroTouch/AST callable)
    /// </summary>
    public static class DataExchangeUtils
    {
        /// <summary>
        /// Gets exchange metadata from the serialized selection JSON.
        /// This function is called by the AST during graph evaluation.
        /// </summary>
        /// <param name="selectionJson">JSON string containing exchange metadata from the UI selection</param>
        /// <returns>Exchange object with metadata</returns>
        [IsVisibleInDynamoLibrary(false)]
        public static Exchange GetExchangeFromSelection(string selectionJson)
        {
            if (string.IsNullOrEmpty(selectionJson))
            {
                return null;
            }

            try
            {
                // Parse the JSON and create an Exchange object
                return Exchange.FromJson(selectionJson);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to parse exchange data: {ex.Message}", ex);
            }
        }
    }
}

