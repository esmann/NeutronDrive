# NeutronDrive

Very simple command line tool to upload files to Proton Drive.  
This is in no way affiliated with Proton, and is only published for demonstration purposes. Use at your own risk.  
Builds on the sdk-tech-demo and dotnet-crypto repositories, and is only tested on Linux.  
Both sdk-tech-demo and the publicly available dotnet-crypto are outdated, so this will probably need to be changed when
when a newer version is released. 

Clone and follow the instructions in the https://github.com/ProtonDriveApps/dotnet-crypto  
Be aware of the typo in step 3 in dotnet-crypto (missing h in path to .csproj file).
And to get it to work with the sdk-tech-demo Version seems to need to be 0.10.4

Clone and follow the instructions in  https://github.com/ProtonDriveApps/sdk-tech-demo
It seems you also need to do

```sh
dotnet pack -c Release -p:Version=1.0.3 src/Proton.Sdk.Instrumentation/Proton.Sdk.Instrumentation.csproj --output ~/.cache/nuget/
```

You can create a binary using
```sh
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

Has two arguments:
```
--file Path to the file to upload
--folder name of the folder to upload to, it will be created if it doesn't exist,
    defaults to root folder if not provided (do not support subfolders) 
```
