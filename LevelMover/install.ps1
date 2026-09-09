<#
    Собирает плагин и устанавливает его для Revit 2022 в профиль текущего пользователя.
    Запуск:  powershell -ExecutionPolicy Bypass -File install.ps1
    Revit при запуске читает манифест из %AppData%\Autodesk\Revit\Addins\2022.
#>
param(
    [string]$RevitVersion = '2022',
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\LevelMover\LevelMover.csproj'

if (-not $SkipBuild) {
    Write-Host 'Сборка...' -ForegroundColor Cyan
    & dotnet build $project -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }
}

$addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$targetDir = Join-Path $addinsDir 'LevelMover'

if (-not (Test-Path $addinsDir)) {
    throw "Папка плагинов не найдена: $addinsDir. Проверьте, что Revit $RevitVersion установлен и хотя бы раз запускался."
}
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir | Out-Null
}

$binDir = Join-Path $root "src\LevelMover\bin\$Configuration"

# Revit держит DLL загруженной, пока открыт — при запущенном Revit копирование упадёт.
$revitRunning = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue
if ($revitRunning) {
    throw 'Revit запущен — закройте его перед установкой, иначе DLL заблокирована.'
}

Copy-Item (Join-Path $binDir 'LevelMover.dll') $targetDir -Force
$pdb = Join-Path $binDir 'LevelMover.pdb'
if (Test-Path $pdb) { Copy-Item $pdb $targetDir -Force }

Copy-Item (Join-Path $root 'src\LevelMover\LevelMover.addin') $addinsDir -Force

Write-Host ''
Write-Host 'Установлено:' -ForegroundColor Green
Write-Host "  манифест : $(Join-Path $addinsDir 'LevelMover.addin')"
Write-Host "  сборка   : $(Join-Path $targetDir 'LevelMover.dll')"
Write-Host ''
Write-Host "Запустите Revit $RevitVersion — на ленте появится вкладка «Перенос элементов»."
