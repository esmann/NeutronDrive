Build for two platforms: linux/amd64 and linux/arm64

dotnet-crypto: 
```
dotnet pack -c Release -r linux-arm64 -p:Version=1.0.0 src/dotnet/Proton.Cryptography.csproj --output ~/.cache/nuget/linux-arm64/
dotnet pack -c Release -r linux-x64 -p:Version=1.0.0 src/dotnet/Proton.Cryptography.csproj --output ~/.cache/nuget/linux-x64/
```

sdk-tech-demo:
```
dotnet pack -c Release -r linux-arm64 -p:Version=1.0.0 src/Proton.Sdk/Proton.Sdk.csproj --output ~/.cache/nuget/linux-arm64 --source ~/.cache/nuget/linux-arm64
dotnet pack -c Release -r linux-x64 -p:Version=1.0.0 src/Proton.Sdk/Proton.Sdk.csproj --output ~/.cache/nuget/linux-x64 --source ~/.cache/nuget/linux-x64

dotnet pack -c Release -r linux-arm64 -p:Version=1.0.0 src/Proton.Sdk.Drive/Proton.Sdk.Drive.csproj --output ~/.cache/nuget/linux-arm64 --source ~/.cache/nuget/linux-arm64
dotnet pack -c Release -r linux-x64 -p:Version=1.0.0 src/Proton.Sdk.Drive/Proton.Sdk.Drive.csproj --output ~/.cache/nuget/linux-x64 --source ~/.cache/nuget/linux-x64

dotnet pack -c Release -r linux-arm64 -p:Version=1.0.0 src/Proton.Sdk.Instrumentation/Proton.Sdk.Instrumentation.csproj --output ~/.cache/nuget/linux-arm64/ --source ~/.cache/nuget/linux-arm64
dotnet pack -c Release -r linux-x64 -p:Version=1.0.0 src/Proton.Sdk.Instrumentation/Proton.Sdk.Instrumentation.csproj --output ~/.cache/nuget/linux-x64/ --source ~/.cache/nuget/linux-x64
```

```
dotnet publish -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true --source ~/.cache/nuget/linux-arm64
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true --source ~/.cache/nuget/linux-x64
```

