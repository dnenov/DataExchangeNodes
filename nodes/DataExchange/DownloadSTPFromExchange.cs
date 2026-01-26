using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.DataModels;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Download geometries from a Data Exchange and export them to STEP files (.stp or .step)
    /// </summary>
    public static class DownloadSTPFromExchange
    {
        /// <summary>
        /// Downloads the complete exchange as a single STEP file using the official SDK method
        /// </summary>
        /// <param name="exchange">The Exchange to download from</param>
        /// <param name="outputDirectory">Directory to save STEP file (default: temp directory)</param>
        /// <returns>Dictionary with success status, diagnostics, file paths, and geometry count</returns>
        [MultiReturn(new[] { "success", "diagnostics", "stepFilePaths", "geometryCount" })]
        public static Dictionary<string, object> Download(Exchange exchange, string outputDirectory)
        {
            var log = new DiagnosticsLogger(DiagnosticLevel.Error);
            var stepFilePaths = new List<string>();
            int geometryCount = 0;

            try
            {
                if (exchange == null)
                {
                    log.Error("Exchange is null");
                    return BuildResult(log, false, stepFilePaths, geometryCount);
                }

                // Validate client
                var (client, isValid, errorMessage) = DataExchangeUtils.GetValidatedClient();
                if (!isValid)
                {
                    log.Error(errorMessage);
                    return BuildResult(log, false, stepFilePaths, geometryCount);
                }

                // Create identifier using shared utility
                var identifier = DataExchangeUtils.CreateIdentifier(exchange);

                // Get ElementDataModel
                var elementDataModelResponse = DataExchangeUtils.RunSync(async () =>
                    await DataExchangeClient.GetElementDataModelAsync(identifier, CancellationToken.None));

                ElementDataModel elementDataModel = null;
                if (elementDataModelResponse != null)
                {
                    var responseType = elementDataModelResponse.GetType();
                    var valueProp = responseType.GetProperty("Value");
                    if (valueProp != null)
                    {
                        elementDataModel = valueProp.GetValue(elementDataModelResponse) as ElementDataModel;
                    }
                    else if (elementDataModelResponse is ElementDataModel directModel)
                    {
                        elementDataModel = directModel;
                    }
                }

                if (elementDataModel == null)
                {
                    log.Error("Could not get ElementDataModel");
                    return BuildResult(log, false, stepFilePaths, geometryCount);
                }

                // Find DownloadCompleteExchangeAsSTEP method via reflection
                var downloadStepMethod = client.GetType().GetMethod(
                    "DownloadCompleteExchangeAsSTEP",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(DataExchangeIdentifier), typeof(string), typeof(CancellationToken) },
                    null);

                if (downloadStepMethod == null)
                {
                    log.Error("DownloadCompleteExchangeAsSTEP method not found on Client");
                    return BuildResult(log, false, stepFilePaths, geometryCount);
                }

                // Call the SDK method
                var downloadResponse = downloadStepMethod.Invoke(client, new object[] { identifier, null, CancellationToken.None });
                var stepFilePath = ExtractResponseValue<string>(downloadResponse);

                if (string.IsNullOrEmpty(stepFilePath) || !File.Exists(stepFilePath))
                {
                    log.Error($"DownloadCompleteExchangeAsSTEP returned invalid path: {stepFilePath}");
                    return BuildResult(log, false, stepFilePaths, geometryCount);
                }

                // Determine output path
                string outputFilePath = DetermineOutputPath(exchange, outputDirectory, stepFilePath);

                File.Copy(stepFilePath, outputFilePath, overwrite: true);
                stepFilePaths.Add(outputFilePath);

                // Get geometry count
                geometryCount = GetGeometryCount(elementDataModel);

                return BuildResult(log, true, stepFilePaths, geometryCount);
            }
            catch (Exception ex)
            {
                log.Error($"{ex.GetType().Name}: {ex.Message}");
                return BuildResult(log, false, stepFilePaths, geometryCount);
            }
        }

        private static string DetermineOutputPath(Exchange exchange, string outputDirectory, string sourcePath)
        {
            if (string.IsNullOrEmpty(outputDirectory))
            {
                outputDirectory = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "DownloadedSTEP");
            }
            else
            {
                outputDirectory = Path.GetFullPath(outputDirectory);
            }

            var outputExtension = Path.GetExtension(outputDirectory)?.ToLowerInvariant();
            if (outputExtension == ".stp" || outputExtension == ".step")
            {
                var outputDir = Path.GetDirectoryName(outputDirectory);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);
                return outputDirectory;
            }

            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            var safeTitle = (exchange.ExchangeTitle ?? "Exchange").Replace(" ", "_").Replace(":", "_");
            var outputFileName = $"{safeTitle}_{exchange.ExchangeId.Substring(0, 8)}.stp";
            return Path.Combine(outputDirectory, outputFileName);
        }

        private static int GetGeometryCount(ElementDataModel elementDataModel)
        {
            try
            {
                var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
                if (elementsProperty == null) return 1;

                var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                if (elements == null) return 1;

                var elementList = elements.Cast<object>()
                    .OfType<Element>()
                    .ToList();

                if (elementList.Count == 0) return 1;

                var geometriesDict = elementDataModel.GetElementGeometriesAsync(elementList, CancellationToken.None, null).Result;
                return geometriesDict?.Values.Sum(g => g?.Count() ?? 0) ?? 1;
            }
            catch
            {
                return 1;
            }
        }

        private static T ExtractResponseValue<T>(object response)
        {
            if (response == null) return default;
            if (response is T direct) return direct;

            var valueProp = response.GetType().GetProperty("Value");
            if (valueProp?.GetValue(response) is T typedValue)
                return typedValue;

            return default;
        }

        private static Dictionary<string, object> BuildResult(DiagnosticsLogger log, bool success, List<string> stepFilePaths, int geometryCount)
        {
            return new NodeResultBuilder()
                .WithSuccess(success)
                .WithDiagnostics(log)
                .WithProperty("stepFilePaths", stepFilePaths)
                .WithProperty("geometryCount", geometryCount)
                .Build();
        }
    }
}
