# Builds the smallest practical WinKeyGuard.exe.
#
# Framework-dependent single file: a few hundred KB, needs the .NET Desktop Runtime
# (you already have it if `dotnet run` works). This is the recommended build.
dotnet publish -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -o .\publish

Write-Host ""
Get-ChildItem .\publish\*.exe | Select-Object Name, @{n='SizeKB';e={[math]::Round($_.Length/1KB)}}

# If you ever need a build that runs on a PC with no .NET installed, use this instead.
# It is much larger (tens of MB) because it bundles the whole runtime and WinForms:
#
# dotnet publish -c Release -r win-x64 --self-contained true `
#     -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
#     -o .\publish-selfcontained
