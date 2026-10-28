Param (
    [string]$FilePath,
    [string]$FullVersion
)

# Split the full version string into parts
$versionParts = $FullVersion -split '\.'

# Extract only the major, minor, and patch numbers
if ($versionParts.Length -ge 3) {
    $MajorMinorPatchVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2])"
} else {
    throw "Invalid version format: $FullVersion. Expected a four-part version string."
}

# Read the contents of the JSON file
$jsonContent = Get-Content -Path $FilePath -Raw

# Use regex to replace the version
$jsonContent = [System.Text.RegularExpressions.Regex]::Replace($jsonContent, '"version":\s*"[^\"]+"', '"version": "' + $MajorMinorPatchVersion + '"')

# Write the modified content back to the file
Set-Content -Path $FilePath -Value $jsonContent
