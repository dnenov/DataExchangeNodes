using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.DesignScript.Runtime;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Represents a node in the DataExchange tree structure.
    /// This class provides a clean, serializable representation of the exchange hierarchy
    /// that "glues" geometries to their names/IDs.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    public class ExchangeTreeNode
    {
        /// <summary>
        /// Unique identifier for this node (GUID from Asset.Id or Element.Id)
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Human-readable name for this node
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Parent node's ID (null for root nodes)
        /// </summary>
        public string ParentId { get; set; }

        /// <summary>
        /// Type of asset: "Element", "DesignAsset", "InstanceAsset", "GeometryAsset", "GroupAsset", "TopLevelAssembly"
        /// </summary>
        public string AssetType { get; set; }

        /// <summary>
        /// Whether this node has geometry data attached
        /// </summary>
        public bool HasGeometry { get; set; }

        /// <summary>
        /// IDs of child nodes
        /// </summary>
        public List<string> ChildIds { get; set; }

        /// <summary>
        /// Additional properties (category, discipline, etc.)
        /// </summary>
        public Dictionary<string, string> Properties { get; set; }

        /// <summary>
        /// Depth level in the tree (0 = root)
        /// </summary>
        public int Depth { get; set; }

        /// <summary>
        /// Path from root to this node (e.g., "Root/Assembly1/Part1")
        /// </summary>
        public string TreePath { get; set; }

        /// <summary>
        /// Creates an empty ExchangeTreeNode
        /// </summary>
        public ExchangeTreeNode()
        {
            ChildIds = new List<string>();
            Properties = new Dictionary<string, string>();
        }

        /// <summary>
        /// Creates an ExchangeTreeNode with required properties
        /// </summary>
        public ExchangeTreeNode(string id, string name, string assetType, string parentId = null)
        {
            Id = id;
            Name = name;
            AssetType = assetType;
            ParentId = parentId;
            ChildIds = new List<string>();
            Properties = new Dictionary<string, string>();
        }

        /// <summary>
        /// Returns a string representation of this node
        /// </summary>
        public override string ToString()
        {
            var geoIndicator = HasGeometry ? " [G]" : "";
            return $"{Name} ({AssetType}){geoIndicator}";
        }
    }

    /// <summary>
    /// Represents the complete tree structure of a DataExchange.
    /// Contains all nodes and provides methods for traversal and filtering.
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    public class ExchangeTree
    {
        /// <summary>
        /// All nodes in the tree, keyed by ID
        /// </summary>
        public Dictionary<string, ExchangeTreeNode> Nodes { get; set; }

        /// <summary>
        /// ID of the root node (TopLevelAssembly)
        /// </summary>
        public string RootId { get; set; }

        /// <summary>
        /// Total number of elements in the exchange
        /// </summary>
        public int ElementCount { get; set; }

        /// <summary>
        /// Total number of geometry assets in the exchange
        /// </summary>
        public int GeometryCount { get; set; }

        /// <summary>
        /// Exchange identifier
        /// </summary>
        public string ExchangeId { get; set; }

        /// <summary>
        /// Exchange title/name
        /// </summary>
        public string ExchangeTitle { get; set; }

        /// <summary>
        /// Creates an empty ExchangeTree
        /// </summary>
        public ExchangeTree()
        {
            Nodes = new Dictionary<string, ExchangeTreeNode>();
        }

        /// <summary>
        /// Gets the root node of the tree
        /// </summary>
        public ExchangeTreeNode GetRoot()
        {
            if (string.IsNullOrEmpty(RootId) || !Nodes.ContainsKey(RootId))
                return null;
            return Nodes[RootId];
        }

        /// <summary>
        /// Gets all children of a node
        /// </summary>
        public List<ExchangeTreeNode> GetChildren(string nodeId)
        {
            var children = new List<ExchangeTreeNode>();
            if (!Nodes.ContainsKey(nodeId))
                return children;

            var node = Nodes[nodeId];
            foreach (var childId in node.ChildIds)
            {
                if (Nodes.ContainsKey(childId))
                {
                    children.Add(Nodes[childId]);
                }
            }
            return children;
        }

        /// <summary>
        /// Gets all nodes with geometry
        /// </summary>
        public List<ExchangeTreeNode> GetGeometryNodes()
        {
            var geometryNodes = new List<ExchangeTreeNode>();
            foreach (var node in Nodes.Values)
            {
                if (node.HasGeometry)
                {
                    geometryNodes.Add(node);
                }
            }
            return geometryNodes;
        }

        /// <summary>
        /// Finds nodes by name (case-insensitive)
        /// </summary>
        public List<ExchangeTreeNode> FindByName(string name)
        {
            var matches = new List<ExchangeTreeNode>();
            foreach (var node in Nodes.Values)
            {
                if (node.Name != null && node.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(node);
                }
            }
            return matches;
        }

        /// <summary>
        /// Gets all element nodes
        /// </summary>
        public List<ExchangeTreeNode> GetElements()
        {
            var elements = new List<ExchangeTreeNode>();
            foreach (var node in Nodes.Values)
            {
                if (node.AssetType == "Element")
                {
                    elements.Add(node);
                }
            }
            return elements;
        }

        /// <summary>
        /// Builds a flat list representation of the tree for display
        /// </summary>
        public List<string> ToDisplayList()
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(RootId) || !Nodes.ContainsKey(RootId))
                return lines;

            BuildDisplayListRecursive(RootId, 0, lines);
            return lines;
        }

        private void BuildDisplayListRecursive(string nodeId, int depth, List<string> lines)
        {
            if (!Nodes.ContainsKey(nodeId))
                return;

            var node = Nodes[nodeId];
            var indent = new string(' ', depth * 2);
            var geoIndicator = node.HasGeometry ? " [G]" : "";
            lines.Add($"{indent}{node.Name} ({node.AssetType}){geoIndicator}");

            foreach (var childId in node.ChildIds)
            {
                BuildDisplayListRecursive(childId, depth + 1, lines);
            }
        }

        /// <summary>
        /// Returns a string representation of the tree
        /// </summary>
        public override string ToString()
        {
            return $"ExchangeTree: {ExchangeTitle} ({ElementCount} elements, {GeometryCount} geometries)";
        }

        /// <summary>
        /// Resolves a list of element/asset names to geometry asset IDs.
        /// For each name match:
        /// - If the matched node has geometry, its ID is included
        /// - If it doesn't have geometry, its children are searched recursively
        /// </summary>
        /// <param name="names">List of names to search for (case-insensitive)</param>
        /// <returns>Tuple of (geometry IDs list, matched names list)</returns>
        public (List<string> geometryIds, List<string> matchedNames) ResolveGeometryIds(List<string> names)
        {
            var geometryIds = new HashSet<string>();
            var matchedNames = new List<string>();

            if (names == null || names.Count == 0)
                return (new List<string>(), matchedNames);

            var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

            foreach (var node in Nodes.Values)
            {
                if (node.Name != null && nameSet.Contains(node.Name))
                {
                    if (!matchedNames.Contains(node.Name))
                        matchedNames.Add(node.Name);

                    CollectGeometryIds(node, geometryIds);
                }
            }

            return (geometryIds.ToList(), matchedNames);
        }

        /// <summary>
        /// Recursively collects geometry IDs from a node and its descendants.
        /// If the node has geometry, its ID is added.
        /// If not, child nodes are searched recursively.
        /// </summary>
        private void CollectGeometryIds(ExchangeTreeNode node, HashSet<string> geometryIds)
        {
            if (node == null) return;

            if (node.HasGeometry)
            {
                geometryIds.Add(node.Id);
            }

            // Also search children for additional geometry
            foreach (var childId in node.ChildIds)
            {
                if (Nodes.TryGetValue(childId, out var childNode))
                {
                    CollectGeometryIds(childNode, geometryIds);
                }
            }
        }
    }
}
