using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.Interface;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Internal helper class for SMB loading operations.
    /// Contains all the helper methods extracted from LoadGeometryFromExchange to keep the main class focused on Dynamo nodes.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    internal static class SMBLoaderHelper
    {
        // Type names for reflection lookups
        private const string GeometryAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset";
        private const string DataExchangeIdentifierTypeName = "Autodesk.DataExchange.Core.Models.DataExchangeIdentifier";
        private const string AssetInfoTypeName = "Autodesk.GeometryUtilities.SDK.AssetInfo";
        private const string AssetInfoAltTypeName = "Autodesk.DataExchange.DataModels.AssetInfo";

        #region Download Coordination

        /// <summary>
        /// Downloads ALL SMB files from DataExchange API (one per GeometryAsset)
        /// </summary>
        internal static async Task<List<string>> DownloadAllSMBFilesAsync(
            Exchange exchange,
            string downloadDirectory,
            DiagnosticsLogger log)
        {
            var smbFilePaths = new List<string>();

            var tempDir = string.IsNullOrEmpty(downloadDirectory)
                ? Path.Combine(Path.GetTempPath(), "DataExchangeNodes")
                : downloadDirectory;
            if (!Directory.Exists(tempDir))
                Directory.CreateDirectory(tempDir);

            var client = DataExchangeClient.GetClient();
            if (client == null)
            {
                log?.Error("Client instance is null - ensure SelectExchangeElements node has been used first");
                return smbFilePaths;
            }

            // Cast to IClient - required for proper SDK method access
            if (!(client is IClient iClient))
            {
                log?.Error($"Client is not IClient: {client.GetType().FullName}");
                return smbFilePaths;
            }

            var clientType = client.GetType();
            var allMethods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            var results = await DownloadViaBinaryToSMBConversionAsync(client, iClient, exchange, tempDir, allMethods, log);
            if (results != null && results.Count > 0)
            {
                smbFilePaths.AddRange(results);
            }

            if (smbFilePaths.Count == 0 || !smbFilePaths.Any(File.Exists))
            {
                throw new IOException("No SMB files were downloaded. Check that GeometryAssets exist in the exchange.");
            }

            return smbFilePaths.Where(File.Exists).ToList();
        }

        /// <summary>
        /// Downloads SELECTED SMB files from DataExchange API, filtering by IDs or names.
        /// </summary>
        /// <param name="exchange">The exchange to download from</param>
        /// <param name="downloadDirectory">Directory to save SMB files</param>
        /// <param name="filterIds">Set of IDs to filter by (null = no ID filter)</param>
        /// <param name="filterNames">Set of names to filter by (null = no name filter)</param>
        /// <param name="log">Logger for diagnostics</param>
        /// <returns>List of downloaded SMB file paths</returns>
        internal static async Task<List<string>> DownloadSelectedSMBFilesAsync(
            Exchange exchange,
            string downloadDirectory,
            HashSet<string> filterIds,
            HashSet<string> filterNames,
            DiagnosticsLogger log)
        {
            var smbFilePaths = new List<string>();

            var tempDir = string.IsNullOrEmpty(downloadDirectory)
                ? Path.Combine(Path.GetTempPath(), "DataExchangeNodes")
                : downloadDirectory;
            if (!Directory.Exists(tempDir))
                Directory.CreateDirectory(tempDir);

            var client = DataExchangeClient.GetClient();
            if (client == null)
            {
                log?.Error("Client instance is null - ensure SelectExchangeElements node has been used first");
                return smbFilePaths;
            }

            // Cast to IClient - required for proper SDK method access
            if (!(client is IClient iClient))
            {
                log?.Error($"Client is not IClient: {client.GetType().FullName}");
                return smbFilePaths;
            }

            var clientType = client.GetType();
            var allMethods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            var results = await DownloadSelectedViaBinaryConversionAsync(
                client, iClient, exchange, tempDir, allMethods, filterIds, filterNames, log);

            if (results != null && results.Count > 0)
            {
                smbFilePaths.AddRange(results);
            }

            if (smbFilePaths.Count == 0 || !smbFilePaths.Any(File.Exists))
            {
                log?.Info("No matching SMB files were downloaded for the specified filter.");
            }

            return smbFilePaths.Where(File.Exists).ToList();
        }

        /// <summary>
        /// Resolves download directory, defaulting to temp path
        /// </summary>
        internal static string ResolveDownloadDirectory(string downloadDirectory)
        {
            if (string.IsNullOrEmpty(downloadDirectory))
                return Path.Combine(Path.GetTempPath(), "DataExchangeNodes");

            return Path.GetFullPath(downloadDirectory);
        }

        #endregion

        #region Binary Download and Conversion

        /// <summary>
        /// Downloads binary data and converts to SMB using ParseGeometryAssetBinaryToIntermediateGeometry
        /// </summary>
        private static async Task<List<string>> DownloadViaBinaryToSMBConversionAsync(
            object client,
            IClient iClient,
            Exchange exchange,
            string tempDir,
            MethodInfo[] allMethods,
            DiagnosticsLogger log)
        {
            var smbFilePaths = new List<string>();

            // Get binary data for ALL GeometryAssets
            var allBinaryData = await GetAllGeometryAssetBinaryDataAsync(
                client, iClient, exchange, allMethods, log);

            if (allBinaryData == null || allBinaryData.Count == 0)
            {
                log?.Error("Failed to get binary data from any GeometryAsset");
                return smbFilePaths;
            }

            // Convert each binary data to SMB
            int index = 0;
            foreach (var (binaryData, geometryAsset, assetInfo) in allBinaryData)
            {
                if (binaryData == null || binaryData.Length == 0)
                    continue;

                index++;
                var geometryAssetId = GetAssetId(geometryAsset) ?? index.ToString();
                var smbFilePath = Path.Combine(tempDir, $"Exchange_{exchange.ExchangeId}_{exchange.CollectionId}_{geometryAssetId}.smb");

                var success = ConvertBinaryToSMB(
                    client, binaryData, geometryAsset, assetInfo, smbFilePath, allMethods, log);

                if (success && File.Exists(smbFilePath))
                {
                    smbFilePaths.Add(smbFilePath);
                }
            }

            return smbFilePaths;
        }

        /// <summary>
        /// Downloads SELECTED binary data and converts to SMB, filtering by IDs or names
        /// </summary>
        private static async Task<List<string>> DownloadSelectedViaBinaryConversionAsync(
            object client,
            IClient iClient,
            Exchange exchange,
            string tempDir,
            MethodInfo[] allMethods,
            HashSet<string> filterIds,
            HashSet<string> filterNames,
            DiagnosticsLogger log)
        {
            var smbFilePaths = new List<string>();

            // Get filtered binary data
            var filteredBinaryData = await GetSelectedGeometryAssetBinaryDataAsync(
                client, iClient, exchange, allMethods, filterIds, filterNames, log);

            if (filteredBinaryData == null || filteredBinaryData.Count == 0)
            {
                log?.Info("No geometry assets matched the filter criteria");
                return smbFilePaths;
            }

            log?.Info($"Found {filteredBinaryData.Count} geometry asset(s) matching filter");

            // Convert each binary data to SMB
            int index = 0;
            foreach (var (binaryData, geometryAsset, assetInfo, assetName) in filteredBinaryData)
            {
                if (binaryData == null || binaryData.Length == 0)
                    continue;

                index++;
                var geometryAssetId = GetAssetId(geometryAsset) ?? index.ToString();
                var smbFileName = !string.IsNullOrEmpty(assetName)
                    ? $"{assetName}_{geometryAssetId}.smb"
                    : $"Exchange_{exchange.ExchangeId}_{geometryAssetId}.smb";
                var smbFilePath = Path.Combine(tempDir, smbFileName);

                var success = ConvertBinaryToSMB(
                    client, binaryData, geometryAsset, assetInfo, smbFilePath, allMethods, log);

                if (success && File.Exists(smbFilePath))
                {
                    smbFilePaths.Add(smbFilePath);
                }
            }

            return smbFilePaths;
        }

        /// <summary>
        /// Gets binary data for SELECTED geometry assets, filtered by IDs or names
        /// </summary>
        private static async Task<List<(byte[] binaryData, object geometryAsset, object assetInfo, string assetName)>> GetSelectedGeometryAssetBinaryDataAsync(
            object client,
            IClient iClient,
            Exchange exchange,
            MethodInfo[] allMethods,
            HashSet<string> filterIds,
            HashSet<string> filterNames,
            DiagnosticsLogger log)
        {
            var results = new List<(byte[] binaryData, object geometryAsset, object assetInfo, string assetName)>();

            // Find required methods
            var downloadBinaryMethod = allMethods.FirstOrDefault(m => m.Name == "DownloadAndCacheBinaryForBinaryAsset");
            if (downloadBinaryMethod == null)
            {
                log?.Error("DownloadAndCacheBinaryForBinaryAsset method not found");
                return results;
            }

            // Create identifier
            var identifier = DataExchangeUtils.CreateIdentifier(exchange);

            // Get ElementDataModel using IClient interface directly
            object elementDataModel = null;
            try
            {
                var response = await iClient.GetElementDataModelAsync(identifier);
                if (response != null && response.IsSuccess)
                {
                    elementDataModel = response.ValueOrDefault;
                }
            }
            catch (Exception ex)
            {
                log?.Error($"GetElementDataModelAsync failed: {ex.Message}");
                return results;
            }

            if (elementDataModel == null)
            {
                log?.Error("ElementDataModel is null - exchange may be empty or inaccessible");
                return results;
            }

            // Get ExchangeData from ElementDataModel
            var exchangeDataField = elementDataModel.GetType().GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
            if (exchangeDataField == null)
            {
                log?.Error("Could not find exchangeData field");
                return results;
            }

            var exchangeData = exchangeDataField.GetValue(elementDataModel);
            if (exchangeData == null)
            {
                log?.Error("ExchangeData is null");
                return results;
            }

            // Get all GeometryAssets
            var geometryAssets = GetGeometryAssetsFromExchangeData(exchangeData, log);
            if (geometryAssets == null || geometryAssets.Count == 0)
            {
                log?.Error("No GeometryAssets found in ExchangeData");
                return results;
            }

            // Filter geometry assets
            var filteredAssets = new List<(object asset, string id, string name)>();
            foreach (var geometryAsset in geometryAssets)
            {
                var assetId = GetAssetId(geometryAsset);
                var assetName = GetAssetName(geometryAsset);

                bool matchesId = filterIds == null || (filterIds.Contains(assetId ?? ""));
                bool matchesName = filterNames == null || (filterNames.Contains(assetName ?? "", StringComparer.OrdinalIgnoreCase));

                // If both filters are null, match all; if either matches, include
                if ((filterIds == null && filterNames == null) ||
                    (filterIds != null && matchesId) ||
                    (filterNames != null && matchesName))
                {
                    filteredAssets.Add((geometryAsset, assetId, assetName));
                }
            }

            log?.Info($"Filtered to {filteredAssets.Count} of {geometryAssets.Count} geometry asset(s)");

            // Process each filtered GeometryAsset
            foreach (var (geometryAsset, assetId, assetName) in filteredAssets)
            {
                var binaryResult = await DownloadBinaryForGeometryAssetAsync(
                    client, geometryAsset, identifier, downloadBinaryMethod, allMethods, log);

                if (binaryResult.binaryData != null && binaryResult.binaryData.Length > 0)
                {
                    results.Add((binaryResult.binaryData, binaryResult.geometryAsset, binaryResult.assetInfo, assetName));
                }
            }

            return results;
        }

        /// <summary>
        /// Gets the name of an asset from ObjectInfo or direct Name property
        /// </summary>
        private static string GetAssetName(object asset)
        {
            if (asset == null) return null;

            var assetType = asset.GetType();

            // Try ObjectInfo.Name
            var objectInfoProp = assetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
            if (objectInfoProp != null)
            {
                var objectInfo = objectInfoProp.GetValue(asset);
                if (objectInfo != null)
                {
                    var nameProp = objectInfo.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null)
                    {
                        var name = nameProp.GetValue(objectInfo)?.ToString();
                        if (!string.IsNullOrEmpty(name))
                        {
                            return name;
                        }
                    }
                }
            }

            // Try direct Name property
            var directNameProp = assetType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (directNameProp != null)
            {
                return directNameProp.GetValue(asset)?.ToString();
            }

            return null;
        }

        /// <summary>
        /// Gets binary data for ALL geometry assets from ElementDataModel
        /// </summary>
        private static async Task<List<(byte[] binaryData, object geometryAsset, object assetInfo)>> GetAllGeometryAssetBinaryDataAsync(
            object client,
            IClient iClient,
            Exchange exchange,
            MethodInfo[] allMethods,
            DiagnosticsLogger log)
        {
            var results = new List<(byte[] binaryData, object geometryAsset, object assetInfo)>();

            // Find required methods
            var downloadBinaryMethod = allMethods.FirstOrDefault(m => m.Name == "DownloadAndCacheBinaryForBinaryAsset");
            if (downloadBinaryMethod == null)
            {
                log?.Error("DownloadAndCacheBinaryForBinaryAsset method not found");
                return results;
            }

            // Create identifier
            var identifier = DataExchangeUtils.CreateIdentifier(exchange);

            // Get ElementDataModel using IClient interface directly (not reflection)
            object elementDataModel = null;
            try
            {
                var response = await iClient.GetElementDataModelAsync(identifier);
                if (response != null && response.IsSuccess)
                {
                    elementDataModel = response.ValueOrDefault;
                }
            }
            catch (Exception ex)
            {
                log?.Error($"GetElementDataModelAsync failed: {ex.Message}");
                return results;
            }

            if (elementDataModel == null)
            {
                log?.Error("ElementDataModel is null - exchange may be empty or inaccessible");
                return results;
            }

            // Get ExchangeData
            var exchangeData = GetExchangeDataFromModel(elementDataModel, log);
            if (exchangeData == null)
            {
                log?.Error("ExchangeData is null");
                return results;
            }

            // Get all GeometryAssets
            var geometryAssets = GetGeometryAssetsFromExchangeData(exchangeData, log);
            if (geometryAssets == null || geometryAssets.Count == 0)
            {
                log?.Error("No GeometryAssets found in ExchangeData");
                return results;
            }

            // Process each GeometryAsset
            foreach (var geometryAsset in geometryAssets)
            {
                var binaryResult = await DownloadBinaryForGeometryAssetAsync(
                    client, geometryAsset, identifier, downloadBinaryMethod, allMethods, log);

                if (binaryResult.binaryData != null && binaryResult.binaryData.Length > 0)
                {
                    results.Add(binaryResult);
                }
            }

            return results;
        }

        /// <summary>
        /// Gets ExchangeData property from ElementDataModel
        /// </summary>
        private static object GetExchangeDataFromModel(object elementDataModel, DiagnosticsLogger log)
        {
            var elementDataModelType = elementDataModel.GetType();

            // ExchangeData is internal, try NonPublic first
            var exchangeDataProp = elementDataModelType.GetProperty("ExchangeData",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (exchangeDataProp == null)
            {
                exchangeDataProp = elementDataModelType.GetProperty("ExchangeData",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            return exchangeDataProp?.GetValue(elementDataModel);
        }

        /// <summary>
        /// Gets all GeometryAssets from ExchangeData using GetAssetsByType
        /// </summary>
        private static List<object> GetGeometryAssetsFromExchangeData(object exchangeData, DiagnosticsLogger log)
        {
            var exchangeDataType = exchangeData.GetType();

            // Find GeometryAsset type
            var geometryAssetType = ReflectionUtils.FindType(GeometryAssetTypeName, exchangeDataType);
            if (geometryAssetType == null)
            {
                log?.Error("GeometryAsset type not found");
                return new List<object>();
            }

            // Get GetAssetsByType<GeometryAsset>()
            var getAssetsByTypeMethod = exchangeDataType.GetMethod("GetAssetsByType", BindingFlags.Public | BindingFlags.Instance);
            if (getAssetsByTypeMethod == null)
            {
                log?.Error("GetAssetsByType method not found on ExchangeData");
                return new List<object>();
            }

            var genericMethod = getAssetsByTypeMethod.MakeGenericMethod(geometryAssetType);
            var result = genericMethod.Invoke(exchangeData, null);

            if (result is System.Collections.IEnumerable enumerable)
            {
                return enumerable.Cast<object>().Where(g => g != null).ToList();
            }

            return new List<object>();
        }

        /// <summary>
        /// Downloads binary data for a single GeometryAsset
        /// </summary>
        private static async Task<(byte[] binaryData, object geometryAsset, object assetInfo)> DownloadBinaryForGeometryAssetAsync(
            object client,
            object geometryAsset,
            DataExchangeIdentifier identifier,
            MethodInfo downloadBinaryMethod,
            MethodInfo[] allMethods,
            DiagnosticsLogger log)
        {
            // Get BinaryReference
            var binaryRefProp = geometryAsset.GetType().GetProperty("BinaryReference");
            if (binaryRefProp == null)
                return (null, geometryAsset, null);

            var binaryRef = binaryRefProp.GetValue(geometryAsset);
            if (binaryRef == null)
                return (null, geometryAsset, null);

            // Prepare download parameters
            var downloadParams = downloadBinaryMethod.GetParameters();
            object[] downloadInvokeParams;

            if (downloadParams.Length == 3)
            {
                downloadInvokeParams = new object[] { identifier, binaryRef, CancellationToken.None };
            }
            else if (downloadParams.Length == 4)
            {
                var clientType = client.GetType();
                var workSpaceProp = clientType.GetProperty("WorkSpaceUserGeometryPath");
                var workSpacePath = workSpaceProp?.GetValue(client)?.ToString() ?? Path.GetTempPath();
                downloadInvokeParams = new object[] { identifier, binaryRef, workSpacePath, CancellationToken.None };
            }
            else
            {
                downloadInvokeParams = new object[] { identifier, binaryRef };
            }

            // Execute download
            var downloadResult = downloadBinaryMethod.Invoke(client, downloadInvokeParams);
            if (downloadResult == null || !downloadResult.GetType().IsGenericType)
                return (null, geometryAsset, null);

            var binaryFilePath = ((dynamic)downloadResult).GetAwaiter().GetResult() as string;
            if (string.IsNullOrEmpty(binaryFilePath) || !File.Exists(binaryFilePath))
                return (null, geometryAsset, null);

            // Read binary data (handle range if specified)
            byte[] binaryData = ReadBinaryData(binaryRef, binaryFilePath);

            // Create AssetInfo
            var assetInfo = CreateAssetInfoForGeometryAsset(client, geometryAsset, allMethods);

            return (binaryData, geometryAsset, assetInfo);
        }

        /// <summary>
        /// Reads binary data from file, handling range if BinaryReference has Start/End
        /// </summary>
        private static byte[] ReadBinaryData(object binaryRef, string binaryFilePath)
        {
            var startProp = binaryRef.GetType().GetProperty("Start");
            var endProp = binaryRef.GetType().GetProperty("End");

            if (startProp != null && endProp != null)
            {
                var start = Convert.ToInt64(startProp.GetValue(binaryRef));
                var end = Convert.ToInt64(endProp.GetValue(binaryRef));
                var length = end - start + 1;

                using (var fileStream = new FileStream(binaryFilePath, FileMode.Open, FileAccess.Read))
                {
                    fileStream.Seek(start, SeekOrigin.Begin);
                    var data = new byte[length];
                    var bytesRead = fileStream.Read(data, 0, (int)length);
                    if (bytesRead < length)
                    {
                        Array.Resize(ref data, bytesRead);
                    }
                    return data;
                }
            }

            return File.ReadAllBytes(binaryFilePath);
        }

        /// <summary>
        /// Creates AssetInfo for a GeometryAsset
        /// </summary>
        private static object CreateAssetInfoForGeometryAsset(object client, object geometryAsset, MethodInfo[] allMethods)
        {
            var createAssetInfoMethod = allMethods.FirstOrDefault(m => m.Name == "CreateAssetInfoForGeometryAsset");
            if (createAssetInfoMethod != null)
            {
                try
                {
                    return createAssetInfoMethod.Invoke(client, new object[] { geometryAsset });
                }
                catch
                {
                    // Fall through to create empty AssetInfo
                }
            }

            // Fallback: create empty AssetInfo
            var assetInfoType = ReflectionUtils.FindType(AssetInfoTypeName) ??
                               ReflectionUtils.FindType(AssetInfoAltTypeName);

            if (assetInfoType != null)
            {
                return Activator.CreateInstance(assetInfoType);
            }

            return null;
        }

        /// <summary>
        /// Converts binary data to SMB format using ParseGeometryAssetBinaryToIntermediateGeometry
        /// </summary>
        private static bool ConvertBinaryToSMB(
            object client,
            byte[] binaryData,
            object geometryAsset,
            object assetInfo,
            string smbFilePath,
            MethodInfo[] allMethods,
            DiagnosticsLogger log)
        {
            var parseMethod = allMethods.FirstOrDefault(m => m.Name == "ParseGeometryAssetBinaryToIntermediateGeometry");
            if (parseMethod == null)
            {
                log?.Error("ParseGeometryAssetBinaryToIntermediateGeometry method not found");
                return false;
            }

            if (binaryData == null || binaryData.Length == 0)
            {
                return false;
            }

            try
            {
                var smbBytes = parseMethod.Invoke(client, new object[] { assetInfo, geometryAsset, binaryData }) as byte[];

                if (smbBytes == null || smbBytes.Length == 0)
                {
                    return false;
                }

                File.WriteAllBytes(smbFilePath, smbBytes);
                return true;
            }
            catch (Exception ex)
            {
                log?.Error($"Error converting binary to SMB: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region SMB Loading

        /// <summary>
        /// Loads geometry from SMB file using ProtoGeometry APIs
        /// </summary>
        internal static List<Geometry> LoadGeometryFromSMB(
            string smbFilePath,
            string unit,
            DiagnosticsLogger log)
        {
            var geometries = new List<Geometry>();

            if (!File.Exists(smbFilePath))
            {
                throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
            }

            // Handle paths with spaces
            var normalizedPath = Path.GetFullPath(smbFilePath);
            if (normalizedPath.Contains(" "))
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                var tempPath = Path.Combine(tempDir, Path.GetFileName(smbFilePath));
                File.Copy(normalizedPath, tempPath, overwrite: true);
                smbFilePath = tempPath;
            }
            else
            {
                smbFilePath = normalizedPath;
            }

            // Convert unit
            double mmPerUnit = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

            // Get Geometry type
            var geometryType = ProtoGeometryLoader.GetGeometryType(log);

            // Find ImportFromSMB method
            var importMethod = geometryType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "ImportFromSMB" &&
                    m.GetParameters().Length == 2 &&
                    m.GetParameters()[0].ParameterType == typeof(string) &&
                    m.GetParameters()[1].ParameterType == typeof(double));

            if (importMethod == null)
            {
                throw new InvalidOperationException("ImportFromSMB method not found on Geometry type");
            }

            try
            {
                var result = importMethod.Invoke(null, new object[] { smbFilePath, mmPerUnit }) as Geometry[];
                if (result != null && result.Length > 0)
                {
                    geometries.AddRange(result);
                }
                else
                {
                    throw new InvalidOperationException("ImportFromSMB returned no geometries");
                }
            }
            catch (TargetInvocationException ex)
            {
                log?.Error($"ImportFromSMB failed: {ex.InnerException?.Message ?? ex.Message}");
                throw new InvalidOperationException(
                    $"ImportFromSMB failed in native LibG code. " +
                    $"This may indicate SMB file format incompatibility or LibG implementation issues.",
                    ex.InnerException ?? ex);
            }

            return geometries;
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets the Id property from an asset object
        /// </summary>
        private static string GetAssetId(object asset)
        {
            if (asset == null) return null;

            var idProp = asset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            return idProp?.GetValue(asset)?.ToString();
        }

        #endregion
    }
}
