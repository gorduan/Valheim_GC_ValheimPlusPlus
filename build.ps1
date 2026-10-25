# Baut alle GC-Mods und packt sie in die Pakete GC_Server, GC_Client und ValheimPlusPlus (Gorduan, 24.10.2026: Es bleiben die
# vier Pakete GC_Client, GC_Server, GC_Guilds und ValheimPlusPlus; in deren Ordner liegen die einzelnen Mods als DLLs, der
# Server-Besitzer entscheidet selbst, welche DLLs er behält). GC_Guilds baut weiter build-gc.ps1 im alten Baum.
# Je Paket: manifest.json, README.md (Liste aller DLLs), icon.png; ein Zip für r2modman („Import local mod“); dazu der
# Katalog katalog.json und das Auswahl-Programm.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Ausgabe (neben dem Repo, wenn dort ein Ordner build-output liegt, sonst im Repo; wie GcPackageOut in Directory.Build.props):
#   build-output\GCMods\Gorduan-<Paket>\            Paketordner (DLLs, manifest.json, README.md, icon.png)
#   build-output\GCMods-zips\Gorduan-<Paket>-<Version>.zip
#   build-output\GCMods-zips\katalog.json
#   build-output\GC-Mod-Auswahl.html                 nur mit Programm\GC-Mod-Auswahl.html (fehlt in den öffentlichen Repos)
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
# Übersetzung des Kerns, damit sie zum Spiel passen. Katalog und Auswahl-Programm bleiben deutsch.
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

# Die Pakete: Version (einmal je Veröffentlichung erhöhen, ab der Version im Profil Default), description für manifest.json
# und README (englisch, Thunderstore nimmt höchstens 250 Zeichen), beschreibung für den Katalog (deutsch), Symbol aus dem
# alten Baum. Beide Texte stehen auch in Valheim\Thunderstore-Beschreibungen.md.
$pakete = [ordered]@{
    'GC_Server' = @{
        version = '1.8.1'
        titel = 'GC Server'
        website = 'https://github.com/gorduan/Valheim_GC_Server'
        description = 'Gameplay for server and players, one DLL per feature: animal leashes, whistles and an Animal Care skill, more tameable animals with saddles, Valkyrie cart transport, cultivator fields, ladders, portal name lists.'
        beschreibung = 'Spielmechanik für Server und Spieler, jede Funktion eine eigene DLL: Tierleinen, Tierpfeifen und Skill Tierpflege, mehr zähmbare Tiere mit Sätteln, Walküren-Transport für Karren, Felder, Leitern, Portalnamen.'
    }
    'GC_Client' = @{
        version = '1.6.1'
        titel = 'GC Client'
        website = 'https://github.com/gorduan/Valheim_GC_Client'
        description = 'Client-side quality of life, one DLL per feature: equipment comparison, compass, sorting, auto doors, area harvest, carry limit, trash slot, dungeon map. Includes GC Option, an in-game settings menu for all mods.'
        beschreibung = 'Komfort für Spieler, jede Funktion eine eigene DLL: Ausrüstungs-Vergleich, Kompass, Sortieren, automatische Türen, Umkreis-Ernte, Traglimit, Vernichten, Dungeon-Karte. Mit GC Option, dem Einstellungsmenü für alle Mods.'
    }
    'ValheimPlusPlus' = @{
        version = '1.5.1'
        titel = 'ValheimPlusPlus'
        website = 'https://github.com/gorduan/Valheim_GC_ValheimPlusPlus'
        description = 'Add-ons for ValheimPlus, one DLL per feature: item locks, linked chests with a building overview, auto-stack fix for EpicLoot items, regrowing trees, slower food drain at your base.'
        beschreibung = 'Ergänzungen für ValheimPlus, jede eine eigene DLL: Schlösser für Items, verbundene Kisten mit Gebäude-Übersicht, Auto-Stack-Fix für EpicLoot-Items, nachwachsende Bäume, weniger Hunger in der Basis.'
    }
}

New-Item -ItemType Directory -Force -Path $out, $zips | Out-Null
# Vorlader (patchers) entstehen bei jedem Bau neu; alte Stände raus, sonst lädt BepInEx zwei davon (25.10.2026)
Get-ChildItem $out -Directory | ForEach-Object { $p = Join-Path $_.FullName 'patchers'; if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
$solution = Get-ChildItem $root -Filter '*.slnx' | Select-Object -First 1  # GCMods.slnx, in den öffentlichen Repos <Repo>.slnx
dotnet build $solution.FullName -c Release -nologo -v:q | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Host 'FEHLGESCHLAGEN' -ForegroundColor Red; exit 1 }

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
    foreach ($m in $members) {
        if (-not (Test-Path (Join-Path $folder ($m.name + '.dll')))) { Write-Host "fehlt: $name\$($m.name).dll" -ForegroundColor Red; exit 1 }
        if ($m.id -ne 'kern' -and $m.version -ne $p.version) { Write-Host "Version von $($m.name) ($($m.version)) passt nicht zu $name $($p.version)" -ForegroundColor Red; exit 1 }
    }
    # DLLs, die nicht mehr zum Paket gehören (z. B. umgezogen), kommen nicht mit
    foreach ($dll in Get-ChildItem $folder -Filter '*.dll') {
        if (-not ($members | Where-Object { ($_.name + '.dll') -eq $dll.Name })) { Write-Host "  nicht im Paket, übersprungen: $name\$($dll.Name)" -ForegroundColor Yellow; Remove-Item $dll.FullName }
    }

    # Thunderstore nimmt höchstens 250 Zeichen Beschreibung (Prüfung 24.10.2026)
    if ($p.description.Length -gt 250) { Write-Host "Beschreibung von $name hat $($p.description.Length) Zeichen, höchstens 250" -ForegroundColor Red; exit 1 }
    $manifest = [ordered]@{ name = $name; version_number = $p.version; website_url = $p.website; description = $p.description; dependencies = @($bepinex, $jotunn) }
    [IO.File]::WriteAllText((Join-Path $folder 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), $utf8)

    $lines = @("# $($p.titel) $($p.version)", '', $p.description, '', '## Included mods', '',
        'Each mod is a separate DLL in this folder. Server owners keep only the ones they want.', '',
        '| File | Level | Runs on | Feature |', '|---|---|---|---|')
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
        $lines += "| $($m.name).dll | $($m.stufe) $level | $runs | $what |"
    }
    $lines += @('', '## Rules', '',
        '- **GC_Kern.dll** must always stay. It ships in every GC package; BepInEx loads it once and reports the other copies as "duplicate". That is intended.',
        '- **Removing DLLs:** mods that run on "server and all clients" must be the same on the server and on every client, otherwise the server refuses the connection. If you remove DLLs, your players must remove the same ones.')
    if ($needs.Count -gt 0) { $lines += '- **Dependencies:** ' + ($needs -join '; ') + '.' }
    $lines += '- **Settings:** GC Option menu (button in the pause and main menu) or `BepInEx/config/gorduan.gc.<id>.cfg`. On first start every mod takes over the values from the old cfg files.'
    # Namensnennung für frei lizenzierte Dateien (CC BY verlangt sie in der README, siehe Mods\WalkuerenTransport\CREDITS.md)
    $credits = @($members | ForEach-Object { Join-Path $_.dir 'CREDITS.md' } | Where-Object { Test-Path $_ })
    if ($credits.Count -gt 0) {
        $lines += @('', '## Credits', '')
        foreach ($file in $credits) { $lines += @(Get-Content $file -Encoding UTF8 | Select-Object -Skip 2) }
    }
    $lines += @('', 'Author: Gorduan. Part of the GC mods for the GC Discord server.')
    [IO.File]::WriteAllText((Join-Path $folder 'README.md'), ($lines -join "`n") + "`n", $utf8)
    $icon = Join-Path $root "Pakete\$name\icon.png"
    if (Test-Path $icon) { Copy-Item $icon (Join-Path $folder 'icon.png') -Force }

    $zip = Join-Path $zips ("Gorduan-$name-$($p.version).zip")
    Compress-Archive -Path (Join-Path $folder '*') -DestinationPath $zip -Force
    $packageInfo += [ordered]@{ name = $name; paket = 'Gorduan-' + $name; version = $p.version; titel = $p.titel; beschreibung = $p.beschreibung }
    Write-Host ("{0,-18} {1}  {2} DLLs" -f $name, $p.version, $members.Count)
}

foreach ($m in ($mods.Values | Sort-Object { $_.stufe }, { $_.name })) {
    $catalog += [ordered]@{
        id = $m.id; name = $m.name; pakete = @($m.pakete | ForEach-Object { 'Gorduan-' + $_ }); version = $m.version; dll = $m.name + '.dll'
        guid = $(if ($m.id -eq 'kern') { 'gorduan.gc.kern' } else { 'gorduan.gc.' + $m.id })
        stufe = $m.stufe; stufe_name = $stufen[$m.stufe]; seite = $m.seite
        braucht = @($m.braucht); partner = @($m.partner); fremd = @($m.fremd); ausgleich = $m.ausgleich
        titel = $m.titel; beschreibung = $m.beschreibung
    }
}
$stamp = Get-Date -Format s
[IO.File]::WriteAllText((Join-Path $zips 'katalog.json'), ([ordered]@{ erzeugt = $stamp; pakete = $packageInfo; mods = $catalog } | ConvertTo-Json -Depth 5), $utf8)

# Auswahl-Programm: eine HTML-Datei mit Katalog und allen Paketdateien (Base64), läuft ohne Installation im Browser
if (Test-Path (Join-Path $root 'Programm\GC-Mod-Auswahl.html')) {
    $files = [ordered]@{}
    foreach ($entry in $packageInfo) {
        $map = [ordered]@{}
        $base = Join-Path $out $entry.paket
        # mit Unterordnern (patchers/…: Vorlader GC_ModSchalter), Pfade mit /
        foreach ($file in Get-ChildItem $base -File -Recurse) {
            $map[$file.FullName.Substring($base.Length + 1).Replace('\', '/')] = [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.FullName))
        }
        $files[$entry.paket] = $map
    }
    $data = [ordered]@{ erzeugt = $stamp; pakete = $packageInfo; mods = $catalog; dateien = $files } | ConvertTo-Json -Depth 6 -Compress
    $template = [IO.File]::ReadAllText((Join-Path $root 'Programm\GC-Mod-Auswahl.html'), $utf8)
    if (-not $template.Contains('/*GC_DATEN*/null')) { Write-Host 'Programm-Vorlage ohne Platzhalter /*GC_DATEN*/null' -ForegroundColor Red; exit 1 }
    $program = Join-Path (Split-Path $out -Parent) 'GC-Mod-Auswahl.html'
    [IO.File]::WriteAllText($program, $template.Replace('/*GC_DATEN*/null', $data), $utf8)
    Write-Host ("Auswahl-Programm: {0} ({1:N1} MB)" -f $program, ((Get-Item $program).Length / 1MB))
}
Write-Host "Fertig: $($packageInfo.Count) Pakete in $out, Zips und katalog.json in $zips" -ForegroundColor Green
