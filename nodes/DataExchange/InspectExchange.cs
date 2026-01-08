using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.DesignScript.Runtime;
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
        /// Inspects an Exchange and returns detailed information about its contents
        /// </summary>
        [MultiReturn(new[] { "report", "elementCount", "geometryAssetCount", "designAssetCount", "success" })]
        public static Dictionary<string, object> Inspect(
            Exchange exchange,
            bool includeDetails = true)
        {
            var report = new List<string>();
            var elementCount = 0;
            var geometryAssetCount = 0;
            var designAssetCount = 0;
            var success = false;

            try
            {
                report.Add("=== Exchange Inspection ===");
                report.Add($"Exchange: {exchange?.ExchangeTitle ?? "N/A"} (ID: {exchange?.ExchangeId ?? "N/A"})");
                report.Add($"Collection ID: {exchange?.CollectionId ?? "N/A"}");
                report.Add("");

                if (exchange == null)
                {
                    return CreateErrorResult(report, "Exchange is null");
                }

                // Get Client instance
                var client = TryGetClientInstance(report);
                if (client == null)
                {
                    return CreateErrorResult(report, "Could not get Client instance. Make sure you have selected an Exchange first.");
                }

                var clientType = client.GetType();

                // Create DataExchangeIdentifier
                var identifier = CreateDataExchangeIdentifier(exchange);

                // Get ElementDataModel and ExchangeData
                var (model, exchangeData, exchangeDataType) = GetElementDataModelAndExchangeData(client, clientType, identifier, report);
                if (model == null)
                {
                    return CreateErrorResult(report, "Could not load ElementDataModel");
                }

                report.Add("✓ Successfully loaded ElementDataModel");
                report.Add("");

                // Inspect Elements
                elementCount = InspectElements(model, report, includeDetails);

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
                { "success", success }
            };
        }

        private static Dictionary<string, object> CreateErrorResult(List<string> report, string errorMessage)
        {
            report.Add($"✗ ERROR: {errorMessage}");
            return new Dictionary<string, object>
            {
                { "report", string.Join("\n", report) },
                { "elementCount", 0 },
                { "geometryAssetCount", 0 },
                { "designAssetCount", 0 },
                { "success", false }
            };
        }

        private static object TryGetClientInstance(List<string> report)
        {
            try
            {
                var viewCustomizationType = Type.GetType("DataExchangeNodes.NodeViews.DataExchange.SelectExchangeElementsViewCustomization, ExchangeNodes.NodeViews");
                if (viewCustomizationType != null)
                {
                    var clientInstanceProp = viewCustomizationType.GetProperty("ClientInstance", BindingFlags.Public | BindingFlags.Static);
                    if (clientInstanceProp != null)
                    {
                        var client = clientInstanceProp.GetValue(null);
                        if (client != null)
                        {
                            return client;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report?.Add($"⚠️ Could not get Client via SelectExchangeElementsViewCustomization: {ex.Message}");
            }
            return null;
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

        private static (ElementDataModel model, object exchangeData, Type exchangeDataType) GetElementDataModelAndExchangeData(
            object client, Type clientType, DataExchangeIdentifier identifier, List<string> report)
        {
            var getElementDataModelMethod = clientType.GetMethod("GetElementDataModelAsync",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(DataExchangeIdentifier), typeof(CancellationToken) },
                null);

            if (getElementDataModelMethod == null)
            {
                report.Add("✗ ERROR: Could not find GetElementDataModelAsync method");
                return (null, null, null);
            }

            var elementDataModelTask = getElementDataModelMethod.Invoke(client, new object[] { identifier, CancellationToken.None });
            var elementDataModel = ((dynamic)elementDataModelTask).GetAwaiter().GetResult();

            if (elementDataModel == null)
            {
                report.Add("✗ ERROR: GetElementDataModelAsync returned null");
                return (null, null, null);
            }

            // Check if result is IResponse<ElementDataModel>
            ElementDataModel model = null;
            var responseType = elementDataModel.GetType();
            var valueProp = responseType.GetProperty("Value");
            if (valueProp != null)
            {
                model = valueProp.GetValue(elementDataModel) as ElementDataModel;
            }
            else
            {
                model = elementDataModel as ElementDataModel;
            }

            if (model == null)
            {
                report.Add("✗ ERROR: Could not extract ElementDataModel from response");
                return (null, null, null);
            }

            // Get ExchangeData
            var exchangeDataField = typeof(ElementDataModel).GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
            if (exchangeDataField == null)
            {
                report.Add("✗ ERROR: Could not find exchangeData field");
                return (null, null, null);
            }

            var exchangeData = exchangeDataField.GetValue(model);
            var exchangeDataType = exchangeData.GetType();
            return (model, exchangeData, exchangeDataType);
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
    }
}
