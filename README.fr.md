![Logo Naut](src/Neuterradise.App/Assets/Brand/wordmark-lockup.png)

[English](README.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Français](README.fr.md) · [Bahasa Indonesia](README.id.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [简体中文](README.zh-Hans.md)

# Naut

**A Navigator for Your Things Worth Keeping**

Naut est un gestionnaire de collection multimédia local-first pour Windows. Les profils regroupent images, vidéos et Figures 3D interactives dans un Vault portable, tout en laissant la collection sous le contrôle de l'utilisateur.

Le dépôt privé **naut-dv** est l'autorité d'ingénierie. Ce dépôt organisé **naut** constitue la surface publique du code source, des contributions et des versions officielles. Le code appartenant à Naut est **Source Available** sous PolyForm Shield 1.0.0 ; les composants tiers conservent leurs propres licences.

## Compilation

Windows x64 et .NET SDK **10.0.401** sont requis.

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

Le paquet portable démarre avec `naut.exe` ; conservez `runtime/` et `release-manifest.json` à ses côtés.

## Téléchargements officiels

Les binaires officiels de Naut et les métadonnées de mise à jour signées sont distribués uniquement via la page officielle **GitHub Releases de secondshift-dv/naut**. N'exécutez pas de binaires réhébergés sur Discord, des services de partage de fichiers ou des miroirs tiers.

## Politiques du projet

Voir [LICENSE](LICENSE), [périmètre des licences](docs/licensing.md), [politique de marque](BRAND-POLICY.md), [contributions](CONTRIBUTING.md), [sécurité](SECURITY.md) et [mentions tierces](THIRD-PARTY-NOTICES.txt).

Langues officielles de l'application : anglais, allemand, espagnol, français, indonésien, japonais, coréen et chinois simplifié.
