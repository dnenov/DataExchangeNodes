using DataExchangeNodes.DataExchange;
using FluentAssertions;
using NUnit.Framework;

namespace DataExchangeNodes.Tests
{
    [TestFixture]
    public class ExchangeTreeNodeTests
    {
        #region Constructor Tests

        [Test]
        public void DefaultConstructor_InitializesEmptyCollections()
        {
            // Act
            var node = new ExchangeTreeNode();

            // Assert
            node.ChildIds.Should().NotBeNull();
            node.ChildIds.Should().BeEmpty();
            node.Properties.Should().NotBeNull();
            node.Properties.Should().BeEmpty();
        }

        [Test]
        public void DefaultConstructor_PropertiesAreNull()
        {
            // Act
            var node = new ExchangeTreeNode();

            // Assert
            node.Id.Should().BeNull();
            node.Name.Should().BeNull();
            node.AssetType.Should().BeNull();
            node.ParentId.Should().BeNull();
            node.TreePath.Should().BeNull();
            node.HasGeometry.Should().BeFalse();
            node.Depth.Should().Be(0);
        }

        [Test]
        public void ParameterizedConstructor_SetsProvidedValues()
        {
            // Act
            var node = new ExchangeTreeNode("node-123", "TestNode", "Element", "parent-456");

            // Assert
            node.Id.Should().Be("node-123");
            node.Name.Should().Be("TestNode");
            node.AssetType.Should().Be("Element");
            node.ParentId.Should().Be("parent-456");
        }

        [Test]
        public void ParameterizedConstructor_WithNullParent_SetsParentIdToNull()
        {
            // Act
            var node = new ExchangeTreeNode("node-123", "TestNode", "Element");

            // Assert
            node.ParentId.Should().BeNull();
        }

        [Test]
        public void ParameterizedConstructor_InitializesEmptyCollections()
        {
            // Act
            var node = new ExchangeTreeNode("node-123", "TestNode", "Element");

            // Assert
            node.ChildIds.Should().NotBeNull();
            node.ChildIds.Should().BeEmpty();
            node.Properties.Should().NotBeNull();
            node.Properties.Should().BeEmpty();
        }

        #endregion

        #region ToString Tests

        [Test]
        public void ToString_WithoutGeometry_ReturnsNameAndType()
        {
            // Arrange
            var node = new ExchangeTreeNode("id", "MyPart", "Element")
            {
                HasGeometry = false
            };

            // Act
            var result = node.ToString();

            // Assert
            result.Should().Be("MyPart (Element)");
        }

        [Test]
        public void ToString_WithGeometry_IncludesGeometryIndicator()
        {
            // Arrange
            var node = new ExchangeTreeNode("id", "MyPart", "GeometryAsset")
            {
                HasGeometry = true
            };

            // Act
            var result = node.ToString();

            // Assert
            result.Should().Be("MyPart (GeometryAsset) [G]");
        }

        [Test]
        public void ToString_WithNullName_HandlesGracefully()
        {
            // Arrange
            var node = new ExchangeTreeNode
            {
                Name = null,
                AssetType = "Element"
            };

            // Act
            var result = node.ToString();

            // Assert
            result.Should().Be(" (Element)");
        }

        [Test]
        public void ToString_WithNullAssetType_HandlesGracefully()
        {
            // Arrange
            var node = new ExchangeTreeNode
            {
                Name = "TestNode",
                AssetType = null
            };

            // Act
            var result = node.ToString();

            // Assert
            result.Should().Be("TestNode ()");
        }

        #endregion

        #region Property Tests

        [Test]
        public void Properties_CanAddAndRetrieve()
        {
            // Arrange
            var node = new ExchangeTreeNode();

            // Act
            node.Properties["category"] = "Walls";
            node.Properties["discipline"] = "Architecture";

            // Assert
            node.Properties.Should().HaveCount(2);
            node.Properties["category"].Should().Be("Walls");
            node.Properties["discipline"].Should().Be("Architecture");
        }

        [Test]
        public void ChildIds_CanAddMultipleChildren()
        {
            // Arrange
            var node = new ExchangeTreeNode("parent", "Parent", "Element");

            // Act
            node.ChildIds.Add("child-1");
            node.ChildIds.Add("child-2");
            node.ChildIds.Add("child-3");

            // Assert
            node.ChildIds.Should().HaveCount(3);
            node.ChildIds.Should().Contain("child-1");
            node.ChildIds.Should().Contain("child-2");
            node.ChildIds.Should().Contain("child-3");
        }

        #endregion
    }

    [TestFixture]
    public class ExchangeTreeTests
    {
        #region Constructor Tests

        [Test]
        public void DefaultConstructor_InitializesEmptyNodes()
        {
            // Act
            var tree = new ExchangeTree();

            // Assert
            tree.Nodes.Should().NotBeNull();
            tree.Nodes.Should().BeEmpty();
        }

        [Test]
        public void DefaultConstructor_PropertiesAreDefault()
        {
            // Act
            var tree = new ExchangeTree();

            // Assert
            tree.RootId.Should().BeNull();
            tree.ExchangeId.Should().BeNull();
            tree.ExchangeTitle.Should().BeNull();
            tree.ElementCount.Should().Be(0);
            tree.GeometryCount.Should().Be(0);
        }

        #endregion

        #region GetRoot Tests

        [Test]
        public void GetRoot_WhenRootIdIsNull_ReturnsNull()
        {
            // Arrange
            var tree = new ExchangeTree { RootId = null };

            // Act
            var result = tree.GetRoot();

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void GetRoot_WhenRootIdIsEmpty_ReturnsNull()
        {
            // Arrange
            var tree = new ExchangeTree { RootId = "" };

            // Act
            var result = tree.GetRoot();

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void GetRoot_WhenRootIdNotInNodes_ReturnsNull()
        {
            // Arrange
            var tree = new ExchangeTree { RootId = "missing-root" };

            // Act
            var result = tree.GetRoot();

            // Assert
            result.Should().BeNull();
        }

        [Test]
        public void GetRoot_WhenRootExists_ReturnsRootNode()
        {
            // Arrange
            var rootNode = new ExchangeTreeNode("root-id", "RootAssembly", "TopLevelAssembly");
            var tree = new ExchangeTree { RootId = "root-id" };
            tree.Nodes["root-id"] = rootNode;

            // Act
            var result = tree.GetRoot();

            // Assert
            result.Should().NotBeNull();
            result.Should().BeSameAs(rootNode);
            result.Name.Should().Be("RootAssembly");
        }

        #endregion

        #region GetChildren Tests

        [Test]
        public void GetChildren_WhenNodeIdNotFound_ReturnsEmptyList()
        {
            // Arrange
            var tree = new ExchangeTree();

            // Act
            var result = tree.GetChildren("non-existent");

            // Assert
            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        [Test]
        public void GetChildren_WhenNodeHasNoChildren_ReturnsEmptyList()
        {
            // Arrange
            var parentNode = new ExchangeTreeNode("parent", "Parent", "Element");
            var tree = new ExchangeTree();
            tree.Nodes["parent"] = parentNode;

            // Act
            var result = tree.GetChildren("parent");

            // Assert
            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        [Test]
        public void GetChildren_WhenNodeHasChildren_ReturnsChildNodes()
        {
            // Arrange
            var tree = CreateTreeWithChildren();

            // Act
            var result = tree.GetChildren("parent");

            // Assert
            result.Should().HaveCount(2);
            result.Select(n => n.Id).Should().Contain("child-1");
            result.Select(n => n.Id).Should().Contain("child-2");
        }

        [Test]
        public void GetChildren_WhenChildIdNotInNodes_SkipsInvalidChild()
        {
            // Arrange
            var parentNode = new ExchangeTreeNode("parent", "Parent", "Element");
            parentNode.ChildIds.Add("valid-child");
            parentNode.ChildIds.Add("invalid-child"); // Not in Nodes dictionary

            var validChild = new ExchangeTreeNode("valid-child", "ValidChild", "Element");

            var tree = new ExchangeTree();
            tree.Nodes["parent"] = parentNode;
            tree.Nodes["valid-child"] = validChild;

            // Act
            var result = tree.GetChildren("parent");

            // Assert
            result.Should().HaveCount(1);
            result[0].Id.Should().Be("valid-child");
        }

        #endregion

        #region GetGeometryNodes Tests

        [Test]
        public void GetGeometryNodes_WhenNoNodesHaveGeometry_ReturnsEmptyList()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Node1", "Element") { HasGeometry = false };
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Node2", "Element") { HasGeometry = false };

            // Act
            var result = tree.GetGeometryNodes();

            // Assert
            result.Should().BeEmpty();
        }

        [Test]
        public void GetGeometryNodes_WhenSomeNodesHaveGeometry_ReturnsOnlyGeometryNodes()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Node1", "Element") { HasGeometry = false };
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Node2", "GeometryAsset") { HasGeometry = true };
            tree.Nodes["node3"] = new ExchangeTreeNode("node3", "Node3", "GeometryAsset") { HasGeometry = true };

            // Act
            var result = tree.GetGeometryNodes();

            // Assert
            result.Should().HaveCount(2);
            result.All(n => n.HasGeometry).Should().BeTrue();
        }

        [Test]
        public void GetGeometryNodes_WhenTreeIsEmpty_ReturnsEmptyList()
        {
            // Arrange
            var tree = new ExchangeTree();

            // Act
            var result = tree.GetGeometryNodes();

            // Assert
            result.Should().BeEmpty();
        }

        #endregion

        #region FindByName Tests

        [Test]
        public void FindByName_WhenNoMatch_ReturnsEmptyList()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Apple", "Element");
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Banana", "Element");

            // Act
            var result = tree.FindByName("Cherry");

            // Assert
            result.Should().BeEmpty();
        }

        [Test]
        public void FindByName_WhenExactMatch_ReturnsMatchingNodes()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Wall", "Element");
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Door", "Element");

            // Act
            var result = tree.FindByName("Wall");

            // Assert
            result.Should().HaveCount(1);
            result[0].Name.Should().Be("Wall");
        }

        [Test]
        public void FindByName_IsCaseInsensitive()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Wall", "Element");

            // Act & Assert
            tree.FindByName("wall").Should().HaveCount(1);
            tree.FindByName("WALL").Should().HaveCount(1);
            tree.FindByName("WaLl").Should().HaveCount(1);
        }

        [Test]
        public void FindByName_WhenMultipleMatches_ReturnsAllMatches()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Wall", "Element");
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Wall", "Element");
            tree.Nodes["node3"] = new ExchangeTreeNode("node3", "wall", "Element"); // Different case

            // Act
            var result = tree.FindByName("wall");

            // Assert
            result.Should().HaveCount(3);
        }

        [Test]
        public void FindByName_WhenNodeNameIsNull_DoesNotThrow()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode { Id = "node1", Name = null };
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "ValidName", "Element");

            // Act
            var result = tree.FindByName("ValidName");

            // Assert
            result.Should().HaveCount(1);
            result[0].Id.Should().Be("node2");
        }

        [Test]
        public void FindByName_WithEmptyString_ReturnsEmptyList()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "", "Element");
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Name", "Element");

            // Act
            var result = tree.FindByName("");

            // Assert
            // Empty string should match nodes with empty names
            result.Should().HaveCount(1);
            result[0].Id.Should().Be("node1");
        }

        #endregion

        #region GetElements Tests

        [Test]
        public void GetElements_WhenNoElements_ReturnsEmptyList()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Node1", "GeometryAsset");
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Node2", "DesignAsset");

            // Act
            var result = tree.GetElements();

            // Assert
            result.Should().BeEmpty();
        }

        [Test]
        public void GetElements_WhenElementsExist_ReturnsOnlyElements()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Node1", "Element");
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Node2", "GeometryAsset");
            tree.Nodes["node3"] = new ExchangeTreeNode("node3", "Node3", "Element");

            // Act
            var result = tree.GetElements();

            // Assert
            result.Should().HaveCount(2);
            result.All(n => n.AssetType == "Element").Should().BeTrue();
        }

        [Test]
        public void GetElements_AssetTypeIsCaseSensitive()
        {
            // Arrange
            var tree = new ExchangeTree();
            tree.Nodes["node1"] = new ExchangeTreeNode("node1", "Node1", "Element");
            tree.Nodes["node2"] = new ExchangeTreeNode("node2", "Node2", "element"); // lowercase
            tree.Nodes["node3"] = new ExchangeTreeNode("node3", "Node3", "ELEMENT"); // uppercase

            // Act
            var result = tree.GetElements();

            // Assert - only exact match "Element" should be returned
            result.Should().HaveCount(1);
            result[0].Id.Should().Be("node1");
        }

        #endregion

        #region ToDisplayList Tests

        [Test]
        public void ToDisplayList_WhenRootIdIsNull_ReturnsEmptyList()
        {
            // Arrange
            var tree = new ExchangeTree { RootId = null };

            // Act
            var result = tree.ToDisplayList();

            // Assert
            result.Should().BeEmpty();
        }

        [Test]
        public void ToDisplayList_WhenRootIdNotInNodes_ReturnsEmptyList()
        {
            // Arrange
            var tree = new ExchangeTree { RootId = "missing" };

            // Act
            var result = tree.ToDisplayList();

            // Assert
            result.Should().BeEmpty();
        }

        [Test]
        public void ToDisplayList_SingleNode_ReturnsOneFormattedLine()
        {
            // Arrange
            var tree = new ExchangeTree { RootId = "root" };
            tree.Nodes["root"] = new ExchangeTreeNode("root", "RootAssembly", "TopLevelAssembly");

            // Act
            var result = tree.ToDisplayList();

            // Assert
            result.Should().HaveCount(1);
            result[0].Should().Be("RootAssembly (TopLevelAssembly)");
        }

        [Test]
        public void ToDisplayList_WithGeometry_IncludesGeometryIndicator()
        {
            // Arrange
            var tree = new ExchangeTree { RootId = "root" };
            tree.Nodes["root"] = new ExchangeTreeNode("root", "Geometry", "GeometryAsset")
            {
                HasGeometry = true
            };

            // Act
            var result = tree.ToDisplayList();

            // Assert
            result[0].Should().Contain("[G]");
        }

        [Test]
        public void ToDisplayList_WithChildren_IndentsCorrectly()
        {
            // Arrange
            var tree = CreateHierarchicalTree();

            // Act
            var result = tree.ToDisplayList();

            // Assert
            result.Should().HaveCount(3);
            result[0].Should().Be("Root (TopLevelAssembly)");
            result[1].Should().Be("  Child1 (Element)");        // 2 spaces indent
            result[2].Should().Be("    GrandChild (Element)");  // 4 spaces indent
        }

        [Test]
        public void ToDisplayList_DeepHierarchy_HandlesMultipleLevels()
        {
            // Arrange
            var tree = CreateDeepTree(5);

            // Act
            var result = tree.ToDisplayList();

            // Assert
            result.Should().HaveCount(5);
            for (int i = 0; i < 5; i++)
            {
                var expectedIndent = new string(' ', i * 2);
                result[i].Should().StartWith(expectedIndent);
            }
        }

        [Test]
        public void ToDisplayList_WithInvalidChildId_SkipsInvalidChild()
        {
            // Arrange
            var tree = new ExchangeTree { RootId = "root" };
            var rootNode = new ExchangeTreeNode("root", "Root", "TopLevelAssembly");
            rootNode.ChildIds.Add("valid-child");
            rootNode.ChildIds.Add("invalid-child"); // Not in Nodes

            tree.Nodes["root"] = rootNode;
            tree.Nodes["valid-child"] = new ExchangeTreeNode("valid-child", "ValidChild", "Element");

            // Act
            var result = tree.ToDisplayList();

            // Assert
            result.Should().HaveCount(2);
            result[0].Should().Contain("Root");
            result[1].Should().Contain("ValidChild");
        }

        #endregion

        #region ToString Tests

        [Test]
        public void ToString_ReturnsFormattedSummary()
        {
            // Arrange
            var tree = new ExchangeTree
            {
                ExchangeTitle = "My Exchange",
                ElementCount = 10,
                GeometryCount = 5
            };

            // Act
            var result = tree.ToString();

            // Assert
            result.Should().Be("ExchangeTree: My Exchange (10 elements, 5 geometries)");
        }

        [Test]
        public void ToString_WithNullTitle_HandlesGracefully()
        {
            // Arrange
            var tree = new ExchangeTree
            {
                ExchangeTitle = null,
                ElementCount = 0,
                GeometryCount = 0
            };

            // Act
            var result = tree.ToString();

            // Assert
            result.Should().Be("ExchangeTree:  (0 elements, 0 geometries)");
        }

        #endregion

        #region Helper Methods

        private ExchangeTree CreateTreeWithChildren()
        {
            var parentNode = new ExchangeTreeNode("parent", "Parent", "Element");
            parentNode.ChildIds.Add("child-1");
            parentNode.ChildIds.Add("child-2");

            var child1 = new ExchangeTreeNode("child-1", "Child1", "Element", "parent");
            var child2 = new ExchangeTreeNode("child-2", "Child2", "Element", "parent");

            var tree = new ExchangeTree { RootId = "parent" };
            tree.Nodes["parent"] = parentNode;
            tree.Nodes["child-1"] = child1;
            tree.Nodes["child-2"] = child2;

            return tree;
        }

        private ExchangeTree CreateHierarchicalTree()
        {
            var root = new ExchangeTreeNode("root", "Root", "TopLevelAssembly");
            root.ChildIds.Add("child1");

            var child1 = new ExchangeTreeNode("child1", "Child1", "Element", "root");
            child1.ChildIds.Add("grandchild");

            var grandchild = new ExchangeTreeNode("grandchild", "GrandChild", "Element", "child1");

            var tree = new ExchangeTree { RootId = "root" };
            tree.Nodes["root"] = root;
            tree.Nodes["child1"] = child1;
            tree.Nodes["grandchild"] = grandchild;

            return tree;
        }

        private ExchangeTree CreateDeepTree(int depth)
        {
            var tree = new ExchangeTree { RootId = "level-0" };

            for (int i = 0; i < depth; i++)
            {
                var nodeId = $"level-{i}";
                var parentId = i > 0 ? $"level-{i - 1}" : null;
                var node = new ExchangeTreeNode(nodeId, $"Level{i}", "Element", parentId);

                if (i < depth - 1)
                {
                    node.ChildIds.Add($"level-{i + 1}");
                }

                tree.Nodes[nodeId] = node;
            }

            return tree;
        }

        #endregion
    }
}
