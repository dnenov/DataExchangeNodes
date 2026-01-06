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
                    diagnostics.Add($"  ✓ Downloaded via Client SDK: {new FileInfo(smbFilePath).Length} bytes");
                    return smbFilePath;
                }

                // Fallback to HTTP API if Client SDK method not found or failed
                diagnostics.Add("  Falling back to HTTP API download...");
                await DownloadSMBViaHttpAPI(exchange, accessToken, smbFilePath, diagnostics);
                
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
                // Try multiple ways to find the Client instance
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
                        // Look for Client class in loaded assemblies
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
                                    // Look for static instance or singleton pattern
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

                // Look for methods related to SMB, download, or format conversion
                var candidateMethods = allMethods.Where(m =>
                {
                    var name = m.Name.ToLowerInvariant();
                    // Prioritize SMB methods, but also include download/format methods
                    return name.Contains("smb") || 
                           (name.Contains("download") && !name.Contains("step")) || 
                           name.Contains("getfile") ||
                           (name.Contains("format") && name.Contains("smb")) ||
                           (name.Contains("convert") && name.Contains("smb"));
                }).ToList();

                // Try to find a method that can download SMB
                // Look for methods that take exchange identifier and return string (file path)
                var smbDownloadMethod = candidateMethods.FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    var returnType = m.ReturnType;
                    
                    // Check if it takes exchange/collection IDs and returns string or Task<string>
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

                // Try to invoke GetBinaryAssetDownloadInfoAsync - this might give us SMB download info
                var binaryAssetMethod = allMethods.FirstOrDefault(m => m.Name == "GetBinaryAssetDownloadInfoAsync");
                if (binaryAssetMethod != null)
                {
                    diagnostics.Add($"  === Attempting to use GetBinaryAssetDownloadInfoAsync ===");
                    try
                    {
                        // This method likely needs BinaryDownloadBatchRequest with format="SMB" or similar
                        var binaryRequestType = binaryAssetMethod.GetParameters()
                            .FirstOrDefault(p => p.ParameterType.Name.Contains("BinaryDownload"))?.ParameterType;
                        
                        if (binaryRequestType != null)
                        {
                            diagnostics.Add($"  Found BinaryDownloadBatchRequest type: {binaryRequestType.FullName}");
                            
                            // Try to create the request object
                            var request = Activator.CreateInstance(binaryRequestType);
                            
                            // Look for properties to set format or asset type
                            var props = binaryRequestType.GetProperties();
                            foreach (var prop in props)
                            {
                                diagnostics.Add($"    Property: {prop.Name} ({prop.PropertyType.Name})");
                            }
                            
                            // Try common property names for format
                            var formatProp = props.FirstOrDefault(p => 
                                p.Name.ToLowerInvariant().Contains("format") ||
                                p.Name.ToLowerInvariant().Contains("type") ||
                                p.Name.ToLowerInvariant().Contains("asset"));
                            
                            if (formatProp != null && formatProp.PropertyType == typeof(string))
                            {
                                // Try setting format to "SMB"
                                formatProp.SetValue(request, "SMB");
                                diagnostics.Add($"  Set format property '{formatProp.Name}' to 'SMB'");
                            }
                            
                            // Invoke the method
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
                            var result = binaryAssetMethod.Invoke(client, invokeParams);
                            
                            // Handle Task result
                            if (result != null && result.GetType().IsGenericType && 
                                result.GetType().GetGenericTypeDefinition() == typeof(Task<>))
                            {
                                var taskResult = ((dynamic)result).GetAwaiter().GetResult();
                                diagnostics.Add($"  Result type: {taskResult?.GetType().Name ?? "null"}");
                                
                                // Try to extract download URL or file path from result
                                if (taskResult != null)
                                {
                                    var resultType = taskResult.GetType();
                                    var urlProp = resultType.GetProperty("Url") ?? 
                                                 resultType.GetProperty("DownloadUrl") ?? 
                                                 resultType.GetProperty("Path") ??
                                                 resultType.GetProperty("FilePath");
                                    
                                    if (urlProp != null)
                                    {
                                        var url = urlProp.GetValue(taskResult)?.ToString();
                                        if (!string.IsNullOrEmpty(url))
                                        {
                                            diagnostics.Add($"  Found download URL: {url}");
                                            // Download from URL
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
                    catch (Exception ex)
                    {
                        diagnostics.Add($"  ⚠️ Failed to use GetBinaryAssetDownloadInfoAsync: {ex.Message}");
                        diagnostics.Add($"  Stack: {ex.StackTrace}");
                    }
                }

                // Method 3: Use GetElementDataModelAsync to get geometry assets, then GetAssetInfoForGeometryAsset to get SMB files
                diagnostics.Add($"  === Attempting to get SMB via ElementDataModel and GetAssetInfoForGeometryAsset ===");
                try
                {
                    var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                    if (identifierType != null)
                    {
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

                        // Get ElementDataModel (public method)
                        var getElementDataModelMethod = allMethods.FirstOrDefault(m => 
                            m.Name == "GetElementDataModelAsync" && m.IsPublic);
                        
                        if (getElementDataModelMethod != null)
                        {
                            diagnostics.Add($"  Calling GetElementDataModelAsync...");
                            var elementDataModelResult = getElementDataModelMethod.Invoke(client, new object[] { identifier, CancellationToken.None });
                            
                            if (elementDataModelResult != null && elementDataModelResult.GetType().IsGenericType)
                            {
                                // Handle Task<IResponse<T>> pattern
                                var taskResult = ((dynamic)elementDataModelResult).GetAwaiter().GetResult();
                                
                                diagnostics.Add($"  Task result type: {taskResult?.GetType().Name ?? "null"}");
                                
                                // Try to get Value property - might be IResponse<T> or direct value
                                object elementDataModel = null;
                                var responseType = taskResult?.GetType();
                                
                                if (responseType != null)
                                {
                                    diagnostics.Add($"  Response type: {responseType.FullName}");
                                    
                                    // Check for success/error properties first
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
                                    
                                    // Check if it's IResponse<T> pattern
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
                                            
                                            // Try to inspect what properties are available
                                            var allProps = responseType.GetProperties();
                                            var propNames = new List<string>();
                                            foreach (var prop in allProps)
                                            {
                                                propNames.Add(prop.Name);
                                            }
                                            diagnostics.Add($"  Available properties on Response: {string.Join(", ", propNames)}");
                                            
                                            // Try using the result directly as fallback
                                            elementDataModel = taskResult;
                                        }
                                    }
                                    else
                                    {
                                        diagnostics.Add($"  No Value property found, using result directly");
                                        // Might be the value directly
                                        elementDataModel = taskResult;
                                    }
                                }
                                
                                if (elementDataModel != null)
                                {
                                    diagnostics.Add($"  Got ElementDataModel: {elementDataModel.GetType().Name}");
                                    
                                    // Look for GetElementGeometriesAsync method on ElementDataModel
                                    var elementDataModelType = elementDataModel.GetType();
                                    var getGeometriesMethod = elementDataModelType.GetMethod("GetElementGeometriesAsync", 
                                        BindingFlags.Public | BindingFlags.Instance);
                                    
                                    // Use dynamic invocation to bypass reflection's strict type checking
                                    // From source: Elements property returns IEnumerable<Element>
                                    // From source: GetElementGeometriesAsync(IEnumerable<Element> elements, CancellationToken cancellationToken = default, GeometryOutputOptions geometryOutputOptions = null)
                                    // Returns: Task<Dictionary<Element, IEnumerable<ElementGeometry>>>
                                    try
                                    {
                                        var elementsProp = elementDataModelType.GetProperty("Elements");
                                        var elements = elementsProp?.GetValue(elementDataModel);
                                        
                                        if (elements != null)
                                        {
                                            // Count elements for diagnostics
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
                                            
                                            // Use dynamic to bypass reflection's strict type checking
                                            // The elements object IS IEnumerable<Element>, but reflection can't verify that
                                            dynamic elementDataModelDynamic = elementDataModel;
                                            dynamic elementsDynamic = elements; // This preserves the actual type at runtime
                                            
                                            diagnostics.Add($"  Calling GetElementGeometriesAsync using dynamic invocation...");
                                            
                                            // Call the method directly using dynamic - this will work because
                                            // elementsDynamic is actually IEnumerable<Element> at runtime
                                            var geometriesTask = elementDataModelDynamic.GetElementGeometriesAsync(
                                                elementsDynamic, 
                                                CancellationToken.None, 
                                                null);
                                            
                                            // Await the task
                                            var geometriesDict = await geometriesTask;
                                            
                                            // Now iterate through the dictionary
                                            // geometriesDict is Dictionary<Element, IEnumerable<ElementGeometry>>
                                            var smbFiles = new List<string>();
                                            
                                            foreach (var kvp in geometriesDict)
                                            {
                                                var elementGeometries = kvp.Value; // IEnumerable<ElementGeometry>
                                                
                                                foreach (var geometry in elementGeometries)
                                                {
                                                    // Check if it's FileGeometry (which has FilePath property)
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
                                                            // Log other file types for debugging
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
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ Failed to get SMB via ElementDataModel: {ex.Message}");
                    diagnostics.Add($"  Stack: {ex.StackTrace}");
                }

                // Try GetAllAssetInfosWithTranslatedGeometryPath - this might give us SMB path before translation
                var assetInfoMethod = allMethods.FirstOrDefault(m => m.Name == "GetAllAssetInfosWithTranslatedGeometryPath");
                if (assetInfoMethod != null)
                {
                    diagnostics.Add($"  === Attempting to use GetAllAssetInfosWithTranslatedGeometryPath ===");
                    try
                    {
                        var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                        if (identifierType != null)
                        {
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
                                    // Might be format parameter - try "SMB" or empty
                                    invokeParams[i] = param.Name.ToLowerInvariant().Contains("format") ? "SMB" : "";
                                }
                                else if (param.ParameterType.Name.Contains("ExchangeData"))
                                {
                                    // Might need ExchangeData - try null for now
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
                                
                                // Try to extract SMB path from result
                                if (taskResult != null)
                                {
                                    // Result might be a list or collection - try to iterate
                                    if (taskResult is System.Collections.IEnumerable enumerable)
                                    {
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
                        }
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"  ⚠️ Failed to use GetAllAssetInfosWithTranslatedGeometryPath: {ex.Message}");
                    }
                }

                if (smbDownloadMethod != null)
                {
                    diagnostics.Add($"  ✓ Found potential SMB download method: {smbDownloadMethod.Name}");
                    
                    // Try to invoke it
                    try
                    {
                        // Create DataExchangeIdentifier if needed
                        var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                        if (identifierType != null)
                        {
                            var identifier = Activator.CreateInstance(identifierType);
                            var exchangeIdProp = identifierType.GetProperty("ExchangeId");
                            var collectionIdProp = identifierType.GetProperty("CollectionId");
                            
                            if (exchangeIdProp != null) exchangeIdProp.SetValue(identifier, exchange.ExchangeId);
                            if (collectionIdProp != null) collectionIdProp.SetValue(identifier, exchange.CollectionId);

                            // Prepare method parameters
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
                                    // Try exchange ID or collection ID
                                    invokeParams[i] = exchange.ExchangeId;
                                }
                                else
                                {
                                    invokeParams[i] = null;
                                }
                            }

                            // Invoke the method
                            var result = smbDownloadMethod.Invoke(client, invokeParams);
                            
                            if (result is Task<string> taskResult)
                            {
                                var filePath = taskResult.GetAwaiter().GetResult();
                                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                                {
                                    // Copy to our target location
                                    File.Copy(filePath, smbFilePath, true);
                                    diagnostics.Add($"  ✓ Successfully downloaded SMB via Client SDK method: {smbDownloadMethod.Name}");
                                    return true;
                                }
                            }
                            else if (result is string filePath && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                            {
                                // Copy to our target location
                                File.Copy(filePath, smbFilePath, true);
                                diagnostics.Add($"  ✓ Successfully downloaded SMB via Client SDK method: {smbDownloadMethod.Name}");
                                return true;
                            }
                        }
                    }
                    catch (Exception invokeEx)
                    {
                        diagnostics.Add($"  ⚠️ Failed to invoke {smbDownloadMethod.Name}: {invokeEx.Message}");
                    }
                }

                // Method 4: Directly use ParseGeometryAssetBinaryToIntermediateGeometry to convert binary to SMB
                diagnostics.Add($"  === Attempting to use ParseGeometryAssetBinaryToIntermediateGeometry directly ===");
                try
                {
                    var parseMethod = allMethods.FirstOrDefault(m => m.Name == "ParseGeometryAssetBinaryToIntermediateGeometry");
                    var downloadBinaryMethod = allMethods.FirstOrDefault(m => m.Name == "DownloadAndCacheBinaryForBinaryAsset");
                    
                    if (parseMethod != null && downloadBinaryMethod != null)
                    {
                        diagnostics.Add($"  Found both ParseGeometryAssetBinaryToIntermediateGeometry and DownloadAndCacheBinaryForBinaryAsset");
                        
                        // Try to get ElementDataModel first to get GeometryAssets
                        var identifierType = Type.GetType("Autodesk.DataExchange.Core.Models.DataExchangeIdentifier, Autodesk.DataExchange.Core");
                        if (identifierType != null)
                        {
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
                            
                            if (getElementDataModelMethod != null)
                            {
                                diagnostics.Add($"  Getting ElementDataModel to find GeometryAssets...");
                                var elementDataModelResult = getElementDataModelMethod.Invoke(client, new object[] { identifier, CancellationToken.None });
                                
                                if (elementDataModelResult != null)
                                {
                                    var taskResult = ((dynamic)elementDataModelResult).GetAwaiter().GetResult();
                                    object elementDataModel = null;
                                    
                                    diagnostics.Add($"  Task result type: {taskResult?.GetType().Name ?? "null"}");
                                    
                                    var responseType = taskResult?.GetType();
                                    if (responseType != null)
                                    {
                                        // Check for success first
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
                                                // Try to get error info
                                                var errorProp = responseType.GetProperty("Error");
                                                if (errorProp != null)
                                                {
                                                    var error = errorProp.GetValue(taskResult);
                                                    diagnostics.Add($"  Response Error: {error}");
                                                }
                                                elementDataModel = taskResult; // Fallback
                                            }
                                        }
                                        else
                                        {
                                            elementDataModel = taskResult;
                                        }
                                    }
                                    
                                    if (elementDataModel != null)
                                    {
                                        // Get GeometryAssets from ElementDataModel
                                        var elementDataModelType = elementDataModel.GetType();
                                        diagnostics.Add($"  ElementDataModel type: {elementDataModelType.FullName}");
                                        
                                        // Look for a method or property that gives us GeometryAssets
                                        // The exchange data might have GeometryAssets directly
                                        var exchangeDataProp = elementDataModelType.GetProperty("ExchangeData");
                                        if (exchangeDataProp != null)
                                        {
                                            try
                                            {
                                                var exchangeData = exchangeDataProp.GetValue(elementDataModel);
                                                diagnostics.Add($"  Got ExchangeData: {exchangeData?.GetType().Name ?? "null"}");
                                                if (exchangeData != null)
                                                {
                                                    var geometryAssetsProp = exchangeData.GetType().GetProperty("GeometryAssets");
                                                    if (geometryAssetsProp != null)
                                                    {
                                                        var geometryAssets = geometryAssetsProp.GetValue(exchangeData) as System.Collections.IEnumerable;
                                                        
                                                        if (geometryAssets != null)
                                                        {
                                                            foreach (var geometryAsset in geometryAssets)
                                                            {
                                                                if (geometryAsset != null)
                                                                {
                                                                    try
                                                                    {
                                                                        // Get BinaryReference from GeometryAsset
                                                                        var binaryRefProp = geometryAsset.GetType().GetProperty("BinaryReference");
                                                                        if (binaryRefProp != null)
                                                                        {
                                                                            var binaryRef = binaryRefProp.GetValue(geometryAsset);
                                                                            
                                                                            if (binaryRef != null)
                                                                            {
                                                                                diagnostics.Add($"  Found GeometryAsset, downloading binary...");
                                                                                
                                                                                // Download binary - check method signature
                                                                                var downloadParams = downloadBinaryMethod.GetParameters();
                                                                                object[] downloadInvokeParams;
                                                                                
                                                                                if (downloadParams.Length == 3)
                                                                                {
                                                                                    // DownloadAndCacheBinaryForBinaryAsset(identifier, binaryRef, cancellationToken)
                                                                                    downloadInvokeParams = new object[] { identifier, binaryRef, CancellationToken.None };
                                                                                }
                                                                                else if (downloadParams.Length == 4)
                                                                                {
                                                                                    // DownloadAndCacheBinaryForBinaryAsset(identifier, binaryRef, workSpacePath, cancellationToken)
                                                                                    // Get WorkSpaceUserGeometryPath from client
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
                                                                                    
                                                                                    if (!string.IsNullOrEmpty(binaryFilePath) && File.Exists(binaryFilePath))
                                                                                    {
                                                                                        diagnostics.Add($"  Downloaded binary to: {binaryFilePath}");
                                                                                        
                                                                                        // Read binary data from range
                                                                                        // Check if BinaryReference has Start/End properties
                                                                                        var startProp = binaryRef.GetType().GetProperty("Start");
                                                                                        var endProp = binaryRef.GetType().GetProperty("End");
                                                                                        
                                                                                        byte[] binaryData = null;
                                                                                        if (startProp != null && endProp != null)
                                                                                        {
                                                                                            var start = Convert.ToInt64(startProp.GetValue(binaryRef));
                                                                                            var end = Convert.ToInt64(endProp.GetValue(binaryRef));
                                                                                            var length = end - start + 1;
                                                                                            
                                                                                            diagnostics.Add($"  Reading binary range [{start}-{end}] from file...");
                                                                                            
                                                                                            // Read only the range we need
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
                                                                                            diagnostics.Add($"  Read {binaryData.Length} bytes from range");
                                                                                        }
                                                                                        else
                                                                                        {
                                                                                            // Read entire file
                                                                                            binaryData = File.ReadAllBytes(binaryFilePath);
                                                                                            diagnostics.Add($"  Read entire file: {binaryData.Length} bytes");
                                                                                        }
                                                                                        
                                                                                        // Create AssetInfo (might need to create empty one)
                                                                                        var assetInfoType = Type.GetType("Autodesk.DataExchange.DataModels.AssetInfo, Autodesk.DataExchange.DataModels");
                                                                                        var assetInfo = assetInfoType != null ? Activator.CreateInstance(assetInfoType) : new object();
                                                                                        
                                                                                        // Call ParseGeometryAssetBinaryToIntermediateGeometry
                                                                                        diagnostics.Add($"  Converting binary to SMB format...");
                                                                                        var smbBytes = parseMethod.Invoke(client, new object[] { assetInfo, geometryAsset, binaryData }) as byte[];
                                                                                        
                                                                                        if (smbBytes != null && smbBytes.Length > 0)
                                                                                        {
                                                                                            // Save SMB file
                                                                                            File.WriteAllBytes(smbFilePath, smbBytes);
                                                                                            diagnostics.Add($"  ✓ Successfully converted and saved SMB file: {smbBytes.Length} bytes");
                                                                                            return true;
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                    catch (Exception ex)
                                                                    {
                                                                        diagnostics.Add($"  ⚠️ Error processing GeometryAsset: {ex.Message}");
                                                                        // Continue to next geometry asset
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                diagnostics.Add($"  ⚠️ Error getting ExchangeData: {ex.Message}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        diagnostics.Add($"  ⚠️ Could not find required methods for direct SMB conversion");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"  ⚠️ Error using ParseGeometryAssetBinaryToIntermediateGeometry: {ex.Message}");
                    diagnostics.Add($"  Stack: {ex.StackTrace}");
                }

                // Alternative: Look for internal methods that might be called by DownloadCompleteExchangeAsSTEP
                // These might access SMB before converting to STEP
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
                    
                    // Try to find methods called by STEP download that might use SMB
                    // Look for internal/private methods that might be helpers
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

                    // Also inspect fields and properties that might contain SMB-related data
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

                    // Try to find methods that might be called internally during STEP conversion
                    // Look for methods with "Convert", "Transform", "Translate" that might work with SMB
                    var conversionMethods = allMethods.Where(m =>
                    {
                        var name = m.Name.ToLowerInvariant();
                        return (name.Contains("convert") || name.Contains("transform") || name.Contains("translate")) &&
                               (m.IsPrivate || m.IsAssembly || m.IsFamily);
                    }).ToList();
                    
                    if (conversionMethods.Any())
                    {
                        diagnostics.Add($"  Found {conversionMethods.Count} internal conversion/transformation methods:");
                        foreach (var convMethod in conversionMethods.Take(10)) // Limit to first 10
                        {
                            var accessModifier = convMethod.IsPrivate ? "private" : 
                                                convMethod.IsFamily ? "protected" : "internal";
                            diagnostics.Add($"    [{accessModifier}] {convMethod.ReturnType.Name} {convMethod.Name}({string.Join(", ", convMethod.GetParameters().Select(p => p.ParameterType.Name))})");
                        }
                    }
                }

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

                // Look for SMB-related types/methods
                var types = protoGeometryAssembly.GetTypes();
                var smbType = types.FirstOrDefault(t => 
                    t.Name.Contains("SMB") || 
                    t.Name.Contains("FileLoader") || 
                    t.Name.Contains("GeometryLoader"));

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
                else
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
                // Cleanup SMB file
                try
                {
                    if (File.Exists(smbFilePath))
                        File.Delete(smbFilePath);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }
    }
}
