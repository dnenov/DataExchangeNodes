using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange;
using Autodesk.DataExchange.DataModels;
using Autodesk.DataExchange.Core.Models;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Internal helper class for SMB export and upload operations.
    /// Contains all the helper methods extracted from ExportGeometryToSMB to keep the main class focused on Dynamo nodes.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    internal static class SMBExportHelper
    {
        // Static mappings for the adapter pattern (GeometryAsset ID -> SMB path)
        private static readonly Dictionary<string, string> GeometryAssetIdToSmbPath = new Dictionary<string, string>();
        private static readonly Dictionary<string, int> GeometryAssetIdToGeometryCount = new Dictionary<string, int>();

        #region Input Validation

        /// <summary>
        /// Validates inputs for UploadSMBToExchange
        /// </summary>
        internal static void ValidateInputs(Exchange exchange, string smbFilePath, DiagnosticsLogger log)
        {
            if (exchange == null)
            {
                throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");
            }

            if (string.IsNullOrEmpty(exchange.ExchangeId))
            {
                throw new ArgumentException("Exchange.ExchangeId is required", nameof(exchange));
            }

            if (string.IsNullOrEmpty(exchange.CollectionId))
            {
                throw new ArgumentException("Exchange.CollectionId is required", nameof(exchange));
            }

            if (string.IsNullOrEmpty(smbFilePath) || !File.Exists(smbFilePath))
            {
                throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
            }
        }

        /// <summary>
        /// Checks if a geometry object is valid for processing
        /// </summary>
        internal static bool IsUsableGeometry(Geometry geometry, DiagnosticsLogger log)
        {
            if (geometry == null)
                return false;

            try
            {
                var isDisposedProp = geometry.GetType().GetProperty("IsDisposed", BindingFlags.Public | BindingFlags.Instance);
                if (isDisposedProp != null && isDisposedProp.PropertyType == typeof(bool))
                {
                    var isDisposed = (bool)isDisposedProp.GetValue(geometry);
                    if (isDisposed)
                    {
                        return false;
                    }
                }
            }
            catch
            {
                // If we can't inspect disposal state, assume it's usable.
            }

            // Try a lightweight access to catch invalid native pointers
            try
            {
                var bbox = geometry.BoundingBox;
                if (bbox == null)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        #endregion

        #region ElementDataModel Access

        /// <summary>
        /// Checks if ElementDataModel exists in exchange (read-only check, does not create new one)
        /// </summary>
        internal static ElementDataModel CheckElementDataModel(DataExchangeIdentifier identifier, DiagnosticsLogger log)
        {
            return CheckElementDataModel(identifier, null, log);
        }

        /// <summary>
        /// Checks if ElementDataModel exists in exchange for a specific revision
        /// </summary>
        internal static ElementDataModel CheckElementDataModel(DataExchangeIdentifier identifier, string revisionId, DiagnosticsLogger log)
        {
            if (!DataExchangeClient.IsInitialized())
            {
                throw new InvalidOperationException("Client is not initialized. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
            }

            var response = string.IsNullOrEmpty(revisionId)
                ? DataExchangeClient.GetElementDataModelWithErrorInfoAsync(identifier, CancellationToken.None).GetAwaiter().GetResult()
                : DataExchangeClient.GetElementDataModelWithErrorInfoAsync(identifier, revisionId, CancellationToken.None).GetAwaiter().GetResult();

            if (!response.isSuccess)
            {
                var errorMsg = response.errorMessage ?? "GetElementDataModelAsync failed";
                log.Error(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            return response.model;
        }

        /// <summary>
        /// Gets ElementDataModel from exchange, creating new one if needed.
        /// </summary>
        /// <param name="identifier">The exchange identifier</param>
        /// <param name="log">Diagnostics logger</param>
        /// <param name="replaceMode">Replace mode: "append" (default), "replaceAll", or "replaceByName"</param>
        /// <param name="elementNamesToReplace">Element names to replace (only used for "replaceByName" mode)</param>
        internal static ElementDataModel GetElementDataModel(
            DataExchangeIdentifier identifier,
            DiagnosticsLogger log,
            string replaceMode = "append",
            List<string> elementNamesToReplace = null)
        {
            if (!DataExchangeClient.IsInitialized())
            {
                throw new InvalidOperationException("Client is not initialized. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
            }

            var client = DataExchangeClient.GetClient();
            if (client == null)
            {
                throw new InvalidOperationException("Cannot get ElementDataModel: Client is not initialized");
            }

            // For "replaceAll" mode, always create a fresh ElementDataModel (Grasshopper approach)
            if (replaceMode == "replaceAll")
            {
                log.Info("Replace mode: replaceAll - creating fresh ElementDataModel");
                var freshModel = ElementDataModel.Create(client);

                // CRITICAL: Must sync the fresh model to the exchange BEFORE using it
                // Without this sync, the SDK doesn't properly register the new model
                // and subsequent operations (like GetElementDataModel) return stale data
                var syncResponse = client.SyncExchangeDataAsync(identifier, freshModel).GetAwaiter().GetResult();
                if (!syncResponse.IsSuccess)
                {
                    var errorDetails = syncResponse.Errors != null && syncResponse.Errors.Any()
                        ? string.Join(", ", syncResponse.Errors.Select(e => e.ToString()))
                        : "Unknown error";
                    throw new InvalidOperationException($"Failed to initialize fresh ElementDataModel: {errorDetails}");
                }
                log.Info("Synced fresh ElementDataModel to exchange");

                return freshModel;
            }

            // Load existing ElementDataModel
            var response = DataExchangeClient.GetElementDataModelWithErrorInfoAsync(identifier, CancellationToken.None).GetAwaiter().GetResult();

            if (!response.isSuccess)
            {
                var errorMsg = response.errorMessage ?? "GetElementDataModelAsync failed";
                log.Error(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            if (response.model == null)
            {
                var newElementDataModel = ElementDataModel.Create(client);

                // Sync empty ElementDataModel to initialize exchange structure
                var syncResponse = client.SyncExchangeDataAsync(identifier, newElementDataModel).GetAwaiter().GetResult();
                if (!syncResponse.IsSuccess)
                {
                    var errorDetails = syncResponse.Errors != null && syncResponse.Errors.Any()
                        ? string.Join(", ", syncResponse.Errors.Select(e => e.ToString()))
                        : "Unknown error";
                    throw new InvalidOperationException($"Failed to initialize exchange structure: {errorDetails}");
                }

                return newElementDataModel;
            }

            // For "replaceByName" mode, delete elements with matching names
            if (replaceMode == "replaceByName" && elementNamesToReplace != null && elementNamesToReplace.Count > 0)
            {
                DeleteElementsByName(response.model, elementNamesToReplace, log);
            }

            return response.model;
        }

        /// <summary>
        /// Deletes elements from ElementDataModel by name.
        /// Uses reflection to call ElementDataModel.DeleteElement(elementId).
        /// </summary>
        internal static void DeleteElementsByName(ElementDataModel elementDataModel, List<string> namesToDelete, DiagnosticsLogger log)
        {
            try
            {
                // Get Elements property
                var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
                if (elementsProperty == null)
                {
                    log.Error("Could not find Elements property on ElementDataModel");
                    return;
                }

                var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                if (elements == null)
                {
                    log.Info("No existing elements to check for replacement");
                    return;
                }

                // Find DeleteElement method
                var deleteMethod = typeof(ElementDataModel).GetMethod("DeleteElement",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null);

                if (deleteMethod == null)
                {
                    log.Error("DeleteElement method not found on ElementDataModel - cannot perform replaceByName");
                    return;
                }

                // Build set of names to delete for fast lookup
                var namesToDeleteSet = new HashSet<string>(namesToDelete, StringComparer.OrdinalIgnoreCase);

                // Find elements to delete
                var elementsToDelete = new List<(string id, string name)>();
                foreach (var element in elements.Cast<object>())
                {
                    var nameProp = element.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    var idProp = element.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

                    if (nameProp != null && idProp != null)
                    {
                        var elementName = nameProp.GetValue(element)?.ToString();
                        var elementId = idProp.GetValue(element)?.ToString();

                        if (!string.IsNullOrEmpty(elementName) && namesToDeleteSet.Contains(elementName))
                        {
                            elementsToDelete.Add((elementId, elementName));
                        }
                    }
                }

                // Delete matched elements
                foreach (var (id, name) in elementsToDelete)
                {
                    try
                    {
                        deleteMethod.Invoke(elementDataModel, new object[] { id });
                        log.Info($"Deleted existing element '{name}' (ID: {id}) for replacement");
                    }
                    catch (Exception ex)
                    {
                        log.Error($"Failed to delete element '{name}': {ex.Message}");
                    }
                }

                if (elementsToDelete.Count > 0)
                {
                    log.Info($"Deleted {elementsToDelete.Count} existing element(s) for replacement");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error during element deletion: {ex.Message}");
            }
        }

        #endregion

        #region Element and Asset Creation

        /// <summary>
        /// Creates Element and ElementProperties
        /// </summary>
        internal static object CreateElement(ElementDataModel elementDataModel, string finalElementId, string elementName, DiagnosticsLogger log)
        {
            // Try to find an existing element with the same name
            var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
            if (elementsProperty != null)
            {
                var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                if (elements != null)
                {
                    foreach (var existingElement in elements.Cast<object>())
                    {
                        var nameProp = existingElement.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProp != null)
                        {
                            var existingName = nameProp.GetValue(existingElement)?.ToString();
                            if (existingName == elementName)
                            {
                                return existingElement;
                            }
                        }
                    }
                }
            }

            // Create new element
            var elementProperties = new ElementProperties(
                finalElementId,
                elementName,
                "Geometry",
                "Exported",
                "SMB"
            );

            var element = elementDataModel.AddElement(elementProperties);
            if (element == null)
            {
                throw new InvalidOperationException("AddElement returned null");
            }
            return element;
        }

        /// <summary>
        /// Gets ExchangeData from ElementDataModel using reflection
        /// </summary>
        internal static (object exchangeData, Type exchangeDataType) GetExchangeData(ElementDataModel elementDataModel, DiagnosticsLogger log)
        {
            var exchangeDataField = typeof(ElementDataModel).GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
            if (exchangeDataField == null)
            {
                throw new InvalidOperationException("Could not find exchangeData field on ElementDataModel");
            }

            var exchangeData = exchangeDataField.GetValue(elementDataModel);
            if (exchangeData == null)
            {
                throw new InvalidOperationException("ExchangeData is null");
            }

            return (exchangeData, exchangeData.GetType());
        }

        /// <summary>
        /// Finds all required types across assemblies
        /// </summary>
        internal static Dictionary<string, Type> FindRequiredTypes(Type exchangeDataType, DiagnosticsLogger log)
        {
            var typesToFind = new[]
            {
                "Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset",
                "Autodesk.DataExchange.SchemaObjects.Geometry.GeometryWrapper",
                "Autodesk.DataExchange.Core.Enums.GeometryFormat",
                "Autodesk.DataExchange.Core.Enums.GeometryType",
                "Autodesk.DataExchange.SchemaObjects.Components.GeometryComponent",
                "Autodesk.DataExchange.SchemaObjects.Components.Component",
                "Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset",
                "Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship",
                "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship",
                "Autodesk.DataExchange.SchemaObjects.Components.ModelStructure"
            };

            var foundTypes = new Dictionary<string, Type>();
            foreach (var typeName in typesToFind)
            {
                var foundType = ReflectionUtils.FindType(typeName, exchangeDataType, foundTypes);
                if (foundType != null && !foundTypes.ContainsKey(typeName))
                {
                    foundTypes[typeName] = foundType;
                }
            }

            return foundTypes;
        }

        /// <summary>
        /// Creates a GeometryAsset instance with ID set
        /// </summary>
        internal static (object geometryAsset, Type geometryAssetType, string geometryAssetId) CreateGeometryAsset(Type exchangeDataType, Dictionary<string, Type> foundTypes, DiagnosticsLogger log)
        {
            const string geometryAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset";
            if (!foundTypes.TryGetValue(geometryAssetTypeName, out var geometryAssetType))
            {
                geometryAssetType = ReflectionUtils.FindType(geometryAssetTypeName, exchangeDataType, foundTypes);
                if (geometryAssetType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryAsset type");
                }
            }

            var geometryAssetId = Guid.NewGuid().ToString();
            var geometryAsset = ReflectionUtils.CreateInstanceWithId(geometryAssetType, geometryAssetId, foundTypes, log);

            return (geometryAsset, geometryAssetType, geometryAssetId);
        }

        /// <summary>
        /// Sets units on GeometryAsset
        /// </summary>
        internal static void SetGeometryAssetUnits(object geometryAsset, Type geometryAssetType, DiagnosticsLogger log)
        {
            var lengthUnitProperty = geometryAssetType.GetProperty("LengthUnit", BindingFlags.Public | BindingFlags.Instance);
            if (lengthUnitProperty != null)
            {
                var unitEnumType = Type.GetType("Autodesk.DataExchange.SchemaObjects.Units.LengthUnit, Autodesk.DataExchange.SchemaObjects");
                if (unitEnumType != null)
                {
                    var centimeterValue = Enum.Parse(unitEnumType, "CentiMeter");
                    lengthUnitProperty.SetValue(geometryAsset, centimeterValue);
                }
            }
        }

        /// <summary>
        /// Creates GeometryWrapper for BRep geometry
        /// </summary>
        internal static object CreateGeometryWrapper(Dictionary<string, Type> foundTypes, Type exchangeDataType, DiagnosticsLogger log)
        {
            const string geometryWrapperTypeName = "Autodesk.DataExchange.SchemaObjects.Geometry.GeometryWrapper";
            if (!foundTypes.TryGetValue(geometryWrapperTypeName, out var geometryWrapperType))
            {
                geometryWrapperType = ReflectionUtils.FindType(geometryWrapperTypeName, exchangeDataType, foundTypes);
                if (geometryWrapperType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryWrapper type");
                }
            }

            const string geometryFormatTypeName = "Autodesk.DataExchange.Core.Enums.GeometryFormat";
            if (!foundTypes.TryGetValue(geometryFormatTypeName, out var geometryFormatEnumType))
            {
                geometryFormatEnumType = ReflectionUtils.FindType(geometryFormatTypeName, exchangeDataType, foundTypes);
            }

            const string geometryTypeTypeName = "Autodesk.DataExchange.Core.Enums.GeometryType";
            if (!foundTypes.TryGetValue(geometryTypeTypeName, out var geometryTypeEnumType))
            {
                geometryTypeEnumType = ReflectionUtils.FindType(geometryTypeTypeName, exchangeDataType, foundTypes);
            }

            if (geometryFormatEnumType == null || geometryTypeEnumType == null)
            {
                throw new InvalidOperationException("Could not find GeometryFormat or GeometryType enum types");
            }

            var stepFormat = Enum.Parse(geometryFormatEnumType, "Step");
            var brepType = Enum.Parse(geometryTypeEnumType, "BRep");

            var constructor = geometryWrapperType.GetConstructor(new[] { geometryTypeEnumType, geometryFormatEnumType, typeof(string) });
            object geometryWrapper = null;
            if (constructor != null)
            {
                geometryWrapper = constructor.Invoke(new object[] { brepType, stepFormat, "" });
            }
            else
            {
                constructor = geometryWrapperType.GetConstructor(new[] { geometryTypeEnumType, geometryFormatEnumType });
                if (constructor != null)
                {
                    geometryWrapper = constructor.Invoke(new object[] { brepType, stepFormat });
                }
            }

            if (geometryWrapper == null)
            {
                throw new InvalidOperationException("Could not create GeometryWrapper for BRep SMB format");
            }

            return geometryWrapper;
        }

        /// <summary>
        /// Creates GeometryComponent and sets it on GeometryAsset
        /// </summary>
        internal static void CreateGeometryComponent(object geometryAsset, Type geometryAssetType, object geometryWrapper, Dictionary<string, Type> foundTypes, Type exchangeDataType, string geometryName, DiagnosticsLogger log)
        {
            const string geometryComponentTypeName = "Autodesk.DataExchange.SchemaObjects.Components.GeometryComponent";
            if (!foundTypes.TryGetValue(geometryComponentTypeName, out var geometryComponentType))
            {
                geometryComponentType = ReflectionUtils.FindType(geometryComponentTypeName, exchangeDataType, foundTypes);
                if (geometryComponentType == null)
                {
                    throw new InvalidOperationException("Could not find GeometryComponent type");
                }
            }

            var geometryComponent = Activator.CreateInstance(geometryComponentType);
            var geometryProperty = geometryComponentType.GetProperty("Geometry", BindingFlags.NonPublic | BindingFlags.Instance);
            if (geometryProperty == null)
            {
                throw new InvalidOperationException("Could not find Geometry property on GeometryComponent");
            }

            geometryProperty.SetValue(geometryComponent, geometryWrapper);

            var geometryAssetGeometryProperty = geometryAssetType.GetProperty("Geometry", BindingFlags.Public | BindingFlags.Instance);
            if (geometryAssetGeometryProperty == null)
            {
                throw new InvalidOperationException("Could not find Geometry property on GeometryAsset");
            }

            geometryAssetGeometryProperty.SetValue(geometryAsset, geometryComponent);
        }

        /// <summary>
        /// Creates an ObjectInfo component with the specified name
        /// </summary>
        internal static object CreateObjectInfo(string name, Dictionary<string, Type> foundTypes, Type exchangeDataType, DiagnosticsLogger log)
        {
            const string componentTypeName = "Autodesk.DataExchange.SchemaObjects.Components.Component";
            if (foundTypes.TryGetValue(componentTypeName, out var componentType) ||
                (componentType = ReflectionUtils.FindType(componentTypeName, exchangeDataType, foundTypes)) != null)
            {
                var objectInfo = Activator.CreateInstance(componentType);
                var nameProperty = componentType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                if (nameProperty != null)
                {
                    nameProperty.SetValue(objectInfo, name);
                }
                return objectInfo;
            }
            return null;
        }

        #endregion

        #region Asset Configuration

        /// <summary>
        /// Adds GeometryAsset to ExchangeData and sets ObjectInfo
        /// </summary>
        internal static void AddGeometryAssetToExchangeData(object geometryAsset, object exchangeData, Type exchangeDataType, string geometryFilePath, Dictionary<string, Type> foundTypes, string geometryName, DiagnosticsLogger log)
        {
            var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
            if (exchangeDataAddMethod == null)
            {
                throw new InvalidOperationException("Could not find Add method on ExchangeData");
            }

            exchangeDataAddMethod.Invoke(exchangeData, new object[] { geometryAsset });

            var geometryAssetType = geometryAsset.GetType();
            var objectInfoProperty = geometryAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
            if (objectInfoProperty != null)
            {
                var nameToUse = !string.IsNullOrEmpty(geometryName)
                    ? geometryName
                    : Path.GetFileNameWithoutExtension(geometryFilePath);
                var objectInfo = CreateObjectInfo(nameToUse, foundTypes, exchangeDataType, log);
                if (objectInfo != null)
                {
                    objectInfoProperty.SetValue(geometryAsset, objectInfo);
                }
            }
        }

        /// <summary>
        /// Creates a minimal dummy STEP file for the STEP→SMB adapter pattern
        /// </summary>
        internal static string CreateDummyStepFile(string smbFilePath, DiagnosticsLogger log)
        {
            var dummyStepPath = Path.ChangeExtension(smbFilePath, ".dummy.stp");

            var minimalStepContent = @"ISO-10303-21;
HEADER;
FILE_DESCRIPTION(('Dummy STEP file for SMB upload adapter'),'2;1');
FILE_NAME('dummy.stp','2024-01-01T00:00:00',(''),(''),'','','');
FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));
ENDSEC;
DATA;
ENDSEC;
END-ISO-10303-21;
";

            File.WriteAllText(dummyStepPath, minimalStepContent);
            return dummyStepPath;
        }

        /// <summary>
        /// Adds GeometryAsset to UnsavedGeometryMapping using SetBRepGeometryByAsset
        /// ADAPTER PATTERN: Register dummy STEP file to satisfy SDK's translation contract
        /// </summary>
        internal static string AddGeometryAssetToUnsavedMapping(object geometryAsset, object exchangeData, Type exchangeDataType, string smbFilePath, string exchangeId, int geometryCount, DiagnosticsLogger log)
        {
            if (!File.Exists(smbFilePath))
            {
                throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
            }

            var geometryAssetIdProp = geometryAsset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            var geometryAssetId = geometryAssetIdProp?.GetValue(geometryAsset)?.ToString();

            if (string.IsNullOrEmpty(geometryAssetId))
            {
                throw new InvalidOperationException("Could not get GeometryAsset ID");
            }

            var dummyStepPath = CreateDummyStepFile(smbFilePath, log);

            var setBRepGeometryMethod = exchangeDataType.GetMethod("SetBRepGeometryByAsset", BindingFlags.Public | BindingFlags.Instance);
            if (setBRepGeometryMethod == null)
            {
                throw new InvalidOperationException("Could not find SetBRepGeometryByAsset method on ExchangeData");
            }

            setBRepGeometryMethod.Invoke(exchangeData, new object[] { geometryAsset, dummyStepPath });

            // Use just geometry asset ID as key (not exchange-prefixed) so ProcessAssetInfoForSMB can find it
            GeometryAssetIdToSmbPath[geometryAssetId] = smbFilePath;
            GeometryAssetIdToGeometryCount[geometryAssetId] = geometryCount;

            log.Info($"[SMB Mapping] Stored: Key='{geometryAssetId}' -> SMB='{smbFilePath}' (exists: {File.Exists(smbFilePath)})");

            return dummyStepPath;
        }

        /// <summary>
        /// Sets ExchangeIdentifier on ExchangeData if not already set
        /// </summary>
        internal static void SetExchangeIdentifierIfNeeded(object exchangeData, Type exchangeDataType, DataExchangeIdentifier identifier, DiagnosticsLogger log)
        {
            var exchangeIdentifierProp = exchangeDataType.GetProperty("ExchangeIdentifier", BindingFlags.Public | BindingFlags.Instance);
            if (exchangeIdentifierProp != null)
            {
                var currentIdentifier = exchangeIdentifierProp.GetValue(exchangeData);
                if (currentIdentifier == null)
                {
                    exchangeIdentifierProp.SetValue(exchangeData, identifier);
                }
            }
        }

        #endregion

        #region Geometry Loading

        /// <summary>
        /// Loads geometries from an SMB file
        /// </summary>
        internal static List<Geometry> LoadGeometriesFromSMBFile(string smbFilePath, string unit, DiagnosticsLogger log)
        {
            var geometries = new List<Geometry>();

            if (!File.Exists(smbFilePath))
            {
                throw new FileNotFoundException($"SMB file not found: {smbFilePath}");
            }

            double mmPerUnit = DataExchangeUtils.ConvertUnitToMmPerUnit(unit);

            var geometryType = typeof(Geometry);
            var importMethod = geometryType.GetMethod("ImportFromSMB",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(double) },
                null);

            if (importMethod != null)
            {
                var result = importMethod.Invoke(null, new object[] { smbFilePath, mmPerUnit }) as Geometry[];
                if (result != null && result.Length > 0)
                {
                    geometries.AddRange(result);
                }
            }

            return geometries;
        }

        #endregion

        #region Relationship Setup

        /// <summary>
        /// Sets up DesignAsset and relationships with GeometryAsset
        /// </summary>
        internal static void SetupDesignAssetAndRelationships(
            object element,
            object geometryAsset,
            Type geometryAssetType,
            object exchangeData,
            Type exchangeDataType,
            Dictionary<string, Type> foundTypes,
            string elementName,
            DiagnosticsLogger log)
        {
            var elementAssetProperty = element.GetType().GetProperty("Asset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (elementAssetProperty == null)
            {
                throw new InvalidOperationException("Could not find Asset property on Element");
            }

            var elementAsset = elementAssetProperty.GetValue(element);

            const string designAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset";
            if (!foundTypes.TryGetValue(designAssetTypeName, out var designAssetType))
            {
                designAssetType = ReflectionUtils.FindType(designAssetTypeName, exchangeDataType, foundTypes);
                if (designAssetType == null)
                {
                    throw new InvalidOperationException("Could not find DesignAsset type");
                }
            }

            // Get or create RootAsset
            var rootAssetProp = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
            if (rootAssetProp == null)
            {
                throw new InvalidOperationException("Could not find RootAsset property on ExchangeData");
            }

            object rootAsset = rootAssetProp.GetValue(exchangeData);
            if (rootAsset == null)
            {
                var rootAssetId = Guid.NewGuid().ToString();
                rootAsset = ReflectionUtils.CreateInstanceWithId(designAssetType, rootAssetId, foundTypes, log);

                var objectInfoProp = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProp != null)
                {
                    var objectInfo = CreateObjectInfo("TopLevelAssembly", foundTypes, exchangeDataType, log);
                    if (objectInfo != null)
                    {
                        objectInfoProp.SetValue(rootAsset, objectInfo);
                    }
                }

                var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                exchangeDataAddMethod?.Invoke(exchangeData, new object[] { rootAsset });

                if (rootAssetProp.CanWrite)
                {
                    rootAssetProp.SetValue(exchangeData, rootAsset);
                }
            }

            // Link Element's InstanceAsset to RootAsset if needed
            var elementAssetType = elementAsset.GetType();
            LinkElementToRootAsset(elementAsset, elementAssetType, rootAsset, foundTypes, exchangeDataType);

            // Find or create DesignAsset
            var designAsset = FindOrCreateDesignAsset(
                elementAsset, elementAssetType, designAssetType,
                exchangeData, exchangeDataType, foundTypes, elementName, log);

            // Add GeometryAsset to DesignAsset
            AddGeometryAssetToDesignAsset(designAsset, geometryAsset, foundTypes, exchangeDataType);
        }

        /// <summary>
        /// Sets up DesignAsset and relationships for multiple GeometryAssets
        /// </summary>
        internal static void SetupDesignAssetAndRelationshipsForMultipleGeometries(
            object element,
            List<(object geometryAsset, Type geometryAssetType, string geometryAssetId)> geometryAssets,
            object exchangeData,
            Type exchangeDataType,
            Dictionary<string, Type> foundTypes,
            string elementName,
            DiagnosticsLogger log)
        {
            if (geometryAssets == null || geometryAssets.Count == 0)
            {
                return;
            }

            const string designAssetTypeName = "Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset";
            if (!foundTypes.TryGetValue(designAssetTypeName, out var designAssetType))
            {
                designAssetType = ReflectionUtils.FindType(designAssetTypeName, exchangeDataType, foundTypes);
                if (designAssetType == null)
                {
                    throw new InvalidOperationException("Could not find DesignAsset type");
                }
            }

            var rootAssetProp = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
            if (rootAssetProp == null)
            {
                throw new InvalidOperationException("Could not find RootAsset property on ExchangeData");
            }

            object rootAsset = rootAssetProp.GetValue(exchangeData);
            if (rootAsset == null)
            {
                var rootAssetId = Guid.NewGuid().ToString();
                rootAsset = ReflectionUtils.CreateInstanceWithId(designAssetType, rootAssetId, foundTypes, log);

                var objectInfoProp = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProp != null)
                {
                    var objectInfo = CreateObjectInfo("TopLevelAssembly", foundTypes, exchangeDataType, log);
                    if (objectInfo != null)
                    {
                        objectInfoProp.SetValue(rootAsset, objectInfo);
                    }
                }

                var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                exchangeDataAddMethod?.Invoke(exchangeData, new object[] { rootAsset });

                if (rootAssetProp.CanWrite)
                {
                    rootAssetProp.SetValue(exchangeData, rootAsset);
                }
            }

            var elementAssetProp = element.GetType().GetProperty("Asset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (elementAssetProp == null)
            {
                throw new InvalidOperationException("Could not find Asset property on Element");
            }

            var elementAsset = elementAssetProp.GetValue(element);
            if (elementAsset == null)
            {
                throw new InvalidOperationException("Element's Asset is null");
            }

            var elementAssetType = elementAsset.GetType();

            // Link Element to RootAsset
            LinkElementToRootAsset(elementAsset, elementAssetType, rootAsset, foundTypes, exchangeDataType);

            // Find or create DesignAsset
            var designAsset = FindOrCreateDesignAsset(
                elementAsset, elementAssetType, designAssetType,
                exchangeData, exchangeDataType, foundTypes, elementName, log);

            // Add ALL GeometryAssets to DesignAsset
            if (designAsset != null)
            {
                var addChildMethod = designAsset.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (addChildMethod != null)
                {
                    const string containmentRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship";
                    if (foundTypes.TryGetValue(containmentRelationshipTypeName, out var containmentRelationshipType) ||
                        (containmentRelationshipType = ReflectionUtils.FindType(containmentRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        foreach (var (geometryAsset, geometryAssetType, geometryAssetId) in geometryAssets)
                        {
                            var containmentRelationship = Activator.CreateInstance(containmentRelationshipType);
                            addChildMethod.Invoke(designAsset, new object[] { geometryAsset, containmentRelationship });
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("Could not find ContainmentRelationship type");
                    }
                }
            }
        }

        private static void LinkElementToRootAsset(object elementAsset, Type elementAssetType, object rootAsset, Dictionary<string, Type> foundTypes, Type exchangeDataType)
        {
            var rootAssetChildNodesProp = rootAsset.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            bool elementInstanceLinkedToRoot = false;
            if (rootAssetChildNodesProp != null)
            {
                var rootChildNodes = rootAssetChildNodesProp.GetValue(rootAsset) as System.Collections.IEnumerable;
                if (rootChildNodes != null)
                {
                    foreach (var childNodeRel in rootChildNodes)
                    {
                        var nodeProp = childNodeRel.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                        if (nodeProp != null)
                        {
                            var node = nodeProp.GetValue(childNodeRel);
                            if (node != null && node == elementAsset)
                            {
                                elementInstanceLinkedToRoot = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (!elementInstanceLinkedToRoot)
            {
                var rootAddChildMethod = rootAsset.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
                if (rootAddChildMethod != null)
                {
                    const string containmentRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship";
                    if (foundTypes.TryGetValue(containmentRelationshipTypeName, out var containmentRelationshipType) ||
                        (containmentRelationshipType = ReflectionUtils.FindType(containmentRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                    {
                        var containmentRelationship = Activator.CreateInstance(containmentRelationshipType);
                        rootAddChildMethod.Invoke(rootAsset, new object[] { elementAsset, containmentRelationship });
                    }
                }
            }
        }

        private static object FindOrCreateDesignAsset(
            object elementAsset, Type elementAssetType, Type designAssetType,
            object exchangeData, Type exchangeDataType,
            Dictionary<string, Type> foundTypes, string elementName, DiagnosticsLogger log)
        {
            object designAsset = null;

            // Check if Element's InstanceAsset already has a DesignAsset
            var elementAssetChildNodesProp = elementAssetType.GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            if (elementAssetChildNodesProp != null)
            {
                var elementChildNodes = elementAssetChildNodesProp.GetValue(elementAsset) as System.Collections.IEnumerable;
                if (elementChildNodes != null)
                {
                    foreach (var childNodeRel in elementChildNodes)
                    {
                        var nodeProp = childNodeRel.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                        if (nodeProp != null)
                        {
                            var node = nodeProp.GetValue(childNodeRel);
                            if (node != null && designAssetType.IsAssignableFrom(node.GetType()))
                            {
                                var relationshipProp = childNodeRel.GetType().GetProperty("Relationship", BindingFlags.Public | BindingFlags.Instance);
                                if (relationshipProp != null)
                                {
                                    var relationship = relationshipProp.GetValue(childNodeRel);
                                    if (relationship != null)
                                    {
                                        var modelStructureProp = relationship.GetType().GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
                                        var modelStructure = modelStructureProp?.GetValue(relationship);
                                        if (modelStructure != null)
                                        {
                                            designAsset = node;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Create DesignAsset if not found
            if (designAsset == null)
            {
                var designAssetId = Guid.NewGuid().ToString();
                designAsset = ReflectionUtils.CreateInstanceWithId(designAssetType, designAssetId, foundTypes, log);

                var objectInfoProp = designAssetType.GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                if (objectInfoProp != null)
                {
                    var objectInfo = CreateObjectInfo(elementName, foundTypes, exchangeDataType, log);
                    if (objectInfo != null)
                    {
                        objectInfoProp.SetValue(designAsset, objectInfo);
                    }
                }

                var exchangeDataAddMethod = exchangeDataType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                exchangeDataAddMethod?.Invoke(exchangeData, new object[] { designAsset });

                // Remove problematic ReferenceRelationships and add proper one with ModelStructure
                RemoveProblematicReferenceRelationships(elementAsset, elementAssetType, foundTypes, exchangeDataType);
                LinkDesignAssetToElement(elementAsset, elementAssetType, designAsset, foundTypes, exchangeDataType);
            }

            return designAsset;
        }

        private static void RemoveProblematicReferenceRelationships(object elementAsset, Type elementAssetType, Dictionary<string, Type> foundTypes, Type exchangeDataType)
        {
            var elementAssetChildNodesProp = elementAssetType.GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            if (elementAssetChildNodesProp == null) return;

            var elementChildNodes = elementAssetChildNodesProp.GetValue(elementAsset) as System.Collections.IEnumerable;
            if (elementChildNodes == null) return;

            var childNodesList = elementChildNodes.Cast<object>().ToList();
            const string referenceRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship";
            if (!foundTypes.TryGetValue(referenceRelationshipTypeName, out var referenceRelationshipType))
            {
                referenceRelationshipType = ReflectionUtils.FindType(referenceRelationshipTypeName, exchangeDataType, foundTypes);
            }

            if (referenceRelationshipType == null) return;

            var removeChildMethod = elementAssetType.GetMethod("RemoveChild", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (removeChildMethod == null) return;

            var indicesToRemove = new List<int>();
            for (int i = 0; i < childNodesList.Count; i++)
            {
                var childNodeRel = childNodesList[i];
                var relationshipProp = childNodeRel.GetType().GetProperty("Relationship", BindingFlags.Public | BindingFlags.Instance);
                if (relationshipProp != null)
                {
                    var relationship = relationshipProp.GetValue(childNodeRel);
                    if (relationship != null && referenceRelationshipType.IsAssignableFrom(relationship.GetType()))
                    {
                        var typeProperty = relationship.GetType().GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
                        var modelStructureProperty = relationship.GetType().GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);

                        bool hasType = typeProperty != null && typeProperty.GetValue(relationship) != null;
                        var modelStructure = modelStructureProperty?.GetValue(relationship);
                        bool hasModelStructure = modelStructure != null;

                        if (hasType && !hasModelStructure)
                        {
                            indicesToRemove.Add(i);
                        }
                    }
                }
            }

            for (int i = indicesToRemove.Count - 1; i >= 0; i--)
            {
                removeChildMethod.Invoke(elementAsset, new object[] { indicesToRemove[i] });
            }
        }

        private static void LinkDesignAssetToElement(object elementAsset, Type elementAssetType, object designAsset, Dictionary<string, Type> foundTypes, Type exchangeDataType)
        {
            var elementAddChildMethod = elementAssetType.GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
            if (elementAddChildMethod == null) return;

            const string referenceRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ReferenceRelationship";
            if (!foundTypes.TryGetValue(referenceRelationshipTypeName, out var referenceRelationshipType))
            {
                referenceRelationshipType = ReflectionUtils.FindType(referenceRelationshipTypeName, exchangeDataType, foundTypes);
            }

            if (referenceRelationshipType == null) return;

            var referenceRelationship = Activator.CreateInstance(referenceRelationshipType);
            var modelStructureProperty = referenceRelationshipType.GetProperty("ModelStructure", BindingFlags.Public | BindingFlags.Instance);
            if (modelStructureProperty != null)
            {
                const string modelStructureTypeName = "Autodesk.DataExchange.SchemaObjects.Components.ModelStructure";
                if (foundTypes.TryGetValue(modelStructureTypeName, out var modelStructureType) ||
                    (modelStructureType = ReflectionUtils.FindType(modelStructureTypeName, exchangeDataType, foundTypes)) != null)
                {
                    var modelStructure = Activator.CreateInstance(modelStructureType);
                    var valueProperty = modelStructureType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    valueProperty?.SetValue(modelStructure, true);
                    modelStructureProperty.SetValue(referenceRelationship, modelStructure);
                }
            }

            elementAddChildMethod.Invoke(elementAsset, new object[] { designAsset, referenceRelationship });
        }

        private static void AddGeometryAssetToDesignAsset(object designAsset, object geometryAsset, Dictionary<string, Type> foundTypes, Type exchangeDataType)
        {
            if (designAsset == null) return;

            var addChildMethod = designAsset.GetType().GetMethod("AddChild", BindingFlags.Public | BindingFlags.Instance);
            if (addChildMethod != null)
            {
                const string containmentRelationshipTypeName = "Autodesk.DataExchange.SchemaObjects.Relationships.ContainmentRelationship";
                if (foundTypes.TryGetValue(containmentRelationshipTypeName, out var containmentRelationshipType) ||
                    (containmentRelationshipType = ReflectionUtils.FindType(containmentRelationshipTypeName, exchangeDataType, foundTypes)) != null)
                {
                    var containmentRelationship = Activator.CreateInstance(containmentRelationshipType);
                    addChildMethod.Invoke(designAsset, new object[] { geometryAsset, containmentRelationship });
                }
                else
                {
                    throw new InvalidOperationException("Could not find ContainmentRelationship type");
                }
            }
        }

        #endregion

        #region Async Operations

        /// <summary>
        /// Full SyncExchangeDataAsync flow adapted for direct SMB uploads
        /// </summary>
        internal static async Task SyncExchangeDataForSMBAsync(
            object client, Type clientType,
            DataExchangeIdentifier identifier,
            object exchangeData, Type exchangeDataType,
            DiagnosticsLogger log)
        {
            string fulfillmentId = string.Empty;

            try
            {
                await ProcessRenderStylesFromFileGeometryAsync(client, clientType, exchangeData, exchangeDataType, log);

                fulfillmentId = await StartFulfillmentAsync(client, clientType, identifier, exchangeData, exchangeDataType, log);
                var api = GetAPI(client, clientType);
                var apiType = api?.GetType();

                var assetInfosList = await GetAssetInfosForSMBAsync(client, clientType, exchangeData, exchangeDataType, identifier, fulfillmentId, log);

                AddRenderStylesToAssetInfos(client, clientType, assetInfosList, exchangeData, exchangeDataType, log);

                // Backup SMB files before upload - SDK deletes them during UploadGeometries
                var smbBackups = BackupSmbFiles(log);

                await UploadGeometriesAsync(client, clientType, identifier, fulfillmentId, assetInfosList, exchangeData, exchangeDataType, log);

                // Restore SMB files after upload - needed for ClearLocalStates
                RestoreSmbFiles(smbBackups, log);

                await UploadCustomGeometriesAsync(client, clientType, identifier, fulfillmentId, exchangeData, exchangeDataType, log);
                await UploadLargePrimitiveGeometriesAsync(client, clientType, identifier, fulfillmentId, exchangeData, exchangeDataType, log);

                var fulfillmentSyncRequest = await GetFulfillmentSyncRequestAsync(client, clientType, identifier, exchangeData, exchangeDataType, log);
                var fulfillmentTasks = await BatchAndSendSyncRequestsAsync(client, clientType, identifier, fulfillmentId, fulfillmentSyncRequest, exchangeData, exchangeDataType, log);

                await WaitForAllTasksAsync(fulfillmentTasks, log);
                await FinishFulfillmentAsync(api, apiType, identifier, fulfillmentId, log);
                await PollForFulfillmentAsync(client, clientType, identifier, fulfillmentId, log);

                await GenerateViewableAsync(client, clientType, identifier, log);
                ClearLocalStatesAndSetRevision(client, clientType, exchangeData, exchangeDataType, log);
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(fulfillmentId))
                {
                    await DiscardFulfillmentAsync(client, clientType, identifier, fulfillmentId, log);
                }
                throw;
            }
        }

        // Placeholder methods for async operations - these would be fully implemented
        // by extracting the corresponding methods from ExportGeometryToSMB.cs
        // For brevity, showing the method signatures:

        private static async Task ProcessRenderStylesFromFileGeometryAsync(object client, Type clientType, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            // Implementation from ExportGeometryToSMB
            await Task.CompletedTask;
        }

        private static async Task<string> StartFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            // Create FulfillmentRequest object
            var fulfillmentRequestType = Type.GetType("Autodesk.DataExchange.OpenAPI.FulfillmentRequest, Autodesk.DataExchange.OpenAPI");
            if (fulfillmentRequestType == null)
            {
                throw new InvalidOperationException("Could not find FulfillmentRequest type");
            }

            var fulfillmentRequest = Activator.CreateInstance(fulfillmentRequestType);

            // Set ExecutionOrder to INSERT_FIRST
            var executionOrderProp = fulfillmentRequestType.GetProperty("ExecutionOrder", BindingFlags.Public | BindingFlags.Instance);
            if (executionOrderProp != null)
            {
                var insertFirstEnum = Type.GetType("Autodesk.DataExchange.OpenAPI.FulfillmentRequestExecutionOrder, Autodesk.DataExchange.OpenAPI");
                if (insertFirstEnum != null)
                {
                    var insertFirstValue = Enum.Parse(insertFirstEnum, "INSERT_FIRST");
                    executionOrderProp.SetValue(fulfillmentRequest, insertFirstValue);
                }
            }

            // Set description if available
            var descriptionProp = exchangeDataType.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (descriptionProp != null)
            {
                var description = descriptionProp.GetValue(exchangeData)?.ToString();
                if (!string.IsNullOrEmpty(description))
                {
                    var summaryProp = fulfillmentRequestType.GetProperty("Summary", BindingFlags.Public | BindingFlags.Instance);
                    summaryProp?.SetValue(fulfillmentRequest, description);
                }
            }

            // Get API and call StartFulfillmentAsync with 4 parameters
            var api = GetAPI(client, clientType);
            if (api == null)
            {
                throw new InvalidOperationException("Could not get API from client");
            }

            var apiType = api.GetType();
            var startFulfillmentMethod = ReflectionUtils.GetMethod(apiType, "StartFulfillmentAsync",
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(string), typeof(string), fulfillmentRequestType, typeof(CancellationToken) });

            if (startFulfillmentMethod == null)
            {
                throw new InvalidOperationException("Could not find StartFulfillmentAsync method");
            }

            var fulfillmentResponse = await ((dynamic)startFulfillmentMethod.Invoke(api, new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentRequest, CancellationToken.None }));

            // Extract fulfillment ID from response
            var fulfillmentResponseType = fulfillmentResponse.GetType();
            var idProp = fulfillmentResponseType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            if (idProp == null)
            {
                throw new InvalidOperationException("FulfillmentResponse does not have Id property");
            }

            var fulfillmentId = idProp.GetValue(fulfillmentResponse)?.ToString();
            if (string.IsNullOrEmpty(fulfillmentId))
            {
                throw new InvalidOperationException("FulfillmentResponse.Id is null or empty");
            }

            return fulfillmentId;
        }

        internal static object GetAPI(object client, Type clientType)
        {
            // The SDK has a GetAPI() METHOD (not property!) on the Client class
            var getAPIMethod = clientType.GetMethod("GetAPI", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (getAPIMethod != null)
            {
                return getAPIMethod.Invoke(client, null);
            }

            // Fallback: try as a property (in case SDK changed)
            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var apiProperty = clientType.GetProperty("API", bindingFlags);
            if (apiProperty != null)
            {
                return apiProperty.GetValue(client);
            }

            return null;
        }

        private static async Task<List<object>> GetAssetInfosForSMBAsync(object client, Type clientType, object exchangeData, Type exchangeDataType, DataExchangeIdentifier identifier, string fulfillmentId, DiagnosticsLogger log)
        {
            var allAssetInfos = GetAllAssetInfosWithTranslatedGeometryPathForSMB(
                client, clientType, exchangeData, exchangeDataType, identifier, fulfillmentId,
                GeometryAssetIdToSmbPath, GeometryAssetIdToGeometryCount, log);
            return await Task.FromResult(allAssetInfos.ToList());
        }

        private static IEnumerable<object> GetAllAssetInfosWithTranslatedGeometryPathForSMB(
            object client, Type clientType, object exchangeData, Type exchangeDataType,
            DataExchangeIdentifier identifier, string fulfillmentId,
            Dictionary<string, string> geometryAssetIdToSmbPath,
            Dictionary<string, int> geometryAssetIdToGeometryCount,
            DiagnosticsLogger log)
        {
            log.Info($"[GetAssetInfos] SMB mapping has {geometryAssetIdToSmbPath.Count} entries");

            var getBatchedAssetInfosMethod = ReflectionUtils.GetMethod(
                clientType,
                "GetBatchedAssetInfos",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { exchangeDataType });

            if (getBatchedAssetInfosMethod == null)
            {
                throw new InvalidOperationException("Could not find GetBatchedAssetInfos method on Client");
            }

            var batchedAssetInfos = getBatchedAssetInfosMethod.Invoke(client, new object[] { exchangeData }) as System.Collections.IEnumerable;
            if (batchedAssetInfos == null)
            {
                log.Info("[GetAssetInfos] GetBatchedAssetInfos returned null");
                return Enumerable.Empty<object>();
            }

            var allAssetInfos = new List<object>();
            int batchCount = 0;

            foreach (var assetInfosBatch in batchedAssetInfos)
            {
                batchCount++;
                var assetInfosList = assetInfosBatch as System.Collections.IEnumerable;
                if (assetInfosList != null)
                {
                    int itemCount = 0;
                    foreach (var assetInfo in assetInfosList)
                    {
                        itemCount++;
                        ProcessAssetInfoForSMB(assetInfo, geometryAssetIdToSmbPath, geometryAssetIdToGeometryCount, log);
                        allAssetInfos.Add(assetInfo);
                    }
                    log.Info($"[GetAssetInfos] Batch {batchCount}: {itemCount} asset infos");
                }
            }

            log.Info($"[GetAssetInfos] Total: {allAssetInfos.Count} asset infos from {batchCount} batches");
            return allAssetInfos;
        }

        private static void ProcessAssetInfoForSMB(object assetInfo, Dictionary<string, string> geometryAssetIdToSmbPath, Dictionary<string, int> geometryAssetIdToGeometryCount, DiagnosticsLogger log)
        {
            var pathProp = assetInfo.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
            var outputPathProp = assetInfo.GetType().GetProperty("OutputPath", BindingFlags.Public | BindingFlags.Instance);
            var idProp = assetInfo.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

            if (pathProp == null || outputPathProp == null || idProp == null)
            {
                log.Info($"[ProcessAssetInfo] Missing property: Path={pathProp != null}, OutputPath={outputPathProp != null}, Id={idProp != null}");
                return;
            }

            var path = pathProp.GetValue(assetInfo)?.ToString();
            var assetInfoId = idProp.GetValue(assetInfo)?.ToString();

            log.Info($"[ProcessAssetInfo] AssetInfo ID='{assetInfoId}', Path='{path}'");
            log.Info($"[ProcessAssetInfo] Available mapping keys: [{string.Join(", ", geometryAssetIdToSmbPath.Keys)}]");

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(assetInfoId))
            {
                log.Info($"[ProcessAssetInfo] Skipping - empty path or ID");
                return;
            }
            if (!File.Exists(path))
            {
                log.Info($"[ProcessAssetInfo] Skipping - path file doesn't exist: {path}");
                return;
            }

            if (geometryAssetIdToSmbPath.TryGetValue(assetInfoId, out var realSmbPath))
            {
                log.Info($"[ProcessAssetInfo] FOUND mapping: '{assetInfoId}' -> '{realSmbPath}' (exists: {File.Exists(realSmbPath)})");
                if (File.Exists(realSmbPath))
                {
                    outputPathProp.SetValue(assetInfo, realSmbPath);
                    log.Info($"[ProcessAssetInfo] Set OutputPath to SMB file");
                }
            }
            else
            {
                log.Info($"[ProcessAssetInfo] NO mapping found for key '{assetInfoId}'");
            }

            SetupBodyInfoList(assetInfo, assetInfoId, geometryAssetIdToGeometryCount);
            SetupLengthUnits(assetInfo);
        }

        private static void SetupBodyInfoList(object assetInfo, string assetInfoId, Dictionary<string, int> geometryAssetIdToGeometryCount)
        {
            var bodyInfoListProp = assetInfo.GetType().GetProperty("BodyInfoList", BindingFlags.Public | BindingFlags.Instance);
            if (bodyInfoListProp == null) return;

            var existingBodyInfoList = bodyInfoListProp.GetValue(assetInfo);
            if (existingBodyInfoList != null) return;

            try
            {
                var bodyInfoType = Type.GetType("Autodesk.GeometryUtilities.SDK.BodyInfo, Autodesk.GeometryUtilities");
                var bodyTypeEnum = Type.GetType("Autodesk.GeometryUtilities.SDK.BodyType, Autodesk.GeometryUtilities");

                if (bodyInfoType == null || bodyTypeEnum == null) return;

                int geometryCount = 1;
                if (geometryAssetIdToGeometryCount.TryGetValue(assetInfoId, out var count))
                {
                    geometryCount = count;
                }

                var bodyInfoListType = typeof(List<>).MakeGenericType(bodyInfoType);
                var bodyInfoList = Activator.CreateInstance(bodyInfoListType);
                var addMethod = bodyInfoListType.GetMethod("Add");

                if (addMethod == null) return;

                var brepValue = Enum.GetValues(bodyTypeEnum).Cast<object>()
                    .FirstOrDefault(v => v.ToString().Contains("BREP") || v.ToString().Contains("BRep") || v.ToString().Contains("Solid"));

                if (brepValue == null)
                {
                    var enumValues = Enum.GetValues(bodyTypeEnum);
                    brepValue = enumValues.GetValue(enumValues.Length > 1 ? 1 : 0);
                }

                var bodyIdProp = bodyInfoType.GetProperty("BodyId", BindingFlags.Public | BindingFlags.Instance);

                for (int i = 0; i < geometryCount; i++)
                {
                    var bodyInfo = Activator.CreateInstance(bodyInfoType);

                    var typeProp = bodyInfoType.GetProperty("Type");
                    typeProp?.SetValue(bodyInfo, brepValue);

                    bodyIdProp?.SetValue(bodyInfo, $"geometry_{i}");

                    addMethod.Invoke(bodyInfoList, new object[] { bodyInfo });
                }

                bodyInfoListProp.SetValue(assetInfo, bodyInfoList);
            }
            catch
            {
                // Ignore errors in BodyInfo setup
            }
        }

        private static void SetupLengthUnits(object assetInfo)
        {
            var lengthUnitsProp = assetInfo.GetType().GetProperty("LengthUnits", BindingFlags.Public | BindingFlags.Instance);
            if (lengthUnitsProp == null) return;

            var existingLengthUnits = lengthUnitsProp.GetValue(assetInfo);
            if (existingLengthUnits != null) return;

            try
            {
                var unitEnumType = Type.GetType("Autodesk.DataExchange.SchemaObjects.Units.LengthUnit, Autodesk.DataExchange.SchemaObjects");
                if (unitEnumType != null)
                {
                    var centimeterValue = Enum.Parse(unitEnumType, "CentiMeter");
                    lengthUnitsProp.SetValue(assetInfo, centimeterValue);
                }
            }
            catch
            {
                // Ignore errors in unit setup
            }
        }

        private static void AddRenderStylesToAssetInfos(object client, Type clientType, List<object> assetInfosList, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            // Implementation placeholder
        }

        private static async Task UploadGeometriesAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, List<object> assetInfosList, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            if (assetInfosList == null || assetInfosList.Count == 0)
            {
                log.Info("No geometries to upload");
                return;
            }

            // Find the UploadGeometries method on Client (internal method)
            var uploadGeometriesMethod = clientType.GetMethod("UploadGeometries",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (uploadGeometriesMethod == null)
            {
                // Try alternative method names
                uploadGeometriesMethod = clientType.GetMethod("UploadGeometriesAsync",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (uploadGeometriesMethod == null)
            {
                log.Error("Could not find UploadGeometries method on Client - geometry upload will be skipped");
                return;
            }

            // Log method signature
            var parameters = uploadGeometriesMethod.GetParameters();
            log.Info($"[UploadGeometries] Found method with {parameters.Length} parameters:");
            for (int i = 0; i < parameters.Length; i++)
            {
                log.Info($"  [{i}] {parameters[i].Name}: {parameters[i].ParameterType.Name}");
            }

            try
            {
                // Get the AssetInfo list type for proper method invocation
                var assetInfoType = assetInfosList[0].GetType();
                var listType = typeof(List<>).MakeGenericType(assetInfoType);
                var typedList = Activator.CreateInstance(listType);
                var addMethod = listType.GetMethod("Add");
                foreach (var item in assetInfosList)
                {
                    addMethod.Invoke(typedList, new[] { item });
                }

                // Log what we're passing
                log.Info($"[UploadGeometries] Calling with: identifier={identifier.ExchangeId}, fulfillmentId={fulfillmentId}, assetInfos count={assetInfosList.Count}");

                // Verify OutputPath is set on each AssetInfo
                foreach (var ai in assetInfosList)
                {
                    var outPathProp = ai.GetType().GetProperty("OutputPath", BindingFlags.Public | BindingFlags.Instance);
                    var pathProp = ai.GetType().GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
                    var outPath = outPathProp?.GetValue(ai)?.ToString();
                    var path = pathProp?.GetValue(ai)?.ToString();
                    log.Info($"[UploadGeometries] AssetInfo - Path='{path}', OutputPath='{outPath}', OutputPath exists={!string.IsNullOrEmpty(outPath) && File.Exists(outPath)}");
                }

                // Invoke UploadGeometries
                object result;

                if (parameters.Length == 5)
                {
                    // UploadGeometries(identifier, fulfillmentId, assetInfos, exchangeData, cancellationToken)
                    result = uploadGeometriesMethod.Invoke(client, new object[] { identifier, fulfillmentId, typedList, exchangeData, CancellationToken.None });
                }
                else if (parameters.Length == 4)
                {
                    // UploadGeometries(identifier, fulfillmentId, assetInfos, exchangeData)
                    result = uploadGeometriesMethod.Invoke(client, new object[] { identifier, fulfillmentId, typedList, exchangeData });
                }
                else if (parameters.Length == 3)
                {
                    // UploadGeometries(identifier, fulfillmentId, assetInfos)
                    result = uploadGeometriesMethod.Invoke(client, new object[] { identifier, fulfillmentId, typedList });
                }
                else if (parameters.Length == 2)
                {
                    // UploadGeometries(fulfillmentId, assetInfos)
                    result = uploadGeometriesMethod.Invoke(client, new object[] { fulfillmentId, typedList });
                }
                else
                {
                    log.Error($"UploadGeometries method has unexpected parameter count: {parameters.Length}");
                    return;
                }

                log.Info($"[UploadGeometries] Method invoked, result type: {result?.GetType().Name ?? "null"}");

                // Handle async result if needed
                if (result is Task task)
                {
                    log.Info($"[UploadGeometries] Awaiting async task...");
                    await task.ConfigureAwait(false);
                    log.Info($"[UploadGeometries] Async task completed");
                }

                log.Info($"[UploadGeometries] Uploaded {assetInfosList.Count} geometry asset(s)");
            }
            catch (TargetInvocationException tie)
            {
                var innerEx = tie.InnerException ?? tie;
                log.Error($"UploadGeometries failed: {innerEx.Message}");
                throw innerEx;
            }
            catch (Exception ex)
            {
                log.Error($"UploadGeometries failed: {ex.Message}");
                throw;
            }
        }

        private static async Task UploadCustomGeometriesAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            await Task.CompletedTask;
        }

        private static async Task UploadLargePrimitiveGeometriesAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            await Task.CompletedTask;
        }

        private static async Task<object> GetFulfillmentSyncRequestAsync(object client, Type clientType, DataExchangeIdentifier identifier, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            log.Info("Getting FulfillmentSyncRequest...");

            // Step 1: Get exchange details to get schema namespace
            var getExchangeDetailsMethod = ReflectionUtils.GetMethod(
                clientType,
                "GetExchangeDetailsAsync",
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(DataExchangeIdentifier) });

            if (getExchangeDetailsMethod == null)
            {
                log.Error("Could not find GetExchangeDetailsAsync method");
                return null;
            }

            var exchangeDetailsTask = ReflectionUtils.InvokeMethod(client, getExchangeDetailsMethod, new object[] { identifier }, log);
            if (exchangeDetailsTask == null)
            {
                log.Error("GetExchangeDetailsAsync returned null");
                return null;
            }

            var exchangeDetails = await ((dynamic)exchangeDetailsTask).ConfigureAwait(false);
            var exchangeDetailsType = exchangeDetails.GetType();
            var valueProp = exchangeDetailsType.GetProperty("Value");
            object exchangeDetailsValue = null;
            if (valueProp != null)
            {
                exchangeDetailsValue = valueProp.GetValue(exchangeDetails);
            }

            // Step 2: Get FulfillmentSyncRequestHandler via GetService
            var getServiceMethod = clientType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (getServiceMethod == null)
            {
                log.Error("Could not find GetService method");
                return null;
            }

            var fulfillmentSyncRequestHandlerType = Type.GetType("Autodesk.DataExchange.ClientServices.FulfillmentSyncRequestHandler, Autodesk.DataExchange");
            if (fulfillmentSyncRequestHandlerType == null)
            {
                log.Error("Could not find FulfillmentSyncRequestHandler type");
                return null;
            }

            var getServiceGeneric = getServiceMethod.MakeGenericMethod(fulfillmentSyncRequestHandlerType);
            var fulfillmentSyncRequestHandler = getServiceGeneric.Invoke(client, null);
            if (fulfillmentSyncRequestHandler == null)
            {
                log.Error("GetService returned null for FulfillmentSyncRequestHandler");
                return null;
            }

            // Step 3: Get schema namespace from exchangeDetails
            string schemaNamespace = null;
            if (exchangeDetailsValue != null)
            {
                var schemaNamespaceProp = exchangeDetailsValue.GetType().GetProperty("SchemaNamespace", BindingFlags.Public | BindingFlags.Instance);
                if (schemaNamespaceProp != null)
                {
                    schemaNamespace = schemaNamespaceProp.GetValue(exchangeDetailsValue)?.ToString();
                }
            }

            // Step 4: Call GetFulfillmentSyncRequest - find method with correct signature
            var getFulfillmentSyncRequestMethod = ReflectionUtils.GetMethod(
                fulfillmentSyncRequestHandlerType,
                "GetFulfillmentSyncRequest",
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(string), exchangeDataType, typeof(CancellationToken) });

            if (getFulfillmentSyncRequestMethod == null)
            {
                // Try without CancellationToken
                getFulfillmentSyncRequestMethod = ReflectionUtils.GetMethod(
                    fulfillmentSyncRequestHandlerType,
                    "GetFulfillmentSyncRequest",
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(string), exchangeDataType });
            }

            if (getFulfillmentSyncRequestMethod == null)
            {
                log.Error("Could not find GetFulfillmentSyncRequest method with expected signature");
                return null;
            }

            // Check actual parameter count and call accordingly
            var methodParams = getFulfillmentSyncRequestMethod.GetParameters();
            object[] invokeParams;
            if (methodParams.Length == 3)
            {
                invokeParams = new object[] { schemaNamespace, exchangeData, CancellationToken.None };
            }
            else if (methodParams.Length == 2)
            {
                invokeParams = new object[] { schemaNamespace, exchangeData };
            }
            else
            {
                log.Error($"GetFulfillmentSyncRequest has unexpected parameter count: {methodParams.Length}");
                return null;
            }

            var fulfillmentSyncRequestTask = ReflectionUtils.InvokeMethod(fulfillmentSyncRequestHandler, getFulfillmentSyncRequestMethod, invokeParams, log);
            if (fulfillmentSyncRequestTask == null)
            {
                log.Error("GetFulfillmentSyncRequest returned null");
                return null;
            }

            var fulfillmentSyncRequest = await ((dynamic)fulfillmentSyncRequestTask).ConfigureAwait(false);
            log.Info("Got FulfillmentSyncRequest");
            return fulfillmentSyncRequest;
        }

        private static async Task<List<Task>> BatchAndSendSyncRequestsAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, object fulfillmentSyncRequest, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            log.Info("Batching and sending sync requests...");
            var fulfillmentTasks = new List<Task>();

            if (fulfillmentSyncRequest == null)
            {
                log.Info("No fulfillment sync request to process");
                return fulfillmentTasks;
            }

            try
            {
                var fulfillmentSyncRequestType = fulfillmentSyncRequest.GetType();

                // Get batched requests using GetBatchedFulfillmentSyncRequests
                var getBatchedFulfillmentSyncRequestsMethod = ReflectionUtils.GetMethod(
                    clientType,
                    "GetBatchedFulfillmentSyncRequests",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    new Type[] { fulfillmentSyncRequestType });

                if (getBatchedFulfillmentSyncRequestsMethod == null)
                {
                    log.Error("Could not find GetBatchedFulfillmentSyncRequests method");
                    return fulfillmentTasks;
                }

                var batchedRequests = getBatchedFulfillmentSyncRequestsMethod.Invoke(client, new object[] { fulfillmentSyncRequest }) as System.Collections.IEnumerable;
                if (batchedRequests == null)
                {
                    log.Error("GetBatchedFulfillmentSyncRequests returned null");
                    return fulfillmentTasks;
                }

                // Get MakeSyncRequestWithRetries method
                var makeSyncRequestMethod = ReflectionUtils.GetMethod(
                    clientType,
                    "MakeSyncRequestWithRetries",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    new Type[] { typeof(DataExchangeIdentifier), typeof(string), fulfillmentSyncRequestType, typeof(CancellationToken) });

                if (makeSyncRequestMethod == null)
                {
                    log.Error("Could not find MakeSyncRequestWithRetries method");
                    return fulfillmentTasks;
                }

                // Send each batched request
                foreach (var individualRequest in batchedRequests)
                {
                    var syncTask = ReflectionUtils.InvokeMethod(client, makeSyncRequestMethod, new object[] { identifier, fulfillmentId, individualRequest, CancellationToken.None }, log);
                    if (syncTask != null && syncTask is Task task)
                    {
                        fulfillmentTasks.Add(task);
                    }
                }

                // Add ProcessGeometry task
                log.Info("Processing geometry...");
                var processGeometryMethod = ReflectionUtils.GetMethod(
                    clientType,
                    "ProcessGeometry",
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    new[] { exchangeDataType, typeof(DataExchangeIdentifier), typeof(string), typeof(CancellationToken) });

                if (processGeometryMethod != null)
                {
                    var processGeometryTask = ReflectionUtils.InvokeMethod(client, processGeometryMethod, new object[] { exchangeData, identifier, fulfillmentId, CancellationToken.None }, log);
                    if (processGeometryTask != null && processGeometryTask is Task pgTask)
                    {
                        fulfillmentTasks.Add(pgTask);
                        log.Info("Added ProcessGeometry task");
                    }
                }
                else
                {
                    log.Info("ProcessGeometry not found, skipping");
                }

                log.Info($"Created {fulfillmentTasks.Count} sync request task(s)");
            }
            catch (TargetInvocationException tie)
            {
                var innerEx = tie.InnerException ?? tie;
                log.Error($"BatchAndSendSyncRequests failed: {innerEx.Message}");
            }
            catch (Exception ex)
            {
                log.Error($"BatchAndSendSyncRequests failed: {ex.Message}");
            }

            return fulfillmentTasks;
        }

        private static async Task WaitForAllTasksAsync(List<Task> tasks, DiagnosticsLogger log)
        {
            if (tasks != null && tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
            }
        }

        private static async Task FinishFulfillmentAsync(object api, Type apiType, DataExchangeIdentifier identifier, string fulfillmentId, DiagnosticsLogger log)
        {
            if (api == null || apiType == null) return;

            var finishMethod = ReflectionUtils.GetMethod(apiType, "FinishFulfillmentAsync",
                BindingFlags.Public | BindingFlags.Instance,
                new[] { typeof(string), typeof(string), typeof(string), typeof(CancellationToken) });

            if (finishMethod != null)
            {
                var task = finishMethod.Invoke(api, new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentId, CancellationToken.None });
                if (task != null)
                {
                    await ((dynamic)task).ConfigureAwait(false);
                }
            }
        }

        private static async Task PollForFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, DiagnosticsLogger log)
        {
            if (string.IsNullOrEmpty(fulfillmentId))
            {
                log.Info("No fulfillment ID to poll");
                return;
            }

            try
            {
                // Try to find PollForFulfillment method
                var pollMethod = clientType.GetMethod("PollForFulfillment",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (pollMethod == null)
                {
                    pollMethod = clientType.GetMethod("PollForFulfillmentAsync",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (pollMethod == null)
                {
                    // Try public methods
                    pollMethod = clientType.GetMethod("WaitForFulfillmentAsync",
                        BindingFlags.Public | BindingFlags.Instance);
                }

                if (pollMethod != null)
                {
                    var parameters = pollMethod.GetParameters();
                    object result;

                    if (parameters.Length == 3)
                    {
                        result = pollMethod.Invoke(client, new object[] { identifier, fulfillmentId, CancellationToken.None });
                    }
                    else if (parameters.Length == 2)
                    {
                        result = pollMethod.Invoke(client, new object[] { identifier, fulfillmentId });
                    }
                    else if (parameters.Length == 1)
                    {
                        result = pollMethod.Invoke(client, new object[] { fulfillmentId });
                    }
                    else
                    {
                        log.Info($"PollForFulfillment has unexpected parameter count: {parameters.Length}");
                        return;
                    }

                    // Handle async result
                    if (result is Task task)
                    {
                        await task.ConfigureAwait(false);
                    }

                    log.Info("Fulfillment polling completed");
                }
                else
                {
                    // Fallback: use API polling directly
                    var api = GetAPI(client, clientType);
                    if (api != null)
                    {
                        var apiType = api.GetType();
                        var getFulfillmentMethod = ReflectionUtils.GetMethod(apiType, "GetFulfillmentAsync",
                            BindingFlags.Public | BindingFlags.Instance,
                            new[] { typeof(string), typeof(string), typeof(string), typeof(CancellationToken) });

                        if (getFulfillmentMethod != null)
                        {
                            // Poll with timeout
                            var maxAttempts = 60; // 60 attempts x 2 seconds = 2 minutes max
                            var delayMs = 2000;

                            for (int attempt = 0; attempt < maxAttempts; attempt++)
                            {
                                var fulfillmentResponse = await ((dynamic)getFulfillmentMethod.Invoke(api,
                                    new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentId, CancellationToken.None }));

                                if (fulfillmentResponse != null)
                                {
                                    var statusProp = fulfillmentResponse.GetType().GetProperty("Status");
                                    if (statusProp != null)
                                    {
                                        var status = statusProp.GetValue(fulfillmentResponse)?.ToString();
                                        if (status == "COMPLETED" || status == "SUCCESS" || status == "Completed")
                                        {
                                            log.Info("Fulfillment completed successfully");
                                            return;
                                        }
                                        if (status == "FAILED" || status == "ERROR" || status == "Failed")
                                        {
                                            log.Error("Fulfillment failed");
                                            return;
                                        }
                                    }
                                }

                                await Task.Delay(delayMs);
                            }

                            log.Info("Fulfillment polling timed out - continuing anyway");
                        }
                    }
                    else
                    {
                        log.Info("PollForFulfillment method not found - skipping polling");
                    }
                }
            }
            catch (TargetInvocationException tie)
            {
                var innerEx = tie.InnerException ?? tie;
                log.Error($"PollForFulfillment failed: {innerEx.Message}");
            }
            catch (Exception ex)
            {
                log.Error($"PollForFulfillment failed: {ex.Message}");
            }
        }

        private static async Task GenerateViewableAsync(object client, Type clientType, DataExchangeIdentifier identifier, DiagnosticsLogger log)
        {
            log.Info("Generating viewable from exchange geometry...");
            try
            {
                var generateViewableMethod = ReflectionUtils.GetMethod(
                    clientType,
                    "GenerateViewableAsync",
                    BindingFlags.Public | BindingFlags.Instance,
                    new[] { typeof(string), typeof(string) });

                if (generateViewableMethod != null)
                {
                    log.Info($"Calling GenerateViewableAsync with ExchangeId: {identifier.ExchangeId}, CollectionId: {identifier.CollectionId}");

                    var generateTask = ReflectionUtils.InvokeMethod(client, generateViewableMethod, new object[] { identifier.ExchangeId, identifier.CollectionId }, log);
                    if (generateTask != null)
                    {
                        var response = await ((dynamic)generateTask).ConfigureAwait(false);

                        // Inspect the response (should be IResponse<bool>)
                        if (response != null)
                        {
                            var responseType = response.GetType();

                            // Check for IsSuccess property
                            var isSuccessProp = responseType.GetProperty("IsSuccess") ?? responseType.GetProperty("Success");
                            if (isSuccessProp != null)
                            {
                                var isSuccess = (bool)isSuccessProp.GetValue(response);
                                if (isSuccess)
                                {
                                    log.Info("Viewable generation request submitted successfully");
                                    log.Info("Note: Viewable processing is asynchronous and may take 10-30 seconds");
                                }
                                else
                                {
                                    var errorProp = responseType.GetProperty("Error");
                                    if (errorProp != null)
                                    {
                                        var error = errorProp.GetValue(response);
                                        log.Info($"Viewable generation failed: {error}");
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    log.Info("GenerateViewableAsync method not found, geometry may not appear in viewer");
                }
            }
            catch (Exception ex)
            {
                log.Info($"Exception during viewable generation: {ex.Message}");
                // Don't throw - viewable generation failure shouldn't break the upload
            }
        }

        private static void ClearLocalStatesAndSetRevision(object client, Type clientType, object exchangeData, Type exchangeDataType, DiagnosticsLogger log)
        {
            log.Info("Clearing local states and setting revision...");

            try
            {
                // Get fulfillmentStatus to get revision ID
                var fulfillmentStatusField = clientType.GetField("fulfillmentStatus", BindingFlags.NonPublic | BindingFlags.Instance);
                string revisionId = null;
                if (fulfillmentStatusField != null)
                {
                    var fulfillmentStatus = fulfillmentStatusField.GetValue(client);
                    if (fulfillmentStatus != null)
                    {
                        var revisionIdProp = fulfillmentStatus.GetType().GetProperty("RevisionId", BindingFlags.Public | BindingFlags.Instance);
                        if (revisionIdProp != null)
                        {
                            revisionId = revisionIdProp.GetValue(fulfillmentStatus)?.ToString();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(revisionId))
                {
                    // ClearLocalStates on exchangeData with revisionId
                    var clearLocalStatesMethod = exchangeDataType.GetMethod("ClearLocalStates", BindingFlags.Public | BindingFlags.Instance);
                    if (clearLocalStatesMethod != null)
                    {
                        clearLocalStatesMethod.Invoke(exchangeData, new object[] { revisionId });
                        log.Info($"Cleared local states with revision: {revisionId}");
                    }

                    // SetRevision on RootAsset
                    var rootAssetProp = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
                    if (rootAssetProp != null)
                    {
                        var rootAsset = rootAssetProp.GetValue(exchangeData);
                        if (rootAsset != null)
                        {
                            var setRevisionMethod = rootAsset.GetType().GetMethod("SetRevision", BindingFlags.Public | BindingFlags.Instance);
                            if (setRevisionMethod != null)
                            {
                                setRevisionMethod.Invoke(rootAsset, new object[] { revisionId });
                                log.Info($"Set revision on RootAsset: {revisionId}");
                            }
                        }
                    }
                }
                else
                {
                    log.Info("Could not get revision ID, skipping ClearLocalStates and SetRevision");
                }

                // Clear our static mappings
                ClearMappings();
            }
            catch (Exception ex)
            {
                log.Error($"ClearLocalStatesAndSetRevision failed: {ex.Message}");
            }
        }

        private static async Task DiscardFulfillmentAsync(object client, Type clientType, DataExchangeIdentifier identifier, string fulfillmentId, DiagnosticsLogger log)
        {
            try
            {
                var api = GetAPI(client, clientType);
                if (api != null)
                {
                    var apiType = api.GetType();
                    var discardMethod = ReflectionUtils.GetMethod(apiType, "DiscardFulfillmentAsync",
                        BindingFlags.Public | BindingFlags.Instance,
                        new[] { typeof(string), typeof(string), typeof(string), typeof(CancellationToken) });

                    if (discardMethod != null)
                    {
                        var discardTask = discardMethod.Invoke(api, new object[] { identifier.CollectionId, identifier.ExchangeId, fulfillmentId, CancellationToken.None });
                        if (discardTask != null)
                        {
                            await ((dynamic)discardTask).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch
            {
                // Ignore discard errors
            }
        }

        #endregion

        #region Mappings Management

        /// <summary>
        /// Clears the SMB path mappings
        /// </summary>
        internal static void ClearMappings()
        {
            GeometryAssetIdToSmbPath.Clear();
            GeometryAssetIdToGeometryCount.Clear();
        }

        /// <summary>
        /// Gets the current SMB path mappings (for debugging)
        /// </summary>
        internal static Dictionary<string, string> GetSmbPathMappings()
        {
            return new Dictionary<string, string>(GeometryAssetIdToSmbPath);
        }

        /// <summary>
        /// Backs up all SMB files before upload (SDK deletes them during UploadGeometries).
        /// Returns a dictionary of original path -> backup path.
        /// </summary>
        private static Dictionary<string, string> BackupSmbFiles(DiagnosticsLogger log)
        {
            var backups = new Dictionary<string, string>();

            foreach (var kvp in GeometryAssetIdToSmbPath)
            {
                var smbPath = kvp.Value;
                if (File.Exists(smbPath))
                {
                    var backupPath = smbPath + ".backup";
                    try
                    {
                        File.Copy(smbPath, backupPath, overwrite: true);
                        backups[smbPath] = backupPath;
                        log.Info($"[SMB Backup] Created: {backupPath}");
                    }
                    catch (Exception ex)
                    {
                        log.Info($"[SMB Backup] Failed to backup {smbPath}: {ex.Message}");
                    }
                }
            }

            return backups;
        }

        /// <summary>
        /// Restores SMB files from backups after upload (needed for ClearLocalStates).
        /// </summary>
        private static void RestoreSmbFiles(Dictionary<string, string> backups, DiagnosticsLogger log)
        {
            foreach (var kvp in backups)
            {
                var originalPath = kvp.Key;
                var backupPath = kvp.Value;

                if (!File.Exists(originalPath) && File.Exists(backupPath))
                {
                    try
                    {
                        File.Move(backupPath, originalPath);
                        log.Info($"[SMB Restore] Restored: {originalPath}");
                    }
                    catch (Exception ex)
                    {
                        log.Info($"[SMB Restore] Failed to restore {originalPath}: {ex.Message}");
                    }
                }
                else if (File.Exists(backupPath))
                {
                    // Original still exists, just clean up backup
                    try
                    {
                        File.Delete(backupPath);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
            }
        }

        #endregion
    }
}
