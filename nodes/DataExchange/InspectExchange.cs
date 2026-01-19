using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange;
using Autodesk.DataExchange.Core;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.DataModels;
using DataExchangeNodes.DataExchange;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Inspects an Exchange to see what assets, elements, and geometry are present
    /// </summary>
    public static class InspectExchange
    {
        /// <summary>
        /// Inspects an Exchange and exports a comprehensive report to a text file
        /// </summary>
        [MultiReturn(new[] { "logFilePath", "report", "success" })]
        public static Dictionary<string, object> InspectToFile(
            Exchange exchange,
            string outputFilePath = null)
        {
            var report = new List<string>();
            var success = false;
            string logFilePath = null;

            try
            {
                if (exchange == null)
                {
                    return CreateFileErrorResult(report, "Exchange is null", null);
                }

                // Determine output file path
                if (string.IsNullOrEmpty(outputFilePath))
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "Inspection");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);
                    var fileName = $"ExchangeInspection_{exchange.ExchangeId}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                    logFilePath = Path.Combine(tempDir, fileName);
                }
                else
                {
                    logFilePath = Path.GetFullPath(outputFilePath);
                    var outputDir = Path.GetDirectoryName(logFilePath);
                    if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                        Directory.CreateDirectory(outputDir);
                }

                // Perform comprehensive inspection
                PerformComprehensiveInspection(exchange, report);

                // Write to file
                File.WriteAllLines(logFilePath, report);
                success = true;

                report.Insert(0, $"=== Inspection exported to: {logFilePath} ===");
            }
            catch (Exception ex)
            {
                report.Add($"✗ ERROR: {ex.GetType().Name}: {ex.Message}");
                report.Add($"Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    report.Add($"Inner: {ex.InnerException.Message}");
                }
            }

            return new Dictionary<string, object>
            {
                { "logFilePath", logFilePath ?? string.Empty },
                { "report", string.Join("\n", report.Take(50)) }, // Preview only
                { "success", success }
            };
        }

        /// <summary>
        /// Inspects an Exchange and returns detailed information about its contents
        /// </summary>
        [MultiReturn(new[] { "report", "elementCount", "geometryAssetCount", "designAssetCount", "customParameters", "elementDataModel", "success" })]
        public static Dictionary<string, object> Inspect(
            Exchange exchange,
            bool includeDetails = true)
        {
            var report = new List<string>();
            var elementCount = 0;
            var geometryAssetCount = 0;
            var designAssetCount = 0;
            var customParameters = new Dictionary<string, object>();
            ElementDataModel elementDataModel = null;
            var success = false;

            try
            {
                report.Add("=== Exchange Inspection ===");
                report.Add($"Exchange: {exchange?.ExchangeTitle ?? "N/A"} (ID: {exchange?.ExchangeId ?? "N/A"})");
                report.Add($"Collection ID: {exchange?.CollectionId ?? "N/A"}");
                report.Add("");

                if (exchange == null)
                {
                    return CreateErrorResult(report, "Exchange is null", customParameters, null);
                }

                // Get Client instance using centralized DataExchangeClient
                var client = DataExchangeClient.GetClient();
                if (client == null)
                {
                    return CreateErrorResult(report, "Could not get Client instance. Make sure you have selected an Exchange first using the SelectExchangeElements node.", customParameters, null);
                }

                // Create DataExchangeIdentifier
                var identifier = CreateDataExchangeIdentifier(exchange);

                // Get ElementDataModel using the direct Client method (same pattern as grasshopper-connector)
                ElementDataModel model = null;
                try
                {
                    var elementDataModelResponse = Task.Run(async () => await client.GetElementDataModelAsync(identifier, CancellationToken.None)).Result;
                    model = elementDataModelResponse?.Value;
                }
                catch (Exception ex)
                {
                    report.Add($"✗ ERROR: Could not load ElementDataModel: {ex.Message}");
                    return CreateErrorResult(report, $"Failed to get ElementDataModel: {ex.Message}", customParameters, null);
                }

                if (model == null)
                {
                    return CreateErrorResult(report, "Could not load ElementDataModel - response was null", customParameters, null);
                }

                // Get ExchangeData
                var exchangeDataField = typeof(ElementDataModel).GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (exchangeDataField == null)
                {
                    report.Add("✗ ERROR: Could not find exchangeData field");
                    return CreateErrorResult(report, "Could not access ExchangeData from ElementDataModel", customParameters, null);
                }

                var exchangeData = exchangeDataField.GetValue(model);
                var exchangeDataType = exchangeData.GetType();
                
                elementDataModel = model;

                report.Add("✓ Successfully loaded ElementDataModel");
                report.Add("");

                // Inspect Elements and collect custom parameters
                elementCount = InspectElements(model, report, includeDetails);
                customParameters = CollectCustomParameters(model, report);

                // Inspect GeometryAssets
                geometryAssetCount = InspectGeometryAssets(exchangeData, exchangeDataType, report, includeDetails);

                // Inspect DesignAssets
                designAssetCount = InspectDesignAssets(exchangeData, exchangeDataType, report, includeDetails);

                success = true;
                report.Add("=== Inspection Complete ===");
            }
            catch (Exception ex)
            {
                report.Add($"✗ ERROR: {ex.GetType().Name}: {ex.Message}"); 
                report.Add($"Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    report.Add($"Inner: {ex.InnerException.Message}");
                }
            }

            return new Dictionary<string, object>
            {
                { "report", string.Join("\n", report) },
                { "elementCount", elementCount },
                { "geometryAssetCount", geometryAssetCount },
                { "designAssetCount", designAssetCount },
                { "customParameters", customParameters },
                { "elementDataModel", elementDataModel },
                { "success", success }
            };
        }

        private static Dictionary<string, object> CreateErrorResult(List<string> report, string errorMessage, Dictionary<string, object> customParameters = null, ElementDataModel elementDataModel = null)
        {
            report.Add($"✗ ERROR: {errorMessage}");
            return new Dictionary<string, object>
            {
                { "report", string.Join("\n", report) },
                { "elementCount", 0 },
                { "geometryAssetCount", 0 },
                { "designAssetCount", 0 },
                { "customParameters", customParameters ?? new Dictionary<string, object>() },
                { "elementDataModel", elementDataModel },
                { "success", false }
            };
        }

        private static Dictionary<string, object> CreateFileErrorResult(List<string> report, string errorMessage, string logFilePath)
        {
            report.Add($"✗ ERROR: {errorMessage}");
            return new Dictionary<string, object>
            {
                { "logFilePath", logFilePath ?? string.Empty },
                { "report", string.Join("\n", report) },
                { "success", false }
            };
        }

        private static void PerformComprehensiveInspection(Exchange exchange, List<string> report)
        {
            report.Add("=".PadRight(80, '='));
            report.Add("COMPREHENSIVE EXCHANGE INSPECTION REPORT");
            report.Add("=".PadRight(80, '='));
            report.Add($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.Add("");
            
            report.Add("=== EXCHANGE INFORMATION ===");
            report.Add($"Exchange Title: {exchange?.ExchangeTitle ?? "N/A"}");
            report.Add($"Exchange ID: {exchange?.ExchangeId ?? "N/A"}");
            report.Add($"Collection ID: {exchange?.CollectionId ?? "N/A"}");
            report.Add($"Hub ID: {exchange?.HubId ?? "N/A"}");
            report.Add("");

            // Get Client instance using centralized DataExchangeClient
            var client = DataExchangeClient.GetClient();
            if (client == null)
            {
                report.Add("✗ ERROR: Could not get Client instance. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
                return;
            }

            var identifier = CreateDataExchangeIdentifier(exchange);
            
            // Get ElementDataModel using the direct Client method
            ElementDataModel model = null;
            try
            {
                var elementDataModelResponse = Task.Run(async () => await client.GetElementDataModelAsync(identifier, CancellationToken.None)).Result;
                model = elementDataModelResponse?.Value;
            }
            catch (Exception ex)
            {
                report.Add($"✗ ERROR: Could not load ElementDataModel: {ex.Message}");
                return;
            }

            if (model == null)
            {
                report.Add("✗ ERROR: Could not load ElementDataModel - response was null");
                return;
            }

            // Get ExchangeData
            var exchangeDataField = typeof(ElementDataModel).GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
            if (exchangeDataField == null)
            {
                report.Add("✗ ERROR: Could not find exchangeData field");
                return;
            }

            var exchangeData = exchangeDataField.GetValue(model);
            var exchangeDataType = exchangeData.GetType();
            
            if (model == null || exchangeData == null)
            {
                report.Add("✗ ERROR: Could not load ElementDataModel or ExchangeData");
                return;
            }

            report.Add("✓ Successfully loaded ElementDataModel and ExchangeData");
            report.Add("");

            // Inspect ExchangeData metadata
            InspectExchangeDataMetadata(exchangeData, exchangeDataType, report);

            // Inspect RootAsset
            InspectRootAsset(exchangeData, exchangeDataType, report);

            // Inspect all asset types
            InspectAllAssetTypes(exchangeData, exchangeDataType, report);

            // Inspect Elements
            InspectElementsDetailed(model, report);

            // Inspect Relationships
            InspectAllRelationships(exchangeData, exchangeDataType, report);

            // Inspect Geometry Mappings
            InspectGeometryMappings(exchangeData, exchangeDataType, report);

            // Inspect Metadata and Properties
            InspectMetadataAndProperties(exchangeData, exchangeDataType, report);

            report.Add("");
            report.Add("=".PadRight(80, '='));
            report.Add("END OF INSPECTION REPORT");
            report.Add("=".PadRight(80, '='));
        }


        private static DataExchangeIdentifier CreateDataExchangeIdentifier(Exchange exchange)
        {
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
        /// Diagnostic method: Lists all public methods on the Client that might help access exchange data
        /// (Note: This method is kept for diagnostics but may not be needed now that we use Client directly)
        /// </summary>
        private static void ListAllPublicClientMethods(Client client, Type clientType, DataExchangeIdentifier identifier, List<string> report)
        {
            report.Add("=== DIAGNOSTIC: All Public Methods on Client ===");
            
            var allMethods = clientType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            var relevantMethods = allMethods
                .Where(m => 
                    m.Name.Contains("Get", StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains("Element", StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains("Asset", StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains("Geometry", StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains("Data", StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Contains("Exchange", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.Name)
                .ToList();
            
            report.Add($"Found {relevantMethods.Count} potentially relevant public methods:");
            foreach (var method in relevantMethods)
            {
                var parameters = method.GetParameters();
                var paramList = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                report.Add($"  - {method.Name}({paramList})");
            }
            report.Add("");
            
            // Try to find methods that take DataExchangeIdentifier
            report.Add("=== Methods that take DataExchangeIdentifier ===");
            var methodsWithIdentifier = allMethods
                .Where(m => m.GetParameters().Any(p => 
                    p.ParameterType.Name.Contains("DataExchangeIdentifier") || 
                    p.ParameterType.Name.Contains("Identifier")))
                .ToList();
            
            foreach (var method in methodsWithIdentifier)
            {
                var parameters = method.GetParameters();
                var paramList = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                report.Add($"  - {method.Name}({paramList})");
                
                // Try to invoke if it looks safe (no parameters or just identifier + cancellation token)
                if (parameters.Length <= 2 && 
                    parameters.Any(p => p.ParameterType.Name.Contains("DataExchangeIdentifier") || p.ParameterType.Name.Contains("Identifier")))
                {
                    try
                    {
                        report.Add($"    Attempting to invoke {method.Name}...");
                        object[] invokeParams = new object[parameters.Length];
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            if (parameters[i].ParameterType.Name.Contains("DataExchangeIdentifier") || 
                                parameters[i].ParameterType.Name.Contains("Identifier"))
                            {
                                invokeParams[i] = identifier;
                            }
                            else if (parameters[i].ParameterType == typeof(CancellationToken))
                            {
                                invokeParams[i] = CancellationToken.None;
                            }
                            else
                            {
                                invokeParams[i] = null;
                            }
                        }
                        
                        var result = method.Invoke(client, invokeParams);
                        if (result != null)
                        {
                            var resultType = result.GetType();
                            report.Add($"    ✓ Method returned: {resultType.Name}");
                            
                            // If it's a Task, try to get the result
                            if (resultType.IsGenericType && resultType.GetGenericTypeDefinition().Name.Contains("Task"))
                            {
                                try
                                {
                                    var taskResult = ((dynamic)result).GetAwaiter().GetResult();
                                    report.Add($"    ✓ Task result type: {taskResult?.GetType().Name ?? "null"}");
                                    
                                    // Check if result has Value property
                                    var valueProp = taskResult?.GetType().GetProperty("Value");
                                    if (valueProp != null)
                                    {
                                        var value = valueProp.GetValue(taskResult);
                                        report.Add($"    ✓ Value property found: {value?.GetType().Name ?? "null"}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    report.Add($"    ⚠️ Could not await task: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            report.Add($"    ⚠️ Method returned null");
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Add($"    ⚠️ Error invoking method: {ex.Message}");
                    }
                }
            }
            report.Add("");
        }

        private static int InspectElements(ElementDataModel model, List<string> report, bool includeDetails)
        {
            var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
            if (elementsProperty == null)
            {
                return 0;
            }

            var elements = elementsProperty.GetValue(model) as System.Collections.IEnumerable;
            if (elements == null)
            {
                return 0;
            }

            var elementsList = elements.Cast<object>().ToList();
            var elementCount = elementsList.Count;
            report.Add($"=== Elements ({elementCount}) ===");

            if (includeDetails && elementCount > 0)
            {
                var idx = 0;
                var itemsToShow = Math.Min(10, elementCount);
                var lastItemsToShow = Math.Min(2, elementCount);
                var showLastItems = elementCount > 10;
                
                // Show first items
                foreach (var element in elementsList.Take(itemsToShow))
                {
                    idx++;
                    var elementIdProp = element.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                    var elementId = elementIdProp?.GetValue(element)?.ToString() ?? "N/A";

                    var elementNameProp = element.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    var elementName = elementNameProp?.GetValue(element)?.ToString() ?? "N/A";

                    report.Add($"  Element #{idx}: {elementName} (ID: {elementId})");
                }
                
                // Show last items if there are more than 10
                if (showLastItems)
                {
                    var skippedCount = elementCount - itemsToShow - lastItemsToShow;
                    if (skippedCount > 0)
                    {
                        report.Add($"  ... and {skippedCount} more");
                    }
                    
                    // Show last 2 items
                    foreach (var element in elementsList.Skip(elementCount - lastItemsToShow))
                    {
                        idx++;
                        var elementIdProp = element.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                        var elementId = elementIdProp?.GetValue(element)?.ToString() ?? "N/A";

                        var elementNameProp = element.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        var elementName = elementNameProp?.GetValue(element)?.ToString() ?? "N/A";

                        report.Add($"  Element #{idx}: {elementName} (ID: {elementId})");
                    }
                }
            }
            report.Add("");

            return elementCount;
        }

        private static int InspectGeometryAssets(object exchangeData, Type exchangeDataType, List<string> report, bool includeDetails)
        {
            var geometryAssetType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset");
            if (geometryAssetType == null)
            {
                return 0;
            }

            var getAssetsByTypeMethod = exchangeDataType.GetMethod("GetAssetsByType", BindingFlags.Public | BindingFlags.Instance);
            if (getAssetsByTypeMethod == null)
            {
                return 0;
            }

            var genericMethod = getAssetsByTypeMethod.MakeGenericMethod(geometryAssetType);
            var geometryAssets = genericMethod.Invoke(exchangeData, null) as System.Collections.IEnumerable;

            if (geometryAssets == null)
            {
                return 0;
            }

            var geometryAssetsList = geometryAssets.Cast<object>().ToList();
            var geometryAssetCount = geometryAssetsList.Count;
            report.Add($"=== GeometryAssets ({geometryAssetCount}) ===");

            if (includeDetails && geometryAssetCount > 0)
            {
                var idx = 0;
                var itemsToShow = Math.Min(10, geometryAssetCount);
                var lastItemsToShow = Math.Min(2, geometryAssetCount);
                var showLastItems = geometryAssetCount > itemsToShow + lastItemsToShow;
                
                // Show first items
                foreach (var asset in geometryAssetsList.Take(itemsToShow))
                {
                    idx++;
                    InspectSingleGeometryAsset(asset, idx, report);
                }
                
                // Show last items if there are more than first + last
                if (showLastItems)
                {
                    var skippedCount = geometryAssetCount - itemsToShow - lastItemsToShow;
                    if (skippedCount > 0)
                    {
                        report.Add($"  ... and {skippedCount} more");
                    }
                    
                    // Show last 2 items (from the end of the list)
                    var lastTwoItems = geometryAssetsList.Skip(geometryAssetCount - lastItemsToShow).Take(lastItemsToShow);
                    foreach (var asset in lastTwoItems)
                    {
                        idx = geometryAssetCount - lastItemsToShow + (idx - itemsToShow) + 1;
                        InspectSingleGeometryAsset(asset, idx, report);
                    }
                }
                else if (geometryAssetCount > itemsToShow)
                {
                    // If we're showing all items (less than 12 total), just show the remaining ones
                    foreach (var asset in geometryAssetsList.Skip(itemsToShow))
                    {
                        idx++;
                        InspectSingleGeometryAsset(asset, idx, report);
                    }
                }
            }
            report.Add("");

            return geometryAssetCount;
        }

        private static void InspectSingleGeometryAsset(object asset, int idx, List<string> report)
        {
            var idProp = asset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            var binaryRefProp = asset.GetType().GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
            var geometryProp = asset.GetType().GetProperty("Geometry", BindingFlags.Public | BindingFlags.Instance);

            var id = idProp?.GetValue(asset)?.ToString() ?? "N/A";
            var binaryRef = binaryRefProp?.GetValue(asset);
            var geometry = geometryProp?.GetValue(asset);

            report.Add($"  GeometryAsset #{idx}:");
            report.Add($"    ID: {id}");
            report.Add($"    BinaryReference: {(binaryRef != null ? "Set" : "Null")}");
            report.Add($"    Geometry: {(geometry != null ? "Set" : "Null")}");

            // Extract geometry type and format
            var (geometryType, geometryFormat) = ExtractGeometryTypeAndFormat(geometry);
            report.Add($"    Type: {geometryType}");
            report.Add($"    Format: {geometryFormat}");

            // Inspect BinaryReference details
            if (binaryRef != null)
            {
                InspectBinaryReference(binaryRef, report);
            }

            // Inspect parent
            InspectParent(asset, report);
        }

        private static (string type, string format) ExtractGeometryTypeAndFormat(object geometry)
        {
            if (geometry == null)
            {
                return ("Unknown", "Unknown");
            }

            var geometryComponentType = geometry.GetType();
            var geometryWrapperProp = geometryComponentType.GetProperty("Geometry", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (geometryWrapperProp == null)
            {
                geometryWrapperProp = geometryComponentType.GetProperty("Geometry", BindingFlags.Public | BindingFlags.Instance);
            }

            if (geometryWrapperProp == null)
            {
                return ("Unknown", "Unknown");
            }

            var geometryWrapper = geometryWrapperProp.GetValue(geometry);
            if (geometryWrapper == null)
            {
                return ("Unknown", "Unknown");
            }

            var wrapperType = geometryWrapper.GetType();
            var typeProp = wrapperType.GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
            var formatProp = wrapperType.GetProperty("Format", BindingFlags.Public | BindingFlags.Instance);

            string geometryType = "Unknown";
            string geometryFormat = "Unknown";

            if (typeProp != null)
            {
                var typeValue = typeProp.GetValue(geometryWrapper);
                if (typeValue != null)
                {
                    // Handle enum types - try to get the name, not the numeric value
                    if (typeValue.GetType().IsEnum)
                    {
                        geometryType = Enum.GetName(typeValue.GetType(), typeValue) ?? typeValue.ToString();
                    }
                    else
                    {
                        geometryType = typeValue.ToString();
                    }
                }
            }

            if (formatProp != null)
            {
                var formatValue = formatProp.GetValue(geometryWrapper);
                if (formatValue != null)
                {
                    // Handle enum types - try to get the name, not the numeric value
                    if (formatValue.GetType().IsEnum)
                    {
                        geometryFormat = Enum.GetName(formatValue.GetType(), formatValue) ?? formatValue.ToString();
                    }
                    else
                    {
                        geometryFormat = formatValue.ToString();
                    }
                }
            }

            return (geometryType, geometryFormat);
        }

        private static void InspectBinaryReference(object binaryRef, List<string> report)
        {
            var startProp = binaryRef.GetType().GetProperty("Start");
            var endProp = binaryRef.GetType().GetProperty("End");
            var binaryRefIdProp = binaryRef.GetType().GetProperty("Id");

            if (startProp != null && endProp != null && binaryRefIdProp != null)
            {
                report.Add($"      BinaryRef ID: {binaryRefIdProp.GetValue(binaryRef)}");
                report.Add($"      BinaryRef Start: {startProp.GetValue(binaryRef)}");
                report.Add($"      BinaryRef End: {endProp.GetValue(binaryRef)}");
            }
        }

        private static void InspectParent(object asset, List<string> report)
        {
            var parentProp = asset.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
            if (parentProp == null)
            {
                return;
            }

            var parent = parentProp.GetValue(asset);
            if (parent == null)
            {
                return;
            }

            var parentIdProp = parent.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            var parentType = parent.GetType().Name;
            report.Add($"    Parent: {parentType} (ID: {parentIdProp?.GetValue(parent)})");
        }

        private static int InspectDesignAssets(object exchangeData, Type exchangeDataType, List<string> report, bool includeDetails)
        {
            var designAssetType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset");
            if (designAssetType == null)
            {
                return 0;
            }

            var getAssetsByTypeMethod = exchangeDataType.GetMethod("GetAssetsByType", BindingFlags.Public | BindingFlags.Instance);
            if (getAssetsByTypeMethod == null)
            {
                return 0;
            }

            var genericMethod = getAssetsByTypeMethod.MakeGenericMethod(designAssetType);
            var designAssets = genericMethod.Invoke(exchangeData, null) as System.Collections.IEnumerable;

            if (designAssets == null)
            {
                return 0;
            }

            var designAssetsList = designAssets.Cast<object>().ToList();
            var designAssetCount = designAssetsList.Count;
            report.Add($"=== DesignAssets ({designAssetCount}) ===");

            if (includeDetails && designAssetCount > 0)
            {
                var idx = 0;
                var itemsToShow = Math.Min(10, designAssetCount);
                var lastItemsToShow = Math.Min(2, designAssetCount);
                var showLastItems = designAssetCount > 10;
                
                // Show first items
                foreach (var asset in designAssetsList.Take(itemsToShow))
                {
                    idx++;
                    InspectSingleDesignAsset(asset, idx, exchangeData, exchangeDataType, report);
                }
                
                // Show last items if there are more than 10
                if (showLastItems)
                {
                    var skippedCount = designAssetCount - itemsToShow - lastItemsToShow;
                    if (skippedCount > 0)
                    {
                        report.Add($"  ... and {skippedCount} more");
                    }
                    
                    // Show last 2 items
                    foreach (var asset in designAssetsList.Skip(designAssetCount - lastItemsToShow))
                    {
                        idx++;
                        InspectSingleDesignAsset(asset, idx, exchangeData, exchangeDataType, report);
                    }
                }
            }
            report.Add("");

            return designAssetCount;
        }

        private static void InspectSingleDesignAsset(object asset, int idx, object exchangeData, Type exchangeDataType, List<string> report)
        {
            var idProp = asset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            var id = idProp?.GetValue(asset)?.ToString() ?? "N/A";

            report.Add($"  DesignAsset #{idx}:");
            report.Add($"    ID: {id}");

            // Get ObjectInfo
            var objectInfoProp = asset.GetType().GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
            if (objectInfoProp != null)
            {
                var objectInfo = objectInfoProp.GetValue(asset);
                if (objectInfo != null)
                {
                    var nameProp = objectInfo.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null)
                    {
                        var name = nameProp.GetValue(objectInfo)?.ToString() ?? "N/A";
                        report.Add($"    Name: {name}");
                    }
                }
            }

            // Get ChildNodes to see relationships
            var childNodesProp = asset.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            if (childNodesProp != null)
            {
                var childNodes = childNodesProp.GetValue(asset) as System.Collections.IEnumerable;
                if (childNodes != null)
                {
                    var childNodesList = childNodes.Cast<object>().ToList();
                    report.Add($"    ChildNodes: {childNodesList.Count}");

                    if (childNodesList.Count > 0)
                    {
                        foreach (var childNode in childNodesList)
                        {
                            // Get the relationship type
                            var nodeProp = childNode.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                            var relationshipProp = childNode.GetType().GetProperty("Relationship", BindingFlags.Public | BindingFlags.Instance);
                            
                            if (nodeProp != null)
                            {
                                var node = nodeProp.GetValue(childNode);
                                if (node != null)
                                {
                                    var nodeIdProp = node.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                    var nodeId = nodeIdProp?.GetValue(node)?.ToString() ?? "N/A";
                                    var nodeType = node.GetType().Name;
                                    
                                    string relationshipType = "Unknown";
                                    if (relationshipProp != null)
                                    {
                                        var relationship = relationshipProp.GetValue(childNode);
                                        if (relationship != null)
                                        {
                                            relationshipType = relationship.GetType().Name;
                                        }
                                    }
                                    
                                    // For TopLevelAssembly, show more details about InstanceAssets and GroupAssets
                                    if (idx == 2 && (nodeType == "InstanceAsset" || nodeType == "GroupAsset"))
                                    {
                                        // Get ObjectInfo to see the name/ID
                                        var nodeObjectInfoProp = node.GetType().GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
                                        if (nodeObjectInfoProp != null)
                                        {
                                            var nodeObjectInfo = nodeObjectInfoProp.GetValue(node);
                                            if (nodeObjectInfo != null)
                                            {
                                                var nodeNameProp = nodeObjectInfo.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                                                if (nodeNameProp != null)
                                                {
                                                    var nodeName = nodeNameProp.GetValue(nodeObjectInfo)?.ToString();
                                                    if (!string.IsNullOrEmpty(nodeName))
                                                    {
                                                        report.Add($"      -> {relationshipType}: {nodeType} (ID: {nodeId}, Name: {nodeName})");
                                                        
                                                        // Check if this InstanceAsset/GroupAsset has child nodes
                                                        var nodeChildNodesProp = node.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
                                                        if (nodeChildNodesProp != null)
                                                        {
                                                            var nodeChildNodes = nodeChildNodesProp.GetValue(node) as System.Collections.IEnumerable;
                                                            if (nodeChildNodes != null)
                                                            {
                                                                var nodeChildNodesList = nodeChildNodes.Cast<object>().ToList();
                                                                if (nodeChildNodesList.Count > 0)
                                                                {
                                                                    report.Add($"        Children: {nodeChildNodesList.Count}");
                                                                    // Show first few children
                                                                    foreach (var nestedChildNodeRel in nodeChildNodesList.Take(5))
                                                                    {
                                                                        var nestedNodeProp = nestedChildNodeRel.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                                                                        if (nestedNodeProp != null)
                                                                        {
                                                                            var nestedNode = nestedNodeProp.GetValue(nestedChildNodeRel);
                                                                            if (nestedNode != null)
                                                                            {
                                                                                var nestedNodeIdProp = nestedNode.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                                                                                var nestedNodeId = nestedNodeIdProp?.GetValue(nestedNode)?.ToString() ?? "N/A";
                                                                                var nestedNodeType = nestedNode.GetType().Name;
                                                                                report.Add($"          -> {nestedNodeType} (ID: {nestedNodeId})");
                                                                            }
                                                                        }
                                                                    }
                                                                    if (nodeChildNodesList.Count > 5)
                                                                    {
                                                                        report.Add($"          ... and {nodeChildNodesList.Count - 5} more");
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        report.Add($"      -> {relationshipType}: {nodeType} (ID: {nodeId})");
                                                    }
                                                }
                                                else
                                                {
                                                    report.Add($"      -> {relationshipType}: {nodeType} (ID: {nodeId})");
                                                }
                                            }
                                            else
                                            {
                                                report.Add($"      -> {relationshipType}: {nodeType} (ID: {nodeId})");
                                            }
                                        }
                                        else
                                        {
                                            report.Add($"      -> {relationshipType}: {nodeType} (ID: {nodeId})");
                                        }
                                    }
                                    else
                                    {
                                        report.Add($"      -> {relationshipType}: {nodeType} (ID: {nodeId})");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void InspectExchangeDataMetadata(object exchangeData, Type exchangeDataType, List<string> report)
        {
            report.Add("=== EXCHANGE DATA METADATA ===");
            
            var exchangeIdProp = exchangeDataType.GetProperty("ExchangeID", BindingFlags.Public | BindingFlags.Instance);
            if (exchangeIdProp != null)
            {
                report.Add($"ExchangeID: {exchangeIdProp.GetValue(exchangeData)}");
            }

            var descriptionProp = exchangeDataType.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance);
            if (descriptionProp != null)
            {
                report.Add($"Description: {descriptionProp.GetValue(exchangeData) ?? "N/A"}");
            }

            var exchangeIdentifierProp = exchangeDataType.GetProperty("ExchangeIdentifier", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (exchangeIdentifierProp != null)
            {
                var identifier = exchangeIdentifierProp.GetValue(exchangeData);
                if (identifier != null)
                {
                    var idType = identifier.GetType();
                    var exchangeIdProp2 = idType.GetProperty("ExchangeId");
                    var collectionIdProp = idType.GetProperty("CollectionId");
                    var hubIdProp = idType.GetProperty("HubId");
                    report.Add($"ExchangeIdentifier - ExchangeId: {exchangeIdProp2?.GetValue(identifier)}, CollectionId: {collectionIdProp?.GetValue(identifier)}, HubId: {hubIdProp?.GetValue(identifier)}");
                }
            }

            report.Add("");
        }

        private static void InspectRootAsset(object exchangeData, Type exchangeDataType, List<string> report)
        {
            report.Add("=== ROOT ASSET ===");
            
            var rootAssetProp = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
            if (rootAssetProp != null)
            {
                var rootAsset = rootAssetProp.GetValue(exchangeData);
                if (rootAsset != null)
                {
                    InspectAssetDetails(rootAsset, "RootAsset", report, 0);
                }
                else
                {
                    report.Add("RootAsset: NULL");
                }
            }
            else
            {
                report.Add("RootAsset property not found");
            }
            
            report.Add("");
        }

        private static void InspectAllAssetTypes(object exchangeData, Type exchangeDataType, List<string> report)
        {
            report.Add("=== ALL ASSET TYPES ===");
            
            var assetTypes = new[]
            {
                "Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset",
                "Autodesk.DataExchange.SchemaObjects.Assets.DesignAsset",
                "Autodesk.DataExchange.SchemaObjects.Assets.InstanceAsset",
                "Autodesk.DataExchange.SchemaObjects.Assets.GroupAsset",
                "Autodesk.DataExchange.SchemaObjects.Assets.RenderStyleAsset",
                "Autodesk.DataExchange.SchemaObjects.Assets.BinaryAsset"
            };

            var getAssetsByTypeMethod = exchangeDataType.GetMethod("GetAssetsByType", BindingFlags.Public | BindingFlags.Instance);
            if (getAssetsByTypeMethod == null)
            {
                report.Add("✗ GetAssetsByType method not found");
                return;
            }

            foreach (var assetTypeName in assetTypes)
            {
                var assetType = exchangeDataType.Assembly.GetType(assetTypeName);
                if (assetType == null)
                {
                    // Try searching all assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.Contains("DataExchange")))
                    {
                        assetType = asm.GetType(assetTypeName);
                        if (assetType != null) break;
                    }
                }

                if (assetType != null)
                {
                    var genericMethod = getAssetsByTypeMethod.MakeGenericMethod(assetType);
                    var assets = genericMethod.Invoke(exchangeData, null) as System.Collections.IEnumerable;
                    
                    if (assets != null)
                    {
                        var assetsList = assets.Cast<object>().ToList();
                        var typeName = assetType.Name;
                        report.Add($"");
                        report.Add($"--- {typeName} ({assetsList.Count}) ---");
                        
                        foreach (var asset in assetsList)
                        {
                            InspectAssetDetails(asset, typeName, report, 1);
                        }
                    }
                }
            }
            
            report.Add("");
        }

        private static void InspectAssetDetails(object asset, string assetTypeName, List<string> report, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 2);
            
            var idProp = asset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            var id = idProp?.GetValue(asset)?.ToString() ?? "N/A";
            report.Add($"{indent}Asset: {assetTypeName} (ID: {id})");

            // Get all properties
            var properties = asset.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                try
                {
                    var value = prop.GetValue(asset);
                    var propName = prop.Name;
                    
                    // Skip complex properties that we'll inspect separately
                    if (propName == "ChildNodes" || propName == "Parent" || propName == "Geometry" || 
                        propName == "BinaryReference" || propName == "ObjectInfo")
                        continue;

                    if (value != null)
                    {
                        var valueStr = value.ToString();
                        if (valueStr.Length > 100)
                            valueStr = valueStr.Substring(0, 100) + "...";
                        report.Add($"{indent}  {propName}: {valueStr}");
                    }
                }
                catch { }
            }

            // Inspect ObjectInfo
            var objectInfoProp = asset.GetType().GetProperty("ObjectInfo", BindingFlags.Public | BindingFlags.Instance);
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
                            report.Add($"{indent}  ObjectInfo.Name: {name}");
                        }
                    }
                }
            }

            // Inspect BinaryReference for GeometryAssets
            if (assetTypeName.Contains("GeometryAsset"))
            {
                var binaryRefProp = asset.GetType().GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance);
                if (binaryRefProp != null)
                {
                    var binaryRef = binaryRefProp.GetValue(asset);
                    if (binaryRef != null)
                    {
                        InspectBinaryReferenceDetailed(binaryRef, report, indentLevel + 1);
                    }
                    else
                    {
                        report.Add($"{indent}  BinaryReference: NULL");
                    }
                }
            }

            // Inspect Geometry for GeometryAssets
            if (assetTypeName.Contains("GeometryAsset"))
            {
                var geometryProp = asset.GetType().GetProperty("Geometry", BindingFlags.Public | BindingFlags.Instance);
                if (geometryProp != null)
                {
                    var geometry = geometryProp.GetValue(asset);
                    if (geometry != null)
                    {
                        InspectGeometryDetailed(geometry, report, indentLevel + 1);
                    }
                    else
                    {
                        report.Add($"{indent}  Geometry: NULL");
                    }
                }
            }

            // Inspect ChildNodes
            var childNodesProp = asset.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
            if (childNodesProp != null)
            {
                var childNodes = childNodesProp.GetValue(asset) as System.Collections.IEnumerable;
                if (childNodes != null)
                {
                    var childNodesList = childNodes.Cast<object>().ToList();
                    if (childNodesList.Count > 0)
                    {
                        report.Add($"{indent}  ChildNodes: {childNodesList.Count}");
                        foreach (var childNodeRel in childNodesList)
                        {
                            InspectRelationship(childNodeRel, report, indentLevel + 2);
                        }
                    }
                }
            }

            // Inspect Parent
            var parentProp = asset.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance);
            if (parentProp != null)
            {
                var parent = parentProp.GetValue(asset);
                if (parent != null)
                {
                    var parentIdProp = parent.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                    var parentId = parentIdProp?.GetValue(parent)?.ToString() ?? "N/A";
                    var parentType = parent.GetType().Name;
                    report.Add($"{indent}  Parent: {parentType} (ID: {parentId})");
                }
            }

            // Inspect Status
            var statusProp = asset.GetType().GetProperty("Status", BindingFlags.Public | BindingFlags.Instance);
            if (statusProp != null)
            {
                var status = statusProp.GetValue(asset);
                if (status != null)
                {
                    var statusStr = status.GetType().IsEnum ? Enum.GetName(status.GetType(), status) : status.ToString();
                    report.Add($"{indent}  Status: {statusStr}");
                }
            }

            report.Add("");
        }

        private static void InspectBinaryReferenceDetailed(object binaryRef, List<string> report, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 2);
            report.Add($"{indent}BinaryReference:");
            
            var properties = binaryRef.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                try
                {
                    var value = prop.GetValue(binaryRef);
                    report.Add($"{indent}  {prop.Name}: {value ?? "NULL"}");
                }
                catch { }
            }
        }

        private static void InspectGeometryDetailed(object geometry, List<string> report, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 2);
            report.Add($"{indent}Geometry:");
            
            var geometryComponentType = geometry.GetType();
            var geometryWrapperProp = geometryComponentType.GetProperty("Geometry", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (geometryWrapperProp == null)
            {
                geometryWrapperProp = geometryComponentType.GetProperty("Geometry", BindingFlags.Public | BindingFlags.Instance);
            }

            if (geometryWrapperProp != null)
            {
                var geometryWrapper = geometryWrapperProp.GetValue(geometry);
                if (geometryWrapper != null)
                {
                    var wrapperType = geometryWrapper.GetType();
                    var typeProp = wrapperType.GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
                    var formatProp = wrapperType.GetProperty("Format", BindingFlags.Public | BindingFlags.Instance);
                    
                    if (typeProp != null)
                    {
                        var typeValue = typeProp.GetValue(geometryWrapper);
                        var typeStr = typeValue?.GetType().IsEnum == true ? Enum.GetName(typeValue.GetType(), typeValue) : typeValue?.ToString();
                        report.Add($"{indent}  Type: {typeStr}");
                    }
                    
                    if (formatProp != null)
                    {
                        var formatValue = formatProp.GetValue(geometryWrapper);
                        var formatStr = formatValue?.GetType().IsEnum == true ? Enum.GetName(formatValue.GetType(), formatValue) : formatValue?.ToString();
                        report.Add($"{indent}  Format: {formatStr}");
                    }
                }
            }
        }

        private static void InspectRelationship(object relationshipNode, List<string> report, int indentLevel)
        {
            var indent = new string(' ', indentLevel * 2);
            
            var nodeProp = relationshipNode.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
            var relationshipProp = relationshipNode.GetType().GetProperty("Relationship", BindingFlags.Public | BindingFlags.Instance);
            
            if (nodeProp != null)
            {
                var node = nodeProp.GetValue(relationshipNode);
                if (node != null)
                {
                    var nodeIdProp = node.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                    var nodeId = nodeIdProp?.GetValue(node)?.ToString() ?? "N/A";
                    var nodeType = node.GetType().Name;
                    
                    string relationshipType = "Unknown";
                    if (relationshipProp != null)
                    {
                        var relationship = relationshipProp.GetValue(relationshipNode);
                        if (relationship != null)
                        {
                            relationshipType = relationship.GetType().Name;
                            
                            // Inspect relationship properties
                            var relProperties = relationship.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                            foreach (var relProp in relProperties)
                            {
                                try
                                {
                                    var relValue = relProp.GetValue(relationship);
                                    if (relValue != null)
                                    {
                                        report.Add($"{indent}  Relationship.{relProp.Name}: {relValue}");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    
                    report.Add($"{indent}-> {relationshipType}: {nodeType} (ID: {nodeId})");
                    
                    // Recursively inspect child nodes
                    var nodeChildNodesProp = node.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
                    if (nodeChildNodesProp != null)
                    {
                        var nodeChildNodes = nodeChildNodesProp.GetValue(node) as System.Collections.IEnumerable;
                        if (nodeChildNodes != null)
                        {
                            var nodeChildNodesList = nodeChildNodes.Cast<object>().ToList();
                            if (nodeChildNodesList.Count > 0)
                            {
                                report.Add($"{indent}  Children: {nodeChildNodesList.Count}");
                                foreach (var childRel in nodeChildNodesList)
                                {
                                    InspectRelationship(childRel, report, indentLevel + 1);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void InspectElementsDetailed(ElementDataModel model, List<string> report)
        {
            report.Add("=== ELEMENTS (DETAILED) ===");
            
            var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
            if (elementsProperty == null)
            {
                report.Add("Elements property not found");
                return;
            }

            var elements = elementsProperty.GetValue(model) as System.Collections.IEnumerable;
            if (elements == null)
            {
                report.Add("No elements found");
                report.Add("");
                return;
            }

            var elementsList = elements.Cast<object>().ToList();
            report.Add($"Total Elements: {elementsList.Count}");
            report.Add("");

            var idx = 0;
            foreach (var element in elementsList)
            {
                idx++;
                var indent = "  ";
                report.Add($"{indent}Element #{idx}:");
                
                var properties = element.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in properties)
                {
                    try
                    {
                        var value = prop.GetValue(element);
                        if (value != null)
                        {
                            var valueStr = value.ToString();
                            if (valueStr.Length > 200)
                                valueStr = valueStr.Substring(0, 200) + "...";
                            report.Add($"{indent}  {prop.Name}: {valueStr}");
                        }
                    }
                    catch { }
                }
                
                report.Add("");
            }
        }

        private static void InspectAllRelationships(object exchangeData, Type exchangeDataType, List<string> report)
        {
            report.Add("=== ALL RELATIONSHIPS ===");
            
            // Get all assets and inspect their relationships
            var getAssetsByTypeMethod = exchangeDataType.GetMethod("GetAssetsByType", BindingFlags.Public | BindingFlags.Instance);
            if (getAssetsByTypeMethod == null)
            {
                report.Add("GetAssetsByType method not found");
                return;
            }

            var baseAssetType = exchangeDataType.Assembly.GetType("Autodesk.DataExchange.SchemaObjects.Assets.Asset");
            if (baseAssetType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.Contains("DataExchange")))
                {
                    baseAssetType = asm.GetType("Autodesk.DataExchange.SchemaObjects.Assets.Asset");
                    if (baseAssetType != null) break;
                }
            }

            if (baseAssetType != null)
            {
                var genericMethod = getAssetsByTypeMethod.MakeGenericMethod(baseAssetType);
                var allAssets = genericMethod.Invoke(exchangeData, null) as System.Collections.IEnumerable;
                
                if (allAssets != null)
                {
                    var assetsList = allAssets.Cast<object>().ToList();
                    report.Add($"Total Assets: {assetsList.Count}");
                    
                    var relationshipCount = 0;
                    foreach (var asset in assetsList)
                    {
                        var childNodesProp = asset.GetType().GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
                        if (childNodesProp != null)
                        {
                            var childNodes = childNodesProp.GetValue(asset) as System.Collections.IEnumerable;
                            if (childNodes != null)
                            {
                                var childNodesList = childNodes.Cast<object>().ToList();
                                relationshipCount += childNodesList.Count;
                            }
                        }
                    }
                    
                    report.Add($"Total Relationships: {relationshipCount}");
                }
            }
            
            report.Add("");
        }

        private static void InspectGeometryMappings(object exchangeData, Type exchangeDataType, List<string> report)
        {
            report.Add("=== GEOMETRY MAPPINGS ===");
            
            var unsavedGeometryMappingProp = exchangeDataType.GetProperty("UnsavedGeometryMapping", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (unsavedGeometryMappingProp != null)
            {
                var mapping = unsavedGeometryMappingProp.GetValue(exchangeData);
                if (mapping != null)
                {
                    var mappingDict = mapping as System.Collections.IDictionary;
                    if (mappingDict != null)
                    {
                        report.Add($"UnsavedGeometryMapping: {mappingDict.Count} entries");
                        var idx = 0;
                        foreach (System.Collections.DictionaryEntry entry in mappingDict)
                        {
                            idx++;
                            report.Add($"  [{idx}] Asset ID: {entry.Key}, Path: {entry.Value}");
                        }
                    }
                }
                else
                {
                    report.Add("UnsavedGeometryMapping: NULL or empty");
                }
            }

            var unsavedMeshGeometryMappingProp = exchangeDataType.GetProperty("UnsavedMeshGeometryMapping", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (unsavedMeshGeometryMappingProp != null)
            {
                var mapping = unsavedMeshGeometryMappingProp.GetValue(exchangeData);
                if (mapping != null)
                {
                    var mappingDict = mapping as System.Collections.IDictionary;
                    if (mappingDict != null)
                    {
                        report.Add($"UnsavedMeshGeometryMapping: {mappingDict.Count} entries");
                    }
                }
            }

            var unsavedCustomGeometryMappingProp = exchangeDataType.GetProperty("UnsavedCustomGeometryMapping", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (unsavedCustomGeometryMappingProp != null)
            {
                var mapping = unsavedCustomGeometryMappingProp.GetValue(exchangeData);
                if (mapping != null)
                {
                    var mappingDict = mapping as System.Collections.IDictionary;
                    if (mappingDict != null)
                    {
                        report.Add($"UnsavedCustomGeometryMapping: {mappingDict.Count} entries");
                    }
                }
            }
            
            report.Add("");
        }

        private static void InspectMetadataAndProperties(object exchangeData, Type exchangeDataType, List<string> report)
        {
            report.Add("=== METADATA AND PROPERTIES ===");
            
            // Get all properties of ExchangeData
            var properties = exchangeDataType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            report.Add($"ExchangeData Properties: {properties.Length}");
            
            foreach (var prop in properties)
            {
                try
                {
                    // Skip complex properties we've already inspected
                    if (prop.Name == "RootAsset" || prop.Name == "UnsavedGeometryMapping" || 
                        prop.Name == "UnsavedMeshGeometryMapping" || prop.Name == "UnsavedCustomGeometryMapping" ||
                        prop.Name == "ExchangeIdentifier")
                        continue;

                    var value = prop.GetValue(exchangeData);
                    if (value != null)
                    {
                        var valueType = value.GetType();
                        if (valueType.IsPrimitive || valueType == typeof(string) || valueType.IsEnum)
                        {
                            report.Add($"  {prop.Name}: {value}");
                        }
                        else if (value is System.Collections.ICollection collection)
                        {
                            report.Add($"  {prop.Name}: Collection with {collection.Count} items");
                        }
                        else
                        {
                            report.Add($"  {prop.Name}: {valueType.Name} (complex type)");
                        }
                    }
                }
                catch { }
            }
            
            report.Add("");
        }

        /// <summary>
        /// Collects all custom parameters (properties) from all elements in the ElementDataModel
        /// Returns a dictionary where keys are "ElementId.ParameterName" and values are the parameter values
        /// </summary>
        private static Dictionary<string, object> CollectCustomParameters(ElementDataModel model, List<string> report)
        {
            var customParameters = new Dictionary<string, object>();
            
            try
            {
                var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
                if (elementsProperty == null)
                {
                    return customParameters;
                }

                var elements = elementsProperty.GetValue(model) as System.Collections.IEnumerable;
                if (elements == null)
                {
                    return customParameters;
                }

                var elementsList = elements.Cast<object>().ToList();
                
                foreach (var element in elementsList)
                {
                    try
                    {
                        // Get element ID
                        var elementIdProp = element.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                        var elementId = elementIdProp?.GetValue(element)?.ToString() ?? "Unknown";
                        
                        // Get element name
                        var elementNameProp = element.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        var elementName = elementNameProp?.GetValue(element)?.ToString() ?? "Unknown";
                        
                        // Get all properties of the element
                        var properties = element.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                        
                        foreach (var prop in properties)
                        {
                            try
                            {
                                // Skip standard properties that aren't custom parameters
                                if (prop.Name == "Id" || prop.Name == "Name" || prop.Name == "Properties" || 
                                    prop.Name == "Asset" || prop.Name == "Parent" || prop.Name == "ChildNodes")
                                    continue;

                                var value = prop.GetValue(element);
                                
                                // Only include properties with non-null values
                                if (value != null)
                                {
                                    // Create a key like "ElementId.ParameterName" or "ElementName.ParameterName"
                                    var key = $"{elementId}.{prop.Name}";
                                    
                                    // Convert value to a simple type if possible
                                    object paramValue = value;
                                    
                                    // Handle complex types by converting to string
                                    if (!(value is string) && !value.GetType().IsPrimitive && 
                                        value.GetType() != typeof(decimal) && value.GetType() != typeof(DateTime) &&
                                        !value.GetType().IsEnum)
                                    {
                                        // For complex types, convert to string representation
                                        paramValue = value.ToString();
                                    }
                                    
                                    customParameters[key] = paramValue;
                                    
                                    // Also add a version with element name for easier lookup
                                    if (!string.IsNullOrEmpty(elementName) && elementName != "Unknown")
                                    {
                                        var nameKey = $"{elementName}.{prop.Name}";
                                        if (!customParameters.ContainsKey(nameKey))
                                        {
                                            customParameters[nameKey] = paramValue;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                // Skip properties that can't be read
                                report?.Add($"  ⚠️ Could not read property {prop.Name}: {ex.Message}");
                            }
                        }
                        
                        // Also check if there's a Properties collection (ElementProperties)
                        var propertiesCollectionProp = element.GetType().GetProperty("Properties", BindingFlags.Public | BindingFlags.Instance);
                        if (propertiesCollectionProp != null)
                        {
                            var propertiesCollection = propertiesCollectionProp.GetValue(element);
                            if (propertiesCollection != null)
                            {
                                // Try to enumerate the properties collection
                                if (propertiesCollection is System.Collections.IEnumerable propsEnum)
                                {
                                    foreach (var propItem in propsEnum)
                                    {
                                        try
                                        {
                                            // Try to get Name and Value properties from the property item
                                            var propNameProp = propItem.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                                            var propValueProp = propItem.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                            
                                            if (propNameProp != null && propValueProp != null)
                                            {
                                                var propName = propNameProp.GetValue(propItem)?.ToString();
                                                var propValue = propValueProp.GetValue(propItem);
                                                
                                                if (!string.IsNullOrEmpty(propName) && propValue != null)
                                                {
                                                    var key = $"{elementId}.{propName}";
                                                    customParameters[key] = propValue;
                                                    
                                                    if (!string.IsNullOrEmpty(elementName) && elementName != "Unknown")
                                                    {
                                                        var nameKey = $"{elementName}.{propName}";
                                                        if (!customParameters.ContainsKey(nameKey))
                                                        {
                                                            customParameters[nameKey] = propValue;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        report?.Add($"  ⚠️ Could not process element: {ex.Message}");
                    }
                }
                
                report?.Add($"✓ Collected {customParameters.Count} custom parameter(s) from {elementsList.Count} element(s)");
            }
            catch (Exception ex)
            {
                report?.Add($"  ⚠️ Error collecting custom parameters: {ex.Message}");
            }
            
            return customParameters;
        }
    }
}
