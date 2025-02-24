#!/bin/bash

# accept input parameters...
VERSION=${1:-0.0.2} #0.0.2 is the development hack version
BUILD_NUMBER=${2:-1} 
PACKAGE_SOURCE=${3:-LocalFade} # in prod, should be https://nuget.org
PACKAGE_SOURCE_API_KEY=${4}

# the semantic version includes the build number as the fourth number.
SEM_VER="${VERSION}.${BUILD_NUMBER}"

# the output folder controls where the .nupkg files will go
OUTPUT_FOLDER="bin/artifacts_${SEM_VER}"

# the actual command fragment for the nuget push command. If a parameter is provided, prepend "--api-key", otherwise, empty string so that no api key is given to nuget.
NUGET_KEY_STR=${PACKAGE_SOURCE_API_KEY:+"--api-key $PACKAGE_SOURCE_API_KEY"}

echo "cleaning old output folders..."
rm -rf $OUTPUT_FOLDER

echo "installing fade basic development version=${SEM_VER}"

# build all projects once
BUILD_ARGS="-c Release /p:Version=$SEM_VER /p:FadeInstall=true"
dotnet build build.sln $BUILD_ARGS

# build nuget packages (without building, so its quicker)
PACK_ARGS="--output $OUTPUT_FOLDER /p:Version=$SEM_VER --include-symbols --include-source"
dotnet pack ./FadeBasic $PACK_ARGS
dotnet pack ./FadeBasicCommands $PACK_ARGS
dotnet pack ./FadeBasic.Lib.Standard $PACK_ARGS
dotnet pack ./ApplicationSupport $PACK_ARGS
dotnet pack ./CommandSourceGenerator $PACK_ARGS
dotnet pack ./Templates $PACK_ARGS
dotnet pack ./FadeBuildTasks $PACK_ARGS

# build the LSP and DAP and store it in the associated vscode extension folder
dotnet build ./LSP -o ../VsCode/basicscript/out/tools
dotnet build ./DAP -o ../VsCode/basicscript/out/tools

if [ -z "$FADE_USE_LOCAL_SOURCE" ]; then
  if [ -z "$FADE_NUGET_DRYRUN" ]; then
    # install nuget packages to source
    echo "pushing packages, $OUTPUT_FOLDER/*.$BUILD_NUMBER.nupkg, to nuget source, ${PACKAGE_SOURCE}"
    dotnet nuget push $OUTPUT_FOLDER/*.$BUILD_NUMBER.nupkg --source "$PACKAGE_SOURCE" $NUGET_KEY_STR
  else
    echo "Skipping NuGet push because FADE_NUGET_DRYRUN is set."
  fi
else
  # install nuget packages to source
  echo "pushing fade to local!"
  dotnet nuget list source
  ./setup.sh
  dotnet nuget list source
  dotnet nuget push "$OUTPUT_FOLDER/*.$BUILD_NUMBER.nupkg" --source "LocalFade"
fi
