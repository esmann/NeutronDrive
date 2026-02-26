#!/usr/bin/env bash
export DOTNET_ROOT=/home/alex/.dotnet/

rm -rf build
rm -rf NeutronDrive/bin
dotnet nuget locals all --clear

mkdir -p build/nuget
cd build

git clone https://github.com/ProtonDriveApps/sdk-tech-demo.git
git clone https://github.com/ProtonDriveApps/dotnet-crypto.git

cd dotnet-crypto
build/build-go.sh linux/amd64
dotnet pack -c Release -r linux-x64 -p:Version=0.10.4 src/dotnet/Proton.Cryptography.csproj --output ../nuget

cd ../sdk-tech-demo

dotnet pack -c Release -r linux-x64 -p:Version=1.0.0 src/Proton.Sdk/Proton.Sdk.csproj --output ../nuget --source ../nuget --source https://api.nuget.org/v3/index.json
dotnet pack -c Release -r linux-x64 -p:Version=1.0.0 src/Proton.Sdk.Drive/Proton.Sdk.Drive.csproj --output ../nuget --source ../nuget --source https://api.nuget.org/v3/index.json
dotnet pack -c Release -r linux-x64 -p:Version=1.0.0 src/Proton.Sdk.Instrumentation/Proton.Sdk.Instrumentation.csproj --output ../nuget --source ../nuget --source https://api.nuget.org/v3/index.json

cd ../../
pwd
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true NeutronDrive/NeutronDrive.csproj --source ./build/nuget --source https://api.nuget.org/v3/index.json