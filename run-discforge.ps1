# Launch DiscForge (the WinForms GUI) elevated.
# The app manifest requests administrator rights (raw drive access for ripping/
# burning), so it must be started elevated - double-clicking works, but launching
# from a shell needs this. Finds the newest built DiscForge.exe automatically.

$searchRoot = 'C:\dev\DiscForge\src\DiscForge.App\bin\Release'

$exe = Get-ChildItem -Path $searchRoot -Filter 'DiscForge.exe' -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending |
       Select-Object -First 1

if (-not $exe) {
    Write-Host "DiscForge.exe not found under $searchRoot" -ForegroundColor Yellow
    Write-Host "Build it first:" -ForegroundColor Yellow
    Write-Host "    dotnet build C:\dev\DiscForge\DiscForge.sln -c Release"
    exit 1
}

Write-Host "Launching $($exe.FullName)" -ForegroundColor Green
Start-Process -FilePath $exe.FullName -Verb RunAs
