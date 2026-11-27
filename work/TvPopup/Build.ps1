$ErrorActionPreference = 'Stop'
$buildRoot = 'C:\Users\bo-fa\Documents\Codex\2026-10-21\https-github-com-linkq8-klighttv-https\work\android-build'
$env:JAVA_HOME = (Get-ChildItem "$buildRoot/jdk" -Directory | Select-Object -First 1).FullName
$sdk = "$buildRoot/sdk"
$tools = "$sdk/build-tools/34.0.0"
$android = "$sdk/platforms/android-34/android.jar"
Push-Location $PSScriptRoot
try {
    function Run($exe, $arguments) {
        & $exe @arguments
        if ($LASTEXITCODE -ne 0) { throw "Android tool failed: $exe ($LASTEXITCODE)" }
    }
    Run "$env:JAVA_HOME/bin/javac.exe" @('--release','8','-classpath',$android,'-d','classes',
        'PopupActivity.java','TalkService.java','CaptureActivity.java','CaptureService.java')
    Run "$tools/d8.bat" (@('--release','--min-api','21','--lib',$android,'--output','dex') +
        @(Get-ChildItem -LiteralPath 'classes/local/tvmonitor/popup' -Filter '*.class' | ForEach-Object FullName))
    Run "$tools/aapt2.exe" @('link','-I',$android,'--manifest','AndroidManifest.xml','-o','unsigned.apk')
    Run "$env:JAVA_HOME/bin/jar.exe" @('uf','unsigned.apk','-C','dex','classes.dex')
    Run "$tools/zipalign.exe" @('-f','4','unsigned.apk','aligned.apk')
    if (!(Test-Path popup-signing.dpapi)) {
        if (Test-Path popup-signing.p12) { throw 'Signing key exists without its password' }
        $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $env:TV_POPUP_SIGNING_PASSWORD = $password
        Run "$env:JAVA_HOME/bin/keytool.exe" @('-genkeypair','-alias','tvpopup','-dname','CN=TV Monitor Popup','-keyalg','RSA','-keysize','3072','-validity','10000','-storetype','PKCS12','-keystore','popup-signing.p12','-storepass:env','TV_POPUP_SIGNING_PASSWORD','-noprompt')
        ConvertTo-SecureString $password -AsPlainText -Force | ConvertFrom-SecureString | Set-Content popup-signing.dpapi
    } else {
        $protected = Get-Content popup-signing.dpapi | ConvertTo-SecureString
        $env:TV_POPUP_SIGNING_PASSWORD = [System.Net.NetworkCredential]::new('', $protected).Password
    }
    Run "$tools/apksigner.bat" @('sign','--ks','popup-signing.p12','--ks-pass','env:TV_POPUP_SIGNING_PASSWORD','--out','tv-popup.apk','aligned.apk')
    Run "$tools/apksigner.bat" @('verify','--verbose','tv-popup.apk')
} finally {
    Remove-Item Env:\TV_POPUP_SIGNING_PASSWORD -ErrorAction SilentlyContinue
    Pop-Location
}
