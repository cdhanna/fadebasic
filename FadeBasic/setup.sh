#!/bin/bash

SOURCE_FOLDER=$(pwd)/obj/LocalFade

echo "adding development nuget source to $SOURCE_FOLDER"
mkdir -p $SOURCE_FOLDER
dotnet nuget remove source LocalFade || true
dotnet nuget add source $SOURCE_FOLDER --name LocalFade