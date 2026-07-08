# 🔄 Update-Plan für ORG/SongConverters

## 📊 Vergleich: ORG vs. Aktuell

### **Was ORG besser macht:**
✅ **Tuple Return für PDF Converter** - `(string cho, string error)` ermöglicht bessere Fehlerbehandlung
✅ **Umfangreichere PdfConverterOptions** - mehr Konfigurationsmöglichkeiten

### **Was das aktuelle Projekt besser macht:**
⭐ **ChordHelper** - Zentrale Akkord-Logik
⭐ **ChordProHelper** - ChordPro-Formatierung
⭐ **ConverterExceptions** - Strukturierte Fehlerbehandlung
⭐ **Erweiterte StringExtensions** - Deutsche Notation, Silbentrennung, etc.
⭐ **PdfToChoConverterImproved** - Bessere OCR mit deutscher Notation
⭐ **Memory Leak Fixes** - GC nach Tesseract Dispose
⭐ **Vollständige XML-Dokumentation** - 0 Warnings

---

## 🎯 Update-Strategie (OHNE Breaking Changes)

### **Phase 1: Neue Helper-Klassen hinzufügen** ✅
1. ✅ ChordHelper.cs kopieren
2. ✅ ChordProHelper.cs kopieren
3. ✅ ConverterExceptions.cs kopieren

### **Phase 2: StringExtensions erweitern** ✅
Neue Methoden hinzufügen (bestehende NICHT ändern):
- `ToGermanNotation()`
- `SmoothHyphenation()`
- `RemoveDiacritics()`
- `NormalizeWhitespace()`
- `EscapeChordPro()`

### **Phase 3: Async/Await Support** ⭐
Interfaces erweitern (non-breaking):
```csharp
public interface IDocxToChoConverter
{
    string Convert(string docxPath, ConverterOptions options = null);
    
    // NEU - Async Support
    Task<string> ConvertAsync(string docxPath, ConverterOptions options = null, 
        CancellationToken cancellationToken = default);
}

public interface IPdfToChoConverter
{
    (string, string) Convert(string pdfPath, PdfConverterOptions options = null);
    
    // NEU - Async Support
    Task<(string, string)> ConvertAsync(string pdfPath, PdfConverterOptions options = null,
        CancellationToken cancellationToken = default);
}
```

### **Phase 4: XML-Dokumentation ergänzen** ✅
Alle fehlenden XML-Comments hinzufügen

### **Phase 5: Memory Leak Fixes** ✅
Tesseract Dispose-Methoden verbessern mit GC.Collect()

### **Phase 6: PdfToChoConverterImproved** ⭐
Als NEUE Klasse hinzufügen (nicht ersetzen):
- Deutsche Notation
- Bessere Struktur-Erkennung
- Optimiertes OCR

---

## ✅ Was NICHT geändert wird (No Breaking Changes):

❌ Tuple Return bei PDF Converter BLEIBT (gut so!)
❌ PdfConverterOptions Signatur BLEIBT
❌ Bestehende public APIs BLEIBEN unverändert
❌ StringExtensions.IsBlankOrWhitespace() BLEIBT

---

## 🚀 Implementation Order:

1. ✅ ChordHelper.cs
2. ✅ ChordProHelper.cs  
3. ✅ ConverterExceptions.cs
4. ✅ StringExtensions erweitern
5. ⭐ Async-Support zu Interfaces
6. ⭐ Async-Implementierungen in allen Convertern
7. ✅ XML-Dokumentation
8. ✅ Memory Leak Fixes
9. ⭐ PdfToChoConverterImproved als neue Option

---

## 📝 Nächste Schritte:

Soll ich mit der Umsetzung beginnen? Die Änderungen sind zu 100% rückwärtskompatibel.
