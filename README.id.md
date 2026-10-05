![Wordmark Naut](src/Neuterradise.App/Assets/Brand/wordmark-lockup.png)

[English](README.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Français](README.fr.md) · [Bahasa Indonesia](README.id.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [简体中文](README.zh-Hans.md)

# Naut

**A Navigator for Your Things Worth Keeping**

Naut adalah pengelola koleksi media local-first untuk Windows. Profile menyatukan gambar, video, dan Figure 3D interaktif di dalam Vault portabel, sehingga koleksi tetap berada di bawah kendali pengguna.

Repositori privat **naut-dv** adalah otoritas engineering. Repositori **naut** yang dikurasi ini adalah permukaan publik untuk source, kontribusi, dan rilisan resmi. Source milik Naut bersifat **Source Available** di bawah PolyForm Shield 1.0.0; komponen pihak ketiga tetap menggunakan lisensinya masing-masing.

## Build

Memerlukan Windows x64 dan .NET SDK **10.0.401**.

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

Paket portabel dijalankan melalui `naut.exe`; pertahankan `runtime/` dan `release-manifest.json` di sampingnya.

## Unduhan resmi

Binary Naut resmi dan metadata update yang ditandatangani hanya didistribusikan melalui halaman **GitHub Releases secondshift-dv/naut** yang resmi. Jangan menjalankan binary yang diunggah ulang ke Discord, layanan berbagi file, atau mirror pihak ketiga.

## Kebijakan proyek

Lihat [LICENSE](LICENSE), [cakupan lisensi](docs/licensing.md), [kebijakan brand](BRAND-POLICY.md), [kontribusi](CONTRIBUTING.md), [keamanan](SECURITY.md), dan [pemberitahuan pihak ketiga](THIRD-PARTY-NOTICES.txt).

Bahasa resmi aplikasi: Inggris, Jerman, Spanyol, Prancis, Indonesia, Jepang, Korea, dan Mandarin Sederhana.
