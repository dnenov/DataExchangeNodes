using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;
using Dynamo.Graph.Nodes;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Load geometry from a DataExchange using ProtoGeometry SMB file APIs.
    /// Contains only the Dynamo-visible node methods.
    /// Helper methods are in SMBLoaderHelper.cs.
    /// </summary>
    public static class LoadGeometryFromExchange
    {
        // Singleton pattern - auth provider registered by SelectExchangeElements view
        private static Func<string> _getTokenFunc = null;

        /// <summary>
        /// Registers the token provider function (called by SelectExchangeElements view)
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public static void RegisterAuthProvider(Func<string> getTokenFunc)
        {
            _getTokenFunc = getTokenFunc;
        }

        /// <summary>
        /// Standalone test node: Loads geometry from an SMB file on disk.
        /// Use this to test if SMB files work with the LibG implementation.
        /// </summary>
        /// <param name="smbFilePath">Full path to the SMB file on disk</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter"). Options: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</param>
        /// <returns>Dictionary with "geometries" (list of Dynamo geometry), "log" (diagnostic messages), and "success" (boolean)</returns>
        [MultiReturn(new[] { "geometries", "log", "success" })]
        public static Dictionary<string, object> ImportSMBFileFromPath(
            string smbFilePath,
            string unit = "kUnitType_CentiMeter")
        {
            var geometries = new List<Geometry>();
            var log = new DiagnosticsLogger(DiagnosticLevel.Info);
            bool success = false;

            try
            {
                if (string.IsNullOrEmpty(smbFilePath))
                    throw new ArgumentNullException(nameof(smbFilePath), "SMB file path cannot be null or empty");

                if (!File.Exists(smbFilePath))
                    throw new FileNotFoundException($"SMB file not found: {smbFilePath}");

                // Use shared loader helper
                var loadedGeometries = SMBLoaderHelper.LoadGeometryFromSMB(smbFilePath, unit, log);
                geometries.AddRange(loadedGeometries);
                success = geometries.Count > 0;

                if (success)
                {
                    var fileInfo = new FileInfo(smbFilePath);
                    var fileSizeKB = fileInfo.Length / 1024.0;
                    log.Info($"Loaded {geometries.Count} geometry(s) from SMB | Size: {fileSizeKB:F1} KB");
                }
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new NodeResultBuilder()
                .WithSuccess(success)
                .WithLog(log)
                .WithProperty("geometries", geometries)
                .Build();
        }

        /// <summary>
        /// Loads geometry from a DataExchange as Dynamo geometry objects.
        /// Downloads SMB files from DataExchange and reads them using ProtoGeometry APIs.
        /// Authentication is handled automatically using Dynamo's login - no token required!
        /// </summary>
        /// <param name="exchange">Exchange object from SelectExchange node</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter"). Options: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</param>
        /// <param name="downloadDirectory">Optional directory for downloaded files (defaults to temp)</param>
        /// <returns>Dictionary with "geometries" (list of Dynamo geometry), "log" (diagnostic messages), and "success" (boolean)</returns>
        [MultiReturn(new[] { "geometries", "log", "success" })]
        public static Dictionary<string, object> Load(
            Exchange exchange,
            string unit = "kUnitType_CentiMeter",
            [DefaultArgument("")] string downloadDirectory = "")
        {
            var geometries = new List<Geometry>();
            var smbFilePaths = new List<string>();
            var log = new DiagnosticsLogger(DiagnosticLevel.Info);
            bool success = false;

            try
            {
                // Validate inputs
                if (exchange == null)
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");

                if (string.IsNullOrEmpty(exchange.ExchangeId))
                    throw new ArgumentException("ExchangeId is required", nameof(exchange));

                if (string.IsNullOrEmpty(exchange.CollectionId))
                    throw new ArgumentException("CollectionId is required", nameof(exchange));

                // Get token from registered auth provider
                if (_getTokenFunc == null)
                    throw new InvalidOperationException("Authentication not configured. Please use SelectExchange node first to log in.");

                var accessToken = _getTokenFunc();
                if (string.IsNullOrEmpty(accessToken))
                    throw new InvalidOperationException("Not logged in. Please log in to Dynamo first.");

                // Download SMB files using helper
                var resolvedDownloadDir = SMBLoaderHelper.ResolveDownloadDirectory(downloadDirectory);
                smbFilePaths = SMBLoaderHelper.DownloadAllSMBFilesAsync(exchange, resolvedDownloadDir, log)
                    .GetAwaiter().GetResult();

                // Load geometry from each SMB file
                foreach (var smbFilePath in smbFilePaths)
                {
                    if (!string.IsNullOrEmpty(smbFilePath) && File.Exists(smbFilePath))
                    {
                        try
                        {
                            var geometriesFromFile = SMBLoaderHelper.LoadGeometryFromSMB(smbFilePath, unit, log);
                            geometries.AddRange(geometriesFromFile);
                        }
                        catch (Exception ex)
                        {
                            log.Error($"Failed to load {Path.GetFileName(smbFilePath)}: {ex.Message}");
                        }
                    }
                }

                success = geometries.Count > 0;
                if (success)
                {
                    // Calculate total file size of downloaded SMB files
                    long totalBytes = 0;
                    foreach (var path in smbFilePaths)
                    {
                        if (File.Exists(path))
                        {
                            totalBytes += new FileInfo(path).Length;
                        }
                    }
                    var totalSizeKB = totalBytes / 1024.0;

                    log.Info($"Loaded {geometries.Count} geometry(s) from '{exchange.ExchangeTitle}' | Files: {smbFilePaths.Count} | Size: {totalSizeKB:F1} KB");
                }
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new NodeResultBuilder()
                .WithSuccess(success)
                .WithLog(log)
                .WithProperty("geometries", geometries)
                .WithProperty("smbFilePaths", smbFilePaths)
                .Build();
        }

        /// <summary>
        /// Loads geometry from a DataExchange, filtering by element IDs.
        /// Only downloads and loads geometry for the specified element IDs.
        /// Use GetExchangeStructure node to get the IDs of available elements.
        /// </summary>
        /// <param name="exchange">Exchange object from SelectExchange node</param>
        /// <param name="elementIds">List of element IDs to load (from GetExchangeStructure)</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter")</param>
        /// <param name="downloadDirectory">Optional directory for downloaded files</param>
        /// <returns>Dictionary with "geometries", "loadedIds", "log", and "success"</returns>
        [MultiReturn(new[] { "geometries", "loadedIds", "log", "success" })]
        public static Dictionary<string, object> LoadByIds(
            Exchange exchange,
            List<string> elementIds,
            string unit = "kUnitType_CentiMeter",
            [DefaultArgument("")] string downloadDirectory = "")
        {
            var geometries = new List<Geometry>();
            var loadedIds = new List<string>();
            var log = new DiagnosticsLogger(DiagnosticLevel.Info);
            bool success = false;

            try
            {
                // Validate inputs
                if (exchange == null)
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");

                if (elementIds == null || elementIds.Count == 0)
                    throw new ArgumentException("Element IDs list cannot be null or empty", nameof(elementIds));

                if (_getTokenFunc == null)
                    throw new InvalidOperationException("Authentication not configured. Please use SelectExchange node first to log in.");

                var accessToken = _getTokenFunc();
                if (string.IsNullOrEmpty(accessToken))
                    throw new InvalidOperationException("Not logged in. Please log in to Dynamo first.");

                // Create ID set for fast lookup
                var idSet = new HashSet<string>(elementIds, StringComparer.OrdinalIgnoreCase);
                log.Info($"Filtering download to {idSet.Count} element ID(s)");

                // Download SMB files with ID filter
                var resolvedDownloadDir = SMBLoaderHelper.ResolveDownloadDirectory(downloadDirectory);
                var smbFilePaths = SMBLoaderHelper.DownloadSelectedSMBFilesAsync(
                    exchange, resolvedDownloadDir, idSet, null, log)
                    .GetAwaiter().GetResult();

                // Load geometry from downloaded files
                foreach (var smbFilePath in smbFilePaths)
                {
                    if (!string.IsNullOrEmpty(smbFilePath) && File.Exists(smbFilePath))
                    {
                        try
                        {
                            var geometriesFromFile = SMBLoaderHelper.LoadGeometryFromSMB(smbFilePath, unit, log);
                            geometries.AddRange(geometriesFromFile);

                            // Extract ID from filename if available
                            var fileName = Path.GetFileNameWithoutExtension(smbFilePath);
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                loadedIds.Add(fileName);
                            }
                        }
                        catch (Exception ex)
                        {
                            log.Error($"Failed to load {Path.GetFileName(smbFilePath)}: {ex.Message}");
                        }
                    }
                }

                success = geometries.Count > 0;
                if (success)
                {
                    log.Info($"Loaded {geometries.Count} geometry(s) from {loadedIds.Count} element(s) | Requested: {elementIds.Count} IDs");
                }
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new Dictionary<string, object>
            {
                { "geometries", geometries },
                { "loadedIds", loadedIds },
                { "log", log.GetLog() },
                { "success", success }
            };
        }

        /// <summary>
        /// Loads geometry from a DataExchange, filtering by element names.
        /// Only downloads and loads geometry for elements matching the specified names.
        /// Use GetExchangeStructure node to get the names of available elements.
        /// </summary>
        /// <param name="exchange">Exchange object from SelectExchange node</param>
        /// <param name="elementNames">List of element names to load</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter")</param>
        /// <param name="downloadDirectory">Optional directory for downloaded files</param>
        /// <returns>Dictionary with "geometries", "loadedNames", "log", and "success"</returns>
        [MultiReturn(new[] { "geometries", "loadedNames", "log", "success" })]
        public static Dictionary<string, object> LoadByNames(
            Exchange exchange,
            List<string> elementNames,
            string unit = "kUnitType_CentiMeter",
            [DefaultArgument("")] string downloadDirectory = "")
        {
            var geometries = new List<Geometry>();
            var loadedNames = new List<string>();
            var log = new DiagnosticsLogger(DiagnosticLevel.Info);
            bool success = false;

            try
            {
                // Validate inputs
                if (exchange == null)
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");

                if (elementNames == null || elementNames.Count == 0)
                    throw new ArgumentException("Element names list cannot be null or empty", nameof(elementNames));

                if (_getTokenFunc == null)
                    throw new InvalidOperationException("Authentication not configured. Please use SelectExchange node first to log in.");

                var accessToken = _getTokenFunc();
                if (string.IsNullOrEmpty(accessToken))
                    throw new InvalidOperationException("Not logged in. Please log in to Dynamo first.");

                // Create name set for fast lookup (case-insensitive)
                var nameSet = new HashSet<string>(elementNames, StringComparer.OrdinalIgnoreCase);
                log.Info($"Filtering download to {nameSet.Count} element name(s)");

                // Download SMB files with name filter
                var resolvedDownloadDir = SMBLoaderHelper.ResolveDownloadDirectory(downloadDirectory);
                var smbFilePaths = SMBLoaderHelper.DownloadSelectedSMBFilesAsync(
                    exchange, resolvedDownloadDir, null, nameSet, log)
                    .GetAwaiter().GetResult();

                // Load geometry from downloaded files
                foreach (var smbFilePath in smbFilePaths)
                {
                    if (!string.IsNullOrEmpty(smbFilePath) && File.Exists(smbFilePath))
                    {
                        try
                        {
                            var geometriesFromFile = SMBLoaderHelper.LoadGeometryFromSMB(smbFilePath, unit, log);
                            geometries.AddRange(geometriesFromFile);

                            // Extract name from filename if available
                            var fileName = Path.GetFileNameWithoutExtension(smbFilePath);
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                loadedNames.Add(fileName);
                            }
                        }
                        catch (Exception ex)
                        {
                            log.Error($"Failed to load {Path.GetFileName(smbFilePath)}: {ex.Message}");
                        }
                    }
                }

                success = geometries.Count > 0;
                if (success)
                {
                    log.Info($"Loaded {geometries.Count} geometry(s) from {loadedNames.Count} element(s) | Requested: {elementNames.Count} names");
                }
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new Dictionary<string, object>
            {
                { "geometries", geometries },
                { "loadedNames", loadedNames },
                { "log", log.GetLog() },
                { "success", success }
            };
        }
    }
}
