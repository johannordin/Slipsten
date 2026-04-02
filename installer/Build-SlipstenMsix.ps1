param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$Version = "1.0.0.0",
    [string]$AppInstallerUri = "",
    [int]$HoursBetweenUpdateChecks = 24,
    [switch]$TrustCertificate
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\Slipsten\Slipsten.csproj"
$artifactsRoot = Join-Path $PSScriptRoot "artifacts\msix"
$packageDir = Join-Path $artifactsRoot "package"
$certDir = Join-Path $artifactsRoot "cert"
$certificateSubject = "CN=Slipsten"
$certificateFriendlyName = "Slipsten MSIX Dev Certificate"
$certificatePassword = "Slipsten-Dev-Certificate"
$pfxPath = Join-Path $certDir "Slipsten-Dev.pfx"
$cerPath = Join-Path $certDir "Slipsten-Dev.cer"

New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
New-Item -ItemType Directory -Force -Path $certDir | Out-Null

if ([string]::IsNullOrWhiteSpace($AppInstallerUri))
{
    $resolvedPackageDir = (Resolve-Path $packageDir).Path
    $normalizedPackageDir = $resolvedPackageDir.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $packageDirUri = [System.Uri]$normalizedPackageDir
    $AppInstallerUri = $packageDirUri.AbsoluteUri.TrimEnd('/')
}

$certPasswordSecureString = ConvertTo-SecureString -String $certificatePassword -AsPlainText -Force
$existingCertificates = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.FriendlyName -eq $certificateFriendlyName }

foreach ($existingCertificate in $existingCertificates)
{
    Remove-Item -Path $existingCertificate.PSPath -DeleteKey
}

$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $certificateSubject `
    -FriendlyName $certificateFriendlyName `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5)

Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $certPasswordSecureString | Out-Null
Export-Certificate -Cert $certificate -FilePath $cerPath -Force | Out-Null

if ($TrustCertificate)
{
    $alreadyTrusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople |
        Where-Object Thumbprint -eq $certificate.Thumbprint |
        Select-Object -First 1

    if ($null -eq $alreadyTrusted)
    {
        Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" | Out-Null
    }
}

dotnet publish $projectPath `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:AppxPackageDir="$packageDir\" `
    -p:AppxPackageSigningEnabled=true `
    -p:GenerateAppxPackageOnBuild=true `
    -p:GenerateAppInstallerFile=true `
    -p:AppInstallerUri=$AppInstallerUri `
    -p:HoursBetweenUpdateChecks=$HoursBetweenUpdateChecks `
    -p:AppxPackageVersion=$Version `
    -p:PackageCertificateThumbprint=$($certificate.Thumbprint) `
    -p:PackageCertificateKeyFile="" `
    -p:PackageCertificatePassword=""
