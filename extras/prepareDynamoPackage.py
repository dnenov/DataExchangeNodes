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
    try:
      # Make files writable before deleting (Windows permission issue)
      def make_writable(func, path, exc_info):
        os.chmod(path, 0o777)
        func(path)
      
      shutil.rmtree(targetFolder, onerror=make_writable)
    except Exception as e:
      print(f"Warning: Could not remove existing dynamo-package folder: {e}")
      print("Attempting to continue anyway...")

  # The actual build output is in bin\Config\DynamoVersion\DataExchangeNodes
  sourceBinariesFolder = os.path.join(currentFolder, "..", "bin", config, dynamoVersion, "DataExchangeNodes")

  if not (os.path.exists(sourceBinariesFolder) and 
          os.path.exists(templateFolder)):
    print("Incomplete build.")
    print(f"Expected build output at: {sourceBinariesFolder}")
    print(f"Template folder: {templateFolder}")
    print("Build may not have completed successfully, or output path is different.")
    sys.exit(1)  # Exit with error code so MSBuild knows the step failed

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

  # Copy all binaries and subdirectories from source to target bin folder
  for item in os.listdir(sourceBinariesFolder):
    source_item = os.path.join(sourceBinariesFolder, item)
    target_item = os.path.join(targetFolderBin, item)
    if os.path.isfile(source_item):
      shutil.copy2(source_item, targetFolderBin)
    elif os.path.isdir(source_item):
      if os.path.exists(target_item):
        shutil.rmtree(target_item)
      shutil.copytree(source_item, target_item)

  # Helper function to make files writable before deletion (Windows permission issue)
  def make_writable(func, path, exc_info):
    try:
      os.chmod(path, 0o777)
      func(path)
    except:
      pass  # Ignore errors during cleanup

  # Deploy to Dynamo package folders (use install version like 4.1, not full version like 4.1.0-beta3200)
  packageTargetFolder = os.path.join(os.getenv("APPDATA"), "Dynamo", "Dynamo Core", dynamoInstallVersion, "packages", "DataExchangeNodes")

  if os.path.exists(packageTargetFolder):
    try:
      shutil.rmtree(packageTargetFolder, onerror=make_writable)
    except Exception as e:
      print(f"Warning: Could not remove existing package folder {packageTargetFolder}: {e}")
      print("This may be because Dynamo is running. The package will be updated in place.")

  try:
    shutil.copytree(targetFolder, packageTargetFolder)
    print("Dynamo package complete  " + packageTargetFolder)
  except Exception as e:
    print(f"Error copying to {packageTargetFolder}: {e}")
    print("Make sure Dynamo is not running and try again.")

  # Also copy to Dynamo Revit
  packageTargetFolderRevit = os.path.join(os.getenv("APPDATA"), "Dynamo", "Dynamo Revit", dynamoInstallVersion, "packages", "DataExchangeNodes")

  if os.path.exists(packageTargetFolderRevit):
    try:
      shutil.rmtree(packageTargetFolderRevit, onerror=make_writable)
    except Exception as e:
      print(f"Warning: Could not remove existing Revit package folder {packageTargetFolderRevit}: {e}")

  try:
    shutil.copytree(targetFolder, packageTargetFolderRevit)
    print("Dynamo package complete  " + packageTargetFolderRevit)
  except Exception as e:
    print(f"Error copying to {packageTargetFolderRevit}: {e}")
    print("Make sure Dynamo Revit is not running and try again.")

if __name__ == "__main__":
  main()

