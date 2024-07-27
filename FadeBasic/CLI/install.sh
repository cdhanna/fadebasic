#!/bin/bash

version='0.0.1'
package=brewedink.fade.cli
output=bin/nuget

dotnet pack -p:PackageVersion=$version -o $output
dotnet tool uninstall $package -g || true
dotnet tool install --global --version $version --add-source $output $package