using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange.DataModels;
using Autodesk.DataExchange.Interface;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Upload STEP files (.stp or .step) to a Data Exchange using the official SDK
    /// </summary>
    public static class UploadSTPToExchange
    {
        /// <summary>
        /// Uploads a STEP file to a Data Exchange using the official SDK
        /// </summary>
        /// <param name="exchange">The Exchange to upload to</param>
        /// <param name="stepFilePath">Path to the STEP file (.stp or .step)</param>
        /// <param name="elementName">Name for the element/group (default: "ExportedGeometry")</param>
        /// <param name="units">Units for the geometry (default: "kUnitType_Meter")</param>
        /// <returns>Dictionary with success status, diagnostics, and geometry count</returns>
        [MultiReturn(new[] { "success", "diagnostics", "geometryCount", "uploadInfo" })]
        public static Dictionary<string, object> Upload(
            Exchange exchange,
            string stepFilePath,
            string elementName = "ExportedGeometry",
            string units = "kUnitType_Meter")
        {
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            int geometryCount = 0;
            object uploadInfo = null;

            try
            {
                // Validate inputs
                if (exchange == null)
                {
                    log.Error("Exchange is null");
                    return BuildResult(log, false, geometryCount, uploadInfo);
                }

                if (string.IsNullOrEmpty(stepFilePath) || !File.Exists(stepFilePath))
                {
                    log.Error($"STEP file not found: {stepFilePath}");
                    return BuildResult(log, false, geometryCount, uploadInfo);
                }

                // Validate client
                var (client, isValid, errorMessage) = DataExchangeUtils.GetValidatedClient();
                if (!isValid)
                {
                    log.Error(errorMessage);
                    return BuildResult(log, false, geometryCount, uploadInfo);
                }

                // Cast to IClient - required for ElementDataModel.Create()
                if (!(client is IClient iClient))
                {
                    log.Error($"Client is not IClient: {client.GetType().FullName}");
                    return BuildResult(log, false, geometryCount, uploadInfo);
                }

                // Create identifier
                var identifier = DataExchangeUtils.CreateIdentifier(exchange);

                // Get or create ElementDataModel
                var elementDataModel = GetOrCreateElementDataModel(iClient, identifier, log);
                if (elementDataModel == null)
                {
                    return BuildResult(log, false, geometryCount, uploadInfo);
                }

                // Find or create element
                var (element, existingGeometries) = FindOrCreateElement(elementDataModel, elementName, log);
                if (element == null)
                {
                    return BuildResult(log, false, geometryCount, uploadInfo);
                }

                // Create geometry from STEP file
                var geometryProperties = new GeometryProperties(stepFilePath);
                var elementGeometry = ElementDataModel.CreateFileGeometry(geometryProperties);

                // Combine existing geometries with new one
                var geometries = new List<ElementGeometry>(existingGeometries);
                geometries.Add(elementGeometry);

                elementDataModel.SetElementGeometry(element, geometries);
                geometryCount = geometries.Count;

                // Sync to exchange
                var syncResponse = DataExchangeUtils.RunSync(() =>
                    iClient.SyncExchangeDataAsync(identifier, elementDataModel));

                if (!syncResponse.IsSuccess)
                {
                    log.Error("SyncExchangeDataAsync failed");
                    return BuildResult(log, false, geometryCount, uploadInfo);
                }

                uploadInfo = syncResponse.ValueOrDefault;
                log.Info($"STEP file uploaded successfully to exchange '{exchange.ExchangeTitle}' ({geometryCount} geometry)");
                return BuildResult(log, true, geometryCount, uploadInfo);
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
                return BuildResult(log, false, geometryCount, uploadInfo);
            }
        }

        private static ElementDataModel GetOrCreateElementDataModel(
            IClient iClient,
            Autodesk.DataExchange.Core.Models.DataExchangeIdentifier identifier,
            DiagnosticsLogger log)
        {
            // Try to get existing ElementDataModel from the exchange
            ElementDataModel model = null;
            try
            {
                var response = DataExchangeUtils.RunSync(() =>
                    iClient.GetElementDataModelAsync(identifier));

                if (response != null)
                {
                    var valueProp = response.GetType().GetProperty("Value");
                    if (valueProp != null)
                    {
                        model = valueProp.GetValue(response) as ElementDataModel;
                    }
                    else if (response is ElementDataModel directModel)
                    {
                        model = directModel;
                    }
                }
            }
            catch
            {
                // Ignore - will create new model
            }

            if (model == null)
            {
                try
                {
                    model = ElementDataModel.Create(iClient);
                }
                catch (Exception ex)
                {
                    log.Error($"Could not create ElementDataModel: {ex.Message}");
                    return null;
                }
            }

            return model;
        }

        private static (Element element, List<ElementGeometry> existingGeometries) FindOrCreateElement(
            ElementDataModel elementDataModel,
            string elementName,
            DiagnosticsLogger log)
        {
            var existingGeometries = new List<ElementGeometry>();
            Element element = null;

            // Try to find existing element with same name
            var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
            if (elementsProperty != null)
            {
                var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                if (elements != null)
                {
                    foreach (var existingElement in elements.Cast<object>())
                    {
                        var nameProp = existingElement.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProp != null)
                        {
                            var existingName = nameProp.GetValue(existingElement)?.ToString();
                            if (existingName == elementName && existingElement is Element foundElement)
                            {
                                element = foundElement;

                                // Get existing geometries
                                try
                                {
                                    var elementsList = new List<Element> { element };
                                    var geometriesDict = elementDataModel.GetElementGeometriesAsync(elementsList, CancellationToken.None, null).Result;
                                    if (geometriesDict?.ContainsKey(element) == true)
                                    {
                                        existingGeometries.AddRange(geometriesDict[element] ?? Enumerable.Empty<ElementGeometry>());
                                    }
                                }
                                catch
                                {
                                    // Ignore - will just add new geometry
                                }

                                break;
                            }
                        }
                    }
                }
            }

            // Create new element if not found
            if (element == null)
            {
                try
                {
                    var elementProperties = new ElementProperties(elementName, elementName, "Generics", "Generic", "Generic Object");
                    element = elementDataModel.AddElement(elementProperties) as Element;
                    if (element == null)
                    {
                        log.Error("AddElement returned null");
                        return (null, existingGeometries);
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"Could not create element: {ex.Message}");
                    return (null, existingGeometries);
                }
            }

            return (element, existingGeometries);
        }

        private static Dictionary<string, object> BuildResult(DiagnosticsLogger log, bool success, int geometryCount, object uploadInfo)
        {
            return new NodeResultBuilder()
                .WithSuccess(success)
                .WithDiagnostics(log)
                .WithProperty("geometryCount", geometryCount)
                .WithProperty("uploadInfo", uploadInfo)
                .Build();
        }
    }
}
