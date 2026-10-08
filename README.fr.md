<p align="center">
  <img src="src/Neuterradise.App/Assets/Brand/wordmark-lockup.png" alt="Naut" width="460">
</p>

<p align="center"><strong>A Navigator for Your Things Worth Keeping</strong></p>

<p align="center">
  <a href="https://github.com/secondshift-dv/naut/releases/tag/v0.0.3"><strong>Télécharger Naut v0.0.3</strong></a>
  ·
  <a href="https://secondshift-dv.github.io/naut/"><strong>Voir la démo</strong></a>
  ·
  <a href="docs/README.md"><strong>Documentation</strong></a>
</p>

<p align="center">
  <sub><strong>README:</strong>
  <a href="README.md">English</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.zh-Hans.md">简体中文</a>
  </sub>
</p>

<p align="center">
  <img src="docs/assets/readme/naut-showcase.gif" alt="Présentation interactive de Naut" width="682">
</p>

## Votre collection, faite pour être explorée.

Naut est un **gestionnaire de collection multimédia local-first pour Windows**. Il transforme images, vidéos et modèles 3D compatibles en collections centrées sur des Profiles, à reconnaître, présenter et explorer sans céder le contrôle à un service cloud.

## Fonctionnalités phares

<table>
<tr>
<td width="50%" valign="top">
<a href="docs/face-intelligence.md"><img src="docs/assets/readme/feature-face-intelligence.svg" alt="Face Intelligence" width="100%"></a><br>
<strong>Face Intelligence</strong><br>
YuNet détecte les visages applicables, SFace produit des embeddings et Naut peut proposer des candidats de Profile à partir d'échantillons d'identité confirmés. Le traitement reste local et les suggestions restent vérifiables.
</td>
<td width="50%" valign="top">
<a href="docs/figures.md"><img src="docs/assets/readme/feature-3d-figures.svg" alt="Figures 3D interactives" width="100%"></a><br>
<strong>Figures 3D interactives</strong><br>
Les modèles compatibles peuvent devenir des Figures interactives avec rotation, déplacement, zoom, cadrage géré par Naut et limites de texture à l'exécution.
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/import-media.md"><img src="docs/assets/readme/feature-smart-import.svg" alt="Import média intelligent" width="100%"></a><br>
<strong>Import média intelligent</strong><br>
Le même pipeline ne prépare que ce dont chaque type de média a besoin : métadonnées, miniatures, médias vidéo de présentation bornés, analyse faciale si applicable et dérivés Figure pour les modèles pris en charge.
</td>
<td width="50%" valign="top">
<a href="docs/customization.md"><img src="docs/assets/readme/feature-customization.svg" alt="Présentation sans modifier les originaux" width="100%"></a><br>
<strong>Présentation sans modifier les originaux</strong><br>
Personnalisez Home, Gallery, Cards, Profiles, Covers, Banners, Frames, Backdrops, layouts, effets et themes sans modifier les fichiers originaux.
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/languages.md"><img src="docs/assets/readme/feature-multilingual.svg" alt="8 langues d'interface" width="100%"></a><br>
<strong>8 langues d'interface</strong><br>
Naut fournit English, Bahasa Indonesia, 日本語, 한국어, 简体中文, Deutsch, Français et Español. La langue de l'interface ne réécrit ni le Vault ni les métadonnées.
</td>
<td width="50%" valign="top">
<a href="docs/vault.md"><img src="docs/assets/readme/feature-local-vault.svg" alt="Vault local et portable" width="100%"></a><br>
<strong>Vault local et portable</strong><br>
Le paquet applicatif portable et le Vault sont séparés. L'import copie les fichiers dans le Vault et laisse les sources à leur emplacement d'origine.
</td>
</tr>
</table>

## Pourquoi Naut

- **Les Profiles avant les dossiers.** Un Profile peut représenter une personne, un personnage, un projet, un objet, un sujet ou toute identité de collection.
- **Contrôle local par conception.** La collection de référence vit dans le Vault choisi.
- **Préparé pour la navigation.** Naut crée des dérivés de présentation bornés sans modifier les originaux.
- **La 3D est optionnelle.** L'usage normal ne nécessite ni GPU dédié ni Figure.
- **Application portable, collection durable.** Une mise à jour de l'application ne remplace pas le Vault.

## Voir Naut en action

<table>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-home.png" alt="Home — Profiles mis en avant, activité et contexte de collection." width="100%"><br><sub>Home — Profiles mis en avant, activité et contexte de collection.</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-profile.png" alt="Profile — identité, médias, présentation et Figure interactive optionnelle." width="100%"><br><sub>Profile — identité, médias, présentation et Figure interactive optionnelle.</sub></td>
</tr>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-gallery.png" alt="Gallery — navigation, recherche, filtres et présentation des Cards." width="100%"><br><sub>Gallery — navigation, recherche, filtres et présentation des Cards.</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-settings.png" alt="Settings — Theme, langue, contrôles du Vault et options système." width="100%"><br><sub>Settings — Theme, langue, contrôles du Vault et options système.</sub></td>
</tr>
</table>

## Démarrage rapide

1. Téléchargez **Naut v0.0.3** depuis le [GitHub Release](https://github.com/secondshift-dv/naut/releases/tag/v0.0.3) officiel.
2. Extrayez le ZIP complet dans un dossier normal.
3. Lancez `naut.exe`.
4. Choisissez où conserver le Vault.
5. Importez des médias ou créez un Profile puis utilisez **Add media**.

`runtime/` et `release-manifest.json` doivent rester à côté de `naut.exe`. Pour un aperçu navigateur sans toucher aux fichiers locaux ni au Vault : [Interactive Showcase](https://secondshift-dv.github.io/naut/).

## Configuration requise

L'expérience Naut normale est volontairement plus légère que la charge optionnelle des Figures.

| | Minimum | Recommended |
| --- | --- | --- |
| **OS** | Windows 10/11 64-bit | Windows 11 64-bit |
| **CPU** | x86-64, 2 cœurs | x86-64 moderne, 4+ cœurs |
| **RAM** | 8 GB | 16 GB |
| **GPU** | Graphiques intégrés pour l'usage normal | GPU intégré/dédié Direct3D 11 pour des Figures plus fluides |
| **Écran** | 1280 × 720 | 1920 × 1080 ou plus |
| **Espace app/update** | 1.5 GB | 1.5 GB+ sur SSD |

Le stockage du Vault est séparé et dépend de la collection. Voir [Naut v0.0.3 System Requirements](docs/system-requirements.md).

## Documentation

Commencez par [Naut Documentation](docs/README.md) : Import Media, Face Intelligence, Profiles, Vault, Customization, Languages, 3D Figures, System Requirements et Troubleshooting.

Contributor-facing build, licensing, and Presentation Pack material lives under [docs/development](docs/development/).

## Source et releases

Le dépôt privé **naut-dv** est l'autorité d'ingénierie. Ce dépôt **naut** organisé est la surface publique de source, contribution, démo et releases officielles.

Le source appartenant à Naut est **Source Available** sous PolyForm Shield 1.0.0 ; les composants tiers conservent leurs propres licences. Voir [LICENSE](LICENSE), [licensing scope](docs/development/licensing.md), [brand policy](BRAND-POLICY.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md) et [third-party notices](THIRD-PARTY-NOTICES.txt).

Les binaires officiels et métadonnées de mise à jour signées sont distribués uniquement via les **GitHub Releases** officielles de `secondshift-dv/naut`.
