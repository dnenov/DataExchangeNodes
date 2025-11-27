using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.DesignScript.Geometry;
using Autodesk.DesignScript.Runtime;

// Required for IntPtr and ACIS pointer operations
using System.Runtime.InteropServices;

namespace DataExchangeNodes.DataExchange
{
    /// <summary>
    /// Load geometry from a DataExchange using Dynamo.Proxy.FileLoader
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
        /// Uses the native Dynamo FileLoader to download and convert .smb (DataExchange internal format) to ACIS geometry in memory.
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
                diagnostics.Add("=== DataExchange Geometry Loader ===");
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

                // Create .fdx manifest file
                var fdxPath = CreateFdxManifest(exchange, accessToken, unit, diagnostics);
                diagnostics.Add($"✓ Created .fdx manifest: {fdxPath}");

                // Load geometry using Dynamo.Proxy.FileLoader
                geometries = LoadGeometryFromFdx(fdxPath, unit, diagnostics);
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
        /// Creates a .fdx manifest file for the FileLoader
        /// Format based on DynamoATF implementation
        /// </summary>
        private static string CreateFdxManifest(
            Exchange exchange,
            string accessToken,
            string unit,
            List<string> diagnostics)
        {
            try
            {
                // Create temp directory for .fdx file
                var tempDir = Path.Combine(Path.GetTempPath(), "DataExchangeNodes");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                var fdxPath = Path.Combine(tempDir, $"Exchange_{exchange.ExchangeId}.fdx");

                // Build ExchangeFileUrl in the format FileLoader expects (query filter format)
                // Format: https://developer.api.autodesk.com/exchange/v1/exchanges?filters=attribute.exchangeFileUrn==<urn>
                string exchangeUrl;
                if (!string.IsNullOrEmpty(exchange.FileUrn))
                {
                    exchangeUrl = $"https://developer.api.autodesk.com/exchange/v1/exchanges?filters=attribute.exchangeFileUrn=={exchange.FileUrn}";
                }
                else
                {
                    // Fallback: try direct collection URL (might not work with FileLoader)
                    exchangeUrl = $"https://developer.api.autodesk.com/exchange/v1/exchanges/{exchange.ExchangeId}/collections/{exchange.CollectionId}";
                    diagnostics.Add("  ⚠️ Warning: FileUrn not available, using direct URL (may not work)");
                }

                // Write .fdx manifest
                var fileLines = new string[4];
                fileLines[0] = $"Token={accessToken}";
                fileLines[1] = $"ExchangeFileUrl={exchangeUrl}";
                fileLines[2] = "FDXConsumerLog=0";
                fileLines[3] = $"unit={unit}";

                File.WriteAllLines(fdxPath, fileLines);

                diagnostics.Add($"  Exchange URL: {exchangeUrl}");
                diagnostics.Add($"  Unit: {unit}");
                diagnostics.Add($"  Token length: {accessToken.Length} chars");

                return fdxPath;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ Failed to create .fdx manifest: {ex.Message}");
                throw new IOException($"Failed to create .fdx manifest: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads geometry from .fdx file using Dynamo.Proxy.FileLoader
        /// </summary>
        private static List<Geometry> LoadGeometryFromFdx(
            string fdxPath,
            string unit,
            List<string> diagnostics)
        {
            var geometries = new List<Geometry>();
            Dynamo.Proxy.FileLoader fileLoader = null;

            try
            {
                diagnostics.Add("\nLoading via Dynamo.Proxy.FileLoader...");

                // Create FileLoader (native Dynamo component)
                fileLoader = new Dynamo.Proxy.FileLoader(fdxPath, unit);

                // Load geometry from DataExchange
                bool loadSuccess = fileLoader.Load();

                if (!loadSuccess)
                {
                    diagnostics.Add("✗ FileLoader.Load() returned false");
                    throw new InvalidOperationException("FileLoader.Load() failed - check DataExchange connectivity");
                }

                diagnostics.Add("✓ FileLoader.Load() succeeded");

                // Get imported objects
                var importedObjects = fileLoader.GetImportedObjects();
                var objectCount = importedObjects.Count();
                diagnostics.Add($"✓ Retrieved {objectCount} imported object(s)");

                // Extract geometry from imported objects
                for (int i = 0; i < objectCount; i++)
                {
                    var importedObject = importedObjects.GetItem(i);
                    var extractedGeometry = ExtractGeometry(importedObject, diagnostics);
                    
                    if (extractedGeometry != null && extractedGeometry.Any())
                    {
                        geometries.AddRange(extractedGeometry);
                    }
                }

                return geometries;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"✗ Error loading geometry: {ex.Message}");
                throw;
            }
            finally
            {
                // Cleanup FileLoader
                fileLoader?.Dispose();

                // Cleanup .fdx file
                try
                {
                    if (File.Exists(fdxPath))
                        File.Delete(fdxPath);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }

        /// <summary>
        /// Extracts Dynamo geometry from Dynamo.Proxy imported objects
        /// Uses the FromNativePointer pattern from ProtoGeometry to convert ACIS pointers to Dynamo geometry
        /// </summary>
        private static List<Geometry> ExtractGeometry(
            Dynamo.Proxy.ImportedObject importedObject,
            List<string> diagnostics)
        {
            var geometries = new List<Geometry>();

            try
            {
                // Handle Brep (most common for DataExchange)
                if (importedObject is Dynamo.Proxy.Brep brep)
                {
                    var asmBody = brep.GetAsmBody();
                    if (asmBody != IntPtr.Zero)
                    {
                        var brepGeometry = Geometry.FromNativePointer(asmBody);
                        if (brepGeometry != null && brepGeometry.Any())
                        {
                            geometries.AddRange(brepGeometry);
                            diagnostics.Add($"  ✓ Brep: {brepGeometry.Count()} geometries");
                        }
                    }
                    return geometries;
                }

                // Handle Curves
                if (importedObject is Dynamo.Proxy.Curve curve)
                {
                    var arrayLength = curve.getArrayLength();
                    for (int i = 0; i < arrayLength; i++)
                    {
                        var asmCurve = curve.GetAsmCurve(i);
                        if (asmCurve != IntPtr.Zero)
                        {
                            var curveGeometry = Geometry.FromNativePointer(asmCurve);
                            if (curveGeometry != null && curveGeometry.Any())
                            {
                                geometries.AddRange(curveGeometry);
                            }
                        }
                    }
                    if (geometries.Any())
                    {
                        diagnostics.Add($"  ✓ Curve: {geometries.Count} geometries");
                    }
                    return geometries;
                }

                // Handle Points
                if (importedObject is Dynamo.Proxy.Point point)
                {
                    var vertices = point.GetVerticesOfPoint();
                    if (vertices.Count() >= 3)
                    {
                        var x = vertices.ElementAt(0);
                        var y = vertices.ElementAt(1);
                        var z = vertices.ElementAt(2);
                        var dynamoPoint = Point.ByCoordinates(x, y, z);
                        geometries.Add(dynamoPoint);
                        diagnostics.Add($"  ✓ Point: 1 geometry");
                    }
                    return geometries;
                }

                // Handle IndexMesh
                if (importedObject is Dynamo.Proxy.IndexMesh indexMesh)
                {
                    // IndexMesh requires more complex handling - skip for now
                    diagnostics.Add($"  ⚠ IndexMesh: Skipped (not yet implemented)");
                    return geometries;
                }

                // Handle Group (container for other objects)
                if (importedObject is Dynamo.Proxy.Group group)
                {
                    diagnostics.Add($"  ℹ Group: (container - children extracted separately)");
                    return geometries;
                }

                // Handle Layer (container)
                if (importedObject is Dynamo.Proxy.Layer layer)
                {
                    diagnostics.Add($"  ℹ Layer: (container - children extracted separately)");
                    return geometries;
                }

                // Unknown type
                diagnostics.Add($"  ⚠ Unknown type: {importedObject.GetType().Name}");
            }
            catch (Exception ex)
            {
                diagnostics.Add($"  ✗ Error extracting geometry: {ex.Message}");
            }

            return geometries;
        }
    }
}

