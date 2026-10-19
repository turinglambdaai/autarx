$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = Join-Path $repo '.rivet\acceptance\windows'
$fixture = (Resolve-Path -LiteralPath (Join-Path $repo 'shared\fixtures\OemDelivery')).Path
$executable = Get-ChildItem -LiteralPath (Join-Path $repo 'dist') -Filter RivetHost.exe -File -Recurse | Select-Object -First 1
if (-not $executable) { throw 'packaged Autarx executable was not found' }
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Wait-Until {
    param([string]$Description, [scriptblock]$Probe, [int]$TimeoutSeconds = 60)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $value = & $Probe
        if ($null -ne $value -and $value -ne $false) { return $value }
        Start-Sleep -Milliseconds 100
    }
    throw "timed out waiting for $Description"
}
function Find-ById($Root, [string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-ByName($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

$process = $null
$savedOpen = $env:AUTARX_OPEN
try {
    $env:AUTARX_OPEN = $fixture
    $process = Start-Process -FilePath $executable.FullName -PassThru -WorkingDirectory $repo `
        -RedirectStandardOutput (Join-Path $output 'application.stdout.log') `
        -RedirectStandardError (Join-Path $output 'application.stderr.log')
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $window = Wait-Until 'Autarx window' {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    }
    [void](Wait-Until 'workspace result from embedded backend' {
        $status = Find-ById $window 'StatusText'
        if ($status -and $status.Current.Name -match 'objects') { return $true }
        return $false
    })
    $objects = Wait-Until 'Objects navigation item' { Find-ByName $window 'Objects' }
    while ($objects -and $objects.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
        $objects = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($objects)
    }
    if (-not $objects) { throw 'Objects list item was not exposed to UI Automation' }
    $selection = $objects.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    ([System.Windows.Automation.SelectionItemPattern]$selection).Select()
    [void](Wait-Until 'VehicleSpeed object rendered after navigation' { if (Find-ByName $window 'VehicleSpeed') { return $true }; return $false })

    $nodes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $snapshot = for ($index = 0; $index -lt [Math]::Min($nodes.Count, 500); $index++) {
        $node = $nodes[$index]
        [ordered]@{ name = $node.Current.Name; controlType = $node.Current.ControlType.ProgrammaticName; automationId = $node.Current.AutomationId }
    }
    $snapshot | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'accessibility.json') -Encoding utf8
    [ordered]@{ platform = 'windows'; artifact = $executable.FullName; actions = @('select:Objects'); assertions = @('packaged WinUI window activated','fixture opened through RVT1','Objects navigation exposed through UI Automation','VehicleSpeed rendered from embedded backend') } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'interaction-trace.json') -Encoding utf8
    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    [void](Wait-Until 'orderly process exit' { $process.Refresh(); if ($process.HasExited) { return $true }; return $false } 15)
} finally {
    $env:AUTARX_OPEN = $savedOpen
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; $process.WaitForExit() }
    if ($process) { $process.Dispose() }
}
