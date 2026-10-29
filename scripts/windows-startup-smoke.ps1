# Windows startup smoke only: no EULA acceptance, account use, game launch or installation.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$exe = (Resolve-Path 'dist/app/Plain Craft Launcher 2.exe').Path
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $names = @()
    do {
        Start-Sleep -Seconds 2
        $p.Refresh()
        if ($p.HasExited) { throw "Launcher exited during startup: $($p.ExitCode)" }
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
        $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
        $names = @()
        foreach ($root in $roots) {
            $names += $root.Current.Name
            $nodes = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
            foreach ($node in $nodes) { if ($node.Current.Name) { $names += $node.Current.Name } }
        }
    } until ((($names -contains '服务器选择') -and ($names -contains '暮色森林：安装 / 检查更新') -and ($names -contains '宝可梦：安装 / 检查更新') -and ($names -contains '九选一空岛：安装 / 检查更新') -and ($names -contains '原版生存：安装 / 检查更新')) -or [DateTime]::UtcNow -gt $deadline)
    $names | Select-Object -Unique | ConvertTo-Json | Set-Content dist/startup-ui-names.json -Encoding utf8
    if (-not ($names -match 'Steven')) { throw 'Third-party launcher window identity not found' }
    if (-not ($names -contains '服务器选择')) { throw 'Embedded server chooser UI did not appear within 45 seconds' }
    foreach ($server in @('暮色森林','宝可梦','九选一空岛','原版生存')) {
        if (-not ($names -contains ($server + '：安装 / 检查更新'))) { throw "Server card missing: $server" }
    }
    Write-Host 'PASS: Windows process alive, third-party window identity, embedded chooser and four server cards visible.'
    Write-Host 'Not tested: agreement acceptance, real download/install, Microsoft login, joining Minecraft server.'
} finally {
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
