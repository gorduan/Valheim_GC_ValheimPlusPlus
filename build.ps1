# Baut alle GC-Mods und packt sie in die Pakete GC_Server, GC_Client und ValheimPlusPlus. Seit 26.10.2026 (Gorduan: „setze die
# drei Mods wieder zusammen“, wegen Thunderstore) liegen die Mods eines Pakets zusammen in einer DLL (GC_Server.dll,
# GC_Client.dll, GC_ValheimPlusPlus.dll; zusammengeführt von Pakete\<Paket>\*.csproj), daneben GC_Kern.dll und der Vorlader.
# Jede Mod bleibt ein eigenes Plugin und lässt sich im Menü GC Option (Reiter Mods) oder über aus.txt einzeln ausschalten.
# GC_Guilds baut weiter build-gc.ps1 im alten Baum.
# Je Paket: manifest.json, README.md, icon.png; ein Zip für r2modman („Import local mod“) und Thunderstore; dazu katalog.json.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Ausgabe (neben dem Repo, wenn dort ein Ordner build-output liegt, sonst im Repo; wie GcPackageOut in Directory.Build.props):
#   build-output\GCMods\Gorduan-<Paket>\            Paketordner (DLLs, manifest.json, README.md, icon.png)
#   build-output\GCMods-zips\Gorduan-<Paket>-<Version>.zip
#   build-output\GCMods-zips\katalog.json
# Die öffentlichen Repos (Valheim_GC_Server, Valheim_GC_Client, Valheim_GC_ValheimPlusPlus, seit 25.10.2026) enthalten je ein
# Paket; Pakete, zu denen keine Mod außer dem Kern gehört, überspringt das Skript.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$outBase = if (Test-Path (Join-Path $root '..\build-output')) { Join-Path $root '..\build-output' } else { Join-Path $root 'build-output' }
$out = [IO.Path]::GetFullPath((Join-Path $outBase 'GCMods'))
$zips = [IO.Path]::GetFullPath((Join-Path $outBase 'GCMods-zips'))
$utf8 = New-Object System.Text.UTF8Encoding($false)
$bepinex = 'denikson-BepInExPack_Valheim-5.4.2350'
$jotunn = 'ValheimModding-Jotunn-2.30.2'
$stufen = @{ 0 = 'Kern'; 1 = 'Sehr Vanilla'; 2 = 'Vanilla'; 3 = 'Vanilla+'; 4 = 'GC leicht'; 5 = 'GC' }
$seiten = @{
    'Spieler' = 'nur beim Spieler'
    'ServerOptional' = 'beim Spieler, mit Server-Teil'
    'Alle' = 'Server und alle Spieler'
}

# Thunderstore auf Englisch (Gorduan, 25.10.2026): manifest.json und README.md englisch, Mod- und Stufennamen aus der englischen
# Übersetzung des Kerns, damit sie zum Spiel passen. Der Katalog bleibt deutsch.
$sides = @{
    'Spieler' = 'client only'
    'ServerOptional' = 'client, optional server part'
    'Alle' = 'server and all clients'
}
$english = @{}
foreach ($line in Get-Content (Join-Path $root 'Kern\translations\kern.English.yml') -Encoding UTF8) {
    if ($line -match '^([A-Za-z0-9_]+):\s*"(.*)"\s*$') { $english[$Matches[1]] = $Matches[2] }
}
function English([string]$key, [string]$fallback) { if ($english.ContainsKey($key)) { $english[$key] } else { $fallback } }

# Die Pakete: Version (einmal je Veröffentlichung erhöhen, ab der Version im Profil Default), dll = Name der Mod-DLL des
# Pakets, description für manifest.json und README (englisch, Thunderstore nimmt höchstens 250 Zeichen), beschreibung für
# den Katalog (deutsch). Beide Texte stehen auch in Valheim\Thunderstore-Beschreibungen.md. Freigabe-Satz (Gorduan,
# 26.10.2026): Die Mods gehören zum GC-Discord-Server, jeder darf sie frei nutzen, verändern und hochladen.
$freigabe = "Part of the GC Discord server's mods. Free for anyone to use, modify and re-upload (MIT)."
$freigabeDe = 'Teil der Mods des GC-Discord-Servers. Jeder darf sie frei nutzen, verändern und hochladen (MIT).'
$pakete = [ordered]@{
    'GC_Server' = @{
        version = '1.9.0'
        dll = 'GC_Server'
        titel = 'GC Server'
        website = 'https://github.com/gorduan/Valheim_GC_Server'
        description = "Gameplay: animal leashes, whistles, Animal Care skill, tameable animals with saddles, Valkyrie cart transport, cultivator fields, ladders, portal name lists. $freigabe"
        beschreibung = "Spielmechanik für Server und Spieler: Tierleinen, Tierpfeifen, Skill Tierpflege, zähmbare Tiere mit Sätteln, Walküren-Transport, Felder, Leitern, Portalnamen. $freigabeDe"
    }
    'GC_Client' = @{
        version = '1.7.0'
        dll = 'GC_Client'
        titel = 'GC Client'
        website = 'https://github.com/gorduan/Valheim_GC_Client'
        description = "Client-side quality of life: equipment comparison, compass, sorting, auto doors, area harvest, carry limit, trash, dungeon map, GC Option settings menu. $freigabe"
        beschreibung = "Komfort für Spieler: Ausrüstungs-Vergleich, Kompass, Sortieren, automatische Türen, Umkreis-Ernte, Traglimit, Vernichten, Dungeon-Karte, Menü GC Option. $freigabeDe"
    }
    'ValheimPlusPlus' = @{
        version = '1.6.0'
        dll = 'GC_ValheimPlusPlus'
        titel = 'ValheimPlusPlus'
        website = 'https://github.com/gorduan/Valheim_GC_ValheimPlusPlus'
        description = "Add-ons for ValheimPlus: item locks, linked chests with building overview, auto-stack fix for EpicLoot items, regrowing trees, slower food drain at base. $freigabe"
        beschreibung = "Ergänzungen für ValheimPlus: Schlösser, verbundene Kisten mit Gebäude-Übersicht, Auto-Stack-Fix für EpicLoot-Items, nachwachsende Bäume, weniger Hunger in der Basis. $freigabeDe"
    }
}

New-Item -ItemType Directory -Force -Path $out, $zips | Out-Null
# Vorlader (patchers) entstehen bei jedem Bau neu; alte Stände raus, sonst lädt BepInEx zwei davon (25.10.2026)
Get-ChildItem $out -Directory | ForEach-Object { $p = Join-Path $_.FullName 'patchers'; if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
$solution = Get-ChildItem $root -Filter '*.slnx' | Select-Object -First 1  # GCMods.slnx, in den öffentlichen Repos <Repo>.slnx
dotnet build $solution.FullName -c Release -nologo -v:q | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Host 'FEHLGESCHLAGEN' -ForegroundColor Red; exit 1 }

# Prüfung der Paket-DLLs (nur Metadaten, nichts wird ausgeführt): alle Plugins der Mods mit der Paketversion drin, und die
# Patch-Liste jeder Mod (GcMod.PatchTypes aus Directory.Build.targets) gleich den [HarmonyPatch]-Klassen ihrer eigenen DLL.
# ReflectionOnly gibt es nur in Windows PowerShell 5.1 (powershell.exe); unter pwsh entfällt die Prüfung mit Hinweis.
$inspect = $PSVersionTable.PSEdition -ne 'Core'
if ($inspect) {
    $paths = (dotnet msbuild (Join-Path $root 'Kern\GC_Kern.csproj') -nologo -getProperty:Managed -getProperty:GamePath -getProperty:JotunnDll | ConvertFrom-Json).Properties
    $searchDirs = @($paths.Managed, (Join-Path $paths.GamePath 'BepInEx\core'), (Split-Path $paths.JotunnDll), (Join-Path $root 'Kern\bin')) +
        @(Get-ChildItem (Join-Path $root 'Mods') -Directory | ForEach-Object { Join-Path $_.FullName 'bin' })
    [AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve([ResolveEventHandler]{ param($s, $e)
        $file = (New-Object Reflection.AssemblyName($e.Name)).Name + '.dll'
        foreach ($d in $searchDirs) { $p = Join-Path $d $file; if (Test-Path $p) { return (LoadMeta $p) } }
        return [Reflection.Assembly]::ReflectionOnlyLoad($e.Name) })
}
# Aus den Bytes laden: ReflectionOnlyLoadFrom sperrt die Datei bis zum Ende des Skripts, dann scheitert Compress-Archive
# (26.10.2026). Jede Assembly nur einmal, sonst meldet die CLR sie als schon geladen.
function LoadMeta([string]$path) {
    $name = [Reflection.AssemblyName]::GetAssemblyName($path).FullName
    $loaded = [AppDomain]::CurrentDomain.ReflectionOnlyGetAssemblies() | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
    if ($loaded) { return $loaded }
    return [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($path))
}
function TypesOf([string]$path) {
    $asm = LoadMeta $path
    try { $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $_.Exception.Types | Where-Object { $_ } }
}
function Attributes($member, [string]$pattern) {
    try { @([Reflection.CustomAttributeData]::GetCustomAttributes($member) | Where-Object { $_.AttributeType.FullName -like $pattern }) } catch { @() }
}
function Test-Package([string]$dll, $members, [string]$version) {
    $plugins = @{}
    foreach ($t in TypesOf $dll) { foreach ($a in Attributes $t 'BepInEx.BepInPlugin') { $plugins[[string]$a.ConstructorArguments[0].Value] = [string]$a.ConstructorArguments[2].Value } }
    foreach ($m in $members) {
        $guid = 'gorduan.gc.' + $m.id
        if (-not $plugins.ContainsKey($guid)) { return "Plugin $guid fehlt in $(Split-Path $dll -Leaf)" }
        if ($plugins[$guid] -ne $version) { return "Plugin $guid hat Version $($plugins[$guid]), erwartet $version" }
        $listFile = Join-Path $m.dir 'obj\Release\GcPatchTypes.txt'
        $listed = @(if (Test-Path $listFile) { Get-Content $listFile -Encoding UTF8 | Where-Object { $_ } }) | Sort-Object
        $actual = @(TypesOf (Join-Path $m.dir "bin\$($m.name).dll") | Where-Object { (Attributes $_ 'HarmonyLib.HarmonyPatch*').Count -gt 0 } | ForEach-Object { $_.FullName }) | Sort-Object
        if (($listed -join '|') -ne ($actual -join '|')) { return "Patch-Liste von $($m.name) passt nicht zur DLL (GcMod.PatchTypes $(@($listed).Count), DLL $(@($actual).Count))" }
    }
    if ($plugins.Count -ne @($members).Count) { return "$(Split-Path $dll -Leaf) hat $($plugins.Count) Plugins, erwartet $(@($members).Count)" }
    return ''
}

function Prop($xml, [string]$name) {
    foreach ($group in $xml.Project.PropertyGroup) {
        $value = $group.$name
        if ($value) { return [string]$value }
    }
    return ''
}

function List([string]$text) {
    if (-not $text) { return @() }
    return @($text -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$projects = @(Get-Item (Join-Path $root 'Kern\GC_Kern.csproj')) + @(Get-ChildItem (Join-Path $root 'Mods') -Directory | ForEach-Object { Get-ChildItem $_.FullName -Filter '*.csproj' })
$mods = [ordered]@{}
foreach ($project in $projects) {
    $xml = [xml](Get-Content $project.FullName -Raw -Encoding UTF8)
    $asm = Prop $xml 'AssemblyName'
    $id = Prop $xml 'GcId'
    if ($asm -eq 'GC_Kern') {
        # BepInEx liest die Version aus GcKern.Version, nicht aus der csproj (25.10.2026: Kern meldete nach dem Erhöhen weiter 1.0.0)
        $constant = [regex]::Match((Get-Content (Join-Path $root 'Kern\Api\GcKern.cs') -Raw -Encoding UTF8), 'const string Version = "([^"]+)"').Groups[1].Value
        if ($constant -ne (Prop $xml 'Version')) { Write-Host "GcKern.Version ($constant) passt nicht zu GC_Kern.csproj ($(Prop $xml 'Version'))" -ForegroundColor Red; exit 1 }
        $mods[$asm] = [ordered]@{
            id = 'kern'; name = $asm; dir = $project.DirectoryName; version = (Prop $xml 'Version'); stufe = 0; seite = 'Spieler'
            braucht = @(); partner = @(); fremd = @(); ausgleich = 0; pakete = List (Prop $xml 'GcPaket')
            titel = 'GC-Kern'
            beschreibung = 'Laufzeit des GC-SDK, braucht jede GC-Mod: Menü GC Option nach Stufen, Einstellungen, Texte, Rechte, Stufe des Servers, Boss-Fortschritt und Mod-Abgleich. Liegt in jedem Paket; BepInEx lädt ihn einmal.'
        }
        continue
    }
    if (-not $id) { continue }
    $mods[$asm] = [ordered]@{
        id = $id; name = $asm; dir = $project.DirectoryName; version = (Prop $xml 'Version'); stufe = [int](Prop $xml 'GcStufe'); seite = (Prop $xml 'GcSeite')
        braucht = List (Prop $xml 'GcBraucht'); partner = List (Prop $xml 'GcPartner'); fremd = List (Prop $xml 'GcFremd')
        ausgleich = [double]((Prop $xml 'GcAusgleich') -replace ',', '.'); pakete = List (Prop $xml 'GcPaket')
        titel = (Prop $xml 'GcTitel'); beschreibung = (Prop $xml 'GcBeschreibung')
    }
}
$byId = @{}
foreach ($mod in $mods.Values) { $byId[$mod.id] = $mod }

$catalog = @()
$packageInfo = @()
foreach ($name in $pakete.Keys) {
    $p = $pakete[$name]
    $folder = Join-Path $out ('Gorduan-' + $name)
    $members = @($mods.Values | Where-Object { $_.pakete -contains $name } | Sort-Object { $_.stufe }, { $_.name })
    if (-not ($members | Where-Object { $_.id -ne 'kern' })) { continue }  # öffentliches Repo eines anderen Pakets: nur der Kern
    $modsOnly = @($members | Where-Object { $_.id -ne 'kern' })
    foreach ($m in $modsOnly) {
        if ($m.version -ne $p.version) { Write-Host "Version von $($m.name) ($($m.version)) passt nicht zu $name $($p.version)" -ForegroundColor Red; exit 1 }
    }
    # Im Paket: die Mod-DLL und GC_Kern.dll. Alte Einzel-DLLs der Mods (bis 1.8.1) und andere Reste kommen nicht mit.
    $expected = @(($p.dll + '.dll'), 'GC_Kern.dll')  # Klammern: das Komma bindet stärker als +
    foreach ($file in $expected) { if (-not (Test-Path (Join-Path $folder $file))) { Write-Host "fehlt: $name\$file" -ForegroundColor Red; exit 1 } }
    foreach ($dll in Get-ChildItem $folder -Filter '*.dll') {
        if ($expected -notcontains $dll.Name) { Write-Host "  nicht im Paket, übersprungen: $name\$($dll.Name)" -ForegroundColor Yellow; Remove-Item $dll.FullName }
    }
    if ($inspect) {
        $problem = Test-Package (Join-Path $folder ($p.dll + '.dll')) $modsOnly $p.version
        if ($problem) { Write-Host "$name`: $problem" -ForegroundColor Red; exit 1 }
    } else { Write-Host "  $name`: Prüfung der DLL übersprungen (nur mit Windows PowerShell 5.1)" -ForegroundColor Yellow }

    # Thunderstore nimmt höchstens 250 Zeichen Beschreibung (Prüfung 24.10.2026)
    if ($p.description.Length -gt 250) { Write-Host "Beschreibung von $name hat $($p.description.Length) Zeichen, höchstens 250" -ForegroundColor Red; exit 1 }
    $manifest = [ordered]@{ name = $name; version_number = $p.version; website_url = $p.website; description = $p.description; dependencies = @($bepinex, $jotunn) }
    [IO.File]::WriteAllText((Join-Path $folder 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), $utf8)

    $lines = @("# $($p.titel) $($p.version)", '', $p.description, '', '## Included mods', '',
        "All mods of this package are in $($p.dll).dll, next to GC_Kern.dll (the shared core). Each mod can be switched off on its own: GC Option menu, tab Mods (main menu), or its GUID in BepInEx/config/GC_Kern/aus.txt.", '',
        '| Mod | Level | Runs on | Feature |', '|---|---|---|---|')
    $needs = @()
    foreach ($m in $members) {
        $what = English "gck_mod_$($m.id)" $m.titel
        $runs = if ($m.id -eq 'kern') { 'always, on server and clients' } else { $sides[$m.seite] }
        if ($m.braucht.Count -gt 0) {
            $names = ($m.braucht | ForEach-Object { if ($byId[$_]) { $byId[$_].name } else { $_ } }) -join ', '
            $what += " (requires $names)"
            $needs += "$($m.name) requires $names"
        }
        $level = English "gck_stufe$($m.stufe)" $stufen[$m.stufe]
        $lines += "| $($m.name) | $($m.stufe) $level | $runs | $what |"
    }
    $lines += @('', '## Rules', '',
        '- **GC_Kern.dll** must always stay. It ships in every GC package so that each package works on its own; BepInEx loads it once and reports the other copies as "duplicate". That is intended.',
        '- **Switching mods off:** mods that run on "server and all clients" must be the same on the server and on every client, otherwise the server refuses the connection. If you switch such a mod off on the server, your players must switch it off too.')
    if ($needs.Count -gt 0) { $lines += '- **Dependencies:** ' + ($needs -join '; ') + '.' }
    $lines += '- **Settings:** GC Option menu (button in the pause and main menu) or `BepInEx/config/gorduan.gc.<id>.cfg`. On first start every mod takes over the values from the old cfg files.'
    # Namensnennung für frei lizenzierte Dateien (CC BY verlangt sie in der README, siehe Mods\WalkuerenTransport\CREDITS.md)
    $credits = @($members | ForEach-Object { Join-Path $_.dir 'CREDITS.md' } | Where-Object { Test-Path $_ })
    if ($credits.Count -gt 0) {
        $lines += @('', '## Credits', '')
        foreach ($file in $credits) { $lines += @(Get-Content $file -Encoding UTF8 | Select-Object -Skip 2) }
    }
    $lines += @('', '## License', '', "MIT, source code: $($p.website). $freigabe", '', 'Author: Gorduan.')
    [IO.File]::WriteAllText((Join-Path $folder 'README.md'), ($lines -join "`n") + "`n", $utf8)
    $icon = Join-Path $root "Pakete\$name\icon.png"
    if (Test-Path $icon) { Copy-Item $icon (Join-Path $folder 'icon.png') -Force }

    # Zip mit / in den Pfaden (ZIP-Norm). Compress-Archive in Windows PowerShell 5.1 schrieb „patchers\GC_ModSchalter_….dll“
    # (Prüfung 26.10.2026, auch in den Zips bis 1.8.1): Unter Linux wäre das eine Datei mit Backslash im Namen statt Ordner.
    $zip = Join-Path $zips ("Gorduan-$name-$($p.version).zip")
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($file in Get-ChildItem $folder -File -Recurse | Sort-Object FullName) {
            $entry = $file.FullName.Substring($folder.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    $packageInfo += [ordered]@{ name = $name; paket = 'Gorduan-' + $name; version = $p.version; titel = $p.titel; beschreibung = $p.beschreibung }
    Write-Host ("{0,-18} {1}  {2} Mods in {3}.dll" -f $name, $p.version, $modsOnly.Count, $p.dll)
}

foreach ($m in ($mods.Values | Sort-Object { $_.stufe }, { $_.name })) {
    $catalog += [ordered]@{
        id = $m.id; name = $m.name; pakete = @($m.pakete | ForEach-Object { 'Gorduan-' + $_ }); version = $m.version; dll = $(if ($m.id -eq 'kern') { 'GC_Kern.dll' } else { $pakete[@($m.pakete)[0]].dll + '.dll' })
        guid = $(if ($m.id -eq 'kern') { 'gorduan.gc.kern' } else { 'gorduan.gc.' + $m.id })
        stufe = $m.stufe; stufe_name = $stufen[$m.stufe]; seite = $m.seite
        braucht = @($m.braucht); partner = @($m.partner); fremd = @($m.fremd); ausgleich = $m.ausgleich
        titel = $m.titel; beschreibung = $m.beschreibung
    }
}
$stamp = Get-Date -Format s
[IO.File]::WriteAllText((Join-Path $zips 'katalog.json'), ([ordered]@{ erzeugt = $stamp; pakete = $packageInfo; mods = $catalog } | ConvertTo-Json -Depth 5), $utf8)

Write-Host "Fertig: $($packageInfo.Count) Pakete in $out, Zips und katalog.json in $zips" -ForegroundColor Green
