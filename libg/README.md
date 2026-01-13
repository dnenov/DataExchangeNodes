# LibG and ProtoGeometry DLLs

This folder contains the custom LibG and ProtoGeometry DLLs provided by Craig Long.

## Setup Instructions

1. Extract the contents of `RootDir.7z` into the `RootDir/` folder in this directory
2. Extract the contents of `libg_231_0_0.7z` into the `libg_231_0_0/` folder in this directory

The final structure should be:
```
libg/
├── RootDir/
│   ├── ProtoGeometry.dll
│   ├── LibG.Interface.dll
│   └── ... (other DLLs from RootDir.7z)
└── libg_231_0_0/
    ├── LibG.Managed.dll
    ├── LibG.ProtoInterface.dll
    ├── LibG.AsmPreloader.Managed.dll
    ├── LibGCore.dll
    ├── LibG.dll
    └── ... (other DLLs from libg_231_0_0.7z)
```

## How It Works

- During build, these folders are copied to the build output directory
- The package build script (`prepareDynamoPackage.py`) copies them to the Dynamo package
- At runtime, the code automatically detects the package location and uses these DLLs instead of the ones shipped with Dynamo
- If the folders are not found in the package, the code falls back to checking the Downloads folder (for development/testing)

## Notes

- These DLLs replace the ones that Dynamo ships with
- The package will ship with these folders, so users don't need to manually install them
- Make sure to include all files from both archives - missing DLLs may cause runtime errors
