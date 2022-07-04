dotnet restore
dotnet build UndertaleModTool --no-restore
dotnet build UndertaleModToolUpdater --no-restore
dotnet publish UndertaleModTool -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --output win_x64
dotnet publish UndertaleModToolUpdater -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=True -p:TrimMode=CopyUsed --output win_x64\Updater
