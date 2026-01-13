using System;
using System.Collections.Generic;
using Autodesk.DesignScript.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Represents a DataExchange with metadata from Autodesk Construction Cloud
    /// This is a data container class - use InspectExchange node to access its properties
    /// </summary>
    [IsVisibleInDynamoLibrary(false)]
    public class Exchange
    {
        #region Core Identifiers

        /// <summary>
        /// Unique identifier for the exchange
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string ExchangeId { get; set; }

        /// <summary>
        /// Collection ID within the exchange
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string CollectionId { get; set; }

        #endregion

        #region Human-Readable Info

        /// <summary>
        /// Title/name of the exchange
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string ExchangeTitle { get; set; }

        /// <summary>
        /// Description of the exchange
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string ExchangeDescription { get; set; }

        /// <summary>
        /// Name of the project containing this exchange
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string ProjectName { get; set; }

        /// <summary>
        /// Folder path within the project
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string FolderPath { get; set; }

        #endregion

        #region User Information

        /// <summary>
        /// User who created the exchange
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string CreatedBy { get; set; }

        /// <summary>
        /// User who last updated the exchange
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string UpdatedBy { get; set; }

        #endregion

        #region URNs and IDs

        /// <summary>
        /// Project URN (Autodesk Unified Resource Name)
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string ProjectUrn { get; set; }

        /// <summary>
        /// File URN
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string FileUrn { get; set; }

        /// <summary>
        /// Folder URN
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string FolderUrn { get; set; }

        /// <summary>
        /// File version ID
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string FileVersionId { get; set; }

        /// <summary>
        /// Hub ID (ACC hub identifier)
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string HubId { get; set; }

        /// <summary>
        /// Hub region (e.g., US, EMEA)
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string HubRegion { get; set; }

        #endregion

        #region Timestamps

        /// <summary>
        /// When the exchange was created
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string CreateTime { get; set; }

        /// <summary>
        /// When the exchange was last updated
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string Updated { get; set; }

        /// <summary>
        /// When this selection was made
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string Timestamp { get; set; }

        #endregion

        #region Additional Metadata

        /// <summary>
        /// Schema namespace identifier
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string SchemaNamespace { get; set; }

        /// <summary>
        /// Local path to exchange thumbnail image
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string ExchangeThumbnail { get; set; }

        /// <summary>
        /// Type of project (e.g., ACC, BIM360)
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public string ProjectType { get; set; }

        /// <summary>
        /// Whether an update is available for this exchange
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public bool IsUpdateAvailable { get; set; }

        #endregion

        #region Methods

        /// <summary>
        /// Creates an Exchange object from JSON string
        /// </summary>
        /// <param name="json">JSON string containing exchange metadata</param>
        /// <returns>Exchange object</returns>
        [IsVisibleInDynamoLibrary(false)]
        public static Exchange FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;

            try
            {
                var data = JObject.Parse(json);
                
                return new Exchange
                {
                    // Core identifiers
                    ExchangeId = data["exchangeId"]?.ToString() ?? "",
                    CollectionId = data["collectionId"]?.ToString() ?? "",
                    
                    // Human-readable info
                    ExchangeTitle = data["exchangeTitle"]?.ToString() ?? "",
                    ExchangeDescription = data["exchangeDescription"]?.ToString() ?? "",
                    ProjectName = data["projectName"]?.ToString() ?? "",
                    FolderPath = data["folderPath"]?.ToString() ?? "",
                    
                    // User info
                    CreatedBy = data["createdBy"]?.ToString() ?? "",
                    UpdatedBy = data["updatedBy"]?.ToString() ?? "",
                    
                    // URNs and IDs
                    ProjectUrn = data["projectUrn"]?.ToString() ?? "",
                    FileUrn = data["fileUrn"]?.ToString() ?? "",
                    FolderUrn = data["folderUrn"]?.ToString() ?? "",
                    FileVersionId = data["fileVersionId"]?.ToString() ?? "",
                    HubId = data["hubId"]?.ToString() ?? "",
                    HubRegion = data["hubRegion"]?.ToString() ?? "",
                    
                    // Timestamps
                    CreateTime = data["createTime"]?.ToString() ?? "",
                    Updated = data["updated"]?.ToString() ?? "",
                    Timestamp = data["timestamp"]?.ToString() ?? "",
                    
                    // Additional metadata
                    SchemaNamespace = data["schemaNamespace"]?.ToString() ?? "",
                    ExchangeThumbnail = data["exchangeThumbnail"]?.ToString() ?? "",
                    ProjectType = data["projectType"]?.ToString() ?? "",
                    IsUpdateAvailable = bool.TryParse(data["isUpdateAvailable"]?.ToString(), out bool isUpdate) && isUpdate
                };
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to parse Exchange JSON: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Returns a string representation of the Exchange
        /// </summary>
        /// <returns>String describing the exchange</returns>
        public override string ToString()
        {
            return $"Exchange: {ExchangeTitle} (ID: {ExchangeId}, Project: {ProjectName})";
        }

        #endregion
    }
}

