using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.DesignScript.Runtime;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Gets the revision history of a DataExchange.
    /// </summary>
    public static class GetExchangeRevisions
    {
        /// <summary>
        /// Gets all revisions of a DataExchange.
        /// Returns revision IDs, timestamps, and creation info for each version.
        /// </summary>
        /// <param name="exchange">The Exchange object to get revisions for</param>
        /// <returns>Dictionary with "revisionIds" (list), "revisionDates" (list), "revisionCreators" (list), "latestRevisionId", "revisionCount", "log", and "success"</returns>
        [MultiReturn(new[] { "revisionIds", "revisionDates", "revisionCreators", "latestRevisionId", "revisionCount", "log", "success" })]
        public static Dictionary<string, object> GetRevisions(Exchange exchange)
        {
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            bool success = false;
            var revisionIds = new List<string>();
            var revisionDates = new List<string>();
            var revisionCreators = new List<string>();
            string latestRevisionId = "";
            int revisionCount = 0;

            try
            {
                if (exchange == null)
                {
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");
                }

                if (!DataExchangeClient.IsInitialized())
                {
                    throw new InvalidOperationException("Client is not initialized. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
                }

                var client = DataExchangeClient.GetClient();
                if (client == null)
                {
                    throw new InvalidOperationException("Could not get Client instance");
                }

                var identifier = DataExchangeUtils.CreateIdentifier(exchange);

                // Get revisions using SDK method
                var revisionsResponse = Task.Run(async () =>
                    await client.GetExchangeRevisionsAsync(identifier)).Result;

                if (revisionsResponse != null && revisionsResponse.Value != null && revisionsResponse.Value.Any())
                {
                    var revisions = revisionsResponse.Value.ToList();
                    revisionCount = revisions.Count;

                    foreach (var revision in revisions)
                    {
                        // Get ID
                        var id = revision.Id ?? "";
                        revisionIds.Add(id);

                        // Get CreatedDate via reflection
                        var revisionType = revision.GetType();
                        var createdDateProp = revisionType.GetProperty("CreatedDate", BindingFlags.Public | BindingFlags.Instance);
                        if (createdDateProp != null)
                        {
                            var createdDate = createdDateProp.GetValue(revision);
                            revisionDates.Add(createdDate?.ToString() ?? "Unknown");
                        }
                        else
                        {
                            revisionDates.Add("Unknown");
                        }

                        // Get CreatedBy via reflection
                        var createdByProp = revisionType.GetProperty("CreatedBy", BindingFlags.Public | BindingFlags.Instance);
                        if (createdByProp != null)
                        {
                            var createdBy = createdByProp.GetValue(revision);
                            revisionCreators.Add(createdBy?.ToString() ?? "Unknown");
                        }
                        else
                        {
                            revisionCreators.Add("Unknown");
                        }
                    }

                    // Latest revision is first in list
                    if (revisions.Count > 0)
                    {
                        latestRevisionId = revisions.First().Id ?? "";
                    }

                    success = true;
                    log.Info($"Found {revisionCount} revision(s) for exchange '{exchange.ExchangeTitle}'");
                }
                else
                {
                    log.Info("No revisions found for this exchange");
                    success = true;
                }
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new Dictionary<string, object>
            {
                { "revisionIds", revisionIds },
                { "revisionDates", revisionDates },
                { "revisionCreators", revisionCreators },
                { "latestRevisionId", latestRevisionId },
                { "revisionCount", revisionCount },
                { "log", log.GetLog() },
                { "success", success }
            };
        }

        /// <summary>
        /// Gets just the latest revision ID for a DataExchange.
        /// This is a simpler, faster version of GetRevisions when you only need the latest.
        /// </summary>
        /// <param name="exchange">The Exchange object</param>
        /// <returns>Dictionary with "revisionId", "log", and "success"</returns>
        [MultiReturn(new[] { "revisionId", "log", "success" })]
        public static Dictionary<string, object> GetLatestRevision(Exchange exchange)
        {
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            bool success = false;
            string revisionId = "";

            try
            {
                if (exchange == null)
                {
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");
                }

                var identifier = DataExchangeUtils.CreateIdentifier(exchange);
                revisionId = DataExchangeClient.GetLatestRevisionIdAsync(identifier).GetAwaiter().GetResult() ?? "";

                if (!string.IsNullOrEmpty(revisionId))
                {
                    success = true;
                    log.Info($"Latest revision: {revisionId}");
                }
                else
                {
                    log.Info("No revisions found");
                    success = true;
                }
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
            }

            return new Dictionary<string, object>
            {
                { "revisionId", revisionId },
                { "log", log.GetLog() },
                { "success", success }
            };
        }
    }
}
