$ErrorActionPreference = 'Stop'
$file = Join-Path $PSScriptRoot '..\scripts\invoke-control.ps1'
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $file),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count) { throw ($parseErrors.Message -join '; ') }
foreach($fn in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
function Assert-True($Condition,[string]$Label) { if(-not $Condition){throw $Label} }
function Assert-Throws([scriptblock]$Action,[string]$Label) { $failed=$false;try{&$Action}catch{$failed=$true};if(-not $failed){throw $Label} }
$wallet='prl1p'+('q'*58)
$config=[pscustomobject]@{wallet=$wallet;pool='stratum+tcp://prl-ru.kryptex.network:7048';worker='dual4070ti';minerExe='C:\Pearl\onezerominer.exe';logFile='C:\Pearl\miner.log'}
$gpus=@([pscustomobject]@{id=0;cclk=2400;mclk=810;pl=150;fan='65';kernel=2},[pscustomobject]@{id=1;cclk=2400;mclk=810;pl=150;fan='*';kernel=2})
Test-ControlProfile $gpus
$arguments=Get-ControlArgs $config $gpus
Assert-True ($arguments -match '--pl 150,150' -and $arguments -match '--fan 65,\*') 'Command does not match profile'
$script:fakeProcesses=@([pscustomobject]@{ProcessId=10;ParentProcessId=1;ExecutablePath=$config.minerExe;CommandLine=('onezerominer.exe '+$arguments)},[pscustomobject]@{ProcessId=11;ParentProcessId=10;ExecutablePath=$config.minerExe;CommandLine=$null})
function Get-CimInstance { param($ClassName,$Filter) return $script:fakeProcesses }
$family=Get-ManagedMiner $config
Assert-True ($family.Pids.Count -eq 2) 'Packed parent and child were not treated as one launch'
Assert-True (Test-CurrentProfile $family.Root $gpus) 'Current profile not recognized'
$gpus[0].pl=125
Assert-True (-not (Test-CurrentProfile $family.Root $gpus)) 'Changed profile not recognized'
$gpus[0].pl=285
Assert-Throws {Test-ControlProfile $gpus} 'High power limit accepted'
$gpus[0].pl=150; $gpus[0].fan='65;whoami'
Assert-Throws {Test-ControlProfile $gpus} 'Fan command injection accepted'
$gpus[0].fan='65'
$script:fakeProcesses[0].CommandLine=$script:fakeProcesses[0].CommandLine.Replace($wallet,'other-wallet')
Assert-Throws {Get-ManagedMiner $config} 'Foreign wallet was adopted'
$script:fakeProcesses[0].CommandLine='onezerominer.exe '+$arguments.Replace('--api-port 3001','--api-port 4000')
Assert-Throws {Get-ManagedMiner $config} 'Wrong API port was adopted'
Write-Output 'PowerShell control policy checks passed (no GPU or process changes).'
