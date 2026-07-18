# Copilot Handover

## Repository state

- Generated executables, libraries, symbols, installers, packages, archives, and publish directories are excluded by `.gitignore`.
- Six unused PE-based icon export containers were removed from the three application asset directories.
- The `main` branch and `v1.0.0` tag histories were rewritten to remove those six paths from all reachable commits. Existing clones must re-clone or hard-reset to the rewritten remote history.
- Required source assets such as PNG and ICO files remain tracked.
- `dotnet build CHOConverter.slnx --configuration Release` succeeds.
- The build currently reports three existing MSB3030 warnings because the optional local `pdfium.dll` copy source is absent.