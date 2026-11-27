param(
    [string]$IdentityName = 'BoFawzi.TVMonitor.Preview',
    [string]$Publisher = 'CN=BoFawzi',
    [string]$PublisherDisplayName = 'BoFawzi',
    [string]$DisplayName = 'TV Monitor',
    [switch]$StoreIdentity
)
$ErrorActionPreference = 'Stop'
$version = '1.14.0.0'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $PSScriptRoot 'msix-stage'
$out = Join-Path $root 'outputs/Store submission'
$payload = Join-Path $PSScriptRoot 'store-publish'
if (!(Test-Path (Join-Path $payload 'coreclr.dll'))) { throw 'Publish a self-contained win-x64 app first.' }
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage,$out,(Join-Path $stage 'Assets') -Force | Out-Null
Copy-Item -Path (Join-Path $payload '*') -Destination $stage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'outputs/TV Monitor (1.14.0)/scrcpy') -Destination $stage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'outputs/TV Monitor (1.14.0)/tv-popup.apk'),(Join-Path $root 'outputs/TV Monitor (1.14.0)/NAudio-LICENSE.txt') -Destination $stage -Force
# Personal recovery history is never distributed through the Store.
$history = Join-Path $stage 'KLightTV/PreviousSetup.json'
if (Test-Path -LiteralPath $history) { Set-Content -LiteralPath $history -Value '{}' -Encoding utf8 }
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'tv-icon-preview.png'))
foreach ($asset in @(@('Square44x44Logo.png',44),@('Square150x150Logo.png',150),@('StoreLogo.png',50),@('StoreIcon.png',300))) {
    $size=[int]$asset[1]
    $bitmap=New-Object System.Drawing.Bitmap($size,$size)
    $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($source,0,0,$size,$size)
    $bitmap.Save((Join-Path $stage ('Assets/'+$asset[0])),[System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}
$source.Dispose()
Copy-Item -LiteralPath (Join-Path $stage 'Assets/StoreIcon.png') -Destination $out -Force
function XmlText([string]$text) { [System.Security.SecurityElement]::Escape($text) }
$name=XmlText $IdentityName; $pub=XmlText $Publisher; $pubName=XmlText $PublisherDisplayName; $display=XmlText $DisplayName
$manifest=@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap rescap">
 <Identity Name="$name" Publisher="$pub" Version="$version" ProcessorArchitecture="x64" />
 <Properties><DisplayName>$display</DisplayName><PublisherDisplayName>$pubName</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
 <Resources><Resource Language="en-US"/><Resource Language="ar"/></Resources>
 <Applications><Application Id="TVMonitor" Executable="TV Monitor.exe" EntryPoint="Windows.FullTrustApplication">
 <uap:VisualElements DisplayName="$display" Description="Watch and control compatible TVs on your local network." BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
 </Application></Applications>
 <Capabilities><Capability Name="internetClient"/><Capability Name="privateNetworkClientServer"/><rescap:Capability Name="runFullTrust"/><DeviceCapability Name="microphone"/></Capabilities>
</Package>
"@
Set-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding utf8
$sdk='C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/makeappx.exe'
$suffix=if ($StoreIdentity) { 'Store' } else { 'Preview-UNSIGNED' }
$package=Join-Path $out "TVMonitor-$version-x64-$suffix.msix"
& $sdk pack /d $stage /p $package /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $package -Algorithm SHA256 | Format-List
Write-Output $package




