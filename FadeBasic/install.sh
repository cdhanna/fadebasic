#!/bin/bash

VERSION=${1:-0.0.2} #0.0.2 is the development hack version
BUILD_NUMBER=${2:-1} 

SEM_VER="${VERSION}.${BUILD_NUMBER}"
OUTPUT_FOLDER="bin/artifacts_${SEM_VER}"

echo "installing fade basic development version=${SEM_VER}"

# build all projects once
sudo dotnet build -c Release /p:Version=$SEM_VER

# build nuget packages (without building, so its quicker)
dotnet pack --output $OUTPUT_FOLDER /p:Version=$SEM_VER --no-build --include-symbols --include-source 