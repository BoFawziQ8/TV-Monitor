param([string]$Package)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package).Path)
try {
    function ReadEntry([string]$name) {
        $entry=$zip.GetEntry($name)
        if (!$entry) { throw "Missing package entry $name" }
        $reader=New-Object IO.StreamReader($entry.Open())
        try { $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    [xml]$blockmap=ReadEntry 'AppxBlockMap.xml'
    [xml]$manifest=ReadEntry 'AppxManifest.xml'
    $files=0; $blocks=0
    foreach ($file in $blockmap.BlockMap.File) {
        $entry=$zip.GetEntry((($file.Name.Replace('\','/').Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'))
        if (!$entry -or $entry.Length -ne [long]$file.Size) { throw "Missing or wrong-sized file: $($file.Name)" }
        $stream=$entry.Open()
        try {
            foreach ($block in $file.Block) {
                $buffer=New-Object byte[] 65536
                $used=0
                while ($used -lt $buffer.Length) {
                    $read=$stream.Read($buffer,$used,$buffer.Length-$used)
                    if ($read -eq 0) { break }
                    $used+=$read
                }
                $sha=[Security.Cryptography.SHA256]::Create()
                try { $actual=[Convert]::ToBase64String($sha.ComputeHash($buffer,0,$used)) }
                finally { $sha.Dispose() }
                if ($actual -ne $block.Hash) { throw "Block hash mismatch: $($file.Name)" }
                $blocks++
            }
            if ($stream.ReadByte() -ne -1) { throw "Unverified trailing data: $($file.Name)" }
        } finally { $stream.Dispose() }
        $files++
    }
    foreach ($required in @('coreclr.dll','System.Windows.Forms.dll','TV Monitor.exe','TV Monitor.dll','scrcpy/adb.exe','scrcpy/scrcpy.exe','scrcpy/scrcpy-server','tv-popup.apk')) {
        if (!$zip.GetEntry((($required.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'))) { throw "Missing dependency: $required" }
    }
    if ((ReadEntry 'KLightTV/PreviousSetup.json').Trim() -ne '{}') { throw 'Personal recovery history leaked.' }
    if ($zip.GetEntry('AppxSignature.p7x')) { throw 'Unexpected signature in unsigned preview.' }
    [pscustomobject]@{
        Status='PASS'; Files=$files; Blocks=$blocks
        Identity=$manifest.Package.Identity.Name
        Publisher=$manifest.Package.Identity.Publisher
        Version=$manifest.Package.Identity.Version
        Architecture=$manifest.Package.Identity.ProcessorArchitecture
        SHA256=(Get-FileHash -LiteralPath $Package).Hash
        Note='Unsigned preview identity; not associated with a reserved Store product. Installation and WACK not performed.'
    } | ConvertTo-Json
} finally { $zip.Dispose() }

