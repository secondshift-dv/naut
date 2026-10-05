![Naut-Wortmarke](src/Neuterradise.App/Assets/Brand/wordmark-lockup.png)

[English](README.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Français](README.fr.md) · [Bahasa Indonesia](README.id.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [简体中文](README.zh-Hans.md)

# Naut

**A Navigator for Your Things Worth Keeping**

Naut ist ein lokaler Medienmanager für Windows. Profile bündeln Bilder, Videos und interaktive 3D-Figuren in einem portablen Vault, während die Sammlung unter der Kontrolle des Benutzers bleibt.

Das private Repository **naut-dv** ist die technische Autorität. Dieses kuratierte Repository **naut** ist die öffentliche Quellcode-, Beitrags- und offizielle Release-Oberfläche. Naut-eigener Quellcode ist unter PolyForm Shield 1.0.0 **Source Available**; Drittanbieter-Komponenten behalten ihre jeweiligen Lizenzen.

## Build

Erforderlich sind Windows x64 und .NET SDK **10.0.401**.

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

Das portable Paket startet mit `naut.exe`; `runtime/` und `release-manifest.json` müssen daneben bleiben.

## Offizielle Downloads

Offizielle Naut-Binärdateien und signierte Update-Metadaten werden ausschließlich über die offizielle **secondshift-dv/naut GitHub Releases**-Seite verteilt. Verwende keine Binärdateien, die auf Discord, Filesharing-Diensten oder Drittanbieter-Mirrors erneut hochgeladen wurden.

## Richtlinien

Siehe [LICENSE](LICENSE), [Lizenzumfang](docs/licensing.md), [Markenrichtlinie](BRAND-POLICY.md), [Beiträge](CONTRIBUTING.md), [Sicherheit](SECURITY.md) und [Drittanbieterhinweise](THIRD-PARTY-NOTICES.txt).

Offizielle App-Sprachen: Englisch, Deutsch, Spanisch, Französisch, Indonesisch, Japanisch, Koreanisch und vereinfachtes Chinesisch.
