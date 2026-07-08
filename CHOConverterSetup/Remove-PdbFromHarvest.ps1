[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) {
  throw "Harvest file not found: $Path"
}

[xml]$doc = Get-Content -Raw -Encoding UTF8 $Path
$ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
$ns.AddNamespace('w', 'http://schemas.microsoft.com/wix/2006/wi')

$xpathPdbFiles = "//w:File[contains(translate(@Source,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), '.pdb')]"
$xpathPdbComponents = "//w:Component[w:File[contains(translate(@Source,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), '.pdb')]]"

# Remove components that only exist for PDB files.
$components = @($doc.SelectNodes($xpathPdbComponents, $ns))
foreach ($component in $components) {
  $null = $component.ParentNode.RemoveChild($component)
}

# Remove any leftover File nodes referencing PDB (defensive).
$files = @($doc.SelectNodes($xpathPdbFiles, $ns))
foreach ($file in $files) {
  $null = $file.ParentNode.RemoveChild($file)
}

$doc.Save($Path)
