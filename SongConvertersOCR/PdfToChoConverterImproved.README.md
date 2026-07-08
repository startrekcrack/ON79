# Verbesserter PDF-zu-ChordPro-Converter

## Überblick

Der `PdfToChoConverterImproved` ist eine verbesserte Version des OCR-basierten PDF-zu-ChordPro-Converters mit folgenden Verbesserungen:

## Features

### 1. **PDF lesen & sichten**
- Erkennung von Melodiezeilen, Akkordsymbolen, Textblöcken
- Wiederholungszeichen (Fine, D.C. al Fine, D.S. al Fine, Coda)
- Strophennummern und Refrain-Markierungen

### 2. **Text rekonstruieren & bereinigen**
- **Silbentrennung glätten**: "Weih - nachts" → "Weihnachts"
- **Metadaten extrahieren**: Text, Melodie, Satz, Copyright aus Fußnoten
- **OCR-Artefakte entfernen**: Doppelte Leerzeichen, fehlerhafte Zeichen

### 3. **ChordPro-Struktur**
- Automatische Generierung von `{title}`, `{key}`, `{subtitle}`
- Metadaten als `{meta: text=...}`, `{meta: melodie=...}`
- Strukturmarkierungen als `{comment: Vers 1}`, `{comment: Chorus}`, `{comment: Fine}`
- Wiederholungen: `{comment: D.C. al Fine}`

### 4. **Deutsche Notation** ⭐
- **B → H**: Englisches B wird zu deutschem H (z.B. `B7` → `H7`, `Bm` → `Hm`)
- **Bb → B**: Englisches Bb wird zu deutschem B (z.B. `Bb` → `B`, `Bbmaj7` → `Bmaj7`)
- **Bass-Akkorde**: `Bm/F#` → `Hm/F#`
- Konfigurierbar über `UseGermanNotation` (Standard: true)

### 5. **Akkorde positionsgenau inline**
- Akkorde werden vor der korrekten Silbe platziert
- Basiert auf X-Koordinaten aus OCR-Layout
- Spielpraxis-orientiert, ohne "Wortzerhacker"

### 6. **Musikalische Plausibilität**
- Akkordfolgen werden auf Konsistenz geprüft
- OCR-Fehler werden korrigiert (z.B. einzelnes "E" → "Em" im Moll-Kontext)
- Harmonische Sinnhaftigkeit wird gewährleistet

## Verwendung

Der verbesserte Converter wird automatisch verwendet, wenn Layout-OCR-Engines verfügbar sind:

```csharp
var renderer = new LoggingPdfRenderer(new DocnetPdfRenderer());
var (chordOcr, lyricOcr) = BuildLayoutOcrEngines();

var converter = new PdfToChoConverterImproved(renderer, chordOcr, lyricOcr);
var chordPro = converter.Convert("meine-noten.pdf");
```

## Beispiel

### Eingabe (PDF)
```
Die Weihnachtsfreude
Text: ERF, Wetzlar | Melodie: Family Films, USA

Refrain:
D      Em        A
Die Weih - nachts - freu - de
G         Bm/F#    A7
bringt uns al - len Freu - de

Fine
D.C. al Fine
```

### Ausgabe (ChordPro)
```chordpro
{title: Die Weihnachtsfreude}
{meta: text=ERF, Wetzlar}
{meta: melodie=Family Films, USA}
{key: D}
{meta: source=PDF}
{comment: OCR-basierte Übertragung; Silbentrennungen geglättet, Akkorde positionsgenau (Deutsche Notation: B→H, Bb→B).}

{comment: Chorus}
[D]Die [Em]Weihnachtsfreude
[G]bringt uns [Hm/F#]allen [A7]Freude

{comment: Fine}
{comment: D.C. al Fine}
```

## Konfiguration

```csharp
var options = new PdfToChoConverterImproved.Options(
    TabWidth: 12,                  // Tab-Breite für Ausgabe
    MetaSource: "PDF",             // Metadaten-Quelle
    OcrDpi: 300,                   // OCR-Auflösung
    MaxChordToLyricGapPx: 180,     // Max. Abstand Akkord-Text
    UseGermanNotation: true        // Deutsche Notation aktivieren
);

var chordPro = converter.Convert("datei.pdf", options);
```

## Unterschiede zur alten Version

| Feature | PdfToChoConverterOcrDualLayout | PdfToChoConverterImproved |
|---------|-------------------------------|---------------------------|
| Deutsche Notation | ❌ | ✅ B→H, Bb→B |
| Metadaten-Extraktion | Basis | ✅ Erweitert (Text, Melodie, Satz) |
| Struktur-Erkennung | Vers, Chorus | ✅ + Fine, D.C. al Fine, Coda, Bridge |
| Silbentrennung | Basis | ✅ Verbessert |
| Musikalische Plausibilität | ❌ | ✅ Akkordfolgen-Normalisierung |
| OCR-Artefakt-Bereinigung | Basis | ✅ Erweitert |

## Technische Details

### Workflow

1. **OCR-Dual-Pass**: 
   - Akkord-OCR mit Whitelist und Sparse-Mode
   - Lyrik-OCR mit normalem Mode
   
2. **Layout-Analyse**: 
   - X/Y-Koordinaten für präzise Akkord-Platzierung
   - Erkennung von Notenzeilen vs. Text

3. **Text-Normalisierung**:
   - Regex-basierte Silbentrennung
   - Metadaten-Parsing mit Pattern Matching

4. **Akkord-Konvertierung**:
   - Englische → Deutsche Notation
   - OCR-Fehlerkorrektur (z.B. "Bim" → "Hm")

5. **Qualitätssicherung**:
   - Musikalische Plausibilitätsprüfung
   - Konsistenz-Checks

## Bekannte Limitierungen

- OCR-Qualität hängt stark von der PDF-Auflösung ab
- Handgeschriebene Noten werden nicht optimal erkannt
- Sehr komplexe Akkorde (z.B. C13sus4/G#) können fehlerhaft sein
- Mehrsprachige Texte können zu gemischten Ergebnissen führen

## Performance

- Durchschnittlich 3-5 Sekunden pro Seite bei 300 DPI
- Empfohlene DPI: 300 für beste Qualität
- Bei niedriger DPI (<200) sinkt die Erkennungsgenauigkeit

## Lizenz

Siehe Haupt-Projekt-Lizenz.
