# SMB Upload to DataExchange - Progress & Issues

**Date:** Today's session  
**Goal:** Upload SMB geometry files from Dynamo to DataExchange  
**Status:** Partially working - geometry uploads but may not be visible/persisted correctly

---

## What We're Trying To Do

1. Export Dynamo geometry to SMB files (✅ **WORKING** - `ExportToSMB` node)
2. Upload SMB files to DataExchange as geometry assets (⚠️ **PARTIALLY WORKING** - uploads but issues persist)

---

## Current Implementation Flow

1. Create `Element` and `ElementProperties`
2. Create `GeometryAsset` with `GeometryWrapper` (Type: BRep, Format: Step)
3. Add `GeometryAsset` to `ExchangeData`
4. Start fulfillment via `StartFulfillmentAsync`
5. Create `AssetInfo` with:
   - `Id` = GeometryAsset ID
   - `OutputPath` = SMB file path (⚠️ **This gets deleted after upload**)
   - `BodyInfoList` = List with one `BodyInfo` (Type: BRep)
6. Call `UploadGeometries` (internal method via reflection)
7. Finish fulfillment via `FinishFulfillmentAsync`
8. Poll for fulfillment completion
9. Refresh `ElementDataModel` to get `BinaryReference`

---

## Key Issues Encountered Today

### 1. **Redundant Initial Sync (FIXED)**
- **Problem:** We were calling `SyncExchangeDataAsync` first (created version 1), then manually uploading (created version 2)
- **Root Cause:** Misunderstanding - thought we needed to sync schema first, but `SyncExchangeDataAsync` does the full flow
- **Fix:** Removed initial sync call - now only doing manual upload flow
- **Status:** ✅ Fixed

### 2. **AssetInfo.OutputPath vs AssetInfo.Path**
- **Problem:** `ReadProtoBytes` reads from `OutputPath`, not `Path`
- **Fix:** Set `OutputPath` property on `AssetInfo`
- **Status:** ✅ Fixed

### 3. **BodyInfoList Required for WrapBytes**
- **Problem:** `WrapBytes` method needs `BodyInfoList` to determine geometry type (BRep vs Mesh)
- **Error:** `NullReferenceException` in `WrapBytes` when accessing `srcInfo[0].Type`
- **Fix:** Create `BodyInfo` with `Type = BRep` and add to `BodyInfoList`
- **Status:** ✅ Fixed

### 4. **SMB File Deletion**
- **Problem:** `DeleteInternalOutputFiles` deletes files at `AssetInfo.OutputPath` after upload
- **Impact:** User's original SMB file gets deleted
- **Fix Needed:** Copy SMB file to temp location before setting `OutputPath`
- **Status:** ⚠️ Not yet fixed (user said they don't care for now)

### 5. **BinaryReference Not Persisting**
- **Problem:** `BinaryReference` is set on local `GeometryAsset` after `UploadGeometries`, but:
  - May not be visible in DataExchange viewer
  - May not persist when exchange is refreshed
  - New GeometryAssets don't show up in "Existing GeometryAssets" list on subsequent runs
- **Status:** ❓ **UNKNOWN** - Need to investigate why assets aren't persisting

### 6. **Two Versions Created Per Run**
- **Problem:** Each run creates 2 new exchange versions (both with no visible geometry)
- **Root Cause:** Was the redundant initial sync (now fixed)
- **Status:** ⚠️ Should be fixed, but need to verify

---

## Key Learnings About DataExchange SDK

### Internal vs Public APIs
- **SMB support is NOT in public APIs** - must use reflection for:
  - `UploadGeometries` (internal method)
  - `SetBRepGeometryByAsset` / `SetCustomGeometryByAsset` (internal methods)
  - `ExchangeData` (internal class)
  - `UnsavedGeometryMapping` / `UnsavedCustomGeometryMapping` (internal dictionaries)

### Geometry Pipeline
- **SMB is intermediate format** for BRep geometry
- Normal flow: External format (STEP) → Convert to SMB → Upload SMB binary
- We're bypassing: SMB → Upload SMB binary directly (skipping STEP conversion)

### Fulfillment Flow
- `StartFulfillmentAsync` - Gets fulfillment ID
- `UploadGeometries` - Uploads binary data
- `FinishFulfillmentAsync` - Completes fulfillment
- `PollForFulfillment` - Waits for completion
- `SyncExchangeDataAsync` - Syncs schema (does full flow including upload)

### BinaryReference Linking
- `SetBinaryReferenceForGeometryAssetsLinkedToBinaryPack` links `BinaryReference` to `GeometryAsset`
- Requires `GeometryAsset` to exist in `ExchangeData` with matching ID
- `BinaryReference` is set AFTER `UploadGeometries` completes

---

## Current Code Structure

### Main Method: `UploadSMBToExchange`
**File:** `nodes/DataExchange/ExportGeometryToSMB.cs`

**Key Steps:**
1. Get `Client` instance via reflection
2. Get `ElementDataModel` from exchange
3. Create `Element` with `ElementProperties`
4. Create `GeometryAsset` with `GeometryWrapper` (BRep, Step format)
5. Add to `ExchangeData` (via reflection)
6. Create `AssetInfo` with SMB path and `BodyInfoList`
7. Start fulfillment
8. Upload geometries via `UploadGeometries`
9. Finish fulfillment
10. Poll for fulfillment
11. Refresh `ElementDataModel` to get `BinaryReference`

### Reflection Usage
- `Client.GetMethod("UploadGeometries", BindingFlags.NonPublic | BindingFlags.Instance)`
- `ExchangeData.GetType().GetField("P_UnsavedGeometryMapping", BindingFlags.NonPublic | BindingFlags.Instance)`
- `GeometryAsset.GetType().GetProperty("BinaryReference", BindingFlags.Public | BindingFlags.Instance)`

---

## What's Still Not Working

1. **Geometry not visible in viewer** - Uploads succeed, `BinaryReference` is set, but geometry doesn't show
2. **Assets not persisting** - New `GeometryAssets` don't appear in exchange on subsequent runs
3. **Two versions per run** - Should be fixed by removing initial sync, but need to verify

---

## Next Steps / Questions

1. **Why isn't geometry visible?**
   - Is `GeometryFormat.Step` correct for SMB intermediate format?
   - Should we be using `GeometryFormat.Unknown` instead?
   - Does the viewer require STEP format, not SMB?

2. **Why aren't assets persisting?**
   - Is the schema sync happening automatically after fulfillment?
   - Do we need to call `SyncExchangeDataAsync` AFTER the upload?
   - Is the `GeometryAsset` properly linked to the `Element`?

3. **Is this approach even correct?**
   - Should we convert SMB → STEP first, then upload STEP?
   - Is bypassing the normal pipeline causing issues?
   - Are we missing required metadata or relationships?

4. **File deletion issue**
   - Copy SMB to temp location before upload
   - Clean up temp files after upload completes

---

## Code Locations

- **Export to SMB:** `ExportGeometryToSMB.ExportToSMB()` - ✅ Working
- **Upload to Exchange:** `ExportGeometryToSMB.UploadSMBToExchange()` - ⚠️ Partially working
- **DLL Loading:** `LoadLibGDependencies()` - ✅ Working
- **ProtoGeometry Loading:** `LoadExplicitProtoGeometryDLLs()` - ✅ Working

---

## References

- **fdx-connector project:** `c:\Users\nenovd\OneDrive - Autodesk\Documents\GitHub\fdx-connector\`
  - Main SDK source code
  - `Client.cs` - Contains `UploadGeometries`, `SyncExchangeDataAsync`, etc.
  - `BinaryPacks.cs` - Contains `ReadProtoBytes`, `WrapBytes`
  - `ExchangeData.cs` - Contains `UnsavedGeometryMapping`, `SetBRepGeometryByAsset`

---

## Notes

- We're using reflection extensively because SMB support is not in public APIs
- The complexity is high because we're bypassing normal SDK flows
- May need to reconsider approach if geometry visibility/persistence can't be fixed
