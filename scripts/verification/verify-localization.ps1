Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CatalogPath = Join-Path $RepositoryRoot 'src\Neuterradise.Runtime\Localization\LanguageCatalog.cs'
$StringsRoot = Join-Path $RepositoryRoot 'src\Neuterradise.Runtime\Localization\Strings'
$FlagsRoot = Join-Path $RepositoryRoot 'src\Neuterradise.App\Assets\Flags'
$XamlNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml'
$PlaceholderPattern = [regex]'\{[^{}]+\}'

if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
    throw "Language catalog was not found: $CatalogPath"
}

$catalogSource = Get-Content -LiteralPath $CatalogPath -Raw
$languageMatches = [regex]::Matches(
    $catalogSource,
    'new\("([^"]+)",\s*"([^"]+)",\s*"([^"]+)",\s*"([^"]+)"\)')
if ($languageMatches.Count -eq 0) {
    throw 'No supported languages were discovered from LanguageCatalog.'
}
$languages = @(
    foreach ($match in $languageMatches) {
        [pscustomobject]@{
            Code = $match.Groups[1].Value
            Culture = $match.Groups[2].Value
            NativeName = $match.Groups[3].Value
            Flag = $match.Groups[4].Value
        }
    }
)

function Read-LocalizationTable([string]$Path) {
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $table = [Collections.Generic.Dictionary[string,string]]::new(
        [StringComparer]::Ordinal)
    foreach ($node in $document.ResourceDictionary.ChildNodes) {
        if ($node -isnot [Xml.XmlElement] -or $node.LocalName -ne 'String') {
            continue
        }

        $key = $node.GetAttribute('Key', $XamlNamespace)
        if ([string]::IsNullOrWhiteSpace($key)) {
            throw "$Path contains a localization string without x:Key."
        }
        if (-not $table.TryAdd($key, [string]$node.InnerText)) {
            throw "$Path contains duplicate localization key '$key'."
        }
    }
    return $table
}

function Get-PlaceholderSignature([string]$Value) {
    return @(
        $PlaceholderPattern.Matches($Value) |
            ForEach-Object { $_.Value } |
            Sort-Object
    ) -join [char]31
}

$expectedFiles = @($languages | ForEach-Object { "$($_.Code).xml" } | Sort-Object)
$actualFiles = @(
    Get-ChildItem -LiteralPath $StringsRoot -File -Filter '*.xml' |
        ForEach-Object { $_.Name } |
        Sort-Object
)
if (($expectedFiles -join [char]31) -ne ($actualFiles -join [char]31)) {
    throw "Localization resource roster differs from LanguageCatalog. Expected: $($expectedFiles -join ', '); actual: $($actualFiles -join ', ')."
}

$englishPath = Join-Path $StringsRoot 'en.xml'
$english = Read-LocalizationTable $englishPath
if ($english.Count -eq 0) {
    throw 'English localization authority is empty.'
}

$validatedFlags = 0
foreach ($language in $languages) {
    $path = Join-Path $StringsRoot "$($language.Code).xml"
    $table = Read-LocalizationTable $path
    if ($table.Count -ne $english.Count) {
        throw "$($language.Code) has $($table.Count) keys; English has $($english.Count)."
    }

    foreach ($key in $english.Keys) {
        if (-not $table.ContainsKey($key)) {
            throw "$($language.Code) is missing localization key '$key'."
        }

        $sourceValue = $english[$key]
        $targetValue = $table[$key]
        if (-not [string]::IsNullOrEmpty($sourceValue) -and
            [string]::IsNullOrEmpty($targetValue)) {
            throw "$($language.Code) has an empty value for '$key'."
        }
        $sourceSignature = Get-PlaceholderSignature $sourceValue
        $targetSignature = Get-PlaceholderSignature $targetValue
        if (-not [string]::Equals(
            $sourceSignature,
            $targetSignature,
            [StringComparison]::Ordinal)) {
            throw "$($language.Code) changes format markers for '$key'."
        }
    }

    $flagPath = Join-Path $FlagsRoot "$($language.Flag).png"
    if (-not (Test-Path -LiteralPath $flagPath -PathType Leaf)) {
        throw "$($language.Code) flag asset was not found: $flagPath"
    }
    $validatedFlags++
}

Write-Host "LOCALIZATION_LANGUAGES=$($languages.Count)"
Write-Host "LOCALIZATION_KEYS=$($english.Count)"
Write-Host "LOCALIZATION_FLAGS=$validatedFlags"
Write-Host 'LOCALIZATION_VERIFY=PASS'
