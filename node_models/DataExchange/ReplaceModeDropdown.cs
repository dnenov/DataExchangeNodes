using System;
using System.Collections.Generic;
using Autodesk.DesignScript.Runtime;
using CoreNodeModels;
using Dynamo.Graph.Nodes;
using Newtonsoft.Json;
using ProtoCore.AST.AssociativeAST;

namespace DataExchangeNodes.NodeModels.DataExchange
{
    /// <summary>
    /// Dropdown node for selecting replace mode when uploading to DataExchange
    /// </summary>
    [NodeName("Replace Mode")]
    [NodeCategory("ExchangeNodes.DataExchangeNodes.Input")]
    [NodeDescription("Select how to handle existing elements when uploading to DataExchange")]
    [NodeSearchTags("replace", "mode", "dataexchange", "upload", "append")]
    [OutPortTypes("string")]
    [OutPortNames("replaceMode")]
    [OutPortDescriptions("Selected replace mode string (replaceByName, replaceAll, or append)")]
    [IsDesignScriptCompatible]
    public class ReplaceModeDropdown : DSDropDownBase
    {
        private static readonly List<DynamoDropDownItem> ReplaceModeItems = new List<DynamoDropDownItem>
        {
            new DynamoDropDownItem("Replace By Name", "replaceByName"),
            new DynamoDropDownItem("Replace All", "replaceAll"),
            new DynamoDropDownItem("Append", "append")
        };

        /// <summary>
        /// Default constructor
        /// </summary>
        public ReplaceModeDropdown() : base("replaceMode")
        {
            RegisterAllPorts();
            PopulateItems();
            // Default to "Replace By Name" (index 0)
            SelectedIndex = 0;
        }

        /// <summary>
        /// JSON constructor for deserialization
        /// </summary>
        [JsonConstructor]
        private ReplaceModeDropdown(IEnumerable<PortModel> inPorts, IEnumerable<PortModel> outPorts)
            : base("replaceMode", inPorts, outPorts)
        {
            RegisterAllPorts();
            PopulateItems();
        }

        /// <summary>
        /// Populate the dropdown items with replace mode options
        /// </summary>
        protected override SelectionState PopulateItemsCore(string currentSelection)
        {
            Items.Clear();

            foreach (var item in ReplaceModeItems)
            {
                Items.Add(item);
            }

            return SelectionState.Restore;
        }

        /// <summary>
        /// Build the AST output that returns the selected replace mode string
        /// </summary>
        public override IEnumerable<AssociativeNode> BuildOutputAst(List<AssociativeNode> inputAstNodes)
        {
            if (SelectedIndex < 0 || SelectedIndex >= Items.Count)
            {
                // Return empty string if nothing is selected
                return new[] { AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(0), AstFactory.BuildStringNode("")) };
            }

            var selectedItem = Items[SelectedIndex];
            var modeString = selectedItem.Item?.ToString() ?? "";

            var stringNode = AstFactory.BuildStringNode(modeString);
            return new[] { AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(0), stringNode) };
        }
    }
}
