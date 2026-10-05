[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tokenPath = Join-Path $repoRoot 'src/Neuterradise.Runtime/Presentation/Tokens/TokenAuthority.cs'
$source = Get-Content -LiteralPath $tokenPath -Raw
$start = $source.IndexOf('public static ResolvedTokenSet Resolve(')
$finish = $source.IndexOf('return new ResolvedTokenSet(', $start)
$projection = $source.Substring($start, $finish - $start)
$consumerRoots = @('src/Neuterradise.App/Ui', 'src/Neuterradise.App/Assets/Presentation/BuiltIn') | ForEach-Object { Join-Path $repoRoot $_ }
$consumerPaths = Get-ChildItem -LiteralPath $consumerRoots -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.json' -and $_.FullName -notmatch '[\\/]Themes[\\/]' }
$consumers = ($consumerPaths | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
# V1 radius references and accessibility metadata remain part of the exported compatibility contract.
$compatibility = @('radiusXs', 'radiusSm', 'radiusMd', 'radiusLg', 'radiusXl', 'reducedMotion')
$tokens = [regex]::Matches($projection, '(?:colors|numbers|strings)\["([^"\r\n]+)"\]\s*=') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$missing = @($tokens | Where-Object {
    $_ -notin $compatibility -and $consumers -notmatch ('"(?:token:)?' + [regex]::Escape($_) + '"')
})
if ($missing.Count -gt 0) { throw "Active theme tokens have no source/pack consumer: $($missing -join ', ')" }
$owners = @{
    'Ui/Foundation/LivingNavigationButton.cs' = @('navAccentEdgeWidth', 'navSelectedBorderThickness', 'navIndicatorHeight', 'navCornerRadius')
    'Ui/Rendering/CardRenderer.cs' = @('cardBorderThickness', 'cardSurfaceOpacity', 'radiusCard')
    'Ui/Shell/ProductRoot.cs' = @('chromeBorderThickness', 'chromeBorder', 'chromeBackground')
    'Ui/Foundation/ThemeRuntime.cs' = @('glassFill', 'glassDeepFill', 'glassHighlight')
}
foreach ($owner in $owners.Keys) {
    $body = Get-Content -LiteralPath (Join-Path $repoRoot "src/Neuterradise.App/$owner") -Raw
    foreach ($token in $owners[$owner]) {
        if ($body -notmatch ('"' + [regex]::Escape($token) + '"')) { throw "Theme token $token has no owning renderer consumer in $owner" }
    }
}
$retired = @('cardShadow', 'focusRingThickness', 'focusUnderlineThickness', 'lightHalo', 'lightBloom', 'auraPrimary', 'auraSecondary', 'atmosphere', 'grain', 'glassBlur', 'frameAffinity')
foreach ($token in $retired) {
    if ($tokens -contains $token) { throw "Retired theme token is still projected: $token" }
}
$selectionPaths = @(
    'src/Neuterradise.Runtime/Settings/SettingsOperations.cs',
    'src/Neuterradise.Runtime/SystemServices/Lifecycle/ProductionRuntimeRegistry.cs',
    'src/Neuterradise.App/Ui/Foundation/ThemeRuntime.cs'
)
foreach ($path in $selectionPaths) {
    $text = Get-Content -LiteralPath (Join-Path $repoRoot $path) -Raw
    if ($text -match 'GetThemeAsync|PresentationPreferences|FallbackThemeId') { throw "Retired theme selection authority: $path" }
}
Write-Output "THEME_TOKEN_CONSUMERS=PASS; THEME_SELECTION_AUTHORITY=PASS; ACTIVE_TOKENS=$($tokens.Count)"
