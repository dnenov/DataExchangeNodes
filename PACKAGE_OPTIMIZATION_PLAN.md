# Package Optimization Plan

## Problem Identified

The `ExchangeNodes.NodeViews.csproj` has two settings causing excessive DLL copying:

1. **Line 10:** `CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` - Copies ALL transitive dependencies
2. **Line 36:** `PrivateAssets="none"` on Autodesk.DataExchange.UI - Includes all assets

This results in copying ~50+ DLLs, many of which may not be needed.

## Solution Options

### Option 1: Selective Copying (Recommended)
Change `CopyLocalLockFileAssemblies` to `false` and explicitly list only needed DLLs.

### Option 2: Use PrivateAssets to Exclude Unnecessary Dependencies
Set `PrivateAssets="all"` and only include runtime assets for specific packages.

### Option 3: Hybrid Approach
Keep `CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` but use `PrivateAssets` to exclude specific packages.

## Recommended Changes

### Step 1: Update ExchangeNodes.NodeViews.csproj

```xml
<PropertyGroup>
  <!-- Change to false to prevent copying all transitive dependencies -->
  <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
</PropertyGroup>

<ItemGroup>
  <!-- Only include runtime assets for DataExchange.UI, exclude transitive deps -->
  <PackageReference Include="Autodesk.DataExchange.UI" Version="6.2.12-beta">
    <PrivateAssets>build;analyzers</PrivateAssets>
    <IncludeAssets>runtime;native;contentfiles</IncludeAssets>
  </PackageReference>
</ItemGroup>

<!-- Explicitly copy only the DLLs we actually need -->
<ItemGroup>
  <None Include="$(OutputPath)Autodesk.DataExchange.UI.dll" Condition="Exists('$(OutputPath)Autodesk.DataExchange.UI.dll')">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="$(OutputPath)Autodesk.DataExchange.UI.Bridge.dll" Condition="Exists('$(OutputPath)Autodesk.DataExchange.UI.Bridge.dll')">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="$(OutputPath)Autodesk.DataExchange.UI.ViewModels.dll" Condition="Exists('$(OutputPath)Autodesk.DataExchange.UI.ViewModels.dll')">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="$(OutputPath)Autodesk.DataExchange.UI.Core.dll" Condition="Exists('$(OutputPath)Autodesk.DataExchange.UI.Core.dll')">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <!-- Add other explicitly needed DLLs -->
</ItemGroup>
```

### Step 2: Test Minimal Package

Create a test build and verify all functionality works with fewer DLLs.

### Step 3: Remove Unused DLLs

Based on the analysis in PACKAGE_DLL_ANALYSIS.md, test removing:
- `Autodesk.DataExchange.Authentication.dll`
- `Autodesk.DataExchange.CommonDependencies.dll`
- `Autodesk.DataExchange.ContractProvider.dll`
- `Autodesk.DataExchange.Exceptions.dll`
- `Autodesk.DataExchange.Extensions.Loader.dll`
- `Autodesk.DataExchange.Metrics.dll`
- `Autodesk.DataExchange.Resiliency.dll`
- `Autodesk.DataExchange.Resources.dll`
- `Autodesk.DataExchange.SourceProvider.dll`

## Expected Results

- **Current package size:** ~200-300 MB
- **Optimized package size:** ~150-200 MB
- **Savings:** 50-100 MB

## Testing Checklist

After optimization, test:
- [ ] SelectExchangeElements node opens and works
- [ ] LoadGeometryFromExchange loads geometry correctly
- [ ] ExportGeometryToSMB exports correctly
- [ ] All UI components render properly
- [ ] No missing DLL errors in Dynamo console
