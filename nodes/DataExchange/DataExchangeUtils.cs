using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.DataExchange;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DesignScript.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Utility functions for DataExchange operations (ZeroTouch/AST callable)
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
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

        /// <summary>
        /// Creates a DataExchangeIdentifier from an Exchange object.
        /// Consolidates the duplicate pattern found in multiple node files.
        /// </summary>
        /// <param name="exchange">The Exchange object containing IDs</param>
        /// <returns>A properly configured DataExchangeIdentifier</returns>
        [IsVisibleInDynamoLibrary(false)]
        internal static DataExchangeIdentifier CreateIdentifier(Exchange exchange)
        {
            if (exchange == null)
            {
                throw new ArgumentNullException(nameof(exchange));
            }

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

        /// <summary>
        /// Validates that the DataExchange client is initialized and returns it.
        /// Consolidates the duplicate validation pattern found in multiple node files.
        /// </summary>
        /// <returns>Tuple with (client, isValid, errorMessage)</returns>
        [IsVisibleInDynamoLibrary(false)]
        internal static (Client client, bool isValid, string errorMessage) GetValidatedClient()
        {
            if (!DataExchangeClient.IsInitialized())
            {
                return (null, false, "Client is not initialized. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
            }

            var client = DataExchangeClient.GetClient();
            if (client == null)
            {
                return (null, false, "Client instance is null.");
            }

            return (client, true, null);
        }

        /// <summary>
        /// Executes an async function synchronously.
        /// Standardizes the Task.Run pattern used throughout the codebase.
        /// Use this when calling async SDK methods from Dynamo's synchronous nodes.
        /// </summary>
        /// <typeparam name="T">The return type of the async function</typeparam>
        /// <param name="asyncFunc">The async function to execute</param>
        /// <returns>The result of the async function</returns>
        [IsVisibleInDynamoLibrary(false)]
        internal static T RunSync<T>(Func<Task<T>> asyncFunc)
        {
            return Task.Run(asyncFunc).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Executes an async action synchronously.
        /// </summary>
        /// <param name="asyncAction">The async action to execute</param>
        [IsVisibleInDynamoLibrary(false)]
        internal static void RunSync(Func<Task> asyncAction)
        {
            Task.Run(asyncAction).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Converts a unit type string to millimeters per unit.
        /// Consolidates the duplicate unit conversion logic from ExportGeometryToSMB and LoadGeometryFromExchange.
        /// </summary>
        /// <param name="unit">Unit type string (e.g., "kUnitType_Meter", "kUnitType_CentiMeter", "kUnitType_Feet", "kUnitType_Inch")</param>
        /// <returns>Millimeters per unit (default: 10.0 for centimeters)</returns>
        [IsVisibleInDynamoLibrary(false)]
        internal static double ConvertUnitToMmPerUnit(string unit)
        {
            if (string.IsNullOrEmpty(unit))
                return 10.0; // Default to cm

            if (unit.Contains("Meter") && !unit.Contains("Centi"))
                return 1000.0; // Meter
            if (unit.Contains("CentiMeter") || unit.Contains("cm"))
                return 10.0; // Centimeter
            if (unit.Contains("Feet") || unit.Contains("ft"))
                return 304.8; // Feet
            if (unit.Contains("Inch") || unit.Contains("in"))
                return 25.4; // Inch

            return 10.0; // Default to cm
        }

        /// <summary>
        /// Gets the list of available DataExchange unit types.
        /// </summary>
        /// <returns>List of unit type strings</returns>
        [IsVisibleInDynamoLibrary(false)]
        internal static List<string> GetAvailableUnits()
        {
            return new List<string>
            {
                "kUnitType_Meter",
                "kUnitType_CentiMeter",
                "kUnitType_Feet",
                "kUnitType_Inch"
            };
        }
    }

    /// <summary>
    /// Diagnostic logging levels for controlling output verbosity
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    internal enum DiagnosticLevel
    {
        /// <summary>Critical errors that prevent operation</summary>
        Error = 0,
        /// <summary>Warnings about potential issues</summary>
        Warning = 1,
        /// <summary>Key operational information (default)</summary>
        Info = 2,
        /// <summary>Detailed debug information (not shown by default)</summary>
        Debug = 3
    }

    /// <summary>
    /// Categorized diagnostic logger that replaces raw List&lt;string&gt; diagnostics.
    /// Supports filtering by log level to reduce output verbosity.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    internal class DiagnosticsLogger
    {
        private readonly List<string> _messages = new List<string>();
        private readonly DiagnosticLevel _maxLevel;

        /// <summary>
        /// Creates a new DiagnosticsLogger with the specified maximum log level.
        /// Messages above this level will be suppressed.
        /// </summary>
        /// <param name="maxLevel">Maximum level to log (default: Error for aggressive reduction)</param>
        internal DiagnosticsLogger(DiagnosticLevel maxLevel = DiagnosticLevel.Error)
        {
            _maxLevel = maxLevel;
        }

        /// <summary>
        /// Logs an error message (always shown)
        /// </summary>
        internal void Error(string message)
        {
            Log(DiagnosticLevel.Error, $"ERROR: {message}");
        }

        /// <summary>
        /// Logs a warning message
        /// </summary>
        internal void Warning(string message)
        {
            Log(DiagnosticLevel.Warning, $"WARNING: {message}");
        }

        /// <summary>
        /// Logs an informational message
        /// </summary>
        internal void Info(string message)
        {
            Log(DiagnosticLevel.Info, message);
        }

        /// <summary>
        /// Logs a debug message (typically suppressed)
        /// </summary>
        internal void Debug(string message)
        {
            Log(DiagnosticLevel.Debug, message);
        }

        /// <summary>
        /// Logs a success message at Info level
        /// </summary>
        internal void Success(string message)
        {
            Log(DiagnosticLevel.Info, message);
        }

        private void Log(DiagnosticLevel level, string message)
        {
            if (level <= _maxLevel)
            {
                _messages.Add(message);
            }
        }

        /// <summary>
        /// Gets all logged messages as a list
        /// </summary>
        internal List<string> GetMessages() => new List<string>(_messages);

        /// <summary>
        /// Gets all logged messages as a single string
        /// </summary>
        internal string GetLog() => string.Join("\n", _messages);

        /// <summary>
        /// Adds a raw message at Info level (for backwards compatibility)
        /// </summary>
        internal void Add(string message) => Info(message);

        /// <summary>
        /// Gets the number of logged messages
        /// </summary>
        internal int Count => _messages.Count;
    }

    /// <summary>
    /// Fluent builder for creating MultiReturn dictionaries.
    /// Consolidates the duplicate CreateResult/CreateErrorResult patterns.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    internal class NodeResultBuilder
    {
        private readonly Dictionary<string, object> _result = new Dictionary<string, object>();
        private DiagnosticsLogger _logger;

        /// <summary>
        /// Sets the success status
        /// </summary>
        internal NodeResultBuilder WithSuccess(bool success)
        {
            _result["success"] = success;
            return this;
        }

        /// <summary>
        /// Sets diagnostics from a DiagnosticsLogger
        /// </summary>
        internal NodeResultBuilder WithDiagnostics(DiagnosticsLogger logger)
        {
            _logger = logger;
            return this;
        }

        /// <summary>
        /// Sets diagnostics from a list of strings
        /// </summary>
        internal NodeResultBuilder WithDiagnostics(List<string> diagnostics)
        {
            _result["diagnostics"] = string.Join("\n", diagnostics);
            return this;
        }

        /// <summary>
        /// Sets diagnostics from a string
        /// </summary>
        internal NodeResultBuilder WithDiagnostics(string diagnostics)
        {
            _result["diagnostics"] = diagnostics;
            return this;
        }

        /// <summary>
        /// Adds a custom property to the result
        /// </summary>
        internal NodeResultBuilder WithProperty(string key, object value)
        {
            _result[key] = value;
            return this;
        }

        /// <summary>
        /// Adds the log output (alias for WithDiagnostics for nodes using "log" key)
        /// </summary>
        internal NodeResultBuilder WithLog(DiagnosticsLogger logger)
        {
            _result["log"] = logger.GetLog();
            return this;
        }

        /// <summary>
        /// Adds the log output from a list
        /// </summary>
        internal NodeResultBuilder WithLog(List<string> log)
        {
            _result["log"] = string.Join("\n", log);
            return this;
        }

        /// <summary>
        /// Builds and returns the result dictionary
        /// </summary>
        internal Dictionary<string, object> Build()
        {
            if (_logger != null && !_result.ContainsKey("diagnostics"))
            {
                _result["diagnostics"] = _logger.GetLog();
            }
            return new Dictionary<string, object>(_result);
        }

        /// <summary>
        /// Creates a quick error result with diagnostics and success=false
        /// </summary>
        internal static Dictionary<string, object> Error(DiagnosticsLogger logger, params (string key, object value)[] additionalProps)
        {
            var builder = new NodeResultBuilder()
                .WithSuccess(false)
                .WithDiagnostics(logger);

            foreach (var (key, value) in additionalProps)
            {
                builder.WithProperty(key, value);
            }

            return builder.Build();
        }

        /// <summary>
        /// Creates a quick error result with a list of diagnostics
        /// </summary>
        internal static Dictionary<string, object> Error(List<string> diagnostics, params (string key, object value)[] additionalProps)
        {
            var builder = new NodeResultBuilder()
                .WithSuccess(false)
                .WithDiagnostics(diagnostics);

            foreach (var (key, value) in additionalProps)
            {
                builder.WithProperty(key, value);
            }

            return builder.Build();
        }

        /// <summary>
        /// Creates a quick success result with diagnostics
        /// </summary>
        internal static Dictionary<string, object> Ok(DiagnosticsLogger logger, params (string key, object value)[] additionalProps)
        {
            var builder = new NodeResultBuilder()
                .WithSuccess(true)
                .WithDiagnostics(logger);

            foreach (var (key, value) in additionalProps)
            {
                builder.WithProperty(key, value);
            }

            return builder.Build();
        }
    }
}
