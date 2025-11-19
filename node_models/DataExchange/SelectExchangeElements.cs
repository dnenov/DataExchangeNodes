using System;
using System.Collections.Generic;
using Dynamo.Graph.Nodes;
using Newtonsoft.Json;
using ProtoCore.AST.AssociativeAST;

namespace DataExchangeNodes.NodeModels.DataExchange
{
    /// <summary>
    /// NodeModel for selecting elements from a DataExchange collection
    /// </summary>
    [NodeName("Select DataExchange Elements")]
    [NodeCategory("DataExchange.Selection")]
    [NodeDescription("Browse and select elements from a DataExchange collection")]
    [NodeSearchTags("dataexchange", "select", "exchange", "elements")]
    [OutPortTypes("var[]")]
    [OutPortNames("elements")]
    [OutPortDescriptions("Selected DataExchange elements")]
    [IsDesignScriptCompatible]
    public class SelectExchangeElements : CoreNodeModels.Input.String
    {
        private string value;

        /// <summary>
        /// Stores the serialized selection JSON (exchange ID, collection ID, filters, etc.)
        /// </summary>
        [JsonProperty("InputValue", Order = 9)]
        public override string Value
        {
            get
            {
                return value;
            }
            set
            {
                if (this.value is null || this.value != value)
                {
                    this.value = value;
                    if (!Equals(value, null))
                    {
                        OnNodeModified(true);
                    }
                    else
                    {
                        ClearDirtyFlag();
                    }
                    RaisePropertyChanged("Value");
                }
            }
        }

        /// <summary>
        /// Default constructor
        /// </summary>
        public SelectExchangeElements()
        {
            RaisesModificationEvents = false;
            OutPorts.Clear();
            RaisesModificationEvents = true;

            RegisterAllPorts();
            IsSetAsInput = true;
            Value = "";
        }

        /// <summary>
        /// JSON constructor for deserialization
        /// </summary>
        [JsonConstructor]
        private SelectExchangeElements(IEnumerable<PortModel> inPorts, IEnumerable<PortModel> outPorts) 
            : base(inPorts, outPorts)
        {
            Value = "";
        }

        /// <summary>
        /// Builds the AST output that calls the utility function with the stored selection JSON
        /// </summary>
        public override IEnumerable<AssociativeNode> BuildOutputAst(List<AssociativeNode> inputAstNodes)
        {
            // If no selection has been made, return null
            if (string.IsNullOrEmpty(Value))
                return new[] { AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(0), AstFactory.BuildNullNode()) };

            // Convert the stored JSON to a string node
            var inputString = AstFactory.BuildStringNode(Value);

            // Build a deferred function call to our utility function
            // This creates a "promise" that will execute when the graph evaluates
            var func = AstFactory.BuildFunctionCall(
                new Func<string, List<object>>(DataExchangeNodes.DataExchange.DataExchangeUtils.GetElementsFromExchange),
                new List<AssociativeNode> { inputString });

            return new[] { AstFactory.BuildAssignment(GetAstIdentifierForOutputIndex(0), func) };
        }
    }
}

