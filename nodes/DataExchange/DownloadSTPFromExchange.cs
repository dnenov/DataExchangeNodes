using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Runtime;
using Autodesk.DataExchange;
using Autodesk.DataExchange.DataModels;
using Autodesk.DataExchange.Core.Models;
using Autodesk.DataExchange.Interface;
using DataExchangeNodes.DataExchange;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Download geometries from a Data Exchange and export them to STEP files (.stp or .step)
    /// This allows testing round-trip: upload multiple geometries → download → upload again
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
        public static Dictionary<string, object> Download(
            Exchange exchange,
            string outputDirectory)
        {
            var diagnostics = new List<string>();
            var success = false;
            var stepFilePaths = new List<string>();
            int geometryCount = 0;

            try
            {
                diagnostics.Add($"=== Download STEP Files from DataExchange ===");
                diagnostics.Add($"Exchange: {exchange?.ExchangeTitle} (ID: {exchange?.ExchangeId})");
                diagnostics.Add($"Mode: Download complete exchange as STEP");

                if (exchange == null)
                {
                    diagnostics.Add("✗ ERROR: Exchange is null");
                    return CreateErrorResult(diagnostics, stepFilePaths, geometryCount);
                }

                // Get Client instance using centralized DataExchangeClient
                var client = DataExchangeClient.GetClient();
                if (client == null)
                {
                    diagnostics.Add("✗ ERROR: Could not get Client instance. Make sure you have selected an Exchange first using the SelectExchangeElements node.");
                    return CreateErrorResult(diagnostics, stepFilePaths, geometryCount);
                }

                if (!(client is IClient iClient))
                {
                    diagnostics.Add($"✗ ERROR: Client instance is not IClient: {client.GetType().FullName}");
                    return CreateErrorResult(diagnostics, stepFilePaths, geometryCount);
                }

                diagnostics.Add($"✓ Found Client instance: {client.GetType().FullName}");

                // Create DataExchangeIdentifier
                var identifier = new DataExchangeIdentifier
                {
                    ExchangeId = exchange.ExchangeId,
                    CollectionId = exchange.CollectionId
                };

                if (!string.IsNullOrEmpty(exchange.HubId))
                {
                    identifier.HubId = exchange.HubId;
                }

                diagnostics.Add($"✓ Created DataExchangeIdentifier");

                // Get ElementDataModel
                var stopwatch = Stopwatch.StartNew();
                var elementDataModelResponse = iClient.GetElementDataModelAsync(identifier).Result;
                stopwatch.Stop();
                diagnostics.Add($"⏱️ GetElementDataModelAsync: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");

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
                    diagnostics.Add("✗ ERROR: Could not get ElementDataModel");
                    return CreateErrorResult(diagnostics, stepFilePaths, geometryCount);
                }

                diagnostics.Add($"✓ Got ElementDataModel: {elementDataModel.GetType().FullName}");

                // Use official SDK method: DownloadCompleteExchangeAsSTEP
                // This downloads the ENTIRE exchange as a single STEP file (official SDK way)
                diagnostics.Add($"\nUsing official SDK method: DownloadCompleteExchangeAsSTEP");
                diagnostics.Add($"  Note: This downloads the complete exchange as a single STEP file");
                
                stopwatch.Restart();
                
                // Check if Client has DownloadCompleteExchangeAsSTEP method
                var downloadStepMethod = iClient.GetType().GetMethod(
                    "DownloadCompleteExchangeAsSTEP",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(DataExchangeIdentifier), typeof(string), typeof(CancellationToken) },
                    null);
                if (downloadStepMethod == null)
                {
                    diagnostics.Add($"✗ ERROR: DownloadCompleteExchangeAsSTEP method not found on Client");
                    diagnostics.Add($"  Available methods: {string.Join(", ", iClient.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name.Contains("Download")).Select(m => m.Name))}");
                    return CreateErrorResult(diagnostics, stepFilePaths, geometryCount);
                }
                
                // Call the official SDK method (returns IResponse<string>)
                var downloadResponse = downloadStepMethod.Invoke(iClient, new object[] { identifier, null, CancellationToken.None });
                var stepFilePath = ExtractResponseValue<string>(downloadResponse, diagnostics);
                
                stopwatch.Stop();
                diagnostics.Add($"⏱️ DownloadCompleteExchangeAsSTEP: {stopwatch.ElapsedMilliseconds}ms ({stopwatch.Elapsed.TotalSeconds:F3}s)");
                
                if (string.IsNullOrEmpty(stepFilePath) || !File.Exists(stepFilePath))
                {
                    diagnostics.Add($"✗ ERROR: DownloadCompleteExchangeAsSTEP returned invalid path: {stepFilePath}");
                    return CreateErrorResult(diagnostics, stepFilePaths, geometryCount);
                }
                
                // Determine output path: accept either a directory or a full .stp/.step file path
                string outputFilePath;
                if (string.IsNullOrEmpty(outputDirectory))
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes", "DownloadedSTEP");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);
                    outputDirectory = tempDir;
                }
                else
                {
                    outputDirectory = Path.GetFullPath(outputDirectory);
                }

                var outputExtension = Path.GetExtension(outputDirectory)?.ToLowerInvariant();
                if (outputExtension == ".stp" || outputExtension == ".step")
                {
                    // Treat input as a full file path
                    var outputDir = Path.GetDirectoryName(outputDirectory);
                    if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                        Directory.CreateDirectory(outputDir);
                    outputFilePath = outputDirectory;
                    diagnostics.Add($"✓ Output path treated as file: {outputFilePath}");
                }
                else
                {
                    // Treat input as a directory
                    if (!Directory.Exists(outputDirectory))
                        Directory.CreateDirectory(outputDirectory);
                    var safeTitle = (exchange.ExchangeTitle ?? "Exchange").Replace(" ", "_").Replace(":", "_");
                    var outputFileName = $"{safeTitle}_{exchange.ExchangeId.Substring(0, 8)}.stp";
                    outputFilePath = Path.Combine(outputDirectory, outputFileName);
                }
                
                File.Copy(stepFilePath, outputFilePath, overwrite: true);
                stepFilePaths.Add(outputFilePath);
                
                diagnostics.Add($"✓ Downloaded complete exchange as STEP file");
                diagnostics.Add($"  Source: {stepFilePath}");
                diagnostics.Add($"  Saved to: {outputFilePath}");
                
                // Get geometry count from ElementDataModel
                try
                {
                    var elementsProperty = typeof(ElementDataModel).GetProperty("Elements", BindingFlags.Public | BindingFlags.Instance);
                    if (elementsProperty != null)
                    {
                        var elements = elementsProperty.GetValue(elementDataModel) as System.Collections.IEnumerable;
                        if (elements != null)
                        {
                            var elementsList = elements.Cast<object>().ToList();
                            var elementList = new List<Autodesk.DataExchange.DataModels.Element>();
                            foreach (var elem in elementsList)
                            {
                                if (elem is Autodesk.DataExchange.DataModels.Element element)
                                {
                                    elementList.Add(element);
                                }
                            }
                            
                            if (elementList.Count > 0)
                            {
                                var geometriesDict = elementDataModel.GetElementGeometriesAsync(elementList, CancellationToken.None, null).Result;
                                if (geometriesDict != null)
                                {
                                    geometryCount = geometriesDict.Values.Sum(g => g?.Count() ?? 0);
                                    diagnostics.Add($"  Total geometries in exchange: {geometryCount}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ Could not count geometries: {ex.Message}");
                    geometryCount = 1; // Assume at least 1 since file was downloaded
                }

                success = stepFilePaths.Count > 0;
                if (success)
                {
                    diagnostics.Add($"✓ Download completed successfully");
                }
                else
                {
                    diagnostics.Add($"✗ ERROR: No STEP files were created");
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ ERROR: {ex.GetType().Name}: {ex.Message}");
                diagnostics.Add($"Stack: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"Inner: {ex.InnerException.Message}");
                }
            }

            return new Dictionary<string, object>
            {
                { "success", success },
                { "diagnostics", string.Join("\n", diagnostics) },
                { "stepFilePaths", stepFilePaths },
                { "geometryCount", geometryCount }
            };
        }


        /// <summary>
        /// Creates an error result dictionary
        /// </summary>
        private static Dictionary<string, object> CreateErrorResult(List<string> diagnostics, List<string> stepFilePaths, int geometryCount)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "diagnostics", string.Join("\n", diagnostics) },
                { "stepFilePaths", stepFilePaths },
                { "geometryCount", geometryCount }
            };
        }

        private static T ExtractResponseValue<T>(object response, List<string> diagnostics)
        {
            if (response == null)
            {
                return default;
            }

            // Direct cast
            if (response is T direct)
            {
                return direct;
            }

            // Try IResponse<T> pattern: Value property
            var responseType = response.GetType();
            var valueProp = responseType.GetProperty("Value");
            if (valueProp != null)
            {
                var value = valueProp.GetValue(response);
                diagnostics?.Add($"  Value property found, value type: {value?.GetType().FullName ?? "null"}");

                if (value is T typedValue)
                {
                    return typedValue;
                }
            }

            return default;
        }
    }
}
