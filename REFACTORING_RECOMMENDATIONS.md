# Refactoring Recommendations for ExportGeometryToSMB.cs

## Completed Refactoring

### ✅ Removed Unused Methods
- `WrapSMBIntoProtobuf` - Never called, removed (~160 lines)
- `GetEnumValue` - Never used in ReflectionHelper, removed
- `GetPropertyValue` - Never used in ReflectionHelper, removed  
- `SetPropertyValue` - Never used in ReflectionHelper, removed

### ✅ Consolidated Repeated Patterns
- **ObjectInfo Creation**: Extracted to `CreateObjectInfo()` helper method
  - Replaced 3 duplicate implementations (60+ lines → 1 reusable method)
  - Used in: AddGeometryAssetToExchangeData, SetupDesignAssetAndRelationships (2 places)

## Recommended Module Extractions

### 1. **DataExchangeSyncHelper.cs** (High Priority)
**Purpose**: Extract the entire sync flow logic to a separate class

**Methods to Extract** (~1000+ lines):
- `SyncExchangeDataAsyncForSMB`
- `ProcessRenderStylesFromFileGeometryAsync`
- `StartFulfillmentAsync`
- `GetAssetInfosForSMBAsync`
- `AddRenderStylesToAssetInfos`
- `UploadGeometriesAsync`
- `UploadCustomGeometriesAsync`
- `UploadLargePrimitiveGeometriesAsync`
- `GetFulfillmentSyncRequestAsync`
- `BatchAndSendSyncRequestsAsync`
- `WaitForAllTasksAsync`
- `FinishFulfillmentAsync`
- `PollForFulfillmentAsync`
- `GenerateViewableAsync`
- `ClearLocalStatesAndSetRevision`
- `SetExchangeIdentifierIfNeeded`
- `DiscardFulfillmentAsync`

**Benefits**:
- Reduces main class from ~3200 lines to ~2200 lines
- Makes sync flow testable independently
- Clearer separation of concerns
- Can be reused by other DataExchange operations

**Structure**:
```csharp
namespace DataExchangeNodes.DataExchange.Helpers
{
    public static class DataExchangeSyncHelper
    {
        public static async Task SyncExchangeDataAsyncForSMB(...)
        // All sync-related methods
    }
}
```

### 2. **DependencyLoader.cs** (Medium Priority)
**Purpose**: Extract DLL loading and ProtoGeometry assembly management

**Methods to Extract** (~200 lines):
- `GetPackageRootDirectory`
- `GetRootDir`
- `GetLibgDir`
- `GetNewProtoGeometryPath`
- `LoadLibGDependencies`
- `GetGeometryTypeFromNewAssembly`
- Related static fields: `_packageRootDir`, `_packageLibgDir`, `_newProtoGeometryPath`, `_newProtoGeometryAssembly`, `_geometryTypeFromNewAssembly`, `_dependenciesLoaded`

**Benefits**:
- Isolates platform-specific DLL loading logic
- Makes dependency management testable
- Can be reused by other nodes that need ProtoGeometry

**Structure**:
```csharp
namespace DataExchangeNodes.DataExchange.Helpers
{
    public static class DependencyLoader
    {
        public static Type GetGeometryTypeFromNewAssembly(List<string> diagnostics)
        // All dependency loading methods
    }
}
```

### 3. **DataExchangeTypeHelper.cs** (Low Priority - Optional)
**Purpose**: Extract type discovery and caching logic

**Methods to Extract** (~150 lines):
- `FindRequiredTypes`
- `CreateGeometryAsset`
- `CreateGeometryWrapper`
- `CreateGeometryComponent`
- Static caches: `_typeCache`, `_methodCache`, `_propertyCache`, etc.

**Benefits**:
- Centralizes type discovery logic
- Makes caching strategy reusable
- Could be shared with other DataExchange nodes

**Note**: This is already well-organized in ReflectionHelper, so extraction is optional.

### 4. **DataExchangeAssetHelper.cs** (Low Priority - Optional)
**Purpose**: Extract asset creation and relationship setup

**Methods to Extract** (~400 lines):
- `CreateElement`
- `SetupDesignAssetAndRelationships`
- `CreateObjectInfo` (already extracted)
- `AddGeometryAssetToExchangeData`
- `AddGeometryAssetToUnsavedMapping`
- `GetAllAssetInfosWithTranslatedGeometryPathForSMB`

**Benefits**:
- Groups related asset manipulation logic
- Makes asset hierarchy management clearer

## Code Organization Summary

### Current Structure (After Cleanup)
```
ExportGeometryToSMB.cs (~3000 lines)
├── ReflectionHelper (nested class)
│   ├── GetMethod
│   ├── InvokeMethod
│   ├── InvokeMethodAsync
│   ├── FindType
│   ├── CreateInstanceWithId
│   └── HandleResponse
├── Static Caches
├── Dependency Loading (could be extracted)
├── ExportToSMB (public API)
├── UploadSMBToExchange (public API)
├── Helper Methods
│   ├── TryGetClientInstance
│   ├── ValidateInputs
│   ├── CreateDataExchangeIdentifier
│   ├── GetElementDataModelAsync
│   ├── GetExchangeData
│   ├── FindRequiredTypes
│   ├── CreateGeometryAsset
│   ├── CreateGeometryWrapper
│   ├── CreateGeometryComponent
│   ├── CreateObjectInfo (NEW - consolidated)
│   ├── AddGeometryAssetToExchangeData
│   ├── CreateDummyStepFile
│   ├── AddGeometryAssetToUnsavedMapping
│   ├── GetAllAssetInfosWithTranslatedGeometryPathForSMB
│   ├── SetupDesignAssetAndRelationships
│   └── InspectGeometryAsset
└── Sync Flow (could be extracted)
    └── 15+ async methods
```

### Recommended Structure
```
ExportGeometryToSMB.cs (~1500 lines)
├── ReflectionHelper
├── Static Caches
├── ExportToSMB (public API)
└── UploadSMBToExchange (public API - orchestrates helpers)

DataExchangeSyncHelper.cs (~1000 lines)
└── All sync flow methods

DependencyLoader.cs (~200 lines)
└── All DLL/assembly loading

DataExchangeAssetHelper.cs (~400 lines) [Optional]
└── Asset creation and relationships
```

## Priority Recommendations

1. **High Priority**: Extract `DataExchangeSyncHelper` - This is the largest, most self-contained block
2. **Medium Priority**: Extract `DependencyLoader` - Clear separation of concerns
3. **Low Priority**: Extract `DataExchangeAssetHelper` - Only if you plan to reuse asset logic elsewhere

## Additional Observations

### Methods That Could Be Simplified Further
- `GetAllAssetInfosWithTranslatedGeometryPathForSMB` - Very long method (~200 lines), could be split into smaller methods
- `SetupDesignAssetAndRelationships` - Complex method (~300 lines), could be broken into:
  - `EnsureRootAssetExists`
  - `LinkInstanceAssetToRoot`
  - `CreateAndLinkDesignAsset`
  - `LinkGeometryAssetToDesignAsset`

### Potential Future Improvements
- Consider using a builder pattern for complex asset hierarchies
- Extract constants for type names (currently string literals scattered throughout)
- Consider dependency injection for testability (though static methods are fine for Dynamo nodes)
