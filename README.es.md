![Logotipo de Naut](src/Neuterradise.App/Assets/Brand/wordmark-lockup.png)

[English](README.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Français](README.fr.md) · [Bahasa Indonesia](README.id.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [简体中文](README.zh-Hans.md)

# Naut

**A Navigator for Your Things Worth Keeping**

Naut es un gestor de colecciones multimedia local para Windows. Los perfiles reúnen imágenes, vídeos y Figuras 3D interactivas en un Vault portátil, manteniendo la colección bajo el control del usuario.

El repositorio privado **naut-dv** es la autoridad de ingeniería. Este repositorio curado **naut** es la superficie pública de código fuente, contribuciones y lanzamientos oficiales. El código propiedad de Naut es **Source Available** bajo PolyForm Shield 1.0.0; los componentes de terceros conservan sus propias licencias.

## Compilación

Se requieren Windows x64 y .NET SDK **10.0.401**.

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

El paquete portátil se inicia con `naut.exe`; mantén `runtime/` y `release-manifest.json` junto a él.

## Descargas oficiales

Los binarios oficiales de Naut y los metadatos de actualización firmados se distribuyen únicamente mediante la página oficial de **GitHub Releases de secondshift-dv/naut**. No ejecutes binarios republicados en Discord, servicios de archivos o mirrors de terceros.

## Políticas

Consulta [LICENSE](LICENSE), [alcance de licencias](docs/licensing.md), [política de marca](BRAND-POLICY.md), [contribuciones](CONTRIBUTING.md), [seguridad](SECURITY.md) y [avisos de terceros](THIRD-PARTY-NOTICES.txt).

Idiomas oficiales de la aplicación: inglés, alemán, español, francés, indonesio, japonés, coreano y chino simplificado.
