# NeutronDrive

Very simple command line tool to upload files to Proton Drive.  
This is in no way affiliated with Proton, and is only published for demonstration purposes. Use at your own risk.  
Builds on the sdk-tech-demo and dotnet-crypto repositories, and is only tested on Linux.  
Both sdk-tech-demo and the publicly available dotnet-crypto are outdated, so this will probably need to be changed when
when a newer version is released. 

# Setup project

run
```shell
build_amd64.sh
```

It should download and build the sdk-tech-demo and dotnet-crypto projects, and then build the neutron-drive project. The resulting executable will be in the `bin` folder.
After the first build, you can just run `build.sh` to build the project, as long as you don't need to update the sdk-tech-demo and dotnet-crypto dependencies.
Also after the first build, the project should work in Rider.

Has two arguments:
```
--file Path to the file to upload
--folder name of the folder to upload to, it will be created if it doesn't exist,
    defaults to root folder if not provided (do not support subfolders) 
```
