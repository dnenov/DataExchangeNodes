# DataExchange SDK - Reflection-Based API Usage

**Purpose:** This document outlines the internal SDK methods we're currently accessing via reflection to implement SMB geometry upload/download in Dynamo. We're sharing this with the DataExchange SDK team to request either:

1. **Option A:** Expose these methods as public SDK APIs
2. **Option B:** Provide a single high-level public method that handles the entire flow

---

## 1. Upload SMB to Exchange (Export Flow)

### Current Implementation Summary

We need to upload ProtoGeometry SMB files directly to DataExchange without STEP conversion. The public `SyncExchangeDataAsync` method expects STEP files and runs internal conversion, which is slow (~131s for viewable generation).

### High-Level Flow (What We Need)

```csharp
// 1. Get or create ElementDataModel
var elementDataModel = SMBExportHelper.GetElementDataModel(identifier, log, replaceMode, elementNamesToReplace);

// 2. Create Element
var element = SMBExportHelper.CreateElement(elementDataModel, elementId, elementName, log);

// 3. Get ExchangeData (requires reflection - internal field)
var (exchangeData, exchangeDataType) = SMBExportHelper.GetExchangeData(elementDataModel, log);

// 4. Set ExchangeIdentifier
SMBExportHelper.SetExchangeIdentifierIfNeeded(exchangeData, exchangeDataType, identifier, log);

// 5. Find internal types (GeometryAsset, DesignAsset, etc.)
var foundTypes = SMBExportHelper.FindRequiredTypes(exchangeDataType, log);

// 6. Create GeometryAsset(s) for each geometry in SMB file
var (geometryAsset, geometryAssetType, geometryAssetId) = SMBExportHelper.CreateGeometryAsset(...);
SMBExportHelper.SetGeometryAssetUnits(geometryAsset, geometryAssetType, log);

// 7. Create GeometryWrapper and GeometryComponent
var geometryWrapper = SMBExportHelper.CreateGeometryWrapper(foundTypes, exchangeDataType, log);
SMBExportHelper.CreateGeometryComponent(geometryAsset, geometryAssetType, geometryWrapper, ...);

// 8. Add to ExchangeData mappings (requires reflection)
SMBExportHelper.AddGeometryAssetToExchangeData(geometryAsset, exchangeData, ...);
SMBExportHelper.AddGeometryAssetToUnsavedMapping(geometryAsset, exchangeData, ...);

// 9. Setup relationships (Element -> DesignAsset -> GeometryAsset)
SMBExportHelper.SetupDesignAssetAndRelationshipsForMultipleGeometries(...);

// 10. Sync to Exchange (the big one - requires many internal methods)
await SMBExportHelper.SyncExchangeDataForSMBAsync(client, clientType, identifier, exchangeData, ...);
```

### Internal Methods Used in Sync Flow

```csharp
internal static async Task SyncExchangeDataForSMBAsync(...)
{
    // Step 1: Process render styles
    await ProcessRenderStylesFromFileGeometryAsync(client, clientType, exchangeData, ...);

    // Step 2: Start fulfillment transaction
    fulfillmentId = await StartFulfillmentAsync(client, clientType, identifier, ...);

    // Step 3: Get asset infos for SMB upload
    var assetInfosList = await GetAssetInfosForSMBAsync(client, clientType, exchangeData, ...);

    // Step 4: Add render styles to asset infos
    AddRenderStylesToAssetInfos(client, clientType, assetInfosList, exchangeData, ...);

    // Step 5: Upload geometry binaries
    await UploadGeometriesAsync(client, clientType, identifier, fulfillmentId, assetInfosList, ...);

    // Step 6: Upload custom geometries (if any)
    await UploadCustomGeometriesAsync(client, clientType, identifier, fulfillmentId, ...);

    // Step 7: Upload large primitive geometries (if any)
    await UploadLargePrimitiveGeometriesAsync(client, clientType, identifier, fulfillmentId, ...);

    // Step 8: Get fulfillment sync request (schema data)
    var fulfillmentSyncRequest = await GetFulfillmentSyncRequestAsync(client, clientType, ...);

    // Step 9: Batch and send sync requests
    var fulfillmentTasks = await BatchAndSendSyncRequestsAsync(client, clientType, ...);

    // Step 10: Wait for all upload tasks
    await WaitForAllTasksAsync(fulfillmentTasks, log);

    // Step 11: Finish fulfillment transaction
    await FinishFulfillmentAsync(api, apiType, identifier, fulfillmentId, log);

    // Step 12: Poll for completion
    await PollForFulfillmentAsync(client, clientType, identifier, fulfillmentId, log);

    // Step 13: Generate viewable (optional, slow, might not be needed)
    await GenerateViewableAsync(client, clientType, identifier, log);

    // Step 14: Clear local states and set revision
    ClearLocalStatesAndSetRevision(client, clientType, exchangeData, ...);
}
```

### Types We Access via Reflection

```
Autodesk.DataExchange.DataModels.GeometryAsset
Autodesk.DataExchange.DataModels.DesignAsset
Autodesk.DataExchange.DataModels.InstanceAsset
Autodesk.DataExchange.DataModels.Element
Autodesk.DataExchange.DataModels.GeometryWrapper
Autodesk.DataExchange.DataModels.GeometryComponent
Autodesk.DataExchange.DataModels.BinaryReference
Autodesk.DataExchange.OpenAPI.FulfillmentRequest
Autodesk.DataExchange.OpenAPI.FulfillmentRequestExecutionOrder
```

---

## 2. Download SMB from Exchange (Import Flow)

### Current Implementation Summary

We need to download geometry from DataExchange as SMB files for ProtoGeometry to load. The SDK stores geometry in a proprietary binary format that must be converted to SMB.

### High-Level Flow (What We Need)

```csharp
// 1. Get ElementDataModel (public API - works)
var response = await iClient.GetElementDataModelAsync(identifier);
var elementDataModel = response.ValueOrDefault;

// 2. Access ExchangeData (requires reflection - internal field)
var exchangeDataField = elementDataModel.GetType().GetField("exchangeData", BindingFlags.NonPublic | BindingFlags.Instance);
var exchangeData = exchangeDataField.GetValue(elementDataModel);

// 3. Get all GeometryAssets (requires reflection)
var geometryAssets = exchangeData.GetAssetsByType<GeometryAsset>();

// 4. For each GeometryAsset, download binary data (requires reflection)
var downloadBinaryMethod = clientType.GetMethod("DownloadAndCacheBinaryForBinaryAsset");
var binaryFilePath = await downloadBinaryMethod.Invoke(client, [identifier, binaryRef, ...]);
var binaryData = File.ReadAllBytes(binaryFilePath);

// 5. Convert binary to SMB (requires reflection - key method!)
var parseMethod = clientType.GetMethod("ParseGeometryAssetBinaryToIntermediateGeometry");
var smbBytes = parseMethod.Invoke(client, [assetInfo, geometryAsset, binaryData]);
File.WriteAllBytes(smbFilePath, smbBytes);

// 6. Load SMB into ProtoGeometry (public API - works)
var geometries = Geometry.ImportFromSMB(smbFilePath, mmPerUnit);
```

### Key Internal Methods We Need

| Method | Purpose |
|--------|---------|
| `DownloadAndCacheBinaryForBinaryAsset` | Downloads raw geometry binary from cloud storage |
| `ParseGeometryAssetBinaryToIntermediateGeometry` | **Critical:** Converts proprietary binary → SMB format |
| `CreateAssetInfoForGeometryAsset` | Creates AssetInfo needed for parsing |

---

## 3. Requested Public API

### Option A: Expose Individual Methods

```csharp
// For Download - expose as public:
public byte[] Client.ConvertGeometryAssetToSMB(DataExchangeIdentifier id, GeometryAsset asset);

// For Upload - expose as public:
public Task Client.SyncExchangeDataWithSMBAsync(
    DataExchangeIdentifier identifier,
    ElementDataModel model,
    List<string> smbFilePaths,
    SyncOptions options);  // replaceMode, skipViewable, etc.
```

### Option B: High-Level Methods (Preferred)

```csharp
// Download all geometries as SMB files
public Task<List<string>> Client.DownloadGeometriesAsSMBAsync(
    DataExchangeIdentifier identifier,
    string outputDirectory,
    GeometryFilter filter = null);  // optional: filter by element name, ID, etc.

// Upload SMB files to exchange
public Task<SyncResult> Client.UploadSMBGeometriesAsync(
    DataExchangeIdentifier identifier,
    List<SMBGeometryInput> geometries,  // {smbFilePath, elementName, elementId}
    UploadOptions options);  // replaceMode, generateViewable, etc.
```

---

## 4. Current Workaround Impact

Our reflection-based implementation:
- ~50,000+ lines of code in `ExportGeometryToSMB.cs` and `SMBExportHelper.cs`
- Caches 20+ types and methods via reflection
- Replicates the entire internal `SyncExchangeDataAsync` flow
- Requires updates whenever SDK internals change
- Cannot handle future SDK optimizations automatically

A public API would reduce this to ~100 lines and ensure forward compatibility.

---

**Contact:** Deyan Nenov (nenovd)
**Project:** DataExchangeNodes for Dynamo
**Repository:** Internal GitHub
