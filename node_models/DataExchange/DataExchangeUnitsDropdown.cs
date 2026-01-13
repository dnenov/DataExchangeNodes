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
    /// Dropdown node for selecting DataExchange unit types
    /// </summary>
    [NodeName("DataExchange Units")]
    [NodeCategory("ExchangeNodes.DataExchangeNodes.Input")]
    [NodeDescription("Select a unit type for geometry export/upload (CentiMeter, Meter, Feet, Inch)")]
    [NodeSearchTags("units", "unit", "dataexchange", "geometry", "export")]
    [OutPortTypes("string")]
    [OutPortNames("unit")]
    [OutPortDescriptions("Selected unit string (e.g., kUnitType_CentiMeter)")]
    [IsDesignScriptCompatible]
    public class DataExchangeUnitsDropdown : DSDropDownBase
    {
        private static readonly List<DynamoDropDownItem> UnitItems = new List<DynamoDropDownItem>
        {
            new DynamoDropDownItem("Centimeter", "kUnitType_CentiMeter"),
            new DynamoDropDownItem("Meter", "kUnitType_Meter"),
            new DynamoDropDownItem("Feet", "kUnitType_Feet"),
            new DynamoDropDownItem("Inch", "kUnitType_Inch")
        };

        /// <summary>
        /// Default constructor
        /// </summary>
        public DataExchangeUnitsDropdown() : base("unit")
        {
            RegisterAllPorts();
            PopulateItems();
            // Default to Centimeter (index 0)
            SelectedIndex = 0;
        }

        /// <summary>
        /// JSON constructor for deserialization
        /// </summary>
        [JsonConstructor]
        private DataExchangeUnitsDropdown(IEnumerable<PortModel> inPorts, IEnumerable<PortModel> outPorts) 
            : base("unit", inPorts, outPorts)
        {
            RegisterAllPorts();
            PopulateItems();
        }

        /// <summary>
        /// Populate the dropdown items with unit options
        /// </summary>
        protected override SelectionState PopulateItemsCore(string currentSelection)
        {
            Items.Clear();
            
            foreach (var item in UnitItems)
            {
                Items.Add(item);
            }

            return SelectionState.Restore;
        }

        /// <summary>
        /// Build the AST output that returns the selected unit string
        /// </summary>
        public override IEnumerable<AssociativeNode> BuildOutputAst(List<AssociativeNode> inputAstNodes)
        {
            if (SelectedIndex < 0 || SelectedIndex >= Items.Count)
            {
                // Return empty string if nothing is selected
                return new[] { AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(0), AstFactory.BuildStringNode("")) };
            }

            var selectedItem = Items[SelectedIndex];
            var unitString = selectedItem.Item?.ToString() ?? "";
            
            var stringNode = AstFactory.BuildStringNode(unitString);
            return new[] { AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(0), stringNode) };
        }
    }
}
