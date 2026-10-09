<#
  Builds LabelScope-Setup.exe. Double-click build-installer.cmd (it runs this script).
  Steps: publish the app, sign it, build the installer, sign the installer.
  Signing uses Azure Artifact Signing (account p4software) through your Azure login (az login).
  If signing is not possible the installer is still built and clearly marked UNSIGNED.
#>
$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'publish\win-x64'
$iscc    = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
$out     = Join-Path $root 'publish\installer\LabelScope-Setup.exe'

function SignFile($file) {
  # Returns $true only when the file really carries a signature afterwards.
  $signArgs = @(
    'code', 'artifact-signing', $file,
    '--artifact-signing-endpoint', 'https://eus.codesigning.azure.net/',
    '--artifact-signing-account', 'p4software',
    '--artifact-signing-certificate-profile', 'p4software',
    '--azure-credential-type', 'azure-cli',
    '--description', 'LabelScope',
    '-v', 'warning')
  & sign @signArgs | Out-Host
  # Trust the file, not the exit code.
  try { $null = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($file); return $true } catch { return $false }
}

try {
  if (-not (Test-Path $iscc)) { throw "Inno Setup is not installed. Install it with: winget install JRSoftware.InnoSetup --scope user" }

  Write-Host '1/4 Publishing the app...'
  & dotnet publish (Join-Path $root 'src\LabelScope.App') -c Release -r win-x64 --self-contained true -o $publish -p:RestoreSources=https://api.nuget.org/v3/index.json
  if ($LASTEXITCODE -ne 0) { throw 'The app could not be published. Read the messages above.' }

  Write-Host '2/4 Signing the program...'
  $signed = SignFile (Join-Path $publish 'LabelScope.exe')

  Write-Host '3/4 Building the installer...'
  & $iscc (Join-Path $PSScriptRoot 'LabelScope.iss')
  if ($LASTEXITCODE -ne 0) { throw 'The installer could not be built. Read the messages above.' }

  Write-Host '4/4 Signing the installer...'
  $signed = (SignFile $out) -and $signed

  Write-Host ''
  if ($signed) { Write-Host "Done. Signed installer: $out" -ForegroundColor Green }
  else { Write-Host "Done, but UNSIGNED (Windows will warn users). Run 'az login' and try again to sign. File: $out" -ForegroundColor Yellow }
} catch {
  Write-Host "Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
}
if (-not $env:LABELSCOPE_NOPAUSE) { Read-Host 'Press Enter to close this window' }
