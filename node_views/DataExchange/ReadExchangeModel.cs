using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DataExchange.BaseModels;
using Autodesk.DataExchange.Core;
using Autodesk.DataExchange.Core.Enums;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.Interface;
using DataExchangeNodes.NodeModels.DataExchange;
using Newtonsoft.Json;

namespace DataExchangeNodes.NodeViews.DataExchange
{
    /// <summary>
    /// Read-only DataExchange model for element selection
    /// Extends BaseReadWriteExchangeModel but only implements read functionality
    /// </summary>
    public class ReadExchangeModel : BaseReadWriteExchangeModel
    {
        private static ReadExchangeModel _instance;
        private List<Autodesk.DataExchange.Core.Models.DataExchange> _localStorage = new List<Autodesk.DataExchange.Core.Models.DataExchange>();
        
        /// <summary>
        /// The current node that is using this exchange model
        /// </summary>
        public static SelectExchangeElements CurrentNode { get; set; }

        private ReadExchangeModel(IClient client) : base(client)
        {
        }

        public static ReadExchangeModel GetInstance(IClient client)
        {
            if (_instance == null || _instance.Client != client)
            {
                _instance = new ReadExchangeModel(client);
            }
            return _instance;
        }

        public override async Task<List<Autodesk.DataExchange.Core.Models.DataExchange>> GetExchangesAsync(ExchangeSearchFilter exchangeSearchFilter)
        {
            // Merge cloud exchanges with local storage
            _localStorage = await GetValidExchangesAsync(exchangeSearchFilter, _localStorage);
            return _localStorage;
        }

        public override async Task<Autodesk.DataExchange.Core.Models.DataExchange> GetExchangeAsync(DataExchangeIdentifier dataExchangeIdentifier)
        {
            var response = await base.GetExchangeAsync(dataExchangeIdentifier);

            if (response != null && !_localStorage.Any(item => item.ExchangeID == response.ExchangeID))
            {
                response.IsExchangeFromRead = true;
                _localStorage.Add(response);
            }

            return response;
        }

        public override List<Autodesk.DataExchange.Core.Models.DataExchange> GetCachedExchanges()
        {
            return _localStorage?.ToList() ?? new List<Autodesk.DataExchange.Core.Models.DataExchange>();
        }

        public override Task UpdateExchangeAsync(ExchangeItem exchangeItem, CancellationToken cancellationToken = default)
        {
            // Read-only mode - no updates allowed
            return Task.CompletedTask;
        }

        public override Task<IEnumerable<string>> UnloadExchangesAsync(List<ExchangeItem> exchanges)
        {
            return Task.FromResult(exchanges.Select(n => n.ExchangeID));
        }

        public override Task<bool> SelectElementsAsync(List<string> exchangeIds)
        {
            try
            {
                // When an exchange is selected, capture its metadata
                if (exchangeIds != null && exchangeIds.Any() && CurrentNode != null)
                {
                    var exchangeId = exchangeIds.First();
                    var exchange = _localStorage.FirstOrDefault(e => e.ExchangeID == exchangeId);

                    if (exchange != null)
                    {
                        // Create selection data dictionary
                        var selectionData = new Dictionary<string, string>
                        {
                            { "exchangeId", exchange.ExchangeID ?? "" },
                            { "collectionId", exchange.CollectionID ?? "" },
                            { "exchangeName", "" }, // Name property not available in DataExchange object
                            { "projectUrn", exchange.ProjectURN ?? "" },
                            { "folderUrn", exchange.FolderURN ?? "" },
                            { "hubId", exchange.HubId ?? "" },
                            { "fileVersionId", exchange.FileVersionId ?? "" },
                            { "timestamp", DateTime.Now.ToString("o") }
                        };

                        // Serialize and store in node
                        string selectionJson = JsonConvert.SerializeObject(selectionData);
                        CurrentNode.Value = selectionJson;
                        CurrentNode.OnNodeModified(forceExecute: true);
                    }
                }

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ReadExchangeModel: Error in SelectElementsAsync - {ex.Message}");
                return Task.FromResult(false);
            }
        }
    }
}

