# DataExchange Geometry Upload Flow Analysis

**Date:** Today  
**Purpose:** Compare public STEP flow vs internal SMB flow to identify gaps

---

## Executive Summary

We're trying to upload SMB files directly to DataExchange, bypassing the normal STEP → SMB conversion pipeline. The public API expects STEP files and converts them internally to SMB. We're trying to inject SMB files directly into the internal pipeline, but we're missing critical steps that happen in the public flow.

---

## Public Flow: STEP File Upload (How It's Supposed To Work)

### Step-by-Step Flow

1. **User calls `SyncExchangeDataAsync`**
   - Location: `Client.SyncExchangeDataAsync(DataExchangeIdentifier, ElementDataModel)`
   - This is the main entry point for public API

2. **Internal SyncExchangeDataAsync is called**
   - Location: `Client.SyncExchangeDataAsyncInternal`
   - Creates fulfillment via `StartFulfillmentAsync`
   - Calls `ProcessRenderStylesFromFileGeometry`
   - **Calls `ProcessGeometry`** ← **KEY METHOD**

3. **ProcessGeometry Method** (Line 2804 in Client.cs)
   - **Purpose:** Converts external geometry files (STEP) to internal format (SMB)
   - **Key Steps:**
     ```
     a. Sets up GeometrySDK context (output directory, log file)
     b. Gets batched AssetInfos from ExchangeData
     c. For each batch, calls ProcessGeometries()
     ```
   - **Important:** This method reads from `ExchangeData.UnsavedGeometryMapping`
   - **Important:** It calls `GeometriesToInternal()` which converts STEP → SMB

4. **ProcessGeometries Method** (called from ProcessGeometry)
   - **Purpose:** Uploads geometry after conversion
   - **Key Steps:**
     ```
     a. Calls GeometriesToInternal() - converts STEP to SMB
        - This reads files from UnsavedGeometryMapping
        - Outputs SMB files to temp directory
        - Returns AssetInfo with OutputPath pointing to SMB files
     
     b. Calls UploadGeometries() - uploads SMB binaries
        - Creates BinaryPacks from AssetInfos
        - Wraps SMB bytes in protobuf (BrepTopologicalData)
        - Uploads to cloud
        - Sets BinaryReference on GeometryAssets
     
     c. Calls DeleteInternalOutputFiles() - cleans up temp SMB files
     ```

5. **GeometriesToInternal Method**
   - **Purpose:** Converts external formats (STEP) to internal format (SMB)
   - **Input:** AssetInfo with `Path` pointing to STEP file
   - **Output:** AssetInfo with `OutputPath` pointing to SMB file
   - **Key:** This is where STEP → SMB conversion happens
   - **Location:** `Client.GeometriesToInternal()` → `GeometrySDK.GeometriesToInternal()`

6. **UploadGeometries Method** (Line 1259 in Client.cs)
   - **Purpose:** Uploads SMB binary data to cloud
   - **Key Steps:**
     ```
     a. Creates BinaryPacks from AssetInfos
     b. For each AssetInfo:
        - Reads SMB file from OutputPath
        - Calls BinaryPacks.ReadProtoBytes() - wraps SMB in protobuf
        - Adds to BinaryPack
     c. Calls UploadBinaryPacksAsGeometries()
     d. Sets BinaryReference on GeometryAssets
     e. Deletes temp files via DeleteInternalOutputFiles()
     ```

7. **BinaryPacks.ReadProtoBytes** (Line 40 in BinaryPacks.cs)
   - **Purpose:** Wraps binary data in protobuf format
   - **Key Logic:**
     ```csharp
     - Reads file from AssetInfo.OutputPath
     - Calls WrapBytes(buffer, asset.BodyInfoList, ...)
     - WrapBytes creates BrepTopologicalData protobuf
     - Returns protobuf bytes
     ```

8. **UploadBinaryPacksAsGeometries**
   - **Purpose:** Uploads binary packs to cloud
   - **Key Steps:**
     ```
     a. Creates BinaryPack entities on cloud
     b. Gets upload URLs
     c. Uploads protobuf-wrapped SMB data
     d. Calls SetBinaryReferenceForGeometryAssetsLinkedToBinaryPack()
     ```

9. **SetBinaryReferenceForGeometryAssetsLinkedToBinaryPack** (Line 2647)
   - **Purpose:** Links BinaryReference to GeometryAsset
   - **Key:** Sets `GeometryAsset.BinaryReference` property
   - **Important:** This links the uploaded binary to the asset

10. **FinishFulfillmentAsync**
    - Completes the fulfillment
    - Cloud processes the uploaded data

11. **PollForFulfillment**
    - Waits for fulfillment to complete
    - Gets revision ID

12. **ClearLocalStates**
    - Clears temporary mappings
    - Updates asset statuses
    - Sets revision IDs

---

## Our Internal Flow: Direct SMB Upload (What We're Doing)

### Step-by-Step Flow

1. **Get ElementDataModel**
   - ✅ Get existing exchange data

2. **Create Element and ElementProperties**
   - ✅ Create element structure

3. **Create GeometryAsset**
   - ✅ Create GeometryAsset with GeometryWrapper (BRep, Step format)
   - ✅ Add to ExchangeData
   - ⚠️ **ISSUE:** We're NOT using `SetBRepGeometryByAsset()` which would add to `UnsavedGeometryMapping`

4. **Create AssetInfo manually**
   - ✅ Create AssetInfo with SMB file path
   - ✅ Set OutputPath to SMB file
   - ✅ Create BodyInfoList with BRep type
   - ⚠️ **ISSUE:** We're bypassing the normal AssetInfo creation flow

5. **Start Fulfillment**
   - ✅ Call `StartFulfillmentAsync`

6. **Upload Geometries directly**
   - ✅ Call `UploadGeometries` via reflection
   - ✅ This reads SMB file and wraps in protobuf
   - ✅ Uploads to cloud
   - ✅ Sets BinaryReference
   - ⚠️ **ISSUE:** We're calling this directly, not through ProcessGeometry

7. **Finish Fulfillment**
   - ✅ Call `FinishFulfillmentAsync`

8. **Poll for Fulfillment**
   - ✅ Wait for completion

9. **Refresh ElementDataModel**
   - ✅ Get updated data
   - ⚠️ **ISSUE:** We're NOT calling `SyncExchangeDataAsync` which would sync the schema

---

## Critical Differences & Missing Steps

### 1. **Missing: SetBRepGeometryByAsset() Call**

**Public Flow:**
```csharp
exchangeData.SetBRepGeometryByAsset(geometryAsset, stepFilePath);
// This adds to UnsavedGeometryMapping dictionary
```

**Our Flow:**
```csharp
// We skip this - we add GeometryAsset directly to ExchangeData
// But we never add to UnsavedGeometryMapping
```

**Impact:**
- `ProcessGeometry` reads from `UnsavedGeometryMapping` to know which files to process
- We're not in that dictionary, so if we called `SyncExchangeDataAsync`, our geometry wouldn't be processed
- However, we're bypassing `ProcessGeometry` entirely, so this might be OK

### 2. **Missing: ProcessGeometry → GeometriesToInternal Flow**

**Public Flow:**
```
ProcessGeometry()
  → GeometriesToInternal()  // STEP → SMB conversion
  → UploadGeometries()      // SMB → Cloud upload
```

**Our Flow:**
```
UploadGeometries()  // Direct SMB → Cloud upload (skipping conversion)
```

**Impact:**
- We're skipping the conversion step (which is fine since we already have SMB)
- But we're also skipping the AssetInfo preparation that happens in `GeometriesToInternal`
- The AssetInfo we create manually might be missing some properties

### 3. **Missing: Schema Sync After Upload**

**Public Flow:**
```
SyncExchangeDataAsync()
  → ProcessGeometry()
  → UploadGeometries()
  → FinishFulfillment()
  → PollForFulfillment()
  → ClearLocalStates()  // Updates statuses, sets revisions
  → Schema sync happens automatically
```

**Our Flow:**
```
UploadGeometries()
  → FinishFulfillment()
  → PollForFulfillment()
  → GetElementDataModelAsync()  // Refresh
  → ❌ NO ClearLocalStates()
  → ❌ NO Schema sync
```

**Impact:**
- **This is likely why geometry isn't persisting!**
- The schema sync is what makes the GeometryAsset visible in the exchange
- Without it, the asset exists in memory but isn't persisted to the cloud schema
- The BinaryReference is set locally but not synced to cloud

### 4. **Missing: Relationship to Element**

**Public Flow:**
- When you add geometry via `ElementDataModel.AddGeometry()`, it:
  - Creates GeometryAsset
  - Creates ReferenceRelationship from Element → GeometryAsset
  - Adds to ExchangeData
  - Adds to UnsavedGeometryMapping

**Our Flow:**
- We create GeometryAsset
- We add to ExchangeData
- ⚠️ **We might not have proper relationship to Element**

**Impact:**
- GeometryAsset might not be properly linked to Element
- This could cause it to not show up in the exchange viewer

### 5. **Missing: ClearLocalStates() Call**

**Public Flow:**
```csharp
exchangeData.ClearLocalStates(this.fulfillmentStatus.RevisionId);
// This:
// - Clears UnsavedGeometryMapping
// - Updates asset statuses (CreatedLocal → Created)
// - Sets revision IDs on assets
```

**Our Flow:**
- We never call `ClearLocalStates()`
- Our assets might still have `CreatedLocal` status
- Revision IDs might not be set

**Impact:**
- Assets might not be marked as "synced"
- They might not appear in subsequent `GetElementDataModelAsync` calls

---

## What We Need To Fix

### Priority 1: Schema Sync After Upload

**Problem:** We upload the binary but never sync the schema to cloud.

**Solution:** Call `SyncExchangeDataAsync` AFTER upload, but with a twist:
- We need to sync the schema WITHOUT re-processing geometry
- Or we need to ensure our GeometryAsset is in the right state for sync

**Code Location:**
```csharp
// After PollForFulfillment, we need to:
await client.SyncExchangeDataAsync(identifier, elementDataModel, cancellationToken);
```

**But wait:** This will call `ProcessGeometry` again, which will try to process `UnsavedGeometryMapping`. Since we're not in that dictionary, it should skip our geometry. But we need to make sure the schema sync still happens.

### Priority 2: Proper Relationship to Element

**Problem:** GeometryAsset might not be properly linked to Element.

**Solution:** Ensure we create a ReferenceRelationship:
```csharp
// After creating GeometryAsset and adding to ExchangeData:
var referenceRelationship = new ReferenceRelationship();
exchangeData.AddRelationship(elementAsset, geometryAsset, referenceRelationship);
```

### Priority 3: ClearLocalStates After Sync

**Problem:** Assets might not have correct status/revision.

**Solution:** After schema sync, the `ClearLocalStates` should be called automatically. But we might need to ensure our assets are in the right state.

### Priority 4: Use SetBRepGeometryByAsset (Optional)

**Problem:** We're not using the standard method to register geometry.

**Solution:** Even though we're bypassing the conversion, we could still call:
```csharp
exchangeData.SetBRepGeometryByAsset(geometryAsset, smbFilePath);
```
This would add us to `UnsavedGeometryMapping`, which might help with schema sync.

---

## Recommended Fix Strategy

### Option A: Call SyncExchangeDataAsync After Upload (✅ RECOMMENDED - CONFIRMED SAFE)

1. Upload SMB via `UploadGeometries` (as we do now)
2. Finish fulfillment
3. Poll for fulfillment
4. **Call `SyncExchangeDataAsync` to sync schema**
   - This will sync the GeometryAsset to cloud
   - **It will skip geometry processing** because:
     - `GetBatchedAssetInfos` reads from `UnsavedGeometryMapping`
     - Since we never called `SetBRepGeometryByAsset`, this dictionary is empty
     - Empty dictionary → empty enumerable → `ProcessGeometry` does nothing
   - **But it WILL sync the schema** via `PublishSyncDataAsync` calls
   - This makes assets visible and persistent

**Pros:**
- ✅ Uses standard flow for schema sync
- ✅ Should make assets visible
- ✅ Confirmed safe - won't re-process geometry
- ✅ BinaryReference will be synced as part of asset schema

**Cons:**
- ⚠️ Will create a second fulfillment (but this is necessary for schema sync)
- ⚠️ Need to ensure GeometryAsset is properly linked to Element (relationship)

### Option B: Manual Schema Sync

1. Upload SMB via `UploadGeometries`
2. Finish fulfillment
3. Poll for fulfillment
4. **Manually call the schema sync part** (via reflection)
   - Get `FulfillmentSyncRequestHandler`
   - Create sync request with our ExchangeData
   - Call `PublishSyncDataAsync`

**Pros:**
- More control
- No risk of re-processing geometry

**Cons:**
- More complex
- Uses more reflection

### Option C: Use SetBRepGeometryByAsset + Full Sync

1. Create GeometryAsset
2. **Call `SetBRepGeometryByAsset`** (adds to UnsavedGeometryMapping)
3. **Call `SyncExchangeDataAsync`** (full flow)
   - This will call `ProcessGeometry`
   - But since file is already SMB, `GeometriesToInternal` should skip conversion
   - Or we need to ensure it doesn't try to convert

**Pros:**
- Uses standard flow entirely
- Should work if we can make GeometriesToInternal skip conversion

**Cons:**
- Might try to convert SMB → SMB (wasteful)
- More complex to ensure it works

---

## Key Questions to Answer

1. **Does `GeometriesToInternal` check file format before converting?**
   - If it sees SMB, does it skip conversion?
   - Or does it always try to convert?
   - **ANSWER:** Not relevant - we're bypassing this entirely

2. **Can we call `SyncExchangeDataAsync` after upload without re-processing geometry?**
   - If `UnsavedGeometryMapping` is empty, does it skip `ProcessGeometry`?
   - Or does it always process?
   - **ANSWER:** ✅ **YES!** Looking at `GetBatchedAssetInfos` (line 2701):
     - It only creates AssetInfos from `UnsavedGeometryMapping`, `UnsavedMeshGeometryMapping`, and `UnsavedMeshObjectGeometryMapping`
     - If these are empty, `GetBatchedAssetInfos` returns empty enumerable
     - `ProcessGeometry` iterates over `GetBatchedAssetInfos(exchangeData)`
     - If enumerable is empty, the foreach loop does nothing
     - So `ProcessGeometries` is never called
     - **BUT** schema sync still happens via `PublishSyncDataAsync` calls
     - **CONCLUSION:** We can safely call `SyncExchangeDataAsync` after upload!

3. **Is the BinaryReference set correctly?**
   - We set it locally, but is it synced to cloud?
   - Does schema sync preserve BinaryReference?
   - **ANSWER:** BinaryReference is part of the GeometryAsset schema, so it should be synced

4. **What's the relationship between BinaryReference and schema sync?**
   - Is BinaryReference part of the schema?
   - Or is it separate metadata?
   - **ANSWER:** BinaryReference is a property on GeometryAsset, so it's part of the asset schema that gets synced

---

## Next Steps

1. **✅ IMPLEMENT: Call `SyncExchangeDataAsync` after upload**
   - Add call after `PollForFulfillment` completes
   - This will sync schema without re-processing geometry
   - Should make assets visible and persistent
   - **Code:**
     ```csharp
     // After PollForFulfillment
     var syncMethod = clientType.GetMethod("SyncExchangeDataAsync", 
         BindingFlags.Public | BindingFlags.Instance,
         null,
         new[] { typeof(DataExchangeIdentifier), typeof(ElementDataModel), typeof(CancellationToken) },
         null);
     if (syncMethod != null)
     {
         var syncTask = syncMethod.Invoke(client, new object[] { identifier, elementDataModel, CancellationToken.None });
         // Await the task...
     }
     ```

2. **Verify relationship creation**
   - Ensure GeometryAsset is properly linked to Element
   - Check if ReferenceRelationship is needed
   - **Current code:** We're adding GeometryAsset to ExchangeData, but might need explicit relationship

3. **Test the fix**
   - Upload SMB file
   - Verify geometry appears in viewer
   - Verify assets persist on subsequent runs
   - Check that only one fulfillment is created (or two if needed)

4. **Optional: Use SetBRepGeometryByAsset**
   - We could call this to add to UnsavedGeometryMapping
   - But since we're bypassing conversion, it's not necessary
   - Might help with relationship tracking though

---

## Code References

### Public Flow Entry Points
- `Client.SyncExchangeDataAsync()` - Line 1644
- `Client.SyncExchangeDataAsyncInternal()` - Line 1655
- `Client.ProcessGeometry()` - Line 2804
- `Client.ProcessGeometries()` - Line 2350
- `Client.UploadGeometries()` - Line 1259

### Internal Methods We're Using
- `Client.UploadGeometries()` - Line 1259 (via reflection)
- `Client.StartFulfillmentAsync()` - Line 1456
- `Client.FinishFulfillmentAsync()` - Line 1500
- `Client.PollForFulfillment()` - Line 2918

### Key Data Structures
- `ExchangeData.UnsavedGeometryMapping` - Dictionary<string, string> (assetId → filePath)
- `ExchangeData.SetBRepGeometryByAsset()` - Line 561 in ExchangeData.cs
- `BinaryPacks.ReadProtoBytes()` - Line 40 in BinaryPacks.cs
- `Client.SetBinaryReferenceForGeometryAssetsLinkedToBinaryPack()` - Line 2647

---

## Conclusion

The main issue is that **we're uploading the binary data but never syncing the schema**. The `SyncExchangeDataAsync` method does two things:
1. Processes geometry (STEP → SMB conversion)
2. Syncs schema to cloud (makes assets visible)

We're doing #1 manually (skipping conversion, uploading SMB directly), but we're **completely skipping #2**. This is why:
- Geometry uploads successfully
- BinaryReference is set locally
- But geometry doesn't show in viewer
- Assets don't persist on subsequent runs

**The fix:** We need to call `SyncExchangeDataAsync` AFTER our upload, but we need to ensure it only syncs schema and doesn't re-process geometry.
