# Linux-Build von DHLabel

Zweck: DHLabel unter Linux mit dem .NET SDK 8 bauen (Ziel `net472`). Fody/Costura
entfallen, daher entsteht ein Ordner mit EXE und DLLs statt einer Einzel-EXE.
Die Quellen kommen unverändert aus `../Program/`. Die Original-`*.csproj`,
`DHLabel.sln` und `packages.config` bleiben unverändert.

## Voraussetzung

.NET SDK 8 per `dotnet-install.sh` nach `~/.dotnet`:

    curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0

## Build

Im Repo-Wurzelverzeichnis:

    ~/.dotnet/dotnet build build-linux/DHLabel.build.csproj -c Release

## Ausgabe

`build-linux/bin/Release/net472/` (Zwischendateien in `build-linux/obj/`).
Beide Ordner sind per `.gitignore` ausgeschlossen.

Der komplette Inhalt dieses Ordners muss zusammen kopiert werden: `DHLabel.exe`,
`DHLabel.exe.config` sowie alle mitgebauten DLLs (PDFsharp, PDFiumSharpV2,
PDFium-Native-DLLs, System.Resources.Extensions usw.).
