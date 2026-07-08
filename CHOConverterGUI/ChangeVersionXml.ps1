
# New Version Parameter
param([String] $Version)
$newVersion = $Version

# Path to xml-File
$xmlFilePath = ".\choconvertersetupversion.xml"
$jsonFilePath = ".\choconvertersetupversion.json"

# Load XML-File
$xml = [xml](Get-Content $xmlFilePath)

# Change Version
$currentVersion = $xml.TitlesTable.SongItemRow.Title -replace "Version: "
$xml.TitlesTable.SongItemRow.Title = "Version: $newVersion"

# Save XML-File
$xml.Save($xmlFilePath)


# Funktion zum Formatieren von JSON
function Format-Json {
    param (
        [Parameter(Mandatory, ValueFromPipeline)]
        [String] $json
    )

    $indent = 0
    ($json -split "`n" | ForEach-Object {
        if ($_ -match '[\}\]]\s*,?\s*$') {
            # Diese Zeile endet mit ] oder }, reduziere den Einzug
            $indent--
        }
        $line = (' ' * $indent) + $_.TrimStart()
        if ($_ -match '[\{\[]\s*$') {
            # Diese Zeile beginnt mit { oder [, erhöhe den Einzug
            $indent++
        }
        $line
    }) -join "`n"
}


# Load Json-File
$json = Get-Content $jsonFilePath | ConvertFrom-Json

# Aktualisieren der Versionsnummer im JSON-Dokument
$json.TitlesTable.SongItemRow.Title = "Version: $newVersion"

# Speichern der aktualisierten JSON-Datei
$jsonFormatted = $json | ConvertTo-Json
$jsonFormatted | Format-Json | Set-Content $jsonFilePath -Encoding utf8

# Ausgabe der neuen Versionsnummer
Write-Output "New Version written: $newVersion"
