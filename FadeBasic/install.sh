#!/bin/bash

# accept input parameters...
VERSION=${1:-0.0.2} #0.0.2 is the development hack version
BUILD_NUMBER=${2:-1}
PACKAGE_SOURCE=${3:-LocalFade} # in prod, should be https://nuget.org
PACKAGE_SOURCE_API_KEY=${4}

SKIP_WASM=false
for arg in "$@"; do
  case $arg in --skip-wasm) SKIP_WASM=true ;; esac
done

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
dotnet clean build.sln -c Release
dotnet build build.sln $BUILD_ARGS

# build nuget packages (without building, so its quicker)
PACK_ARGS="--output $OUTPUT_FOLDER /p:Version=$SEM_VER --include-symbols --include-source -p:SymbolPackageFormat=snupkg -c Release"
dotnet pack ./FadeBasic $PACK_ARGS
dotnet pack ./FadeBasicCommands $PACK_ARGS
dotnet pack ./FadeBasic.Lib.Standard $PACK_ARGS
dotnet pack ./FadeBasic.Lib.Web $PACK_ARGS
dotnet pack ./ApplicationSupport $PACK_ARGS
dotnet pack ./CommandSourceGenerator $PACK_ARGS
dotnet pack ./Templates $PACK_ARGS
dotnet pack ./FadeBuildTasks $PACK_ARGS
# FadeBasic.TestAdapter.dll is bundled inside FadeBasic.Testing.nupkg (see
# the ProjectReference + <None Include build/_common/> in FadeBasic.Testing.csproj).
# No separate adapter package — referencing FadeBasic.Testing alone gets both.
dotnet pack ./FadeBasic.Testing $PACK_ARGS

if [ "$SKIP_WASM" = false ]; then

  WASM_ARTIFIACT_DIR="$PWD/bin/wasm_${SEM_VER}"
  echo "publishing FadeBasic.Export.Web WASM bundle..."
  # No --include-symbols/--include-source: FadeBasic.Export.Web is a content-only package.
  #dotnet publish ./FadeBasic.Export.Web -c Release -o bin/wasm_t2 /p:IsPublish=true
  dotnet publish ./FadeBasic.Export.Web -c Release -o $WASM_ARTIFIACT_DIR /p:IsPublish=true
  dotnet pack ./FadeBasic.Export.Web --output "$OUTPUT_FOLDER" /p:Version=$SEM_VER -c Release /p:FADE_WASM_ARTIFACT_DIR=${WASM_ARTIFIACT_DIR} /p:IsPack=true
else
  echo "skipping WASM build (--skip-wasm)"
fi

# build the LSP and DAP once, then fan out the result to each editor extension
TOOLS_OUTPUT="bin/tools_${SEM_VER}"
rm -rf "$TOOLS_OUTPUT"
dotnet build ./LSP -o "$TOOLS_OUTPUT" -c Release
dotnet build ./DAP -o "$TOOLS_OUTPUT" -c Release

for dest in ../VsCode/basicscript/out/tools ../Zed/fade-basic-zed/tools; do
  mkdir -p "$dest"
  cp -R "$TOOLS_OUTPUT"/. "$dest"/
done

if [ -z "$FADE_USE_LOCAL_SOURCE" ]; then
  if [ -z "$FADE_NUGET_DRYRUN" ]; then
    echo "pushing packages to nuget source: ${PACKAGE_SOURCE}"
    # This glob pushes EVERY nupkg in the output folder, including
    # FadeBasic.Export.Web.$SEM_VER (it ends in .$BUILD_NUMBER.nupkg) when WASM
    # was built — so no separate Export.Web push is needed. --skip-duplicate
    # makes re-running a release idempotent against already-pushed packages.
    dotnet nuget push "$OUTPUT_FOLDER/*.$BUILD_NUMBER.nupkg" --source "$PACKAGE_SOURCE" $NUGET_KEY_STR --skip-duplicate
  else
    echo "Skipping NuGet push because FADE_NUGET_DRYRUN is set."
  fi
else
  echo "pushing fade to local!"
  dotnet nuget list source
  ./setup.sh
  dotnet nuget list source
  dotnet nuget push "$OUTPUT_FOLDER/*.$BUILD_NUMBER.nupkg" --source "LocalFade" --skip-duplicate
fi
