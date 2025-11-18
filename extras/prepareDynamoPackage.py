import shutil
import os
import sys
import json

def main():

  print("Dynamo package creation started")

  if len(sys.argv) != 5:
    print("Missing package config params")
    return

  platform = sys.argv[1]
  config = sys.argv[2]
  dynamoVersion = sys.argv[3] # Full Dynamo version (e.g., 4.1.0-beta3200)
  dynamoInstallVersion = sys.argv[4] # Major.minor Dynamo version (e.g., 4.1)

  currentFolder = os.path.dirname(os.path.realpath(__file__))
  templateFolder = os.path.join(currentFolder, "package-template")
  targetFolder = os.path.join(currentFolder, "..", "dynamo-package")

  if (os.path.exists(targetFolder)):
    shutil.rmtree(targetFolder)

  # The actual build output is in bin\Config\DynamoVersion\DataExchangeNodes\win-x64
  sourceBinariesFolder = os.path.join(currentFolder, "..", "bin", config, dynamoVersion, "DataExchangeNodes", "win-x64")

  if not (os.path.exists(sourceBinariesFolder) and 
          os.path.exists(templateFolder)):
    print("Incomplete build.")
    return

  shutil.copytree(templateFolder, targetFolder)
  targetFolderBin = os.path.join(targetFolder, "bin")
  targetFolderDyf = os.path.join(targetFolder, "dyf")
  targetFolderExtra = os.path.join(targetFolder, "extra")

  os.makedirs(targetFolderBin, exist_ok=True)
  os.makedirs(targetFolderDyf, exist_ok=True)
  os.makedirs(targetFolderExtra, exist_ok=True)

  # Check if pkg.json exists in template
  pkgJsonPath = os.path.join(targetFolder, "pkg.json")
  if not os.path.exists(pkgJsonPath):
    print("Incomplete build. Missing pkg.json in template")
    return

  # Read version from project (we'll use the template version for now)
  # In the future, this could read from the built assembly
  packageVersion = "0.1.0"

  # Replace placeholders in template pkg.json
  with open(pkgJsonPath, 'r') as file:
    pkgJsonContent = file.read()
    pkgJsonContent = pkgJsonContent.replace('$Version$', packageVersion)
    pkgJsonContent = pkgJsonContent.replace('$DynamoVersion$', dynamoInstallVersion)

  with open(pkgJsonPath, 'w') as file:
    file.write(pkgJsonContent)

  print(f"Package version set to: {packageVersion}")

  # Copy all binaries from source to target bin folder
  for item in os.listdir(sourceBinariesFolder):
    source_item = os.path.join(sourceBinariesFolder, item)
    if os.path.isfile(source_item):
      shutil.copy2(source_item, targetFolderBin)

  # Deploy to Dynamo package folders (use install version like 4.1, not full version like 4.1.0-beta3200)
  packageTargetFolder = os.path.join(os.getenv("APPDATA"), "Dynamo", "Dynamo Core", dynamoInstallVersion, "packages", "DataExchangeNodes")

  if os.path.exists(packageTargetFolder):
    shutil.rmtree(packageTargetFolder)

  shutil.copytree(targetFolder, packageTargetFolder)
  print("Dynamo package complete  " + packageTargetFolder)

  # Also copy to Dynamo Revit
  packageTargetFolderRevit = os.path.join(os.getenv("APPDATA"), "Dynamo", "Dynamo Revit", dynamoInstallVersion, "packages", "DataExchangeNodes")

  if os.path.exists(packageTargetFolderRevit):
    shutil.rmtree(packageTargetFolderRevit)

  shutil.copytree(targetFolder, packageTargetFolderRevit)
  print("Dynamo package complete  " + packageTargetFolderRevit)

if __name__ == "__main__":
  main()

