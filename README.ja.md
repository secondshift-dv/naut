<p align="center">
  <img src="src/Neuterradise.App/Assets/Brand/wordmark-lockup.png" alt="Naut" width="460">
</p>

<p align="center"><strong>A Navigator for Your Things Worth Keeping</strong></p>

<p align="center">
  <a href="https://github.com/secondshift-dv/naut/releases/tag/v0.0.1"><strong>Naut v0.0.1 をダウンロード</strong></a>
  ·
  <a href="https://secondshift-dv.github.io/naut/"><strong>ライブデモを見る</strong></a>
  ·
  <a href="docs/README.md"><strong>ドキュメント</strong></a>
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
  <img src="docs/assets/readme/naut-showcase.gif" alt="Naut インタラクティブショーケース" width="682">
</p>

## 大切なコレクションを、探索したくなる形へ。

Naut は Windows 向けの **local-first メディアコレクション管理アプリ**です。画像、動画、対応 3D モデルを Profile 中心のコレクションとして整理し、クラウドサービスに所有権を渡すことなく認識・表示・探索できます。

## 主な機能

<table>
<tr>
<td width="50%" valign="top">
<a href="docs/face-intelligence.md"><img src="docs/assets/readme/feature-face-intelligence.svg" alt="Face Intelligence" width="100%"></a><br>
<strong>Face Intelligence</strong><br>
YuNet が対象の顔を検出し、SFace が embedding を生成します。確認済みの identity sample がある場合、Naut は Profile 候補をローカルで提示できます。候補は確認可能な提案として扱われます。
</td>
<td width="50%" valign="top">
<a href="docs/figures.md"><img src="docs/assets/readme/feature-3d-figures.svg" alt="インタラクティブ 3D Figure" width="100%"></a><br>
<strong>インタラクティブ 3D Figure</strong><br>
対応モデルは回転・パン・ズーム、Naut 管理の framing、上限付き runtime texture policy を備えた Figure として表示できます。
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/import-media.md"><img src="docs/assets/readme/feature-smart-import.svg" alt="Smart Media Import" width="100%"></a><br>
<strong>Smart Media Import</strong><br>
同じ import path がメディア種別ごとに必要な処理だけを実行します。metadata、thumbnail、上限付き video presentation media、必要な場合の face analysis、対応モデルの Figure derivative を準備します。
</td>
<td width="50%" valign="top">
<a href="docs/customization.md"><img src="docs/assets/readme/feature-customization.svg" alt="元データを壊さないプレゼンテーション" width="100%"></a><br>
<strong>元データを壊さないプレゼンテーション</strong><br>
Home、Gallery、Card、Profile、Cover、Banner、Frame、Backdrop、layout、effect、theme を変更しても元メディアは変更しません。
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/languages.md"><img src="docs/assets/readme/feature-multilingual.svg" alt="8 つの UI 言語" width="100%"></a><br>
<strong>8 つの UI 言語</strong><br>
English、Bahasa Indonesia、日本語、한국어、简体中文、Deutsch、Français、Español を搭載。UI 言語を変更しても Vault やメディア metadata は書き換えません。
</td>
<td width="50%" valign="top">
<a href="docs/vault.md"><img src="docs/assets/readme/feature-local-vault.svg" alt="ポータブルなローカル Vault" width="100%"></a><br>
<strong>ポータブルなローカル Vault</strong><br>
ポータブルアプリと Vault は分離されています。Import はファイルを Vault にコピーし、元ファイルは元の場所に残します。
</td>
</tr>
</table>

## Naut を選ぶ理由

- **フォルダーより Profile を中心に。** Profile は人物、キャラクター、プロジェクト、物、テーマなどを表現できます。
- **ローカル制御。** 正規のコレクションは選択した Vault に保持されます。
- **閲覧向けに準備。** Naut は元メディアを書き換えず、上限付き presentation derivative を生成します。
- **3D は追加機能。** 通常利用に discrete GPU や Figure は必須ではありません。
- **アプリは交換可能、コレクションは持続。** アプリ更新で Vault は置き換わりません。

## 実際の画面

<table>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-home.png" alt="Home — 注目の Profiles、activity、コレクション状況。" width="100%"><br><sub>Home — 注目の Profiles、activity、コレクション状況。</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-profile.png" alt="Profile — identity、media、presentation、任意の interactive Figure。" width="100%"><br><sub>Profile — identity、media、presentation、任意の interactive Figure。</sub></td>
</tr>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-gallery.png" alt="Gallery — コレクション閲覧、検索、filter、Card 表示。" width="100%"><br><sub>Gallery — コレクション閲覧、検索、filter、Card 表示。</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-settings.png" alt="Settings — Theme、言語、Vault control、system option。" width="100%"><br><sub>Settings — Theme、言語、Vault control、system option。</sub></td>
</tr>
</table>

## クイックスタート

1. 公式 [GitHub Release](https://github.com/secondshift-dv/naut/releases/tag/v0.0.1) から **Naut v0.0.1** をダウンロードします。
2. ZIP 全体を通常のフォルダーへ展開します。
3. `naut.exe` を起動します。
4. Vault の保存場所を選びます。
5. メディアを import するか Profile を作成して **Add media** を使います。

`runtime/` と `release-manifest.json` は `naut.exe` の隣に残してください。ローカルファイルや Vault に触れないブラウザプレビューは [Interactive Showcase](https://secondshift-dv.github.io/naut/) で確認できます。

## システム要件

通常の Naut 利用は、任意の Figure workload より軽量になるよう設計されています。

| | Minimum | Recommended |
| --- | --- | --- |
| **OS** | Windows 10/11 64-bit | Windows 11 64-bit |
| **CPU** | x86-64、2 cores | Modern x86-64、4+ cores |
| **RAM** | 8 GB | 16 GB |
| **GPU** | 通常利用は integrated graphics | より滑らかな Figure には Direct3D 11 対応 integrated/discrete GPU |
| **Display** | 1280 × 720 | 1920 × 1080 以上 |
| **App/update 空き容量** | 1.5 GB | SSD に 1.5 GB+ |

Vault 容量は別で、コレクションサイズに依存します。詳しくは [Naut v0.0.1 System Requirements](docs/system-requirements.md) を参照してください。

## ドキュメント

[Naut Documentation](docs/README.md) から Import Media、Face Intelligence、Profiles、Vault、Customization、Languages、3D Figures、System Requirements、Troubleshooting を確認できます。

Contributor-facing build, licensing, and Presentation Pack material lives under [docs/development](docs/development/).

## Source と releases

非公開の **naut-dv** repository が engineering authority です。この **naut** repository は public source、contribution、demo、official release surface です。

Naut 所有の source は PolyForm Shield 1.0.0 に基づく **Source Available** です。third-party component は各自の license を維持します。[LICENSE](LICENSE)、[licensing scope](docs/development/licensing.md)、[brand policy](BRAND-POLICY.md)、[contributing](CONTRIBUTING.md)、[security](SECURITY.md)、[third-party notices](THIRD-PARTY-NOTICES.txt) を参照してください。

公式 binary と署名済み update metadata は `secondshift-dv/naut` の公式 **GitHub Releases** からのみ配布されます。
