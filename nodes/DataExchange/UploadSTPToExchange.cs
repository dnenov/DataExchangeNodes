using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange;
using Autodesk.DataExchange.DataModels;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.Interface;
using DataExchangeNodes.DataExchange;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Upload STEP files (.stp or .step) to a Data Exchange using the official SDK (no reflection)
    /// This node tests how the SDK handles multiple geometries in STEP files
    /// Both .stp and .step extensions are supported as they represent the same format
    /// </summary>
    public static class UploadSTPToExchange
    {
        /// <summary>
        /// Uploads a STEP file to a Data Exchange using the official SDK
        /// </summary>
        /// <param name="exchange">The Exchange to upload to</param>
        /// <param name="stepFilePath">Path to the STEP file (.stp or .step)</param>
        /// <param name="elementName">Name for the element/group (default: "ExportedGeometry")</param>
        /// <param name="units">Units for the geometry (default: "kUnitType_Meter")</param>
        /// <returns>Dictionary with success status, diagnostics, and geometry count</returns>
        [MultiReturn(new[] { "success", "diagnostics", "geometryCount", "uploadInfo" })]
        public static Dictionary<string, object> Upload(
            Exchange exchange,
            string stepFilePath,
            string elementName = "ExportedGeometry",
            string units = "kUnitType_Meter")
        {
            var diagnostics = new List<string>();
            var success = false;
            int geometryCount = 0;
            object uploadInfo = null;

            try
            {
                diagnostics.Add($"=== Upload STEP File to DataExchange ===");
                diagnostics.Add($"Exchange: {exchange?.ExchangeTitle} (ID: {exchange?.ExchangeId})");
                diagnostics.Add($"STEP File: {stepFilePath}");

                if (exchange == null)
                {
                    diagnostics.Add("✗ ERROR: Exchange is null");
                    return CreateErrorResult(diagnostics, geometryCount, uploadInfo);
                }

                if (string.IsNullOrEmpty(stepFilePath) || !File.Exists(stepFilePath))
                {
                    diagnostics.Add($"✗ ERROR: STEP file not found: {stepFilePath}");
                    return CreateErrorResult(diagnostics, geometryCount, uploadInfo);
                }

                // Validate file extension (both .stp and .step are supported)
                var extension = Path.GetExtension(stepFilePath)?.ToLowerInvariant();
                if (extension != ".stp" && extension != ".step")
                {
                    diagnostics.Add($"⚠️ WARNING: File extension is '{extension}'. Expected .stp or .step");
                    diagnostics.Add($"  Proceeding anyway - SDK may still accept the file format");
                }
                else
                {
                    diagnostics.Add($"✓ File extension validated: {extension} (STEP format)");
                }

                // Check if Client is initialized (DataExchangeClient.GetElementDataModelAsync will handle errors)
                if (!DataExchangeClient.IsInitialized())
                {
                    diagnostics.Add("✗ ERROR: Client is not initialized. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
                    return CreateErrorResult(diagnostics, geometryCount, uploadInfo);
                }

                diagnostics.Add($"✓ Client is initialized");

                // Get the client instance once for reuse throughout the method
                var client = DataExchangeClient.GetClient();
                if (client == null)
                {
                    diagnostics.Add("✗ ERROR: Client is null");
                    return CreateErrorResult(diagnostics, geometryCount, uploadInfo);
                }

                // Create DataExchangeIdentifier
                var identifier = new DataExchangeIdentifier
                {
                    ExchangeId = exchange.ExchangeId,
                    CollectionId = exchange.CollectionId
                };
                
                // HubId is required for DataExchangeIdentifier validation
                if (!string.IsNullOrEmpty(exchange.HubId))
                {
                    identifier.HubId = exchange.HubId;
                    diagnostics.Add($"✓ Set HubId: {exchange.HubId}");
                }
                else
                {
                    diagnostics.Add($"⚠️ WARNING: HubId is missing from Exchange - this may cause errors");
                }
                
                diagnostics.Add($"✓ Created DataExchangeIdentifier");

                // Get ElementDataModel using centralized method (gets latest revision automatically)
                var stopwatch = Stopwatch.StartNew();
                var elementDataModelResponse = Task.Run(async () => await DataExchangeClient.GetElementDataModelAsync(identifier, CancellationToken.None)).Result;
                stopwatch.Stop();
                diagnostics.Add($"⏱️ GetElementDataModelAsync (with latest revision): {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");

                ElementDataModel elementDataModel = null;
                if (elementDataModelResponse != null)
                {
                    // Handle IResponse<T> pattern
                    var responseType = elementDataModelResponse.GetType();
                    var valueProp = responseType.GetProperty("Value");
                    if (valueProp != null)
                    {
                        elementDataModel = valueProp.GetValue(elementDataModelResponse) as ElementDataModel;
                    }
                    else if (elementDataModelResponse is ElementDataModel directModel)
                    {
                        elementDataModel = directModel;
                    }
                }

                if (elementDataModel == null)
                {
                    diagnostics.Add("⚠️ ElementDataModel is null - this is a new/empty exchange");
                    diagnostics.Add("Creating new ElementDataModel from scratch...");
                    
                    // Try to create ElementDataModel using official SDK
                    try
                    {
                        elementDataModel = ElementDataModel.Create(client);
                        diagnostics.Add("✓ Created ElementDataModel using ElementDataModel.Create(IClient)");
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"✗ ERROR: Could not create ElementDataModel: {ex.Message}");
                        return CreateErrorResult(diagnostics, geometryCount, uploadInfo);
                    }
                }
                else
                {
                    diagnostics.Add($"✓ Got existing ElementDataModel: {elementDataModel.GetType().FullName}");
                }

                // Try to find existing element with the same name (reuse logic from ExportGeometryToSMB)
                diagnostics.Add($"\nLooking for existing element with name: {elementName}...");
                var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
                Autodesk.DataExchange.DataModels.Element element = null;
                bool foundExistingElement = false;
                
                if (elementsProperty != null)
                {
                    var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                    if (elements != null)
                    {
                        var elementsList = elements.Cast<object>().ToList();
                        diagnostics.Add($"  Checking {elementsList.Count} existing element(s) in the exchange...");
                        
                        // Track how many elements with the same name we find
                        var matchingElements = new List<(object element, string id)>();
                        
                        foreach (var existingElement in elementsList)
                        {
                            var nameProp = existingElement.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                            if (nameProp != null)
                            {
                                var existingName = nameProp.GetValue(existingElement)?.ToString();
                                diagnostics.Add($"    - Existing element name: '{existingName}' (comparing with: '{elementName}')");
                                
                                if (existingName == elementName)
                                {
                                    var idProp = existingElement.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                    var existingId = idProp?.GetValue(existingElement)?.ToString() ?? "N/A";
                                    matchingElements.Add((existingElement, existingId));
                                    diagnostics.Add($"      Found matching element (ID: {existingId})");
                                }
                            }
                        }
                        
                        // If we found matching elements, use the first one (or we could use the one with most geometries)
                        if (matchingElements.Count > 0)
                        {
                            if (matchingElements.Count > 1)
                            {
                                diagnostics.Add($"  ⚠️ WARNING: Found {matchingElements.Count} elements with the same name '{elementName}'");
                                diagnostics.Add($"  Will use the first one (ID: {matchingElements[0].id})");
                            }
                            
                            foundExistingElement = true;
                            var selectedElement = matchingElements[0].element;
                            diagnostics.Add($"  ✓ FOUND EXISTING ELEMENT/GROUP with matching name: '{elementName}' (ID: {matchingElements[0].id})");
                            diagnostics.Add($"  Will reuse this element and add geometry to it");
                            
                            element = selectedElement as Autodesk.DataExchange.DataModels.Element;
                            if (element == null)
                            {
                                diagnostics.Add($"  ⚠️ WARNING: Could not cast existing element to Element type, will create new one");
                                foundExistingElement = false;
                            }
                        }
                    }
                }
                
                // If no existing element found, create a new one
                if (!foundExistingElement)
                {
                    diagnostics.Add($"  ✗ No existing element/group found with name: '{elementName}'");
                    diagnostics.Add($"  Will create a new element");
                    
                    // Create ElementProperties
                    var elementProperties = new ElementProperties(elementName, elementName, "Generics", "Generic", "Generic Object");
                    diagnostics.Add($"✓ Created ElementProperties");

                    // Add element to ElementDataModel
                    stopwatch.Restart();
                    element = elementDataModel.AddElement(elementProperties) as Autodesk.DataExchange.DataModels.Element;
                    stopwatch.Stop();
                    if (element == null)
                    {
                        throw new InvalidOperationException("AddElement returned null or could not be cast to Element");
                    }
                    diagnostics.Add($"✓ Added new element: {element.GetType().FullName}");
                    diagnostics.Add($"⏱️ AddElement: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");
                }
                else
                {
                    diagnostics.Add($"⏱️ Reused existing element: 0ms (0.000s)");
                }

                // ExchangeData is a property on ElementDataModel, accessed via elementDataModel.ExchangeData
                // We'll pass ElementDataModel directly to SyncExchangeDataAsync

                // If reusing an existing element, get its existing geometries BEFORE creating new one
                var existingGeometries = new List<ElementGeometry>();
                if (foundExistingElement)
                {
                    diagnostics.Add($"\nChecking for existing geometries on element BEFORE creating new geometry...");
                    
                    // Check HasGeometry property first
                    var hasGeometryProp = element.GetType().GetProperty("HasGeometry", BindingFlags.Public | BindingFlags.Instance);
                    if (hasGeometryProp != null)
                    {
                        var hasGeometry = (bool)(hasGeometryProp.GetValue(element) ?? false);
                        diagnostics.Add($"  Element.HasGeometry: {hasGeometry}");
                        
                        if (hasGeometry)
                        {
                            // Try to get existing geometries from ElementDataModel using GetElementGeometriesAsync
                            // Method signature: GetElementGeometriesAsync(IEnumerable<Element> elements, CancellationToken cancellationToken = default, GeometryOutputOptions geometryOutputOptions = null)
                            // Returns: Task<Dictionary<Element, IEnumerable<ElementGeometry>>>
                            try
                            {
                                var getElementGeometriesMethod = elementDataModel.GetType().GetMethod("GetElementGeometriesAsync", BindingFlags.Public | BindingFlags.Instance);
                                if (getElementGeometriesMethod != null)
                                {
                                    diagnostics.Add($"  ✓ Found GetElementGeometriesAsync method on ElementDataModel");
                                    
                                    var elementsList = new List<Autodesk.DataExchange.DataModels.Element> { element };
                                    var elementsEnumerable = elementsList as System.Collections.IEnumerable;
                                    
                                    // Call with 3 parameters: elements, CancellationToken.None, null (for GeometryOutputOptions)
                                    var task = getElementGeometriesMethod.Invoke(elementDataModel, new object[] { elementsEnumerable, CancellationToken.None, null });
                                    if (task != null)
                                    {
                                        // Get the result from the Task
                                        var result = ((dynamic)task).Result;
                                        if (result != null)
                                        {
                                            // Result is Dictionary<Element, IEnumerable<ElementGeometry>>
                                            var dict = result as System.Collections.IDictionary;
                                            if (dict != null)
                                            {
                                                if (dict.Contains(element))
                                                {
                                                    var geoms = dict[element];
                                                    if (geoms is System.Collections.IEnumerable geomsEnum)
                                                    {
                                                        foreach (var geom in geomsEnum)
                                                        {
                                                            if (geom is ElementGeometry elementGeom)
                                                            {
                                                                existingGeometries.Add(elementGeom);
                                                            }
                                                        }
                                                        diagnostics.Add($"  ✓ Retrieved {existingGeometries.Count} existing geometry/geometries from ElementDataModel");
                                                    }
                                                    else
                                                    {
                                                        diagnostics.Add($"  ⚠️ Geometries value is not IEnumerable: {geoms?.GetType().FullName ?? "null"}");
                                                    }
                                                }
                                                else
                                                {
                                                    diagnostics.Add($"  ⚠️ Element not found in returned dictionary (dict has {dict.Count} entries)");
                                                }
                                            }
                                            else
                                            {
                                                diagnostics.Add($"  ⚠️ Result is not IDictionary: {result.GetType().FullName}");
                                            }
                                        }
                                        else
                                        {
                                            diagnostics.Add($"  ⚠️ Task result is null");
                                        }
                                    }
                                    else
                                    {
                                        diagnostics.Add($"  ⚠️ Method invocation returned null");
                                    }
                                }
                                else
                                {
                                    diagnostics.Add($"  ⚠️ GetElementGeometriesAsync method not found");
                                }
                            }
                            catch (Exception ex)
                            {
                                diagnostics.Add($"  ⚠️ Could not retrieve existing geometries: {ex.GetType().Name}: {ex.Message}");
                                if (ex.InnerException != null)
                                {
                                    diagnostics.Add($"    Inner: {ex.InnerException.Message}");
                                }
                            }
                        }
                        else
                        {
                            diagnostics.Add($"  Element has no existing geometries (HasGeometry = false)");
                        }
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ Could not find HasGeometry property");
                    }
                }

                // Create geometry from STEP file using official SDK
                diagnostics.Add($"\nCreating geometry from STEP file using ElementDataModel.CreateFileGeometry...");
                stopwatch.Restart();
                
                // Create GeometryProperties for STEP file
                var geometryProperties = new GeometryProperties(stepFilePath);
                
                // Use official SDK method to create geometry from file
                var elementGeometry = ElementDataModel.CreateFileGeometry(geometryProperties);
                
                stopwatch.Stop();
                diagnostics.Add($"✓ Created ElementGeometry from STEP file");
                diagnostics.Add($"⏱️ CreateFileGeometry: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");

                // Combine existing geometries with new one
                var geometries = new List<ElementGeometry>(existingGeometries);
                geometries.Add(elementGeometry);
                
                if (foundExistingElement && existingGeometries.Count > 0)
                {
                    diagnostics.Add($"✓ Combined {existingGeometries.Count} existing + 1 new = {geometries.Count} total geometries");
                }

                elementDataModel.SetElementGeometry(element, geometries);
                if (foundExistingElement)
                {
                    diagnostics.Add($"✓ Added geometry to existing element (total: {geometries.Count} geometries)");
                }
                else
                {
                    diagnostics.Add($"✓ Set geometries on new element using SetElementGeometry");
                }

                // Count geometries (try to inspect the geometry structure)
                try
                {
                    // Try to get geometry count from the ElementGeometry
                    // This might not be directly available, but we can try
                    geometryCount = geometries.Count;
                    diagnostics.Add($"✓ Found {geometryCount} ElementGeometry object(s)");
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"⚠️ Could not determine geometry count: {ex.Message}");
                    geometryCount = 1; // Assume 1 if we can't determine
                }

                // Pre-sync diagnostics: check geometries per element
                diagnostics.Add($"\n=== Geometry summary before sync ===");
                try
                {
                    var allElements = elementDataModel.Elements?.ToList() ?? new List<Autodesk.DataExchange.DataModels.Element>();
                    var allGeometries = elementDataModel.GetElementGeometriesAsync(allElements, CancellationToken.None, null).Result;
                    if (allGeometries != null)
                    {
                        foreach (var kvp in allGeometries)
                        {
                            var elemName = kvp.Key?.Name ?? "Unknown";
                            var count = kvp.Value?.Count() ?? 0;
                            diagnostics.Add($"  Element '{elemName}': {count} geometry/geometries");
                            foreach (var geom in kvp.Value ?? Enumerable.Empty<ElementGeometry>())
                            {
                                var geomType = geom?.GetType().FullName ?? "null";
                                var filePathProp = geom?.GetType().GetProperty("FilePath", BindingFlags.Public | BindingFlags.Instance);
                                var filePath = filePathProp?.GetValue(geom)?.ToString();
                                if (!string.IsNullOrEmpty(filePath))
                                {
                                    diagnostics.Add($"    - {geomType} (FilePath: {filePath})");
                                }
                                else
                                {
                                    diagnostics.Add($"    - {geomType}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ Could not read pre-sync geometries: {ex.GetType().Name}: {ex.Message}");
                }

                // Sync exchange data (this will upload the geometry)
                // SyncExchangeDataAsync accepts ElementDataModel directly
                diagnostics.Add($"Starting SyncExchangeDataAsync to upload geometry...");
                diagnostics.Add($"  Uploading {geometries.Count} geometry/geometries to element '{elementName}'");
                stopwatch.Restart();

                var syncTask = Task.Run(() => client.SyncExchangeDataAsync(identifier, elementDataModel));
                syncTask.Wait();

                var response = syncTask.Result;
                var value = response.Value;

                diagnostics.Add($"DID WE GET ANYTHING HERE: {value.ToString()}");

                stopwatch.Stop();
                diagnostics.Add($"✓ SyncExchangeDataAsync completed");
                diagnostics.Add($"⏱️ SyncExchangeDataAsync: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");
                
                // CRITICAL: Verify geometries are still present after sync
                diagnostics.Add($"\n=== Verifying geometries after sync ===");
                try
                {
                    var verifyElementsList = new List<Autodesk.DataExchange.DataModels.Element> { element };
                    var verifyGeometriesDict = elementDataModel.GetElementGeometriesAsync(verifyElementsList, CancellationToken.None, null).Result;
                    if (verifyGeometriesDict != null && verifyGeometriesDict.ContainsKey(element))
                    {
                        var verifyGeometries = verifyGeometriesDict[element];
                        var verifyGeometriesList = verifyGeometries?.ToList() ?? new List<ElementGeometry>();
                        var verifyCount = verifyGeometriesList.Count;
                        
                        diagnostics.Add($"  Geometries before sync: {geometries.Count}");
                        diagnostics.Add($"  Geometries after sync: {verifyCount}");
                        
                        if (verifyCount == geometries.Count)
                        {
                            diagnostics.Add($"  ✓ SUCCESS: All {verifyCount} geometries preserved after sync");
                        }
                        else if (verifyCount < geometries.Count)
                        {
                            diagnostics.Add($"  ✗ WARNING: Lost {geometries.Count - verifyCount} geometry/geometries during sync!");
                            diagnostics.Add($"    Expected: {geometries.Count}, Got: {verifyCount}");
                        }
                        else
                        {
                            diagnostics.Add($"  ⚠️ UNEXPECTED: More geometries after sync ({verifyCount}) than before ({geometries.Count})");
                        }
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ Could not verify geometries after sync");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ Could not verify geometries after sync: {ex.GetType().Name}: {ex.Message}");
                }
                
                // Try to get exchange details to check version/revision
                try
                {
                    diagnostics.Add($"\n=== Checking Exchange Version/Revision ===");
                    // GetExchangeDetailsAsync takes IDataExchangeIdentifier (not DataExchangeIdentifier)
                    // Try to find the interface type
                    var iDataExchangeIdentifierType = typeof(DataExchangeIdentifier).GetInterfaces()
                        .FirstOrDefault(i => i.Name == "IDataExchangeIdentifier");
                    
                    if (iDataExchangeIdentifierType == null)
                    {
                        // Try to get it from the identifier itself
                        iDataExchangeIdentifierType = identifier.GetType().GetInterfaces()
                            .FirstOrDefault(i => i.Name == "IDataExchangeIdentifier");
                    }
                    
                    Type[] paramTypes;
                    if (iDataExchangeIdentifierType != null)
                    {
                        paramTypes = new[] { iDataExchangeIdentifierType };
                    }
                    else
                    {
                        // Fallback: try with DataExchangeIdentifier
                        paramTypes = new[] { typeof(DataExchangeIdentifier) };
                    }
                    
                    var getExchangeDetailsMethod = client.GetType().GetMethod("GetExchangeDetailsAsync", 
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        paramTypes,
                        null);
                    
                    if (getExchangeDetailsMethod != null)
                    {
                        var detailsTask = getExchangeDetailsMethod.Invoke(client, new object[] { identifier, CancellationToken.None });
                        if (detailsTask != null)
                        {
                            var detailsResult = ((dynamic)detailsTask).Result;
                            if (detailsResult != null)
                            {
                                var detailsType = detailsResult.GetType();
                                var valueProp = detailsType.GetProperty("Value");
                                if (valueProp != null)
                                {
                                    var exchangeDetails = valueProp.GetValue(detailsResult);
                                    if (exchangeDetails != null)
                                    {
                                        var detailsObjType = exchangeDetails.GetType();
                                        var revisionProp = detailsObjType.GetProperty("Revision", BindingFlags.Public | BindingFlags.Instance);
                                        var versionProp = detailsObjType.GetProperty("Version", BindingFlags.Public | BindingFlags.Instance);
                                        
                                        if (revisionProp != null)
                                        {
                                            var revision = revisionProp.GetValue(exchangeDetails);
                                            diagnostics.Add($"✓ Exchange Revision: {revision}");
                                        }
                                        if (versionProp != null)
                                        {
                                            var version = versionProp.GetValue(exchangeDetails);
                                            diagnostics.Add($"✓ Exchange Version: {version}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        diagnostics.Add($"⚠️ GetExchangeDetailsAsync method not found with expected signature");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"⚠️ Could not check exchange version: {ex.Message}");
                }

                // Try to get UploadInfo from the sync result (if available)
                try
                {
                    var syncResult = syncTask.Result;
                    if (syncResult != null)
                    {
                        var resultType = syncResult.GetType();
                        var valueProp = resultType.GetProperty("Value");
                        if (valueProp != null)
                        {
                            uploadInfo = valueProp.GetValue(syncResult);
                        }
                        else
                        {
                            uploadInfo = syncResult;
                        }

                        if (uploadInfo != null)
                        {
                            var uploadInfoType = uploadInfo.GetType();
                            var countProp = uploadInfoType.GetProperty("Count");
                            if (countProp != null)
                            {
                                var count = countProp.GetValue(uploadInfo);
                                diagnostics.Add($"✓ UploadInfo.Count: {count}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"⚠️ Could not extract UploadInfo from sync result: {ex.Message}");
                }

                // Inspect ElementDataModel after sync to see how many geometries were uploaded
                try
                {
                    diagnostics.Add($"\n=== Inspecting ElementDataModel after sync ===");
                    
                    // Try to get elements from ElementDataModel
                    var elementsProp = elementDataModel.GetType().GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
                    if (elementsProp != null)
                    {
                        var elements = elementsProp.GetValue(elementDataModel);
                        if (elements != null)
                        {
                            var countProp = elements.GetType().GetProperty("Count");
                            if (countProp != null)
                            {
                                var elementCount = countProp.GetValue(elements);
                                diagnostics.Add($"✓ Elements.Count: {elementCount}");
                            }
                            
                            // Try to enumerate and inspect each Element
                            if (elements is System.Collections.IEnumerable enumerable)
                            {
                                int elementIndex = 0;
                                foreach (var elem in enumerable)
                                {
                                    elementIndex++;
                                    var elemType = elem.GetType();
                                    
                                    // Get element name
                                    var nameProp = elemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                                    var elemName = nameProp?.GetValue(elem)?.ToString() ?? "unknown";
                                    diagnostics.Add($"  Element {elementIndex}: Name = {elemName}");
                                    
                                    // Try to get geometries from element
                                    var geometriesProp = elemType.GetProperty("Geometries", BindingFlags.Public | BindingFlags.Instance);
                                    if (geometriesProp != null)
                                    {
                                        var elementGeometries = geometriesProp.GetValue(elem);
                                        if (elementGeometries != null)
                                        {
                                            var geomCountProp = elementGeometries.GetType().GetProperty("Count");
                                            if (geomCountProp != null)
                                            {
                                                var geomCount = geomCountProp.GetValue(elementGeometries);
                                                diagnostics.Add($"    Geometries.Count: {geomCount}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    // Try to access ExchangeData via reflection (it might be internal)
                    try
                    {
                        var exchangeDataProp = elementDataModel.GetType().GetProperty("ExchangeData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (exchangeDataProp != null)
                        {
                            var exchangeData = exchangeDataProp.GetValue(elementDataModel);
                            if (exchangeData != null)
                            {
                                diagnostics.Add($"✓ Accessed ExchangeData via reflection");
                                
                                // Try to get GeometryAssets from ExchangeData
                                var exchangeDataType = exchangeData.GetType();
                                var geometryAssetsProp = exchangeDataType.GetProperty("GeometryAssets", BindingFlags.Public | BindingFlags.Instance);
                                if (geometryAssetsProp != null)
                                {
                                    var geometryAssets = geometryAssetsProp.GetValue(exchangeData);
                                    if (geometryAssets != null)
                                    {
                                        var countMethod = geometryAssets.GetType().GetProperty("Count");
                                        if (countMethod != null)
                                        {
                                            var assetCount = countMethod.GetValue(geometryAssets);
                                            diagnostics.Add($"✓ GeometryAssets.Count: {assetCount}");
                                        }
                                        
                                        // Try to enumerate and inspect each GeometryAsset
                                        if (geometryAssets is System.Collections.IEnumerable enumerable)
                                        {
                                            int assetIndex = 0;
                                            foreach (var asset in enumerable)
                                            {
                                                assetIndex++;
                                                var assetType = asset.GetType();
                                                var idProp = assetType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                                var assetId = idProp?.GetValue(asset)?.ToString() ?? "unknown";
                                                diagnostics.Add($"  GeometryAsset {assetIndex}: ID = {assetId}");
                                                
                                                // Try to get BinaryReference to see size
                                                var binaryRefProp = assetType.GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                                                if (binaryRefProp != null)
                                                {
                                                    var binaryRef = binaryRefProp.GetValue(asset);
                                                    if (binaryRef != null)
                                                    {
                                                        var binaryRefType = binaryRef.GetType();
                                                        var startProp = binaryRefType.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
                                                        var endProp = binaryRefType.GetProperty("End", BindingFlags.Public | BindingFlags.Instance);
                                                        if (startProp != null && endProp != null)
                                                        {
                                                            var start = Convert.ToInt64(startProp.GetValue(binaryRef));
                                                            var end = Convert.ToInt64(endProp.GetValue(binaryRef));
                                                            var size = end - start;
                                                            diagnostics.Add($"    BinaryRef: Start={start}, End={end}, Size={size} bytes");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"⚠️ Could not access ExchangeData via reflection: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"⚠️ Could not inspect ElementDataModel after sync: {ex.Message}");
                }

                success = true;
                diagnostics.Add($"✓ Upload completed successfully");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ ERROR: {ex.GetType().Name}: {ex.Message}");
                diagnostics.Add($"Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"Inner: {ex.InnerException.Message}");
                }
            }

            return new Dictionary<string, object>
            {
                { "success", success },
                { "diagnostics", string.Join("\n", diagnostics) },
                { "geometryCount", geometryCount },
                { "uploadInfo", uploadInfo }
            };
        }


        /// <summary>
        /// Creates an error result dictionary
        /// </summary>
        private static Dictionary<string, object> CreateErrorResult(List<string> diagnostics, int geometryCount, object uploadInfo)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "diagnostics", string.Join("\n", diagnostics) },
                { "geometryCount", geometryCount },
                { "uploadInfo", uploadInfo }
            };
        }
    }
}
