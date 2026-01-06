using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;

// Required for IntPtr and ACIS pointer operations
using System.Runtime.InteropServices;
using System.Reflection;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Load geometry from a DataExchange using ProtoGeometry SMB file APIs
    /// </summary>
    public static class LoadGeometryFromExchange
    {
        // Singleton pattern - auth provider registered by SelectExchangeElements view
        private static Func<string> _getTokenFunc = null;

        /// <summary>
        /// Registers the token provider function (called by SelectExchangeElements view)
        /// </summary>
        [IsVisibleInDynamoLibrary(false)]
        public static void RegisterAuthProvider(Func<string> getTokenFunc)
        {
            _getTokenFunc = getTokenFunc;
        }

        /// <summary>
        /// Loads geometry from a DataExchange as Dynamo geometry objects.
        /// Downloads SMB files from DataExchange and reads them using ProtoGeometry APIs.
        /// Authentication is handled automatically using Dynamo's login - no token required!
        /// </summary>
        /// <param name="exchange">Exchange object from SelectExchange node</param>
        /// <param name="unit">Unit type for geometry (default: "kUnitType_CentiMeter"). Options: kUnitType_CentiMeter, kUnitType_Meter, kUnitType_Feet, kUnitType_Inch</param>
        /// <returns>Dictionary with "geometries" (list of Dynamo geometry), "log" (diagnostic messages), and "success" (boolean)</returns>
        [MultiReturn(new[] { "geometries", "log", "success" })]
        public static Dictionary<string, object> Load(
            Exchange exchange,
            string unit = "kUnitType_CentiMeter")
        {
            var geometries = new List<Geometry>();
            var diagnostics = new List<string>();
            bool success = false;

            try
            {
                diagnostics.Add("=== DataExchange Geometry Loader (ProtoGeometry SMB) ===");
                diagnostics.Add($"Exchange: {exchange?.ExchangeTitle ?? "null"}");
                diagnostics.Add($"Unit: {unit}");

                // Validate inputs
                if (exchange == null)
                    throw new ArgumentNullException(nameof(exchange), "Exchange cannot be null");

                if (string.IsNullOrEmpty(exchange.ExchangeId))
                    throw new ArgumentException("ExchangeId is required", nameof(exchange));

                if (string.IsNullOrEmpty(exchange.CollectionId))
                    throw new ArgumentException("CollectionId is required", nameof(exchange));

                // Get token from registered auth provider
                if (_getTokenFunc == null)
                    throw new InvalidOperationException("Authentication not configured. Please use SelectExchange node first to log in.");

                var accessToken = _getTokenFunc();
                if (string.IsNullOrEmpty(accessToken))
                    throw new InvalidOperationException("Not logged in. Please log in to Dynamo first.");

                diagnostics.Add($"✓ Authenticated (token length: {accessToken.Length} chars)");

                // Download SMB file from DataExchange
                // Note: Using .Result blocks the thread, but Dynamo ZeroTouch nodes are synchronous
                // This is acceptable for now as Dynamo will handle the blocking appropriately
                var smbFilePath = DownloadSMBFile(exchange, accessToken, diagnostics).GetAwaiter().GetResult();
                diagnostics.Add($"✓ Downloaded SMB file: {smbFilePath}");

                // Load geometry using ProtoGeometry SMB APIs
                geometries = LoadGeometryFromSMB(smbFilePath, unit, diagnostics);
                diagnostics.Add($"✓ Loaded {geometries.Count} geometry object(s)");

                success = geometries.Count > 0;
                diagnostics.Add($"\n=== Load Completed: {geometries.Count} geometries ===");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"\nERROR: {ex.Message}");
                diagnostics.Add($"Type: {ex.GetType().Name}");
                diagnostics.Add($"Stack: {ex.StackTrace}");

                if (ex.InnerException != null)
                {
                    diagnostics.Add($"Inner: {ex.InnerException.Message}");
                }
            }

            return new Dictionary<string, object>
            {
                { "geometries", geometries },
                { "log", string.Join("\n", diagnostics) },
                { "success", success }
            };
        }

        /// <summary>
        /// Downloads SMB file from DataExchange API
        /// First tries to use internal Client SDK methods via reflection, then falls back to HTTP API
        /// </summary>
        private static async Task<string> DownloadSMBFile(
            Exchange exchange,
            string accessToken,
            List<string> diagnostics)
        {
            try
            {
                // Create temp directory for SMB file
                var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                var smbFilePath = Path.Combine(tempDir, $"Exchange_{exchange.ExchangeId}_{exchange.CollectionId}.smb");

                // First, try to use the DataExchange Client SDK via reflection to access internal/private methods
                diagnostics.Add("  Attempting to use DataExchange Client SDK (reflection)...");
                var clientDownloadResult = await TryDownloadSMBViaClientReflection(exchange, smbFilePath, diagnostics);
                
                if (clientDownloadResult)
                {
                    if (File.Exists(smbFilePath))
                    {
                        diagnostics.Add($"  ✓ Downloaded via Client SDK: {new FileInfo(smbFilePath).Length} bytes");
                        return smbFilePath;
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ Client SDK reported success but file not found: {smbFilePath}");
                    }
                }

                // Fallback to HTTP API if Client SDK method not found or failed
                // NOTE: HTTP API fallback is disabled as it doesn't work - need to fix GetBinaryAssetDownloadInfoAsync instead
                //diagnostics.Add("  Falling back to HTTP API download...");
                //await DownloadSMBViaHttpAPI(exchange, accessToken, smbFilePath, diagnostics);
                
                // Verify file was actually downloaded
                if (!File.Exists(smbFilePath))
                {
                    throw new IOException($"SMB file was not downloaded via Client SDK. GetBinaryAssetDownloadInfoAsync needs proper BinaryDownloadBatchRequest structure.");
                }
                
                return smbFilePath;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ Failed to download SMB file: {ex.Message}");
                throw new IOException($"Failed to download SMB file from DataExchange: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Attempts to download SMB file using DataExchange Client SDK via reflection
        /// Inspects internal/private methods to find SMB download functionality
        /// </summary>
        private static async Task<bool> TryDownloadSMBViaClientReflection(
            Exchange exchange,
            string smbFilePath,
            List<string> diagnostics)
        {
            try
            {
                // Get Client instance
                var client = TryGetClientInstance(diagnostics);
                if (client == null)
                {
                    diagnostics.Add("  ⚠️ Client instance is null - ensure SelectExchangeElements node has been used first");
                    diagnostics.Add("  This is expected if the SelectExchangeElements node hasn't been opened yet");
                    return false;
                }

                diagnostics.Add($"  ✓ Found Client instance: {client.GetType().FullName}");

                // Inspect all methods (public, private, internal, protected, static, instance)
                var clientType = client.GetType();
                var allMethods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | 
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);

                // Try each download method in sequence
                // Approach B (Parked): GetBinaryAssetDownloadInfoAsync requires binary asset IDs from ElementDataModel first
                //if (await TryDownloadViaBinaryAssetDownloadInfoAsync(client, exchange, smbFilePath, allMethods, diagnostics))
                //    return true;

                // Approach A (WRONG - returns STEP, not SMB): GetElementDataModel → GetElementGeometriesAsync 
                // This returns STEP files, not SMB files. Disabled.
                //if (await TryDownloadViaElementDataModelAsync(client, exchange, smbFilePath, allMethods, diagnostics))
                //    return true;

                //if (await TryDownloadViaGetAllAssetInfosWithTranslatedGeometryPath(client, exchange, smbFilePath, allMethods, diagnostics))
                //    return true;

                // Look for direct SMB download methods
                var candidateMethods = allMethods.Where(m =>
                {
                    var name = m.Name.ToLowerInvariant();
                    return name.Contains("smb") || 
                           (name.Contains("download") && !name.Contains("step")) || 
                           name.Contains("getfile") ||
                           (name.Contains("format") && name.Contains("smb")) ||
                           (name.Contains("convert") && name.Contains("smb"));
                }).ToList();

                var smbDownloadMethod = candidateMethods.FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    var returnType = m.ReturnType;
                    
                    bool hasExchangeParams = parameters.Any(p => 
                        p.ParameterType.Name.Contains("Exchange") || 
                        p.Name.ToLowerInvariant().Contains("exchange") ||
                        p.Name.ToLowerInvariant().Contains("collection"));
                    
                    bool returnsPath = returnType == typeof(string) || 
                                      returnType == typeof(Task<string>) ||
                                      (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>));
                    
                    return hasExchangeParams && (m.Name.ToLowerInvariant().Contains("smb") || 
                                                m.Name.ToLowerInvariant().Contains("download"));
                });

                if (await TryDownloadViaDirectSmbMethod(client, exchange, smbFilePath, smbDownloadMethod, diagnostics))
                    return true;

                if (await TryDownloadViaParseGeometryAssetBinaryToIntermediateGeometry(client, exchange, smbFilePath, allMethods, diagnostics))
                    return true;

                // Log internal format conversion methods for diagnostics
                LogInternalFormatConversionMethods(clientType, allMethods, diagnostics);

                return false;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Reflection inspection failed: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Tries to get the Client instance from various sources
        /// </summary>
        private static object TryGetClientInstance(List<string> diagnostics)
        {
            object client = null;
            
            // Method 1: Try to get from SelectExchangeElementsViewCustomization
            try
            {
                var viewCustomizationType = Type.GetType("DataExchangeNodes.NodeViews.DataExchange.SelectExchangeElementsViewCustomization, ExchangeNodes.NodeViews");
                if (viewCustomizationType != null)
                {
                    var clientProperty = viewCustomizationType.GetProperty("ClientInstance", BindingFlags.Public | BindingFlags.Static);
                    if (clientProperty != null)
                    {
                        client = clientProperty.GetValue(null);
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Could not access ClientInstance property: {ex.Message}");
            }

            // Method 2: Try to find Client type directly and look for static instances
            if (client == null)
            {
                try
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            var foundClientType = assembly.GetTypes().FirstOrDefault(t => 
                                t.Name == "Client" && 
                                t.Namespace != null && 
                                t.Namespace.Contains("DataExchange"));
                            
                            if (foundClientType != null)
                            {
                                diagnostics.Add($"  Found Client type: {foundClientType.FullName}");
                                var staticFields = foundClientType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                                var instanceField = staticFields.FirstOrDefault(f => 
                                    f.FieldType == foundClientType || 
                                    f.FieldType.IsAssignableFrom(foundClientType));
                                
                                if (instanceField != null)
                                {
                                    client = instanceField.GetValue(null);
                                    if (client != null)
                                    {
                                        diagnostics.Add($"  Found Client via static field: {instanceField.Name}");
                                        break;
                                    }
                                }
                            }
                        }
                        catch { /* Skip assemblies we can't inspect */ }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ Could not find Client via type search: {ex.Message}");
                }
            }

            return client;
        }

        /// <summary>
        /// Attempts to download SMB using GetBinaryAssetDownloadInfoAsync
        /// 
        /// APPROACH B (Parked for now - see Approach A in TryDownloadViaElementDataModelAsync):
        /// This is an indirect approach that requires:
        /// 1. First get ElementDataModel to find geometry assets
        /// 2. Extract BinaryReference.Id from each geometry asset
        /// 3. Create BinaryDownloadBatchRequest with List&lt;BinaryDownloadRequestItem&gt; where each item has:
        ///    - Id (required): Binary asset ID from geometry asset's BinaryReference.Id
        ///    - RevisionId (optional): Revision ID
        ///    - ContentType (optional): MIME type, defaults to "text/plain"
        /// 4. Call GetBinaryAssetDownloadInfoAsync with the batch request
        /// 5. Download binary files from the returned URLs
        /// 6. Convert binary → SMB using ParseGeometryAssetBinaryToIntermediateGeometry
        /// 
        /// However, Approach A (TryDownloadViaElementDataModelAsync) is simpler:
        /// - Get ElementDataModel → GetElementGeometriesAsync → Extract SMB file paths directly
        /// - No conversion needed, SMB files are already available
        /// 
        /// References from fdx-connector:
        /// - DownloadGeometryHelper.cs line 189-209: Shows how to create BinaryDownloadRequestItem
        /// - ElementDataModel.cs line 1877: Shows adding items with just Id: new BinaryDownloadRequestItem { Id = geometryAsset.BinaryReference.Id }
        /// </summary>
        private static async Task<bool> TryDownloadViaBinaryAssetDownloadInfoAsync(
            object client,
            Exchange exchange,
            string smbFilePath,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            var binaryAssetMethod = allMethods.FirstOrDefault(m => m.Name == "GetBinaryAssetDownloadInfoAsync");
            if (binaryAssetMethod == null)
                return false;

            diagnostics.Add($"  === Attempting to use GetBinaryAssetDownloadInfoAsync ===");
            try
            {
                var binaryRequestType = binaryAssetMethod.GetParameters()
                    .FirstOrDefault(p => p.ParameterType.Name.Contains("BinaryDownload"))?.ParameterType;
                
                if (binaryRequestType != null)
                {
                    diagnostics.Add($"  Found BinaryDownloadBatchRequest type: {binaryRequestType.FullName}");
                    
                    var request = Activator.CreateInstance(binaryRequestType);
                    var props = binaryRequestType.GetProperties();
                    foreach (var prop in props)
                    {
                        diagnostics.Add($"    Property: {prop.Name} ({prop.PropertyType.Name})");
                    }
                    
                    // Populate Binaries collection - this is required for the request
                    var binariesProp = props.FirstOrDefault(p => p.Name == "Binaries");
                    if (binariesProp != null)
                    {
                        var binariesCollection = binariesProp.GetValue(request);
                        if (binariesCollection != null)
                        {
                            diagnostics.Add($"  Found Binaries collection: {binariesCollection.GetType().FullName}");
                            
                            // Check what type of items the collection expects
                            var collectionType = binariesCollection.GetType();
                            if (collectionType.IsGenericType)
                            {
                                var itemType = collectionType.GetGenericArguments()[0];
                                diagnostics.Add($"  Binaries collection expects items of type: {itemType.FullName}");
                                
                                // Try to create a binary request item
                                try
                                {
                                    var binaryItem = Activator.CreateInstance(itemType);
                                    var itemProps = itemType.GetProperties();
                                    diagnostics.Add($"  Binary item properties: {string.Join(", ", itemProps.Select(p => $"{p.Name} ({p.PropertyType.Name})"))}");
                                    
                                    // BinaryDownloadRequestItem requires an Id property (from fdx-connector source)
                                    var idProp = itemProps.FirstOrDefault(p => 
                                        p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
                                    if (idProp != null && idProp.PropertyType == typeof(string))
                                    {
                                        // We don't have a binary asset ID yet - we'd need ElementDataModel first
                                        // For now, this method won't work without knowing the binary asset IDs
                                        diagnostics.Add($"  ⚠️ BinaryDownloadRequestItem requires Id property, but we need ElementDataModel first to get binary asset IDs");
                                        diagnostics.Add($"  ⚠️ Skipping GetBinaryAssetDownloadInfoAsync - need to use ElementDataModel approach instead");
                                        return false;
                                    }
                                    
                                    // Set ContentType if available
                                    var contentTypeProp = itemProps.FirstOrDefault(p => 
                                        p.Name.Equals("ContentType", StringComparison.OrdinalIgnoreCase));
                                    if (contentTypeProp != null && contentTypeProp.PropertyType == typeof(string))
                                    {
                                        contentTypeProp.SetValue(binaryItem, "application/octet-stream");
                                        diagnostics.Add($"  Set ContentType to 'application/octet-stream'");
                                    }
                                    
                                    // Add item to collection
                                    var addMethod = collectionType.GetMethod("Add");
                                    if (addMethod != null)
                                    {
                                        addMethod.Invoke(binariesCollection, new object[] { binaryItem });
                                        diagnostics.Add($"  Added binary item to Binaries collection");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    diagnostics.Add($"  ⚠️ Error creating binary item: {ex.Message}");
                                }
                            }
                        }
                    }
                    
                    var formatProp = props.FirstOrDefault(p => 
                        p.Name.ToLowerInvariant().Contains("format") ||
                        p.Name.ToLowerInvariant().Contains("type") ||
                        p.Name.ToLowerInvariant().Contains("asset"));
                    
                    if (formatProp != null && formatProp.PropertyType == typeof(string))
                    {
                        formatProp.SetValue(request, "SMB");
                        diagnostics.Add($"  Set format property '{formatProp.Name}' to 'SMB'");
                    }
                    
                    var methodParams = binaryAssetMethod.GetParameters();
                    object[] invokeParams = new object[methodParams.Length];
                    
                    for (int i = 0; i < methodParams.Length; i++)
                    {
                        var param = methodParams[i];
                        if (param.ParameterType == typeof(string))
                        {
                            if (param.Name.ToLowerInvariant().Contains("collection"))
                                invokeParams[i] = exchange.CollectionId;
                            else if (param.Name.ToLowerInvariant().Contains("exchange"))
                                invokeParams[i] = exchange.ExchangeId;
                            else
                                invokeParams[i] = exchange.ExchangeId;
                        }
                        else if (param.ParameterType == binaryRequestType)
                        {
                            invokeParams[i] = request;
                        }
                        else
                        {
                            invokeParams[i] = null;
                        }
                    }
                    
                    diagnostics.Add($"  Invoking GetBinaryAssetDownloadInfoAsync...");
                    diagnostics.Add($"  Method parameters: {string.Join(", ", methodParams.Select(p => $"{p.ParameterType.Name} {p.Name} = {(invokeParams[Array.IndexOf(methodParams, p)]?.ToString() ?? "null")}"))}");
                    
                    var result = binaryAssetMethod.Invoke(client, invokeParams);
                    
                    // Add diagnostics about what we got back
                    diagnostics.Add($"  Invoke result: {result?.GetType().FullName ?? "null"}");
                    
                    if (result == null)
                    {
                        diagnostics.Add($"  ⚠️ GetBinaryAssetDownloadInfoAsync returned null");
                        return false;
                    }

                    if (!result.GetType().IsGenericType || 
                        result.GetType().GetGenericTypeDefinition() != typeof(Task<>))
                    {
                        diagnostics.Add($"  ⚠️ GetBinaryAssetDownloadInfoAsync did not return a Task<>, returned: {result.GetType().FullName}");
                        
                        // Handle AsyncStateMachineBox - extract the Result property
                        var resultType = result.GetType();
                        PropertyInfo[] allProps = resultType.GetProperties();
                        diagnostics.Add($"  Available properties on result: {string.Join(", ", allProps.Select(p => $"{p.Name} ({p.PropertyType.Name})"))}");
                        
                        // Try to await AsyncStateMachineBox using dynamic GetAwaiter().GetResult()
                        diagnostics.Add($"  Attempting to await AsyncStateMachineBox using GetAwaiter()...");
                        try
                        {
                            // AsyncStateMachineBox is awaitable - use dynamic to call GetAwaiter().GetResult()
                            dynamic asyncResult = result;
                            var binaryResponse = asyncResult.GetAwaiter().GetResult();
                            
                            if (binaryResponse != null)
                            {
                                diagnostics.Add($"  ✓ Extracted BinaryDownloadBatchResponse: {binaryResponse.GetType().FullName}");
                                var responseType = binaryResponse.GetType();
                                PropertyInfo[] responseProps = responseType.GetProperties();
                                diagnostics.Add($"  BinaryDownloadBatchResponse properties: {string.Join(", ", responseProps.Select(p => $"{p.Name} ({p.PropertyType.Name})"))}");
                                
                                // BinaryDownloadBatchResponse likely has a collection of download info items
                                // Look for properties that might contain download URLs (Binaries, Items, Data, etc.)
                                    var responseBinariesProp = responseType.GetProperty("Binaries") ?? 
                                                          responseType.GetProperty("Items") ??
                                                          responseType.GetProperty("Data");
                                
                                if (responseBinariesProp != null)
                                {
                                    var binaries = responseBinariesProp.GetValue(binaryResponse);
                                    if (binaries != null && binaries is System.Collections.IEnumerable)
                                    {
                                        var enumerable = binaries as System.Collections.IEnumerable;
                                        diagnostics.Add($"  Found binaries collection, iterating...");
                                        foreach (var binary in enumerable)
                                        {
                                            if (binary != null)
                                            {
                                                var binaryType = binary.GetType();
                                                var urlProp = binaryType.GetProperty("Url") ?? 
                                                             binaryType.GetProperty("DownloadUrl") ?? 
                                                             binaryType.GetProperty("Path") ??
                                                             binaryType.GetProperty("FilePath");
                                                
                                                if (urlProp != null)
                                                {
                                                    var url = urlProp.GetValue(binary)?.ToString();
                                                    if (!string.IsNullOrEmpty(url))
                                                    {
                                                        diagnostics.Add($"  Found download URL: {url}");
                                                        using (var httpClient = new HttpClient())
                                                        {
                                                            var response = await httpClient.GetAsync(url);
                                                            response.EnsureSuccessStatusCode();
                                                            using (var fileStream = new FileStream(smbFilePath, FileMode.Create))
                                                            {
                                                                await response.Content.CopyToAsync(fileStream);
                                                            }
                                                            diagnostics.Add($"  ✓ Downloaded SMB via GetBinaryAssetDownloadInfoAsync");
                                                            return true;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                                
                                // Also try direct URL properties on the response itself
                                var urlPropDirect = responseType.GetProperty("Url") ?? 
                                                   responseType.GetProperty("DownloadUrl") ??
                                                   responseType.GetProperty("Path");
                                if (urlPropDirect != null)
                                {
                                    var url = urlPropDirect.GetValue(binaryResponse)?.ToString();
                                    if (!string.IsNullOrEmpty(url))
                                    {
                                        diagnostics.Add($"  Found direct download URL: {url}");
                                        using (var httpClient = new HttpClient())
                                        {
                                            var response = await httpClient.GetAsync(url);
                                            response.EnsureSuccessStatusCode();
                                            using (var fileStream = new FileStream(smbFilePath, FileMode.Create))
                                            {
                                                await response.Content.CopyToAsync(fileStream);
                                            }
                                            diagnostics.Add($"  ✓ Downloaded SMB via GetBinaryAssetDownloadInfoAsync");
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add($"  ⚠️ Error awaiting AsyncStateMachineBox: {ex.Message}");
                            if (ex.InnerException != null)
                            {
                                diagnostics.Add($"  Inner exception: {ex.InnerException.Message}");
                            }
                            diagnostics.Add($"  Stack trace: {ex.StackTrace}");
                        }
                        
                        // Fallback: Check if it's already the result we need (not wrapped in Task)
                        var urlPropFallback = resultType.GetProperty("Url") ?? 
                                             resultType.GetProperty("DownloadUrl") ?? 
                                             resultType.GetProperty("Path") ??
                                             resultType.GetProperty("FilePath");
                        
                        if (urlPropFallback != null)
                        {
                            var url = urlPropFallback.GetValue(result)?.ToString();
                            if (!string.IsNullOrEmpty(url))
                            {
                                diagnostics.Add($"  Found download URL (direct): {url}");
                                using (var httpClient = new HttpClient())
                                {
                                    var response = await httpClient.GetAsync(url);
                                    response.EnsureSuccessStatusCode();
                                    using (var fileStream = new FileStream(smbFilePath, FileMode.Create))
                                    {
                                        await response.Content.CopyToAsync(fileStream);
                                    }
                                    diagnostics.Add($"  ✓ Downloaded SMB via GetBinaryAssetDownloadInfoAsync");
                                    return true;
                                }
                            }
                        }
                        
                        return false;
                    }

                    // It is a Task<> - await it
                    diagnostics.Add($"  Result is Task<>, awaiting...");
                    try
                    {
                        var taskResult = ((dynamic)result).GetAwaiter().GetResult();
                        diagnostics.Add($"  Task result type: {taskResult?.GetType().FullName ?? "null"}");
                        
                        if (taskResult == null)
                        {
                            diagnostics.Add($"  ⚠️ Task result is null");
                            return false;
                        }
                        
                        var resultType = taskResult.GetType();
                        
                        // Log all available properties to understand the structure
                        PropertyInfo[] allProps = resultType.GetProperties();
                        diagnostics.Add($"  Available properties on task result: {string.Join(", ", allProps.Select(p => $"{p.Name} ({p.PropertyType.Name})"))}");
                        
                        // Check for response pattern (IResponse<T>)
                        var isSuccessProp = resultType.GetProperty("IsSuccess") ?? 
                                           resultType.GetProperty("Success");
                        if (isSuccessProp != null)
                        {
                            var isSuccess = (bool)isSuccessProp.GetValue(taskResult);
                            diagnostics.Add($"  Response IsSuccess: {isSuccess}");
                            
                            if (!isSuccess)
                            {
                                var errorProp = resultType.GetProperty("Error") ?? 
                                               resultType.GetProperty("Exception") ??
                                               resultType.GetProperty("Message");
                                if (errorProp != null)
                                {
                                    var error = errorProp.GetValue(taskResult);
                                    diagnostics.Add($"  ⚠️ Response indicates failure: {error}");
                                }
                                return false;
                            }
                            
                            // Check if there's a Value property (IResponse<T> pattern)
                            var valueProp = resultType.GetProperty("Value");
                            if (valueProp != null)
                            {
                                diagnostics.Add($"  Found Value property, extracting...");
                                try
                                {
                                    taskResult = valueProp.GetValue(taskResult);
                                    if (taskResult == null)
                                    {
                                        diagnostics.Add($"  ⚠️ Value property is null");
                                        return false;
                                    }
                                    resultType = taskResult.GetType();
                                    diagnostics.Add($"  Extracted Value type: {resultType.FullName}");
                                    allProps = resultType.GetProperties();
                                    diagnostics.Add($"  Available properties on Value: {string.Join(", ", allProps.Select(p => $"{p.Name} ({p.PropertyType.Name})"))}");
                                }
                                catch (Exception ex)
                                {
                                    diagnostics.Add($"  ⚠️ Error extracting Value: {ex.Message}");
                                    return false;
                                }
                            }
                        }
                        
                        var urlProp = resultType.GetProperty("Url") ?? 
                                     resultType.GetProperty("DownloadUrl") ?? 
                                     resultType.GetProperty("Path") ??
                                     resultType.GetProperty("FilePath");
                        
                        if (urlProp == null)
                        {
                            diagnostics.Add($"  ⚠️ Could not find Url/DownloadUrl/Path/FilePath property on result");
                            diagnostics.Add($"  Result type: {resultType.FullName}");
                            string[] allPropNames = allProps.Select(p => p.Name).ToArray();
                            diagnostics.Add($"  All properties: {string.Join(", ", allPropNames)}");
                            return false;
                        }
                        
                        var url = urlProp.GetValue(taskResult)?.ToString();
                        if (string.IsNullOrEmpty(url))
                        {
                            diagnostics.Add($"  ⚠️ URL property found but is null or empty");
                            return false;
                        }
                        
                        diagnostics.Add($"  ✓ Found download URL: {url}");
                        using (var httpClient = new HttpClient())
                        {
                            var response = await httpClient.GetAsync(url);
                            response.EnsureSuccessStatusCode();
                            using (var fileStream = new FileStream(smbFilePath, FileMode.Create))
                            {
                                await response.Content.CopyToAsync(fileStream);
                            }
                            diagnostics.Add($"  ✓ Downloaded SMB via GetBinaryAssetDownloadInfoAsync");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"  ⚠️ Exception while awaiting task: {ex.Message}");
                        diagnostics.Add($"  Exception type: {ex.GetType().Name}");
                        if (ex.InnerException != null)
                        {
                            diagnostics.Add($"  Inner exception: {ex.InnerException.Message}");
                        }
                        diagnostics.Add($"  Stack: {ex.StackTrace}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Failed to use GetBinaryAssetDownloadInfoAsync: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to download SMB using GetElementDataModelAsync and GetElementGeometriesAsync
        /// </summary>
        private static async Task<bool> TryDownloadViaElementDataModelAsync(
            object client,
            Exchange exchange,
            string smbFilePath,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            diagnostics.Add($"  === Attempting to get SMB via ElementDataModel and GetAssetInfoForGeometryAsset ===");
            try
            {
                var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                if (identifierType == null)
                    return false;

                var identifier = Activator.CreateInstance(identifierType);
                var exchangeIdProp = identifierType.GetProperty("ExchangeId");
                var collectionIdProp = identifierType.GetProperty("CollectionId");
                var hubIdProp = identifierType.GetProperty("HubId");
                
                if (exchangeIdProp != null) exchangeIdProp.SetValue(identifier, exchange.ExchangeId);
                if (collectionIdProp != null) collectionIdProp.SetValue(identifier, exchange.CollectionId);
                if (hubIdProp != null && !string.IsNullOrEmpty(exchange.HubId))
                {
                    hubIdProp.SetValue(identifier, exchange.HubId);
                    diagnostics.Add($"  Set HubId: {exchange.HubId}");
                }
                else
                {
                    diagnostics.Add($"  ⚠️ HubId not set - Exchange.HubId is: {exchange.HubId ?? "null"}");
                }

                var getElementDataModelMethod = allMethods.FirstOrDefault(m => 
                    m.Name == "GetElementDataModelAsync" && m.IsPublic);
                
                if (getElementDataModelMethod == null)
                    return false;

                diagnostics.Add($"  Calling GetElementDataModelAsync...");
                var elementDataModelResult = getElementDataModelMethod.Invoke(client, new object[] { identifier, CancellationToken.None });
                
                if (elementDataModelResult != null && elementDataModelResult.GetType().IsGenericType)
                {
                    var taskResult = ((dynamic)elementDataModelResult).GetAwaiter().GetResult();
                    diagnostics.Add($"  Task result type: {taskResult?.GetType().Name ?? "null"}");
                    
                    object elementDataModel = null;
                    var responseType = taskResult?.GetType();
                    
                    if (responseType != null)
                    {
                        diagnostics.Add($"  Response type: {responseType.FullName}");
                        
                        var isSuccessProp = responseType.GetProperty("IsSuccess") ?? 
                                           responseType.GetProperty("Success") ??
                                           responseType.GetProperty("IsSuccessful");
                        var errorProp = responseType.GetProperty("Error") ?? 
                                       responseType.GetProperty("Exception");
                        
                        if (isSuccessProp != null)
                        {
                            var isSuccess = (bool)isSuccessProp.GetValue(taskResult);
                            diagnostics.Add($"  Response IsSuccess: {isSuccess}");
                            
                            if (!isSuccess && errorProp != null)
                            {
                                var error = errorProp.GetValue(taskResult);
                                diagnostics.Add($"  Response Error: {error}");
                            }
                        }
                        
                        var valueProp = responseType.GetProperty("Value");
                        if (valueProp != null)
                        {
                            diagnostics.Add($"  Found Value property, type: {valueProp.PropertyType.Name}");
                            try
                            {
                                elementDataModel = valueProp.GetValue(taskResult);
                                if (elementDataModel != null)
                                {
                                    diagnostics.Add($"  Successfully got Value: {elementDataModel.GetType().Name}");
                                }
                                else
                                {
                                    diagnostics.Add($"  ⚠️ Value property returned null");
                                }
                            }
                            catch (Exception ex)
                            {
                                diagnostics.Add($"  ⚠️ Could not get Value property: {ex.Message}");
                                diagnostics.Add($"  Exception type: {ex.GetType().Name}");
                                if (ex.InnerException != null)
                                {
                                    diagnostics.Add($"  Inner exception: {ex.InnerException.Message}");
                                }
                                
                                var allProps = responseType.GetProperties();
                                var propNames = new List<string>();
                                foreach (var prop in allProps)
                                {
                                    propNames.Add(prop.Name);
                                }
                                diagnostics.Add($"  Available properties on Response: {string.Join(", ", propNames)}");
                                elementDataModel = taskResult;
                            }
                        }
                        else
                        {
                            diagnostics.Add($"  No Value property found, using result directly");
                            elementDataModel = taskResult;
                        }
                    }
                    
                    if (elementDataModel != null)
                    {
                        diagnostics.Add($"  Got ElementDataModel: {elementDataModel.GetType().Name}");
                        
                        var elementDataModelType = elementDataModel.GetType();
                        var getGeometriesMethod = elementDataModelType.GetMethod("GetElementGeometriesAsync", 
                            BindingFlags.Public | BindingFlags.Instance);
                        
                        try
                        {
                            var elementsProp = elementDataModelType.GetProperty("Elements");
                            var elements = elementsProp?.GetValue(elementDataModel);
                            
                            if (elements != null)
                            {
                                int elementCount = 0;
                                if (elements is System.Collections.ICollection collection)
                                {
                                    elementCount = collection.Count;
                                }
                                else if (elements is System.Collections.IEnumerable enumerable)
                                {
                                    elementCount = enumerable.Cast<object>().Count();
                                }
                                
                                diagnostics.Add($"  Found {elementCount} elements, getting geometries...");
                                
                                dynamic elementDataModelDynamic = elementDataModel;
                                dynamic elementsDynamic = elements;
                                
                                diagnostics.Add($"  Calling GetElementGeometriesAsync using dynamic invocation...");
                                var geometriesTask = elementDataModelDynamic.GetElementGeometriesAsync(
                                    elementsDynamic, 
                                    CancellationToken.None, 
                                    null);
                                
                                var geometriesDict = await geometriesTask;
                                var smbFiles = new List<string>();
                                
                                foreach (var kvp in geometriesDict)
                                {
                                    var elementGeometries = kvp.Value;
                                    
                                    foreach (var geometry in elementGeometries)
                                    {
                                        var geometryType = geometry.GetType();
                                        var filePathProp = geometryType.GetProperty("FilePath");
                                        
                                        if (filePathProp != null)
                                        {
                                            var path = filePathProp.GetValue(geometry)?.ToString();
                                            
                                            if (!string.IsNullOrEmpty(path) && 
                                                path.ToLowerInvariant().EndsWith(".smb") && 
                                                File.Exists(path))
                                            {
                                                smbFiles.Add(path);
                                                diagnostics.Add($"  Found SMB file: {path}");
                                            }
                                            else if (!string.IsNullOrEmpty(path))
                                            {
                                                diagnostics.Add($"  Found geometry file: {path} (type: {geometryType.Name})");
                                            }
                                        }
                                    }
                                }
                                
                                if (smbFiles.Any())
                                {
                                    var firstSmbFile = smbFiles.First();
                                    File.Copy(firstSmbFile, smbFilePath, true);
                                    diagnostics.Add($"  ✓ Copied SMB file from ElementDataModel: {firstSmbFile}");
                                    return true;
                                }
                                else
                                {
                                    diagnostics.Add($"  ⚠️ No SMB files found in geometries (found {geometriesDict.Count} elements with geometries)");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add($"  ⚠️ Dynamic invocation failed: {ex.Message}");
                            diagnostics.Add($"  Exception type: {ex.GetType().Name}");
                            if (ex.InnerException != null)
                            {
                                diagnostics.Add($"  Inner exception: {ex.InnerException.Message}");
                            }
                            diagnostics.Add($"  Stack: {ex.StackTrace}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Failed to get SMB via ElementDataModel: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to download SMB using GetAllAssetInfosWithTranslatedGeometryPath
        /// </summary>
        private static async Task<bool> TryDownloadViaGetAllAssetInfosWithTranslatedGeometryPath(
            object client,
            Exchange exchange,
            string smbFilePath,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            var assetInfoMethod = allMethods.FirstOrDefault(m => m.Name == "GetAllAssetInfosWithTranslatedGeometryPath");
            if (assetInfoMethod == null)
                return false;

            diagnostics.Add($"  === Attempting to use GetAllAssetInfosWithTranslatedGeometryPath ===");
            try
            {
                var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                if (identifierType == null)
                    return false;

                var identifier = Activator.CreateInstance(identifierType);
                var exchangeIdProp = identifierType.GetProperty("ExchangeId");
                var collectionIdProp = identifierType.GetProperty("CollectionId");
                
                if (exchangeIdProp != null) exchangeIdProp.SetValue(identifier, exchange.ExchangeId);
                if (collectionIdProp != null) collectionIdProp.SetValue(identifier, exchange.CollectionId);

                var methodParams = assetInfoMethod.GetParameters();
                object[] invokeParams = new object[methodParams.Length];
                
                for (int i = 0; i < methodParams.Length; i++)
                {
                    var param = methodParams[i];
                    if (param.ParameterType == identifierType || param.ParameterType.IsAssignableFrom(identifierType))
                    {
                        invokeParams[i] = identifier;
                    }
                    else if (param.ParameterType == typeof(string))
                    {
                        invokeParams[i] = param.Name.ToLowerInvariant().Contains("format") ? "SMB" : "";
                    }
                    else if (param.ParameterType.Name.Contains("ExchangeData"))
                    {
                        invokeParams[i] = null;
                    }
                    else if (param.ParameterType == typeof(CancellationToken))
                    {
                        invokeParams[i] = CancellationToken.None;
                    }
                    else
                    {
                        invokeParams[i] = null;
                    }
                }
                
                diagnostics.Add($"  Invoking GetAllAssetInfosWithTranslatedGeometryPath...");
                var result = assetInfoMethod.Invoke(client, invokeParams);
                
                if (result != null && result.GetType().IsGenericType && 
                    result.GetType().GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var taskResult = ((dynamic)result).GetAwaiter().GetResult();
                    diagnostics.Add($"  Result type: {taskResult?.GetType().Name ?? "null"}");
                    
                    if (taskResult != null && taskResult is System.Collections.IEnumerable)
                    {
                        var enumerable = taskResult as System.Collections.IEnumerable;
                        foreach (var item in enumerable)
                        {
                            if (item != null)
                            {
                                var itemType = item.GetType();
                                var pathProp = itemType.GetProperty("Path") ??
                                              itemType.GetProperty("FilePath") ??
                                              itemType.GetProperty("SmbPath") ??
                                              itemType.GetProperty("GeometryPath");
                                
                                if (pathProp != null)
                                {
                                    var path = pathProp.GetValue(item)?.ToString();
                                    if (!string.IsNullOrEmpty(path) && path.ToLowerInvariant().EndsWith(".smb"))
                                    {
                                        if (File.Exists(path))
                                        {
                                            File.Copy(path, smbFilePath, true);
                                            diagnostics.Add($"  ✓ Found SMB file via GetAllAssetInfosWithTranslatedGeometryPath: {path}");
                                            return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Failed to use GetAllAssetInfosWithTranslatedGeometryPath: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Attempts to download SMB using a direct SMB download method found via reflection
        /// </summary>
        private static async Task<bool> TryDownloadViaDirectSmbMethod(
            object client,
            Exchange exchange,
            string smbFilePath,
            MethodInfo smbDownloadMethod,
            List<string> diagnostics)
        {
            if (smbDownloadMethod == null)
                return false;

            diagnostics.Add($"  ✓ Found potential SMB download method: {smbDownloadMethod.Name}");
            
            try
            {
                var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                if (identifierType == null)
                    return false;

                var identifier = Activator.CreateInstance(identifierType);
                var exchangeIdProp = identifierType.GetProperty("ExchangeId");
                var collectionIdProp = identifierType.GetProperty("CollectionId");
                
                if (exchangeIdProp != null) exchangeIdProp.SetValue(identifier, exchange.ExchangeId);
                if (collectionIdProp != null) collectionIdProp.SetValue(identifier, exchange.CollectionId);

                var methodParams = smbDownloadMethod.GetParameters();
                object[] invokeParams = new object[methodParams.Length];
                
                for (int i = 0; i < methodParams.Length; i++)
                {
                    var paramType = methodParams[i].ParameterType;
                    if (paramType == identifierType || paramType.IsAssignableFrom(identifierType))
                    {
                        invokeParams[i] = identifier;
                    }
                    else if (paramType == typeof(string))
                    {
                        invokeParams[i] = exchange.ExchangeId;
                    }
                    else
                    {
                        invokeParams[i] = null;
                    }
                }

                var result = smbDownloadMethod.Invoke(client, invokeParams);
                
                if (result is Task<string> taskResult)
                {
                    var filePath = await taskResult;
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        File.Copy(filePath, smbFilePath, true);
                        diagnostics.Add($"  ✓ Successfully downloaded SMB via Client SDK method: {smbDownloadMethod.Name}");
                        return true;
                    }
                }
                else if (result is string filePath && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    File.Copy(filePath, smbFilePath, true);
                    diagnostics.Add($"  ✓ Successfully downloaded SMB via Client SDK method: {smbDownloadMethod.Name}");
                    return true;
                }
            }
            catch (Exception invokeEx)
            {
                diagnostics.Add($"  ⚠️ Failed to invoke {smbDownloadMethod.Name}: {invokeEx.Message}");
            }

            return false;
        }

        /// <summary>
        /// Gets binary data for a geometry asset from ElementDataModel
        /// Returns the binary data and related objects needed for conversion
        /// </summary>
        private static async Task<(byte[] binaryData, object geometryAsset, object assetInfo, bool success)> GetGeometryAssetBinaryData(
            object client,
            Exchange exchange,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            diagnostics.Add($"  === Step 1: Getting binary data from geometry asset ===");
            try
            {
                var downloadBinaryMethod = allMethods.FirstOrDefault(m => m.Name == "DownloadAndCacheBinaryForBinaryAsset");
                if (downloadBinaryMethod == null)
                {
                    diagnostics.Add($"  ⚠️ DownloadAndCacheBinaryForBinaryAsset method not found");
                    return (null, null, null, false);
                }

                var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                if (identifierType == null)
                {
                    diagnostics.Add($"  ⚠️ DataExchangeIdentifier type not found");
                    return (null, null, null, false);
                }

                var identifier = Activator.CreateInstance(identifierType);
                var exchangeIdProp = identifierType.GetProperty("ExchangeId");
                var collectionIdProp = identifierType.GetProperty("CollectionId");
                var hubIdProp = identifierType.GetProperty("HubId");
                
                if (exchangeIdProp != null) exchangeIdProp.SetValue(identifier, exchange.ExchangeId);
                if (collectionIdProp != null) collectionIdProp.SetValue(identifier, exchange.CollectionId);
                if (hubIdProp != null && !string.IsNullOrEmpty(exchange.HubId))
                {
                    hubIdProp.SetValue(identifier, exchange.HubId);
                }

                var getElementDataModelMethod = allMethods.FirstOrDefault(m => 
                    m.Name == "GetElementDataModelAsync" && m.IsPublic);
                
                if (getElementDataModelMethod == null)
                {
                    diagnostics.Add($"  ⚠️ GetElementDataModelAsync method not found");
                    return (null, null, null, false);
                }

                diagnostics.Add($"  Getting ElementDataModel to find GeometryAssets...");
                var elementDataModelResult = getElementDataModelMethod.Invoke(client, new object[] { identifier, CancellationToken.None });
                
                if (elementDataModelResult == null)
                {
                    diagnostics.Add($"  ⚠️ GetElementDataModelAsync returned null");
                    return (null, null, null, false);
                }

                var taskResult = ((dynamic)elementDataModelResult).GetAwaiter().GetResult();
                object elementDataModel = null;
                
                diagnostics.Add($"  Task result type: {taskResult?.GetType().Name ?? "null"}");
                
                var responseType = taskResult?.GetType();
                if (responseType != null)
                {
                    var isSuccessProp = responseType.GetProperty("IsSuccess") ?? 
                                       responseType.GetProperty("Success");
                    if (isSuccessProp != null)
                    {
                        var isSuccess = (bool)isSuccessProp.GetValue(taskResult);
                        diagnostics.Add($"  Response IsSuccess: {isSuccess}");
                    }
                    
                    var valueProp = responseType.GetProperty("Value");
                    if (valueProp != null)
                    {
                        try
                        {
                            elementDataModel = valueProp.GetValue(taskResult);
                            diagnostics.Add($"  Got ElementDataModel from Value: {elementDataModel?.GetType().Name ?? "null"}");
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add($"  ⚠️ Error getting Value: {ex.Message}");
                            var errorProp = responseType.GetProperty("Error");
                            if (errorProp != null)
                            {
                                var error = errorProp.GetValue(taskResult);
                                diagnostics.Add($"  Response Error: {error}");
                            }
                            elementDataModel = taskResult;
                        }
                    }
                    else
                    {
                        elementDataModel = taskResult;
                    }
                }
                
                if (elementDataModel == null)
                {
                    diagnostics.Add($"  ⚠️ ElementDataModel is null");
                    return (null, null, null, false);
                }

                var elementDataModelType = elementDataModel.GetType();
                diagnostics.Add($"  ElementDataModel type: {elementDataModelType.FullName}");
                
                // List all properties on ElementDataModel to understand its structure
                PropertyInfo[] allElementDataModelProps = elementDataModelType.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                diagnostics.Add($"  ElementDataModel properties: {string.Join(", ", allElementDataModelProps.Select(p => $"{p.Name} ({p.PropertyType.Name})"))}");
                
                // ExchangeData is an internal property, so we need to use NonPublic binding flags
                var exchangeDataProp = elementDataModelType.GetProperty("ExchangeData", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (exchangeDataProp == null)
                {
                    diagnostics.Add($"  ⚠️ ExchangeData property not found on ElementDataModel (tried with NonPublic binding)");
                    return (null, null, null, false);
                }

                var exchangeData = exchangeDataProp.GetValue(elementDataModel);
                diagnostics.Add($"  Got ExchangeData: {exchangeData?.GetType().Name ?? "null"}");
                if (exchangeData == null)
                {
                    diagnostics.Add($"  ⚠️ ExchangeData is null");
                    return (null, null, null, false);
                }

                var exchangeDataType = exchangeData.GetType();
                
                // Get GeometryAsset type first
                var geometryAssetType = Type.GetType("Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset, Autodesk.DataExchange");
                if (geometryAssetType == null)
                {
                    // Fallback: search through loaded assemblies
                    diagnostics.Add($"  GeometryAsset type not found with direct lookup, searching loaded assemblies...");
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            geometryAssetType = assembly.GetType("Autodesk.DataExchange.SchemaObjects.Assets.GeometryAsset");
                            if (geometryAssetType != null)
                            {
                                diagnostics.Add($"  Found GeometryAsset type in assembly: {assembly.FullName}");
                                break;
                            }
                        }
                        catch
                        {
                            // Skip assemblies that can't be queried
                        }
                    }
                }
                
                if (geometryAssetType == null)
                {
                    diagnostics.Add($"  ⚠️ GeometryAsset type not found in any loaded assembly");
                    return (null, null, null, false);
                }
                
                // Use ExchangeData.GetAssetsByType<GeometryAsset>() - this is the simplest and most direct way!
                // Found in ExchangeData.cs line 664: public IEnumerable<T> GetAssetsByType<T>() where T : BaseAsset
                var getAssetsByTypeMethod = exchangeDataType.GetMethod("GetAssetsByType", BindingFlags.Public | BindingFlags.Instance);
                if (getAssetsByTypeMethod == null)
                {
                    diagnostics.Add($"  ⚠️ GetAssetsByType method not found on ExchangeData");
                    return (null, null, null, false);
                }
                
                // Make the generic method with GeometryAsset type
                var genericGetAssetsByType = getAssetsByTypeMethod.MakeGenericMethod(geometryAssetType);
                var geometryAssetsResult = genericGetAssetsByType.Invoke(exchangeData, null);
                
                if (geometryAssetsResult == null)
                {
                    diagnostics.Add($"  ⚠️ GetAssetsByType<GeometryAsset>() returned null");
                    return (null, null, null, false);
                }
                
                var geometryAssets = geometryAssetsResult as System.Collections.IEnumerable;
                if (geometryAssets == null)
                {
                    diagnostics.Add($"  ⚠️ GeometryAssets result is not enumerable");
                    return (null, null, null, false);
                }
                
                // Count geometry assets for diagnostics
                var allGeometryAssets = new List<object>();
                foreach (var geomAsset in geometryAssets)
                {
                    if (geomAsset != null)
                    {
                        allGeometryAssets.Add(geomAsset);
                    }
                }
                
                diagnostics.Add($"  Found {allGeometryAssets.Count} GeometryAsset(s) via ExchangeData.GetAssetsByType<GeometryAsset>()");
                
                if (allGeometryAssets.Count == 0)
                {
                    diagnostics.Add($"  ⚠️ No geometry assets found in ExchangeData");
                    return (null, null, null, false);
                }

                // Try each geometry asset until we successfully download binary data
                foreach (var geometryAsset in geometryAssets)
                {
                    if (geometryAsset == null)
                        continue;

                    try
                    {
                        var binaryRefProp = geometryAsset.GetType().GetProperty("BinaryReference");
                        if (binaryRefProp == null)
                        {
                            diagnostics.Add($"  GeometryAsset has no BinaryReference property, skipping...");
                            continue;
                        }

                        var binaryRef = binaryRefProp.GetValue(geometryAsset);
                        if (binaryRef == null)
                        {
                            diagnostics.Add($"  GeometryAsset BinaryReference is null, skipping...");
                            continue;
                        }

                        diagnostics.Add($"  Found GeometryAsset with BinaryReference, downloading binary...");
                        
                        var downloadParams = downloadBinaryMethod.GetParameters();
                        object[] downloadInvokeParams;
                        
                        if (downloadParams.Length == 3)
                        {
                            downloadInvokeParams = new object[] { identifier, binaryRef, CancellationToken.None };
                        }
                        else if (downloadParams.Length == 4)
                        {
                            var clientTypeForWorkSpace = client.GetType();
                            var workSpaceProp = clientTypeForWorkSpace.GetProperty("WorkSpaceUserGeometryPath");
                            var workSpacePath = workSpaceProp?.GetValue(client)?.ToString() ?? Path.GetTempPath();
                            downloadInvokeParams = new object[] { identifier, binaryRef, workSpacePath, CancellationToken.None };
                        }
                        else
                        {
                            downloadInvokeParams = new object[] { identifier, binaryRef };
                        }
                        
                        diagnostics.Add($"  Invoking DownloadAndCacheBinaryForBinaryAsset with {downloadInvokeParams.Length} parameters...");
                        var downloadResult = downloadBinaryMethod.Invoke(client, downloadInvokeParams);
                        
                        if (downloadResult != null && downloadResult.GetType().IsGenericType)
                        {
                            var binaryFilePath = ((dynamic)downloadResult).GetAwaiter().GetResult();
                            
                            if (string.IsNullOrEmpty(binaryFilePath) || !File.Exists(binaryFilePath))
                            {
                                diagnostics.Add($"  ⚠️ Binary file not found or path is empty: {binaryFilePath}");
                                continue;
                            }

                            diagnostics.Add($"  ✓ Downloaded binary file: {binaryFilePath} ({new FileInfo(binaryFilePath).Length} bytes)");
                            
                            var startProp = binaryRef.GetType().GetProperty("Start");
                            var endProp = binaryRef.GetType().GetProperty("End");
                            
                            byte[] binaryData = null;
                            if (startProp != null && endProp != null)
                            {
                                var start = Convert.ToInt64(startProp.GetValue(binaryRef));
                                var end = Convert.ToInt64(endProp.GetValue(binaryRef));
                                var length = end - start + 1;
                                
                                diagnostics.Add($"  Reading binary range [{start}-{end}] from file...");
                                
                                using (var fileStream = new FileStream(binaryFilePath, FileMode.Open, FileAccess.Read))
                                {
                                    fileStream.Seek(start, SeekOrigin.Begin);
                                    binaryData = new byte[length];
                                    var bytesRead = fileStream.Read(binaryData, 0, (int)length);
                                    if (bytesRead < length)
                                    {
                                        Array.Resize(ref binaryData, bytesRead);
                                    }
                                }
                                diagnostics.Add($"  ✓ Read {binaryData.Length} bytes from range");
                            }
                            else
                            {
                                binaryData = File.ReadAllBytes(binaryFilePath);
                                diagnostics.Add($"  ✓ Read entire file: {binaryData.Length} bytes");
                            }

                            // Create AssetInfo using CreateAssetInfoForGeometryAsset method
                            // Reference: fdx-connector/src/FDXSDK/Client.cs line 4521
                            object assetInfo = null;
                            var createAssetInfoMethod = allMethods.FirstOrDefault(m => m.Name == "CreateAssetInfoForGeometryAsset");
                            if (createAssetInfoMethod != null)
                            {
                                diagnostics.Add($"  Creating AssetInfo using CreateAssetInfoForGeometryAsset...");
                                try
                                {
                                    assetInfo = createAssetInfoMethod.Invoke(client, new object[] { geometryAsset });
                                    diagnostics.Add($"  ✓ Created AssetInfo: {assetInfo?.GetType().FullName ?? "null"}");
                                }
                                catch (Exception ex)
                                {
                                    diagnostics.Add($"  ⚠️ Error creating AssetInfo: {ex.Message}");
                                    diagnostics.Add($"  Will try with empty AssetInfo");
                                    // Fallback: create empty AssetInfo
                                    var assetInfoType = Type.GetType("Autodesk.GeometryUtilities.SDK.AssetInfo, Autodesk.GeometryUtilities");
                                    if (assetInfoType == null)
                                    {
                                        // Try alternative namespace
                                        assetInfoType = Type.GetType("Autodesk.DataExchange.DataModels.AssetInfo, Autodesk.DataExchange.DataModels");
                                    }
                                    if (assetInfoType != null)
                                    {
                                        assetInfo = Activator.CreateInstance(assetInfoType);
                                    }
                                }
                            }
                            else
                            {
                                diagnostics.Add($"  ⚠️ CreateAssetInfoForGeometryAsset method not found, creating empty AssetInfo");
                                var assetInfoType = Type.GetType("Autodesk.GeometryUtilities.SDK.AssetInfo, Autodesk.GeometryUtilities");
                                if (assetInfoType == null)
                                {
                                    assetInfoType = Type.GetType("Autodesk.DataExchange.DataModels.AssetInfo, Autodesk.DataExchange.DataModels");
                                }
                                if (assetInfoType != null)
                                {
                                    assetInfo = Activator.CreateInstance(assetInfoType);
                                }
                            }
                            
                            diagnostics.Add($"  ✓ Successfully got binary data: {binaryData.Length} bytes");
                            return (binaryData, geometryAsset, assetInfo, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"  ⚠️ Error processing GeometryAsset: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            diagnostics.Add($"  Inner exception: {ex.InnerException.Message}");
                        }
                    }
                }

                diagnostics.Add($"  ⚠️ No geometry assets with valid binary data found");
                return (null, null, null, false);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Error getting binary data: {ex.Message}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                return (null, null, null, false);
            }
        }

        /// <summary>
        /// Converts binary data to SMB format using ParseGeometryAssetBinaryToIntermediateGeometry
        /// Reference: fdx-connector/src/FDXSDK/Client.cs line 4586-4622
        /// </summary>
        private static bool ConvertBinaryToSMB(
            object client,
            byte[] binaryData,
            object geometryAsset,
            object assetInfo,
            string smbFilePath,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            diagnostics.Add($"  === Step 2: Converting binary data to SMB format ===");
            try
            {
                var parseMethod = allMethods.FirstOrDefault(m => m.Name == "ParseGeometryAssetBinaryToIntermediateGeometry");
                if (parseMethod == null)
                {
                    diagnostics.Add($"  ⚠️ ParseGeometryAssetBinaryToIntermediateGeometry method not found");
                    return false;
                }

                if (binaryData == null || binaryData.Length == 0)
                {
                    diagnostics.Add($"  ⚠️ Binary data is null or empty");
                    return false;
                }

                diagnostics.Add($"  Binary data size: {binaryData.Length} bytes");
                diagnostics.Add($"  GeometryAsset type: {geometryAsset?.GetType().FullName ?? "null"}");
                diagnostics.Add($"  AssetInfo type: {assetInfo?.GetType().FullName ?? "null"}");
                
                diagnostics.Add($"  Invoking ParseGeometryAssetBinaryToIntermediateGeometry...");
                var smbBytes = parseMethod.Invoke(client, new object[] { assetInfo, geometryAsset, binaryData }) as byte[];
                
                if (smbBytes == null)
                {
                    diagnostics.Add($"  ⚠️ ParseGeometryAssetBinaryToIntermediateGeometry returned null");
                    return false;
                }

                if (smbBytes.Length == 0)
                {
                    diagnostics.Add($"  ⚠️ ParseGeometryAssetBinaryToIntermediateGeometry returned empty byte array");
                    return false;
                }

                File.WriteAllBytes(smbFilePath, smbBytes);
                diagnostics.Add($"  ✓ Successfully converted and saved SMB file: {smbBytes.Length} bytes");
                return true;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ⚠️ Error converting binary to SMB: {ex.Message}");
                diagnostics.Add($"  Exception type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    diagnostics.Add($"  Inner exception: {ex.InnerException.Message}");
                }
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to download SMB using ParseGeometryAssetBinaryToIntermediateGeometry
        /// Uses two-step process: 1) Get binary data, 2) Convert to SMB
        /// </summary>
        private static async Task<bool> TryDownloadViaParseGeometryAssetBinaryToIntermediateGeometry(
            object client,
            Exchange exchange,
            string smbFilePath,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            diagnostics.Add($"  === Attempting to use ParseGeometryAssetBinaryToIntermediateGeometry (2-step process) ===");
            
            // Step 1: Get binary data
            var (binaryData, geometryAsset, assetInfo, success) = await GetGeometryAssetBinaryData(
                client, exchange, allMethods, diagnostics);
            
            if (!success || binaryData == null)
            {
                diagnostics.Add($"  ⚠️ Failed to get binary data");
                return false;
            }

            // Step 2: Convert binary to SMB
            return ConvertBinaryToSMB(
                client, binaryData, geometryAsset, assetInfo, smbFilePath, allMethods, diagnostics);
        }

        /// <summary>
        /// Logs internal format conversion methods for diagnostics
        /// </summary>
        private static void LogInternalFormatConversionMethods(
            Type clientType,
            MethodInfo[] allMethods,
            List<string> diagnostics)
        {
            diagnostics.Add("  === Searching for internal format conversion methods ===");
            
            var stepMethod = allMethods.FirstOrDefault(m => 
                m.Name.Contains("DownloadCompleteExchangeAsSTEP") || 
                m.Name.Contains("DownloadAsSTEP"));
            
            if (stepMethod != null)
            {
                diagnostics.Add($"  Found STEP download method: {stepMethod.Name}");
                diagnostics.Add($"  Return type: {stepMethod.ReturnType.Name}");
                diagnostics.Add($"  Parameters: {string.Join(", ", stepMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
                diagnostics.Add("  Inspecting its implementation for SMB access points...");
                
                var internalSmbMethods = allMethods.Where(m => 
                    m.Name.ToLowerInvariant().Contains("smb") && 
                    (m.IsPrivate || m.IsAssembly || m.IsFamily)).ToList();
                
                if (internalSmbMethods.Any())
                {
                    diagnostics.Add($"  Found {internalSmbMethods.Count} internal/private SMB-related methods:");
                    foreach (var smbMethod in internalSmbMethods)
                    {
                        var accessModifier = smbMethod.IsPrivate ? "private" : 
                                            smbMethod.IsFamily ? "protected" : "internal";
                        diagnostics.Add($"    [{accessModifier}] {smbMethod.ReturnType.Name} {smbMethod.Name}({string.Join(", ", smbMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                    }
                }

                var allFields = clientType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | 
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                var allProperties = clientType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | 
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                
                var smbFields = allFields.Where(f => f.Name.ToLowerInvariant().Contains("smb")).ToList();
                var smbProperties = allProperties.Where(p => p.Name.ToLowerInvariant().Contains("smb")).ToList();
                
                if (smbFields.Any() || smbProperties.Any())
                {
                    diagnostics.Add("  Found SMB-related fields/properties:");
                    foreach (var field in smbFields)
                    {
                        var accessModifier = field.IsPrivate ? "private" : 
                                            field.IsFamily ? "protected" : 
                                            field.IsAssembly ? "internal" : "public";
                        diagnostics.Add($"    [{accessModifier}] {field.FieldType.Name} {field.Name}");
                    }
                    foreach (var prop in smbProperties)
                    {
                        var getter = prop.GetGetMethod(true);
                        var accessModifier = getter?.IsPrivate == true ? "private" : 
                                            getter?.IsFamily == true ? "protected" : 
                                            getter?.IsAssembly == true ? "internal" : "public";
                        diagnostics.Add($"    [{accessModifier}] {prop.PropertyType.Name} {prop.Name}");
                    }
                }

                var conversionMethods = allMethods.Where(m =>
                {
                    var name = m.Name.ToLowerInvariant();
                    return (name.Contains("convert") || name.Contains("transform") || name.Contains("translate")) &&
                           (m.IsPrivate || m.IsAssembly || m.IsFamily);
                }).ToList();
                
                if (conversionMethods.Any())
                {
                    diagnostics.Add($"  Found {conversionMethods.Count} internal conversion/transformation methods:");
                    foreach (var convMethod in conversionMethods.Take(10))
                    {
                        var accessModifier = convMethod.IsPrivate ? "private" : 
                                            convMethod.IsFamily ? "protected" : "internal";
                        diagnostics.Add($"    [{accessModifier}] {convMethod.ReturnType.Name} {convMethod.Name}({string.Join(", ", convMethod.GetParameters().Select(p => p.ParameterType.Name))})");
                    }
                }
            }
        }

        /// <summary>
        /// Fallback method: Downloads SMB file using HTTP API
        /// </summary>
        private static async Task DownloadSMBViaHttpAPI(
            Exchange exchange,
            string accessToken,
            string smbFilePath,
            List<string> diagnostics)
        {
            // Build DataExchange API URL for downloading SMB file
            // Typical endpoint: /exchanges/{exchangeId}/collections/{collectionId}/files or /download
            var baseUrl = "https://developer.api.autodesk.com/exchange/v1";
            var downloadUrl = $"{baseUrl}/exchanges/{exchange.ExchangeId}/collections/{exchange.CollectionId}/files";

            diagnostics.Add($"  Downloading from: {downloadUrl}");

            // Download file using HttpClient
            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                httpClient.DefaultRequestHeaders.Add("Accept", "application/octet-stream");

                var response = await httpClient.GetAsync(downloadUrl);
                response.EnsureSuccessStatusCode();

                // If the response is a redirect or JSON with a download URL, handle it
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (contentType == "application/json")
                {
                    // Response might contain a download URL - parse it
                    var jsonContent = await response.Content.ReadAsStringAsync();
                    diagnostics.Add($"  Received JSON response, parsing...");
                    // TODO: Parse JSON to get actual download URL if needed
                    // For now, try alternative endpoint
                    downloadUrl = $"{baseUrl}/exchanges/{exchange.ExchangeId}/collections/{exchange.CollectionId}/download";
                    response = await httpClient.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();
                }

                // Save SMB file
                using (var fileStream = new FileStream(smbFilePath, FileMode.Create))
                {
                    await response.Content.CopyToAsync(fileStream);
                }

                diagnostics.Add($"  ✓ Downloaded {new FileInfo(smbFilePath).Length} bytes");
            }
        }

        /// <summary>
        /// Loads geometry from SMB file using ProtoGeometry APIs
        /// </summary>
        private static List<Geometry> LoadGeometryFromSMB(
            string smbFilePath,
            string unit,
            List<string> diagnostics)
        {
            var geometries = new List<Geometry>();

            try
            {
                diagnostics.Add("\nLoading via ProtoGeometry SMB APIs...");

                // Use ProtoGeometry to open SMB file
                // Note: The exact API signature may vary - adjust based on actual ProtoGeometry API
                // Common patterns:
                // - ProtoGeometry.Geometry.OpenSMB(string path)
                // - ProtoGeometry.FileLoader.OpenSMB(string path, string unit)
                // - Or similar static/instance methods

                // Try reflection-based approach to find the SMB opening method
                var protoGeometryAssembly = System.Reflection.Assembly.LoadFrom(
                    Path.Combine(Path.GetDirectoryName(typeof(Geometry).Assembly.Location), "ProtoGeometry.dll"));
                
                if (protoGeometryAssembly == null)
                {
                    // Fallback: try loading from common locations
                    var possiblePaths = new[]
                    {
                        @"C:\Users\nenovd\Downloads\Dynamo\RootDir\ProtoGeometry.dll",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                            @"Dynamo\Dynamo Core\4.1\ProtoGeometry.dll")
                    };

                    foreach (var path in possiblePaths)
                    {
                        if (File.Exists(path))
                        {
                            protoGeometryAssembly = System.Reflection.Assembly.LoadFrom(path);
                            break;
                        }
                    }
                }

                if (protoGeometryAssembly == null)
                {
                    throw new InvalidOperationException("ProtoGeometry.dll not found. Please ensure it's in the correct location.");
                }

                diagnostics.Add($"  ✓ Loaded ProtoGeometry.dll from: {protoGeometryAssembly.Location}");

                // Look for SMB-related types/methods
                var types = protoGeometryAssembly.GetTypes();
                diagnostics.Add($"  ProtoGeometry.dll contains {types.Length} types");
                
                // List all types that might be relevant for SMB loading
                var relevantTypes = types.Where(t => 
                    t.Name.ToLowerInvariant().Contains("smb") || 
                    t.Name.ToLowerInvariant().Contains("file") ||
                    t.Name.ToLowerInvariant().Contains("loader") ||
                    t.Name.ToLowerInvariant().Contains("read") ||
                    t.Name.ToLowerInvariant().Contains("import") ||
                    t.Name.ToLowerInvariant().Contains("acis")).ToList();
                
                if (relevantTypes.Any())
                {
                    diagnostics.Add($"  Found {relevantTypes.Count} potentially relevant types:");
                    foreach (var type in relevantTypes.Take(20)) // Limit to first 20
                    {
                        diagnostics.Add($"    - {type.FullName}");
                    }
                }
                
                var smbType = types.FirstOrDefault(t => 
                    t.Name.Contains("SMB") || 
                    t.Name.Contains("FileLoader") || 
                    t.Name.Contains("GeometryLoader"));

                // Also check for methods on Geometry class itself
                var geometryType = typeof(Geometry);
                var geometryMethods = geometryType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                var smbMethodsOnGeometry = geometryMethods.Where(m => 
                    m.Name.ToLowerInvariant().Contains("smb") ||
                    m.Name.ToLowerInvariant().Contains("import") ||
                    m.Name.ToLowerInvariant().Contains("fromfile") ||
                    m.Name.ToLowerInvariant().Contains("load")).ToList();
                
                if (smbMethodsOnGeometry.Any())
                {
                    diagnostics.Add($"  Found {smbMethodsOnGeometry.Count} potentially relevant methods on Geometry class:");
                    foreach (var method in smbMethodsOnGeometry)
                    {
                        var paramInfo = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        diagnostics.Add($"    - {method.Name}({paramInfo})");
                    }
                }
                
                if (smbType != null)
                {
                    diagnostics.Add($"  Found type: {smbType.FullName}");

                    // Try to find Open/Load methods
                    var openMethod = smbType.GetMethods(System.Reflection.BindingFlags.Public | 
                        System.Reflection.BindingFlags.Static | 
                        System.Reflection.BindingFlags.Instance)
                        .FirstOrDefault(m => 
                            (m.Name.Contains("Open") || m.Name.Contains("Load")) && 
                            m.GetParameters().Length >= 1);

                    if (openMethod != null)
                    {
                        diagnostics.Add($"  Found method: {openMethod.Name}");

                        object loader = null;
                        if (openMethod.IsStatic)
                        {
                            // Static method - call directly
                            var result = openMethod.Invoke(null, new object[] { smbFilePath });
                            if (result is IEnumerable<Geometry> geoEnumerable)
                            {
                                geometries.AddRange(geoEnumerable);
                            }
                            else if (result is Geometry geo)
                            {
                                geometries.Add(geo);
                            }
                        }
                        else
                        {
                            // Instance method - create instance first
                            loader = Activator.CreateInstance(smbType);
                            var result = openMethod.Invoke(loader, new object[] { smbFilePath });
                            if (result is IEnumerable<Geometry> geoEnumerable)
                            {
                                geometries.AddRange(geoEnumerable);
                            }
                            else if (result is Geometry geo)
                            {
                                geometries.Add(geo);
                            }
                        }

                        // Try to get geometry from loaded objects
                        if (loader != null)
                        {
                            var getGeometryMethod = loader.GetType().GetMethods()
                                .FirstOrDefault(m => m.Name.Contains("GetGeometry") || m.Name.Contains("GetGeometries"));
                            
                            if (getGeometryMethod != null)
                            {
                                var geoResult = getGeometryMethod.Invoke(loader, null);
                                if (geoResult is IEnumerable<Geometry> geoEnumerable)
                                {
                                    geometries.AddRange(geoEnumerable);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Fallback: Try using Geometry.FromNativePointer if SMB file provides pointers
                        diagnostics.Add("  ⚠️ Direct SMB API not found, trying alternative approach...");
                        // This would require knowing how ProtoGeometry exposes SMB data
                    }
                }
                
                // If no SMB type found, try methods on Geometry class
                if (smbType == null && smbMethodsOnGeometry.Any())
                {
                    diagnostics.Add("  ⚠️ SMB loader type not found, trying methods on Geometry class...");
                    foreach (var method in smbMethodsOnGeometry)
                    {
                        try
                        {
                            diagnostics.Add($"  Trying Geometry.{method.Name}...");
                            var methodParams = method.GetParameters();
                            
                            // Try to invoke with SMB file path
                            object[] invokeParams = new object[methodParams.Length];
                            for (int i = 0; i < methodParams.Length; i++)
                            {
                                var param = methodParams[i];
                                if (param.ParameterType == typeof(string))
                                {
                                    invokeParams[i] = smbFilePath;
                                }
                                else if (param.ParameterType == typeof(byte[]))
                                {
                                    invokeParams[i] = File.ReadAllBytes(smbFilePath);
                                }
                                else
                                {
                                    invokeParams[i] = param.HasDefaultValue ? param.DefaultValue : null;
                                }
                            }
                            
                            var result = method.Invoke(null, invokeParams);
                            if (result != null)
                            {
                                if (result is IEnumerable<Geometry> geoEnumerable)
                                {
                                    geometries.AddRange(geoEnumerable);
                                    diagnostics.Add($"  ✓ Successfully loaded {geometries.Count} geometries using Geometry.{method.Name}");
                                    break;
                                }
                                else if (result is Geometry geo)
                                {
                                    geometries.Add(geo);
                                    diagnostics.Add($"  ✓ Successfully loaded 1 geometry using Geometry.{method.Name}");
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add($"  ⚠️ Geometry.{method.Name} failed: {ex.Message}");
                        }
                    }
                }
                
                if (smbType == null && !smbMethodsOnGeometry.Any())
                {
                    // Alternative: Use Geometry.FromNativePointer with file path
                    // Some implementations allow direct file path to native pointer conversion
                    diagnostics.Add("  ⚠️ SMB loader type not found, trying Geometry.FromNativePointer...");
                    
                    // Try: Geometry.FromNativePointer(IntPtr) - but we need to get the pointer from SMB file
                    // This might require a different approach - loading SMB and getting ACIS body pointers
                }

                if (geometries.Count == 0)
                {
                    diagnostics.Add("  ⚠️ No geometries loaded - ProtoGeometry API may need adjustment");
                    diagnostics.Add("  Please check ProtoGeometry.dll for SMB file opening methods");
                }
                else
                {
                    diagnostics.Add($"  ✓ Successfully loaded {geometries.Count} geometry object(s)");
                }

                return geometries;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ Error loading geometry from SMB: {ex.Message}");
                diagnostics.Add($"  Type: {ex.GetType().Name}");
                diagnostics.Add($"  Stack: {ex.StackTrace}");
                throw;
            }
            finally
            {
                // Cleanup SMB file - TEMPORARILY DISABLED for debugging
                // TODO: Re-enable cleanup after verifying SMB loading works
                /*
                try
                {
                    if (File.Exists(smbFilePath))
                        File.Delete(smbFilePath);
                }
                catch { /* Ignore cleanup errors */ }
                
            }
        }
    }


