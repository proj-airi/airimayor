param(
    [Parameter(Mandatory = $true)]
    [string]$DeployDir,

    [string]$GameManaged = 'C:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Managed',

    [string]$IlRepackPath = (Join-Path $env:USERPROFILE '.dotnet\tools\ilrepack.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $IlRepackPath)) {
    throw "ilrepack not found at $IlRepackPath; install with: dotnet tool install -g dotnet-ilrepack"
}

$mainDll = Join-Path $DeployDir 'airimayor.dll'
if (-not (Test-Path -LiteralPath $mainDll)) {
    throw "Main mod dll missing: $mainDll"
}

# The game's mod manager scans every dll in the mod folder and can hit a
# registration-time race when the folder is full of dependency assemblies
# (ExecutableAsset.GetModAssets calls Assembly.Location on every loaded
# assembly and Mono throws for dynamic ones). Keep the folder lean: merge
# all managed dependencies into the single mod dll.
$managedRefs = @(
    Get-ChildItem -LiteralPath $DeployDir -Filter '*.dll' -File |
        Where-Object Name -ne 'airimayor.dll' | ForEach-Object Name
)
if ($managedRefs.Count -eq 0) {
    exit 0
}

$inputs = @($mainDll)
foreach ($name in $managedRefs) {
    $path = Join-Path $DeployDir $name
    # ACP and the game use Json.NET 13; prefer the game's assembly over the SDK's older copy.
    if ($name -eq 'Newtonsoft.Json.dll') {
        $path = Join-Path $GameManaged $name
    }
    $inputs += $path
}

# BCL assemblies the merged dll still references but the game's Managed
# folder does not ship. Pull them from the NuGet cache so the mod stays a
# single self-contained dll.
$facades = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netframework.referenceassemblies.net48\1.0.3\build\.NETFramework\v4.8\Facades'
$stubProject = Join-Path $PSScriptRoot 'Stubs\WindowsBaseStub.csproj'
dotnet build $stubProject -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
    throw "WindowsBase stub build failed with exit code $LASTEXITCODE"
}
$extraRefs = @(
    (Join-Path $env:USERPROFILE '.nuget\packages\system.valuetuple\4.6.2\lib\net47\System.ValueTuple.dll'),
    (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netframework.referenceassemblies.net48\1.0.3\build\.NETFramework\v4.8\System.ComponentModel.DataAnnotations.dll'),
    (Join-Path $facades 'System.Reflection.Emit.dll'),
    (Join-Path $facades 'System.Reflection.Emit.ILGeneration.dll'),
    (Join-Path $facades 'System.Reflection.Emit.Lightweight.dll'),
    (Join-Path $PSScriptRoot 'Stubs\bin\Release\net48\WindowsBase.dll')
)
foreach ($path in $extraRefs) {
    if (Test-Path -LiteralPath $path) {
        $inputs += $path
    }
    else {
        Write-Warning "Extra merge input not found: $path"
    }
}

# ILRepack names the output assembly after the /out file name, so the temp
# output must keep the real file name; a guid file name would leak into the
# shipped assembly identity.
$mergeDir = Join-Path $env:TEMP ("airimayor-merge-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $mergeDir | Out-Null
$merged = Join-Path $mergeDir 'airimayor.dll'

$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
$repackArgs = @(
    ('/out:' + $merged),
    '/target:library',
    '/internalize',
    ('/lib:' + $DeployDir),
    ('/lib:' + $GameManaged)
) + $inputs

& $IlRepackPath $repackArgs
if ($LASTEXITCODE -ne 0) {
    throw "ilrepack failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath $merged -Destination $mainDll -Force
if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($mergeDir)) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')) {
    throw "Merge cleanup target is outside the temporary directory: $mergeDir"
}
Remove-Item -LiteralPath $mergeDir -Recurse -Force

# The merged dll embeds everything; drop the now-redundant dependency dlls and
# the stale pdbs so the mod folder stays as small as possible.
foreach ($name in $managedRefs) {
    $path = Join-Path $DeployDir $name
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}
foreach ($name in @('airimayor.pdb', 'AgentRuntime.pdb')) {
    $pdb = Join-Path $DeployDir $name
    if (Test-Path -LiteralPath $pdb) {
        Remove-Item -LiteralPath $pdb -Force
    }
}

Write-Output "Merged dependencies into $mainDll"
