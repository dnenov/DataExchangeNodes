using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange.DataModels;
using DataExchangeNodes.DataExchange;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Export Dynamo geometry objects to SMB file format and upload to DataExchange.
    /// Contains only the Dynamo-visible node methods.
    /// Helper methods are in SMBExportHelper.cs.
    /// </summary>
    public static class ExportGeometryToSMB
    {
        /// <summary>
        /// Exports Dynamo geometry objects to an SMB file.
        /// </summary>
        /// <param name="geometries">List of Dynamo geometry objects to export</param>
        /// <param name="outputFilePath">Full path where the SMB file should be saved. If null or empty, a temp file will be created.</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter"). Options: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</param>
        /// <returns>Dictionary with "smbFilePath" (path to created SMB file), "log" (diagnostic messages), and "success" (boolean)</returns>
        [MultiReturn(new[] { "smbFilePath", "log", "success" })]
        public static Dictionary<string, object> ExportToSMB(
            List<Geometry> geometries,
            string outputFilePath = null,
            string unit = "kUnitType_CentiMeter")
        {
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            bool success = false;
            string finalSmbFilePath = null;

            try
            {
                if (geometries == null || geometries.Count == 0)
                {
                    throw new ArgumentException("Geometries list cannot be null or empty", nameof(geometries));
                }

                // Filter out null/invalid geometries
                var filtered = new List<Geometry>(geometries.Count);
                foreach (var geometry in geometries)
                {
                    if (SMBExportHelper.IsUsableGeometry(geometry, log))
                    {
                        filtered.Add(geometry);
                    }
                }
                geometries = filtered;

                if (geometries.Count == 0)
                {
                    throw new ArgumentException("All input geometries are null or invalid", nameof(geometries));
                }

                // Convert unit to mmPerUnit
                double mmPerUnit = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

                // Determine output file path
                if (string.IsNullOrEmpty(outputFilePath))
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "Export");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);

                    var fileName = $"DynamoGeometry_{Guid.NewGuid():N}.smb";
                    finalSmbFilePath = Path.Combine(tempDir, fileName);
                }
                else
                {
                    finalSmbFilePath = Path.GetFullPath(outputFilePath);

                    var outputDir = Path.GetDirectoryName(finalSmbFilePath);
                    if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    {
                        Directory.CreateDirectory(outputDir);
                    }
                }

                // Handle paths with spaces - copy to temp location without spaces if needed
                string exportSmbFilePath = finalSmbFilePath;
                if (finalSmbFilePath.Contains(" "))
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "Export");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);

                    var tempFileName = Path.GetFileName(finalSmbFilePath);
                    if (string.IsNullOrEmpty(tempFileName))
                        tempFileName = $"DynamoGeometry_{Guid.NewGuid():N}.smb";

                    exportSmbFilePath = Path.Combine(tempDir, tempFileName);
                }

                // Get Geometry type from ProtoGeometry.dll using shared loader
                var geometryType = ProtoGeometryLoader.GetGeometryType(log);

                // Find ExportToSMB method
                var allExportMethods = geometryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name == "ExportToSMB")
                    .ToList();

                // Try to find the public ExportToSMB method
                var exportMethod = allExportMethods.FirstOrDefault(m =>
                    m.IsPublic &&
                    m.GetParameters().Length == 3 &&
                    m.GetParameters()[0].ParameterType.Name.Contains("IEnumerable") &&
                    m.GetParameters()[1].ParameterType == typeof(string) &&
                    m.GetParameters()[2].ParameterType == typeof(double));

                if (exportMethod == null)
                {
                    exportMethod = allExportMethods.FirstOrDefault(m =>
                        m.IsPublic &&
                        m.GetParameters().Length == 3 &&
                        m.GetParameters()[1].ParameterType == typeof(string) &&
                        m.GetParameters()[2].ParameterType == typeof(double));
                }

                if (exportMethod == null)
                {
                    throw new InvalidOperationException(
                        $"ExportToSMB method not found in {geometryType.Assembly.FullName}. " +
                        $"Found {allExportMethods.Count} method(s) with name ExportToSMB, but none match expected signature.");
                }

                // Call ExportToSMB
                var result = exportMethod.Invoke(null, new object[] { geometries, exportSmbFilePath, mmPerUnit });

                if (result != null)
                {
                    if (File.Exists(exportSmbFilePath))
                    {
                        // If we exported to temp location due to spaces, copy to final location
                        if (exportSmbFilePath != finalSmbFilePath)
                        {
                            try
                            {
                                File.Copy(exportSmbFilePath, finalSmbFilePath, overwrite: true);
                            }
                            catch (Exception)
                            {
                                finalSmbFilePath = exportSmbFilePath;
                            }
                        }

                        success = true;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new Dictionary<string, object>
            {
                { "smbFilePath", finalSmbFilePath ?? string.Empty },
                { "log", log.GetLog() },
                { "success", success }
            };
        }

        /// <summary>
        /// Uploads an SMB file to a DataExchange by creating an element with geometry.
        /// </summary>
        /// <param name="exchange">The Exchange object containing ExchangeId and CollectionId</param>
        /// <param name="smbFilePath">Full path to the SMB file to upload</param>
        /// <param name="elementName">Name for the new element (default: "ExportedGeometry")</param>
        /// <param name="elementId">Unique ID for the element. Leave empty to auto-generate a GUID.</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter")</param>
        /// <returns>Dictionary with "elementId" (ID of created element), "log" (diagnostic messages), and "success" (boolean)</returns>
        [MultiReturn(new[] { "elementId", "log", "success" })]
        public static Dictionary<string, object> UploadSMBToExchange(
            Exchange exchange,
            string smbFilePath,
            string elementName = "ExportedGeometry",
            string elementId = "",
            string unit = "kUnitType_CentiMeter")
        {
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            bool success = false;
            string finalElementId = string.IsNullOrEmpty(elementId) ? Guid.NewGuid().ToString() : elementId;

            try
            {
                // Validate inputs
                SMBExportHelper.ValidateInputs(exchange, smbFilePath, log);

                // Get Client instance using centralized DataExchangeClient
                var client = DataExchangeClient.GetClient();
                if (client == null)
                {
                    throw new InvalidOperationException("Could not find Client instance. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
                }

                var clientType = client.GetType();
                var identifier = DataExchangeUtils.CreateIdentifier(exchange);

                // Get ElementDataModel
                var elementDataModel = SMBExportHelper.GetElementDataModel(identifier, log);

                // Create Element
                var element = SMBExportHelper.CreateElement(elementDataModel, finalElementId, elementName, log);

                // Get ExchangeData
                var (exchangeData, exchangeDataType) = SMBExportHelper.GetExchangeData(elementDataModel, log);

                // Set ExchangeIdentifier on ExchangeData
                SMBExportHelper.SetExchangeIdentifierIfNeeded(exchangeData, exchangeDataType, identifier, log);

                // Find required types
                var foundTypes = SMBExportHelper.FindRequiredTypes(exchangeDataType, log);

                // Load SMB file to check geometry count
                var geometriesInFile = SMBExportHelper.LoadGeometriesFromSMBFile(smbFilePath, unit, log);
                var geometryCount = geometriesInFile.Count;

                if (geometryCount == 0)
                {
                    throw new InvalidOperationException("SMB file contains no geometries");
                }

                // Split multi-geometry SMB files (SDK requires one GeometryAsset per geometry)
                var smbFilesToUpload = new List<string>();

                if (geometryCount > 1)
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "SplitGeometries");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);

                    var baseFileName = Path.GetFileNameWithoutExtension(smbFilePath);
                    var mmPerUnit = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);
                    var geometryType = typeof(Geometry);

                    var allMethods = geometryType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    var exportMethod = allMethods.FirstOrDefault(m =>
                        m.Name == "ExportToSMB" &&
                        m.GetParameters().Length == 3 &&
                        m.GetParameters()[0].ParameterType.Name.Contains("IEnumerable") &&
                        m.GetParameters()[1].ParameterType == typeof(string) &&
                        m.GetParameters()[2].ParameterType == typeof(double));

                    if (exportMethod == null)
                    {
                        exportMethod = allMethods.FirstOrDefault(m =>
                            m.Name == "ExportToSMB" &&
                            m.GetParameters().Length == 3 &&
                            m.GetParameters()[1].ParameterType == typeof(string) &&
                            m.GetParameters()[2].ParameterType == typeof(double));
                    }

                    if (exportMethod != null)
                    {
                        for (int i = 0; i < geometryCount; i++)
                        {
                            var singleGeometrySMB = Path.Combine(tempDir, $"{baseFileName}_geometry_{i + 1}_{Guid.NewGuid():N}.smb");
                            var singleGeometry = new List<Geometry> { geometriesInFile[i] };

                            try
                            {
                                exportMethod.Invoke(null, new object[] { singleGeometry, singleGeometrySMB, mmPerUnit });
                                if (File.Exists(singleGeometrySMB))
                                {
                                    smbFilesToUpload.Add(singleGeometrySMB);
                                }
                                else
                                {
                                    log.Error($"ExportToSMB succeeded but file not found: {singleGeometrySMB}");
                                }
                            }
                            catch (Exception ex)
                            {
                                log.Error($"Failed to export geometry {i + 1} to SMB: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        log.Error("ExportToSMB method not found - cannot split geometries, using single file");
                        smbFilesToUpload.Add(smbFilePath);
                    }
                }
                else
                {
                    smbFilesToUpload.Add(smbFilePath);
                }

                // Create GeometryAssets
                var allGeometryAssets = new List<(object geometryAsset, Type geometryAssetType, string geometryAssetId)>();

                for (int i = 0; i < smbFilesToUpload.Count; i++)
                {
                    var currentSmbFile = smbFilesToUpload[i];
                    var baseFileName = Path.GetFileNameWithoutExtension(smbFilePath);
                    var geometryName = smbFilesToUpload.Count > 1
                        ? $"{baseFileName}_geometry_{i + 1}"
                        : baseFileName;

                    var (geometryAsset, geometryAssetType, geometryAssetId) = SMBExportHelper.CreateGeometryAsset(exchangeDataType, foundTypes, log);
                    SMBExportHelper.SetGeometryAssetUnits(geometryAsset, geometryAssetType, log);

                    var geometryWrapper = SMBExportHelper.CreateGeometryWrapper(foundTypes, exchangeDataType, log);
                    SMBExportHelper.CreateGeometryComponent(geometryAsset, geometryAssetType, geometryWrapper, foundTypes, exchangeDataType, geometryName, log);

                    SMBExportHelper.AddGeometryAssetToExchangeData(geometryAsset, exchangeData, exchangeDataType, currentSmbFile, foundTypes, geometryName, log);
                    SMBExportHelper.AddGeometryAssetToUnsavedMapping(geometryAsset, exchangeData, exchangeDataType, currentSmbFile, identifier.ExchangeId, 1, log);

                    allGeometryAssets.Add((geometryAsset, geometryAssetType, geometryAssetId));
                }

                // Setup relationships
                if (allGeometryAssets.Count > 0)
                {
                    SMBExportHelper.SetupDesignAssetAndRelationshipsForMultipleGeometries(element, allGeometryAssets, exchangeData, exchangeDataType, foundTypes, elementName, log);
                }

                // Sync to exchange
                try
                {
                    var syncTask = SMBExportHelper.SyncExchangeDataForSMBAsync(client, clientType, identifier, exchangeData, exchangeDataType, log);
                    syncTask.GetAwaiter().GetResult();
                    success = true;
                    log.Info($"SMB upload completed successfully to exchange '{exchange.ExchangeTitle}'");
                }
                catch (Exception ex)
                {
                    log.Error($"Sync flow failed: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new Dictionary<string, object>
            {
                { "elementId", finalElementId },
                { "log", log.GetLog() },
                { "success", success }
            };
        }

        /// <summary>
        /// Gets available unit options for geometry export/upload.
        /// Use this node to populate a dropdown for unit selection.
        /// Returns unit strings compatible with ExportToSMB and UploadSMBToExchange.
        /// </summary>
        /// <returns>List of unit strings: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</returns>
        public static List<string> GetDataExchangeUnits()
        {
            return DataExchangeUtils.GetAvailableUnits();
        }
    }
}
