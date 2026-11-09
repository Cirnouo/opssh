#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputPath,

    [ValidateSet('ready', 'first-run', 'new-account', 'provider-failed', 'no-accounts', 'startup')]
    [string] $Scenario = 'ready',

    [ValidateSet('keys', 'activity', 'account', 'settings', 'about')]
    [string] $Page = 'keys',

    [ValidateSet('light', 'dark')]
    [string] $Theme = 'dark',

    [ValidateRange(400, 2000)]
    [int] $Width = 1040,

    [switch] $AtBottom
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../docs/references/opssh-agent-dashboard.html'
$expectedHash = '21202CB9F2EF713FCB63544E838B75B2F4F125CC654EE67BAEC7DC5F401FC216'
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The archived dashboard HTML differs from the approved Visualize source.'
}
$archive = [IO.File]::ReadAllText($source, [Text.UTF8Encoding]::new($false, $true))
$resourcePath = Join-Path $PSScriptRoot '../tray/Opssh.Tray/DashboardResources.xaml'
$resources = [xml] [IO.File]::ReadAllText($resourcePath)
$mark = $resources.SelectSingleNode('//*[local-name()="DataTemplate" and @*[local-name()="Key"]="DashboardGitHubIcon"]/*[local-name()="PathIcon"]')
$path = [Net.WebUtility]::HtmlEncode($mark.GetAttribute('Data'))
$svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><path d="' + $path + '"/></svg>'
$octocat = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($svg))
$configuration = @{ scenario = $Scenario; page = $Page; theme = $Theme; atBottom = [bool] $AtBottom } | ConvertTo-Json -Compress
$frame = @"
<!doctype html>
<html lang="zh-Hans">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline' https://unpkg.com/lucide@1.17.0/dist/umd/lucide.js; style-src 'unsafe-inline'; img-src data: https://cdn.jsdelivr.net; base-uri 'none'; form-action 'none'">
<style>html,body{margin:0;overflow:hidden} #opssh-agent-dashboard[data-theme="system"]{color-scheme:$Theme}</style>
<script src="https://unpkg.com/lucide@1.17.0/dist/umd/lucide.js"></script>
<script>
const preset = $configuration;
Date.now = () => new Date(2026, 10, 10, 15, 20, 0).getTime();
globalThis.Tweak = class {
  constructor(options) { this.changed = options.onChange; }
  addSelect(model, key) {
    model[key] = preset.scenario;
    this.changed();
  }
};
document.addEventListener('DOMContentLoaded', () => {
  document.querySelector('[data-action="nav:' + preset.page + '"]:not([aria-current="page"])')?.click();
  const theme = document.querySelector('select[data-action="theme"]');
  if (theme) {
    theme.value = preset.theme;
    theme.dispatchEvent(new Event('change', { bubbles: true }));
  }
  if (preset.atBottom) requestAnimationFrame(() => {
    const main = document.querySelector('#opssh-agent-dashboard .ops-main');
    if (main) main.scrollTop = main.scrollHeight;
  });
}, { once: true });
</script>
$archive
<style>#opssh-agent-dashboard .ops-github-mark{mask-image:url(data:image/svg+xml;base64,$octocat)}</style>
</html>
"@
$encodedFrame = [Net.WebUtility]::HtmlEncode($frame)
$document = @"
<!doctype html>
<html lang="zh-Hans">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>opssh Visualize reference</title>
<style>
:root { color-scheme:$Theme; }
body { margin:16px; }
iframe { display:block; width:min(100%, ${Width}px); height:642px; border:0; }
</style>
<iframe title="opssh SSH 代理工作区" sandbox="allow-scripts" referrerpolicy="no-referrer" srcdoc="$encodedFrame"></iframe>
</html>
"@
$output = [IO.Path]::GetFullPath($OutputPath, (Get-Location).ProviderPath)
if ($output -eq [IO.Path]::GetFullPath($source)) {
    throw 'Choose an output path outside the original dashboard HTML.'
}
[IO.File]::WriteAllText($output, $document, [Text.UTF8Encoding]::new($false))
Write-Output $output
