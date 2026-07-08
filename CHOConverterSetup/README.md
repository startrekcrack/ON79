# CHOConverterSetup - WiX-basierter Installer

## Übersicht

Dies ist ein **WiX v3.11-basierter Installer** für die CHOConverter-Suite. Das Setup installiert alle drei CHOConverter-Programme (CLI, GUI, WPF) und erstellt Startmenü- und Desktop-Verknüpfungen für die GUI-Anwendungen.

## Installierte Programme

### 1. **CHOConverter CLI** (Kommandozeile)
- **Datei:** `CHOConverter CLI.exe`
- **Beschreibung:** CLI - Standalone DOCX / PDF to ChordPro converter using OpenXML
- **Verwendung:** Kommandozeilen-Tool für Batch-Konvertierungen
- **Keine Shortcuts** (CLI-Tool)

### 2. **CHOConverter GUI** (WinForms)
- **Datei:** `CHOConverter GUI.exe`
- **Beschreibung:** GUI - Standalone DOCX / PDF to ChordPro converter using OpenXML
- **Shortcuts:** Startmenü + Desktop

### 3. **CHOConverter WPF** (WPF GUI)
- **Datei:** `CHOConverter WPF.exe`
- **Beschreibung:** WPF GUI - Standalone DOCX / PDF to ChordPro converter using OpenXML
- **Shortcuts:** Startmenü + Desktop

## Verzeichnisstruktur

```
CHOConverterSetup/
├── .gitignore
├── build-wix.ps1                    # PowerShell Build-Skript (Main Build Entry Point)
├── BuildEvents.CHOConverterSetup.txt # Dokumentation der Build Events
├── CHOConverterSetup.wixproj        # MSBuild WiX Projekt-Datei
├── CHOConverterSetup.wxs            # WiX Source XML Datei (Installer-Definition)
├── Heat.RemovePdb.xsl               # XSL-Transformation für Heat (PDB-Filter)
├── PostBuild.CHOConverterSetup.ps1  # Post-Build Event Script (Signierung, CAB)
├── Remove-PdbFromHarvest.ps1        # Hilfsskript zur PDB-Entfernung
├── Resources/                        # WiX UI Ressourcen (Logos, EULAs, etc.)
│   ├── BackgroundLogo.bmp
│   ├── TopBanner.bmp
│   ├── eula.rtf
│   └── *.wxs  (UI-Dialoge und Localization)
└── obj/                             # Build-Zwischendateien (nicht commiten!)
```

## Wichtige Variablen

| Variable | Wert |
|----------|------|
| **ProductVersion** | 1.0.1 |
| **ProductName** | CHOConverter |
| **Manufacturer** | Oliver Niehus |
| **UpgradeCode** | {F5A3B2C1-8D7E-4A6F-9B5C-2E1D3F4A5B6C} |
| **Install Location** | Program Files\Oliver Niehus\CHOConverter |
| **Description** | DOCX / PDF to ChordPro converter - CLI, GUI and WPF applications |
| **SigningCertSha1** | ce53152c1f26905c8486015021eb7c608f35a759 |
| **TimeStampUrl** | http://timestamp.digicert.com |

## Build-Prozess

### 1. Integration in Visual Studio Solution

Das Projekt ist bereits in die `SearchSong.sln` integriert.

### 2. Direkter Build via PowerShell

```powershell
# Aus dem CHOConverterSetup-Verzeichnis:
.\build-wix.ps1 -Configuration Release
```

### 3. MSBuild (Visual Studio)

```cmd
msbuild CHOConverterSetup.wixproj /p:Configuration=Release /p:Platform=x64
```

## Build-Ablauf (Build Targets)

Das Projekt führt folgende Schritte automatisch aus:

### 1. **BuildCHOConverter** (BeforeTargets="CombineCHOConverterOutputs")
Baut alle drei CHOConverter-Projekte:
- CHOConverterCLI.csproj
- CHOConverterGUI.csproj
- CHOConverterWPF.csproj

### 2. **CombineCHOConverterOutputs** (BeforeTargets="GenerateHarvest")
Kombiniert alle drei Build-Outputs in einen gemeinsamen Ordner:
- Quelle: `CHOConverter\[CLI|GUI|WPF]\bin\Release\net10.0-windows\`
- Ziel: `CHOConverter\bin\Combined\Release\net10.0-windows\`

Dieser Schritt stellt sicher, dass:
- Alle drei Programme im gleichen Ordner installiert werden
- Gemeinsame Dependencies (wie SongConverters.dll) nur einmal vorhanden sind
- Heat.exe alle Dateien in einem Durchlauf harvesten kann

### 3. **GenerateHarvest** (BeforeTargets="Compile")
- Heat.exe durchsucht den kombinierten Output-Ordner
- Generiert automatische WiX-Datei-Definitionen: `HarvestedFiles.wxs`
- ComponentGroup: `CHOConverterFiles`
- Entfernt PDB-Dateien aus dem Installer

### 4. **PreLinkCleanup** (BeforeTargets="Link")
- Löscht die alte MSI-Datei, falls sie gesperrt ist
- Retry-Logik für Explorer-Locks (bis zu 20 Versuche)

### 5. **Compile** (Standard WiX)
- Candle.exe kompiliert die WiX-Quellen
- Erstellt .wixobj-Dateien

### 6. **Link** (Standard WiX)
- Light.exe linkt die Objektdateien zu einer MSI
- Integriert WixUIExtension für den Installations-Dialog

### 7. **PostBuildEvent** (OnBuildSuccess, nur Release)
- Ruft `PostBuild.CHOConverterSetup.ps1` auf
- Signiert die MSI-Datei mit dem Zertifikat
- Erstellt eine CAB-Datei und signiert auch diese
- Kopiert Version-Dateien neben die MSI (falls vorhanden)

## Startmenü-Organisation

Das Setup erstellt einen eigenen Ordner im Startmenü:

```
Startmenü/
└── CHOConverter/
    ├── CHOConverter GUI
    └── CHOConverter WPF
```

**Hinweis:** CLI hat keinen Shortcut, da es ein Kommandozeilen-Tool ist.

## Desktop-Shortcuts

Optional werden Desktop-Shortcuts erstellt für:
- CHOConverter GUI
- CHOConverter WPF

## Post-Build Events

Der Post-Build Event führt folgende Operationen aus (nur im Release-Modus):

### 1. **MSI Signierung**
- Tool: `signtool.exe`
- Algorithmus: SHA256
- Timestamp: http://timestamp.digicert.com

### 2. **CAB-Erstellung**
- Tool: `cabarc.exe`
- Datei: `CHOConverterSetup.cab`

### 3. **CAB Signierung**
- Signiert mit dem gleichen Zertifikat wie die MSI

### 4. **Versions-Dateien kopieren**
- Kopiert `choconvertersetupversion.xml` und `.json` neben die MSI (falls vorhanden)

## Besonderheiten

### x64-Only Build
Das Projekt ist konfiguriert als **x64-only**. Es wird ein Fehler angezeigt, wenn versucht wird, mit anderen Plattformen zu bauen.

### Kombinierter Output
Alle drei Programme werden in einen gemeinsamen Ordner kombiniert, um:
- Diskspace zu sparen (gemeinsame DLLs)
- Den Installer zu vereinfachen
- Alle Programme am gleichen Ort zu installieren

### Automatische Datei-Ernte
WiX Heat wird verwendet, um die Dateien automatisch zu harvesten. Dies vermeidet, dass Dateien manuell in die WXS-Datei hinzugefügt werden müssen.

### PDB-Ausschluss
PDB-Dateien (Debug-Information) werden automatisch aus dem Installer entfernt via:
- `Heat.RemovePdb.xsl` (XSL Transformation)
- `Remove-PdbFromHarvest.ps1` (PowerShell Nachbearbeitung)

### License Dialog ausgeblendet
Das Setup zeigt keinen License-Dialog an (`SUPPRESSLICENSE=1`).

## Konfigurierbare Eigenschaften

In Visual Studio können folgende Eigenschaften in den Projekt-Properties konfiguriert werden:

### Im Tab "Build"

**User Macro Definitions (DefineConstants):**
```
ProductVersion=1.0.1;CHOConverterOutputDir=..\CHOConverter\bin\Combined\$(Configuration)\net10.0-windows;ProductIconPath=..\CHOConverter.ico
```

**Oder einzeln:**
- `ProductVersion` - Versions-String für die MSI (z.B. 1.0.2)
- `CHOConverterOutputDir` - Alternative App Output Directory
- `ProductIconPath` - Pfad zum Produkt-Icon

### Signierung (Conditional)

Im Falle von fehlenden Signing-Tools:
- Ist SignToolExe nicht vorhanden, wird die Signierung übersprungen
- Ist CabarcExe nicht vorhanden, wird die CAB-Erstellung übersprungen

## Troubleshooting

### WiX Toolset nicht installiert
**Error:** `WiX v3 MSBuild targets not found at '...'`

**Lösung:** WiX Toolset v3.11 oder höher installieren:
- https://wixtoolset.org/
- Visual Studio Extension: "WiX Toolset v3 for Visual Studio"

### CHOConverter-Projekte nicht gefunden
**Error:** `CHOConverter output directory not found: ...`

**Lösung:** Sicherstellen, dass alle drei CHOConverter-Projekte im richtigen Verzeichnis existieren und gebaut wurden.

### MSI ist gesperrt
**Error:** `LGHT0128: ... in use`

**Lösung:** Das Projekt versucht automatisch, die alte MSI zu löschen. Falls immer noch gesperrt:
- Explorer schließen
- Antivirus-Software temporär deaktivieren
- Windows Explorer Dateivorkschau-Handler überprüfen

### Signierung fehlgeschlagen
**Error:** `signtool ... failed with exit code ...`

**Lösungen:**
- SignToolExe-Pfad überprüfen
- TimeStampUrl erreichbar?
- Zertifikat vorhanden (SHA1: ce53152c1f26905c8486015021eb7c608f35a759)?

### Kombinierter Output fehlt
**Error:** `Expected app output directory not found: ...CHOConverter\bin\Combined\...`

**Lösung:** 
- Der CombineCHOConverterOutputs-Target erstellt diesen Ordner automatisch
- Stelle sicher, dass alle drei Projekte erfolgreich gebaut wurden
- Prüfe die Build-Logs auf Fehler

## Verwendung nach Installation

Nach der Installation befinden sich alle drei Programme unter:
```
C:\Program Files\Oliver Niehus\CHOConverter\
```

### GUI-Anwendungen starten
- **Über Startmenü:** Startmenü → CHOConverter → [GUI oder WPF wählen]
- **Über Desktop:** Doppelklick auf Desktop-Shortcut
- **Direkt:** `"C:\Program Files\Oliver Niehus\CHOConverter\CHOConverter GUI.exe"`

### CLI verwenden
```cmd
cd "C:\Program Files\Oliver Niehus\CHOConverter"
"CHOConverter CLI.exe" [Argumente]
```

## Deinstallation

- **Windows 10/11:** Einstellungen → Apps → CHOConverter → Deinstallieren
- **Systemsteuerung:** Programme und Features → CHOConverter → Deinstallieren

Bei der Deinstallation werden automatisch entfernt:
- Alle installierten Dateien
- Startmenü-Ordner und Shortcuts
- Desktop-Shortcuts
- Registry-Einträge

## Lizenzinformationen

**Copyright © 2021-2099 Oliver Niehus**

Der Installer installiert die CHOConverter-Suite, welche DOCX- und PDF-Dateien in ChordPro-Format konvertiert.

---

**Erstellt:** 24.02.2026  
**WiX Version:** 3.11  
**MSBuild Version:** 4.0  
**Setup Version:** 1.0.1
