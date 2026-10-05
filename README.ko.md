![Naut 워드마크](src/Neuterradise.App/Assets/Brand/wordmark-lockup.png)

[English](README.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Français](README.fr.md) · [Bahasa Indonesia](README.id.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [简体中文](README.zh-Hans.md)

# Naut

**A Navigator for Your Things Worth Keeping**

Naut는 Windows용 로컬 우선 미디어 컬렉션 관리자입니다. 프로필에서 이미지, 비디오, 인터랙티브 3D Figure를 휴대 가능한 Vault에 함께 관리하며 컬렉션의 통제권을 사용자에게 유지합니다.

비공개 **naut-dv** 저장소가 엔지니어링 권한을 가집니다. 이 큐레이션된 **naut** 저장소는 공개 소스, 기여, 공식 릴리스의 창구입니다. Naut 소유 소스는 PolyForm Shield 1.0.0에 따른 **Source Available**이며, 타사 구성요소에는 각자의 라이선스가 계속 적용됩니다.

## 빌드

Windows x64 및 .NET SDK **10.0.401**이 필요합니다.

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

포터블 패키지는 `naut.exe`로 시작합니다. `runtime/`과 `release-manifest.json`을 같은 위치에 유지하세요.

## 공식 다운로드

공식 Naut 바이너리와 서명된 업데이트 메타데이터는 공식 **secondshift-dv/naut GitHub Releases** 페이지를 통해서만 배포됩니다. Discord, 파일 공유 서비스 또는 타사 미러에 재업로드된 바이너리는 실행하지 마세요.

## 프로젝트 정책

[LICENSE](LICENSE), [라이선스 범위](docs/licensing.md), [브랜드 정책](BRAND-POLICY.md), [기여 안내](CONTRIBUTING.md), [보안](SECURITY.md), [타사 고지](THIRD-PARTY-NOTICES.txt)를 참고하세요.

공식 앱 언어: 영어, 독일어, 스페인어, 프랑스어, 인도네시아어, 일본어, 한국어, 중국어 간체.
