<p align="center">
  <img src="src/Neuterradise.App/Assets/Brand/wordmark-lockup.png" alt="Naut" width="460">
</p>

<p align="center"><strong>A Navigator for Your Things Worth Keeping</strong></p>

<p align="center">
  <a href="https://github.com/secondshift-dv/naut/releases/tag/v0.0.1"><strong>Download Naut v0.0.1</strong></a>
  ·
  <a href="https://secondshift-dv.github.io/naut/"><strong>Lihat Live Demo</strong></a>
  ·
  <a href="docs/README.md"><strong>Dokumentasi</strong></a>
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
  <img src="docs/assets/readme/naut-showcase.gif" alt="Showcase interaktif Naut" width="682">
</p>

## Koleksimu, dibuat layak untuk dijelajahi.

Naut adalah **pengelola koleksi media local-first untuk Windows**. Gambar, video, dan model 3D yang didukung diolah menjadi koleksi berbasis Profile yang dapat dikenali, dipresentasikan, dan dijelajahi tanpa memindahkan kepemilikan koleksi ke layanan cloud.

## Fitur unggulan

<table>
<tr>
<td width="50%" valign="top">
<a href="docs/face-intelligence.md"><img src="docs/assets/readme/feature-face-intelligence.svg" alt="Face Intelligence" width="100%"></a><br>
<strong>Face Intelligence</strong><br>
YuNet mendeteksi wajah yang relevan, SFace menghasilkan embedding, lalu Naut dapat menampilkan kandidat Profile dari sampel identitas yang sudah dikonfirmasi. Pemrosesan tetap lokal dan saran tetap dapat ditinjau.
</td>
<td width="50%" valign="top">
<a href="docs/figures.md"><img src="docs/assets/readme/feature-3d-figures.svg" alt="Figure 3D interaktif" width="100%"></a><br>
<strong>Figure 3D interaktif</strong><br>
Model yang kompatibel dapat menjadi Figure interaktif dengan rotate, pan, zoom, framing yang dikelola Naut, dan batas tekstur runtime yang terkontrol.
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/import-media.md"><img src="docs/assets/readme/feature-smart-import.svg" alt="Smart media import" width="100%"></a><br>
<strong>Smart media import</strong><br>
Jalur import yang sama hanya menyiapkan kebutuhan tiap jenis media: metadata, thumbnail, media presentasi video yang dibatasi, analisis wajah bila relevan, dan derivative Figure untuk model yang didukung.
</td>
<td width="50%" valign="top">
<a href="docs/customization.md"><img src="docs/assets/readme/feature-customization.svg" alt="Presentasi tanpa mengubah media asli" width="100%"></a><br>
<strong>Presentasi tanpa mengubah media asli</strong><br>
Atur Home, Gallery, Card, Profile, Cover, Banner, Frame, Backdrop, layout, effect, dan theme tanpa memodifikasi file media asli.
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/languages.md"><img src="docs/assets/readme/feature-multilingual.svg" alt="8 bahasa antarmuka" width="100%"></a><br>
<strong>8 bahasa antarmuka</strong><br>
Naut menyediakan English, Bahasa Indonesia, 日本語, 한국어, 简体中文, Deutsch, Français, dan Español. Bahasa UI tidak menulis ulang Vault atau metadata media.
</td>
<td width="50%" valign="top">
<a href="docs/vault.md"><img src="docs/assets/readme/feature-local-vault.svg" alt="Vault lokal dan portabel" width="100%"></a><br>
<strong>Vault lokal dan portabel</strong><br>
Paket aplikasi portabel dan Vault adalah batas yang terpisah. Import menyalin file ke Vault sementara file sumber tetap berada di lokasi asal.
</td>
</tr>
</table>

## Kenapa Naut

- **Profile dulu, bukan folder dulu.** Profile dapat mewakili orang, karakter, proyek, objek, subjek, atau identitas koleksi lain.
- **Kontrol lokal sejak awal.** Koleksi authoritative berada di Vault yang kamu pilih.
- **Disiapkan untuk browsing.** Naut membuat derivative presentasi yang terkontrol tanpa memodifikasi media asli.
- **3D bersifat tambahan, bukan kewajiban.** Penggunaan Naut normal tidak membutuhkan discrete GPU atau Figure.
- **Aplikasi portabel, koleksi durable.** Update paket aplikasi tidak mengganti Vault.

## Lihat tampilannya

<table>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-home.png" alt="Home — Profile unggulan, activity, dan konteks koleksi." width="100%"><br><sub>Home — Profile unggulan, activity, dan konteks koleksi.</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-profile.png" alt="Profile — identitas, media, presentasi, dan Figure interaktif opsional." width="100%"><br><sub>Profile — identitas, media, presentasi, dan Figure interaktif opsional.</sub></td>
</tr>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-gallery.png" alt="Gallery — browsing koleksi, search, filter, dan tampilan card." width="100%"><br><sub>Gallery — browsing koleksi, search, filter, dan tampilan card.</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-settings.png" alt="Settings — Theme, bahasa, kontrol Vault, dan opsi sistem." width="100%"><br><sub>Settings — Theme, bahasa, kontrol Vault, dan opsi sistem.</sub></td>
</tr>
</table>

## Mulai cepat

1. Download **Naut v0.0.1** dari [GitHub Release](https://github.com/secondshift-dv/naut/releases/tag/v0.0.1) resmi.
2. Extract seluruh ZIP ke folder biasa.
3. Jalankan `naut.exe`.
4. Pilih **lokasi penyimpanan** Vault.
5. Import media atau buat Profile lalu gunakan **Add media**.

Paket portabel harus tetap menyimpan `runtime/` dan `release-manifest.json` di samping `naut.exe`. Untuk preview browser yang tidak menyentuh file lokal atau Vault, buka [Interactive Showcase](https://secondshift-dv.github.io/naut/).

## System Requirements

Pengalaman koleksi Naut normal sengaja dibuat lebih ringan daripada workload Figure opsional.

| | Minimum | Recommended |
| --- | --- | --- |
| **OS** | Windows 10/11 64-bit | Windows 11 64-bit |
| **CPU** | x86-64, 2 core | x86-64 modern, 4+ core |
| **RAM** | 8 GB | 16 GB |
| **GPU** | Grafis terintegrasi untuk penggunaan Naut normal | GPU integrated/discrete Direct3D 11 untuk Figure yang lebih halus |
| **Display** | 1280 × 720 | 1920 × 1080 atau lebih |
| **Ruang app/update** | 1.5 GB | 1.5 GB+ pada SSD |

Storage Vault terpisah dan bergantung pada ukuran koleksi. Lihat [Naut v0.0.1 System Requirements](docs/system-requirements.md) lengkap.

## Dokumentasi

Mulai dari [Dokumentasi Naut](docs/README.md): **Getting Started** · **Import Media** · **Face Intelligence** · **Profiles** · **Vault** · **Customization** · **Languages** · **3D Figures** · **System Requirements** · **FAQ & Troubleshooting**.

Contributor-facing build, licensing, and Presentation Pack material lives under [docs/development](docs/development/).

## Source dan release

Repository private **naut-dv** adalah engineering authority. Repository **naut** yang dikurasi ini adalah public source, contribution, demo, dan official release surface.

Source milik Naut bersifat **Source Available** di bawah PolyForm Shield 1.0.0; komponen pihak ketiga tetap menggunakan lisensinya masing-masing. Lihat [LICENSE](LICENSE), [licensing scope](docs/development/licensing.md), [brand policy](BRAND-POLICY.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md), dan [third-party notices](THIRD-PARTY-NOTICES.txt).

Binary resmi dan metadata update bertanda tangan hanya didistribusikan melalui halaman **GitHub Releases** resmi `secondshift-dv/naut`.
