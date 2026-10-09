<p align="center">
  <img src="src/Neuterradise.App/Assets/Brand/wordmark-lockup.png" alt="Naut" width="460">
</p>

<p align="center"><strong>A Navigator for Your Things Worth Keeping</strong></p>

<p align="center">Windows용 로컬 미디어 컬렉션 — 오프라인 얼굴 인식, 인터랙티브 3D Figure, 나만의 휴대용 Vault.</p>

<p align="center">
  <a href="https://github.com/secondshift-dv/naut/releases/tag/v0.0.6"><strong>Naut v0.0.6 다운로드</strong></a>
  ·
  <a href="https://secondshift-dv.github.io/naut/"><strong>라이브 데모 보기</strong></a>
  ·
  <a href="docs/README.md"><strong>문서</strong></a>
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
  <img src="docs/assets/readme/naut-showcase.gif" alt="Naut 인터랙티브 쇼케이스" width="682">
</p>

## 소장품을 더 탐험하고 싶게.

Naut는 Windows용 **local-first 미디어 컬렉션 관리자**입니다. 이미지, 비디오, 지원되는 3D 모델을 Profile 중심 컬렉션으로 구성하고 클라우드 서비스에 소유권을 넘기지 않은 채 인식·표현·탐색할 수 있습니다.

## Optional pack collection

Make Naut yours with reviewed, independently versioned customization packs.
Download a pack, import it in **Settings → Presentation → Packs / Advanced**,
then choose its components and save. Application code stays unchanged.

**[Browse and download packs](packs/README.md) · [Install or update a pack](docs/pack-collection.md) · [Contribute your own](docs/contributing-packs.md)**

## 주요 기능

<table>
<tr>
<td width="50%" valign="top">
<a href="docs/face-intelligence.md"><img src="docs/assets/readme/feature-face-intelligence.svg" alt="Face Intelligence" width="100%"></a><br>
<strong>Face Intelligence</strong><br>
YuNet이 적용 가능한 얼굴을 감지하고 SFace가 embedding을 생성합니다. 확인된 identity sample이 있으면 Naut가 로컬에서 Profile 후보를 제안할 수 있으며, 제안은 검토 가능한 상태로 유지됩니다.
</td>
<td width="50%" valign="top">
<a href="docs/figures.md"><img src="docs/assets/readme/feature-3d-figures.svg" alt="인터랙티브 3D Figure" width="100%"></a><br>
<strong>인터랙티브 3D Figure</strong><br>
호환 모델을 회전, 이동, 확대/축소, Naut 관리 framing, 제한된 runtime texture 정책을 갖는 Figure로 사용할 수 있습니다.
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/import-media.md"><img src="docs/assets/readme/feature-smart-import.svg" alt="Smart Media Import" width="100%"></a><br>
<strong>Smart Media Import</strong><br>
하나의 import path가 미디어 유형별로 필요한 작업만 수행합니다. metadata, thumbnail, 제한된 video presentation media, 필요한 경우 face analysis, 지원 모델의 Figure derivative를 준비합니다.
</td>
<td width="50%" valign="top">
<a href="docs/customization.md"><img src="docs/assets/readme/feature-customization.svg" alt="원본을 건드리지 않는 표현" width="100%"></a><br>
<strong>원본을 건드리지 않는 표현</strong><br>
Home, Gallery, Card, Profile, Cover, Banner, Frame, Backdrop, layout, effect, theme을 바꿔도 원본 미디어는 변경하지 않습니다.
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/languages.md"><img src="docs/assets/readme/feature-multilingual.svg" alt="8개 인터페이스 언어" width="100%"></a><br>
<strong>8개 인터페이스 언어</strong><br>
English, Bahasa Indonesia, 日本語, 한국어, 简体中文, Deutsch, Français, Español을 제공합니다. UI 언어 변경은 Vault나 미디어 metadata를 다시 쓰지 않습니다.
</td>
<td width="50%" valign="top">
<a href="docs/vault.md"><img src="docs/assets/readme/feature-local-vault.svg" alt="포터블 로컬 Vault" width="100%"></a><br>
<strong>포터블 로컬 Vault</strong><br>
포터블 앱 패키지와 Vault는 분리된 경계입니다. Import는 파일을 Vault에 복사하고 원본 파일은 원래 위치에 남깁니다.
</td>
</tr>
</table>

## 왜 Naut인가

- **폴더보다 Profile 중심.** Profile은 사람, 캐릭터, 프로젝트, 객체, 주제 등 컬렉션 정체성을 나타낼 수 있습니다.
- **로컬 제어.** authoritative collection은 사용자가 선택한 Vault에 있습니다.
- **탐색에 맞게 준비.** Naut는 원본을 수정하지 않고 제한된 presentation derivative를 생성합니다.
- **3D는 선택 사항.** 일반적인 사용에 discrete GPU나 Figure가 필요하지 않습니다.
- **앱은 포터블, 컬렉션은 지속적.** 앱 업데이트가 Vault를 대체하지 않습니다.

## 실제 화면

<table>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-home.png" alt="Home — 주요 Profiles, activity, collection context." width="100%"><br><sub>Home — 주요 Profiles, activity, collection context.</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-profile.png" alt="Profile — identity, media, presentation, 선택적 interactive Figure." width="100%"><br><sub>Profile — identity, media, presentation, 선택적 interactive Figure.</sub></td>
</tr>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-gallery.png" alt="Gallery — collection browsing, search, filter, Card presentation." width="100%"><br><sub>Gallery — collection browsing, search, filter, Card presentation.</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-settings.png" alt="Settings — Theme, 언어, Vault controls, system options." width="100%"><br><sub>Settings — Theme, 언어, Vault controls, system options.</sub></td>
</tr>
</table>

## 빠른 시작

1. 공식 [GitHub Release](https://github.com/secondshift-dv/naut/releases/tag/v0.0.6)에서 **Naut v0.0.6**을 다운로드합니다.
2. 전체 ZIP을 일반 폴더에 압축 해제합니다.
3. `naut.exe`를 실행합니다.
4. Vault를 보관할 위치를 선택합니다.
5. 미디어를 import하거나 Profile을 만든 뒤 **Add media**를 사용합니다.

`runtime/`과 `release-manifest.json`은 `naut.exe` 옆에 유지해야 합니다. 로컬 파일이나 Vault를 건드리지 않는 브라우저 미리보기는 [Interactive Showcase](https://secondshift-dv.github.io/naut/)에서 확인할 수 있습니다.

## 시스템 요구 사항

일반적인 Naut 사용은 선택적 Figure workload보다 가볍도록 설계되었습니다.

| | Minimum | Recommended |
| --- | --- | --- |
| **OS** | Windows 10/11 64-bit | Windows 11 64-bit |
| **CPU** | x86-64, 2 cores | Modern x86-64, 4+ cores |
| **RAM** | 8 GB | 16 GB |
| **GPU** | 일반 사용은 integrated graphics | 더 부드러운 Figure에는 Direct3D 11 지원 integrated/discrete GPU |
| **Display** | 1280 × 720 | 1920 × 1080 이상 |
| **App/update 여유 공간** | 3 GB | SSD 3 GB+ |

Vault 저장 공간은 별도이며 컬렉션 크기에 따라 달라집니다. [Naut v0.0.6 System Requirements](docs/system-requirements.md)를 참고하세요.

## 문서

[Naut Documentation](docs/README.md)에서 Import Media, Face Intelligence, Profiles, Vault, Customization, Languages, 3D Figures, System Requirements, Troubleshooting을 확인할 수 있습니다.

Contributor-facing build, licensing, and Presentation Pack material lives under [docs/development](docs/development/).

## Source와 releases

비공개 **naut-dv** repository가 engineering authority입니다. 이 정리된 **naut** repository는 public source, contribution, demo, official release surface입니다.

Naut 소유 source는 PolyForm Shield 1.0.0에 따른 **Source Available**입니다. third-party component는 각각의 license를 유지합니다. [LICENSE](LICENSE), [licensing scope](docs/development/licensing.md), [brand policy](BRAND-POLICY.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md), [third-party notices](THIRD-PARTY-NOTICES.txt)를 참고하세요.

공식 binary와 서명된 update metadata는 `secondshift-dv/naut`의 공식 **GitHub Releases**를 통해서만 배포됩니다.
