using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange.DataModels;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Gets the metadata/tree structure of a DataExchange without downloading geometry.
    /// </summary>
    public static class GetExchangeStructure
    {
        /// <summary>
        /// Gets the tree structure of a DataExchange showing all elements and their hierarchy.
        /// This only fetches metadata - no geometry is downloaded.
        /// </summary>
        /// <param name="exchange">The Exchange object to inspect</param>
        /// <param name="revisionId">Optional revision ID. Leave empty for latest revision.</param>
        /// <returns>Dictionary with "tree" (ExchangeTree object), "displayList" (flat text representation), "elementCount", "geometryCount", "log", and "success"</returns>
        [MultiReturn(new[] { "tree", "displayList", "elementCount", "geometryCount", "log", "success" })]
        public static Dictionary<string, object> GetStructure(
            Exchange exchange,
            string revisionId = "")
        {
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            bool success = false;
            ExchangeTree tree = null;
            List<string> displayList = new List<string>();
            int elementCount = 0;
            int geometryCount = 0;

            try
            {
                if (exchange == null)
                {
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");
                }

                var identifier = DataExchangeUtils.CreateIdentifier(exchange);

                // Get ElementDataModel (read-only, no creation)
                ElementDataModel elementDataModel;
                if (string.IsNullOrEmpty(revisionId))
                {
                    elementDataModel = SMBExportHelper.CheckElementDataModel(identifier, log);
                }
                else
                {
                    elementDataModel = SMBExportHelper.CheckElementDataModel(identifier, revisionId, log);
                }

                if (elementDataModel == null)
                {
                    log.Info("Exchange has no ElementDataModel (empty exchange)");
                    tree = new ExchangeTree
                    {
                        ExchangeId = exchange.ExchangeId,
                        ExchangeTitle = exchange.ExchangeTitle,
                        ElementCount = 0,
                        GeometryCount = 0
                    };
                    success = true;
                    return BuildResult(tree, displayList, elementCount, geometryCount, log, success);
                }

                // Build tree from ElementDataModel
                tree = BuildTreeFromElementDataModel(elementDataModel, exchange, log);
                elementCount = tree.ElementCount;
                geometryCount = tree.GeometryCount;
                displayList = tree.ToDisplayList();

                success = true;
                log.Info($"Retrieved structure: {elementCount} elements, {geometryCount} geometries");
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return BuildResult(tree, displayList, elementCount, geometryCount, log, success);
        }

        /// <summary>
        /// Gets just the list of element names from a DataExchange.
        /// Useful for quick inspection or filtering.
        /// </summary>
        /// <param name="exchange">The Exchange object to inspect</param>
        /// <returns>Dictionary with "elementNames" (list of names), "elementIds" (list of IDs), "log", and "success"</returns>
        [MultiReturn(new[] { "elementNames", "elementIds", "log", "success" })]
        public static Dictionary<string, object> GetElementList(Exchange exchange)
        {
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            bool success = false;
            var elementNames = new List<string>();
            var elementIds = new List<string>();

            try
            {
                if (exchange == null)
                {
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");
                }

                var identifier = DataExchangeUtils.CreateIdentifier(exchange);
                var elementDataModel = SMBExportHelper.CheckElementDataModel(identifier, log);

                if (elementDataModel == null)
                {
                    log.Info("Exchange has no ElementDataModel (empty exchange)");
                    success = true;
                    return new Dictionary<string, object>
                    {
                        { "elementNames", elementNames },
                        { "elementIds", elementIds },
                        { "log", log.GetLog() },
                        { "success", success }
                    };
                }

                // Get Elements property
                var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
                if (elementsProperty != null)
                {
                    var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                    if (elements != null)
                    {
                        foreach (var element in elements.Cast<object>())
                        {
                            var nameProp = element.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                            var idProp = element.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

                            var name = nameProp?.GetValue(element)?.ToString() ?? "Unknown";
                            var id = idProp?.GetValue(element)?.ToString() ?? "";

                            elementNames.Add(name);
                            elementIds.Add(id);
                        }
                    }
                }

                success = true;
                log.Info($"Found {elementNames.Count} element(s)");
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new Dictionary<string, object>
            {
                { "elementNames", elementNames },
                { "elementIds", elementIds },
                { "log", log.GetLog() },
                { "success", success }
            };
        }

        private static Dictionary<string, object> BuildResult(
            ExchangeTree tree,
            List<string> displayList,
            int elementCount,
            int geometryCount,
            DiagnosticsLogger log,
            bool success)
        {
            return new Dictionary<string, object>
            {
                { "tree", tree },
                { "displayList", displayList },
                { "elementCount", elementCount },
                { "geometryCount", geometryCount },
                { "log", log.GetLog() },
                { "success", success }
            };
        }

        /// <summary>
        /// Builds an ExchangeTree from an ElementDataModel
        /// </summary>
        private static ExchangeTree BuildTreeFromElementDataModel(
            ElementDataModel elementDataModel,
            Exchange exchange,
            DiagnosticsLogger log)
        {
            var tree = new ExchangeTree
            {
                ExchangeId = exchange.ExchangeId,
                ExchangeTitle = exchange.ExchangeTitle
            };

            try
            {
                // Get ExchangeData via reflection
                var exchangeDataField = typeof(ElementDataModel).GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (exchangeDataField == null)
                {
                    log.Error("Could not find exchangeData field");
                    return tree;
                }

                var exchangeData = exchangeDataField.GetValue(elementDataModel);
                if (exchangeData == null)
                {
                    log.Info("ExchangeData is null");
                    return tree;
                }

                var exchangeDataType = exchangeData.GetType();

                // Get RootAsset (TopLevelAssembly)
                var rootAssetProp = exchangeDataType.GetProperty("RootAsset", BindingFlags.Public | BindingFlags.Instance);
                if (rootAssetProp != null)
                {
                    var rootAsset = rootAssetProp.GetValue(exchangeData);
                    if (rootAsset != null)
                    {
                        var rootNode = BuildNodeFromAsset(rootAsset, null, 0, tree, log);
                        if (rootNode != null)
                        {
                            tree.RootId = rootNode.Id;
                        }
                    }
                }

                // Get Elements
                var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
                if (elementsProperty != null)
                {
                    var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                    if (elements != null)
                    {
                        foreach (var element in elements.Cast<object>())
                        {
                            BuildNodeFromElement(element, tree, log);
                            tree.ElementCount++;
                        }
                    }
                }

                // Count geometry nodes
                tree.GeometryCount = tree.Nodes.Values.Count(n => n.HasGeometry);
            }
            catch (Exception ex)
            {
                log.Error($"Error building tree: {ex.Message}");
            }

            return tree;
        }

        /// <summary>
        /// Builds an ExchangeTreeNode from an Asset object
        /// </summary>
        private static ExchangeTreeNode BuildNodeFromAsset(
            object asset,
            string parentId,
            int depth,
            ExchangeTree tree,
            DiagnosticsLogger log)
        {
            if (asset == null) return null;

            try
            {
                var assetType = asset.GetType();
                var assetTypeName = assetType.Name;

                // Get ID
                string id = null;
                var idProp = assetType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                if (idProp != null)
                {
                    id = idProp.GetValue(asset)?.ToString();
                }

                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString();
                }

                // Skip if already in tree
                if (tree.Nodes.ContainsKey(id))
                {
                    return tree.Nodes[id];
                }

                // Get Name (from ObjectInfo.Name or direct Name property)
                string name = GetAssetName(asset, assetType) ?? assetTypeName;

                // Check if has geometry
                bool hasGeometry = assetTypeName == "GeometryAsset" ||
                                   HasProperty(asset, "BinaryReference") ||
                                   HasProperty(asset, "Geometry");

                var node = new ExchangeTreeNode
                {
                    Id = id,
                    Name = name,
                    ParentId = parentId,
                    AssetType = assetTypeName,
                    HasGeometry = hasGeometry,
                    Depth = depth
                };

                tree.Nodes[id] = node;

                // Process children via ChildNodes property
                var childNodesProp = assetType.GetProperty("ChildNodes", BindingFlags.Public | BindingFlags.Instance);
                if (childNodesProp != null)
                {
                    var childNodes = childNodesProp.GetValue(asset) as System.Collections.IEnumerable;
                    if (childNodes != null)
                    {
                        foreach (var childRelation in childNodes.Cast<object>())
                        {
                            // Get the actual child node from the relationship
                            var nodeProp = childRelation.GetType().GetProperty("Node", BindingFlags.Public | BindingFlags.Instance);
                            if (nodeProp != null)
                            {
                                var childAsset = nodeProp.GetValue(childRelation);
                                if (childAsset != null)
                                {
                                    var childNode = BuildNodeFromAsset(childAsset, id, depth + 1, tree, log);
                                    if (childNode != null)
                                    {
                                        node.ChildIds.Add(childNode.Id);
                                    }
                                }
                            }
                        }
                    }
                }

                return node;
            }
            catch (Exception ex)
            {
                log.Error($"Error building node from asset: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Builds an ExchangeTreeNode from an Element object
        /// </summary>
        private static void BuildNodeFromElement(object element, ExchangeTree tree, DiagnosticsLogger log)
        {
            if (element == null) return;

            try
            {
                var elementType = element.GetType();

                // Get ID
                var idProp = elementType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                var id = idProp?.GetValue(element)?.ToString();

                if (string.IsNullOrEmpty(id))
                {
                    id = Guid.NewGuid().ToString();
                }

                // Get Name
                var nameProp = elementType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                var name = nameProp?.GetValue(element)?.ToString() ?? "Unnamed Element";

                // Check if has geometry
                var hasGeometryProp = elementType.GetProperty("HasGeometry", BindingFlags.Public | BindingFlags.Instance);
                var hasGeometry = hasGeometryProp != null && (bool)(hasGeometryProp.GetValue(element) ?? false);

                // Get Category
                var categoryProp = elementType.GetProperty("Category", BindingFlags.Public | BindingFlags.Instance);
                var category = categoryProp?.GetValue(element)?.ToString() ?? "";

                var node = new ExchangeTreeNode
                {
                    Id = id,
                    Name = name,
                    AssetType = "Element",
                    HasGeometry = hasGeometry,
                    Depth = 1
                };

                if (!string.IsNullOrEmpty(category))
                {
                    node.Properties["Category"] = category;
                }

                // Get Asset reference
                var assetProp = elementType.GetProperty("Asset", BindingFlags.Public | BindingFlags.Instance);
                if (assetProp != null)
                {
                    var asset = assetProp.GetValue(element);
                    if (asset != null)
                    {
                        var assetIdProp = asset.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                        var assetId = assetIdProp?.GetValue(asset)?.ToString();
                        if (!string.IsNullOrEmpty(assetId))
                        {
                            node.Properties["AssetId"] = assetId;
                        }
                    }
                }

                tree.Nodes[id] = node;
            }
            catch (Exception ex)
            {
                log.Error($"Error building node from element: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the name of an asset, trying ObjectInfo.Name first, then direct Name property
        /// </summary>
        private static string GetAssetName(object asset, Type assetType)
        {
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
                var name = directNameProp.GetValue(asset)?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if an object has a specific property
        /// </summary>
        private static bool HasProperty(object obj, string propertyName)
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                var value = prop.GetValue(obj);
                return value != null;
            }
            return false;
        }
    }
}
