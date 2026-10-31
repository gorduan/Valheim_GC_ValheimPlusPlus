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
# Seit 1.9.0 feste Abhängigkeit aller Pakete (Gorduan, 30.10.2026): Erklärfenster für jede GC-Funktion (GC_Kern, GcDeepTip)
$deeptip = 'Gorduan-DeepTip-1.0.0'
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
# 26.10.2026): jeder darf sie frei nutzen, verändern und hochladen. Gorduan, 27.10.2026: GC heißt Gorduan Community; jede
# Beschreibung nennt nur die wichtigsten Funktionen des Pakets, die ganze Liste steht auf GitHub (README).
# Gorduan, 29.10.2026: Den freien Platz für weitere Funktionen nutzen und die Mods nennen, mit denen wir zusammenarbeiten.
$freigabe = 'Gorduan Community (GC). Free to use, modify, re-upload (MIT).'
$freigabeDe = 'Gorduan Community (GC). Frei nutzen, ändern, hochladen (MIT).'
$pakete = [ordered]@{
    'GC_Server' = @{
        version = '1.9.0'
        dll = 'GC_Server'
        titel = 'GC Server'
        website = 'https://github.com/gorduan/Valheim_GC_Server'
        description = "Wardrobe outfits, leashes, whistles, saddles, Animal Care, more tameable animals, fields, ladders, Valkyrie carts. With EpicLoot (magic leashes), WAP, PlantEverything. More on GitHub. $freigabe"
        beschreibung = "Kleiderschrank-Outfits, Leinen, Pfeifen, Sättel, Tierpflege, mehr zähmbare Tiere, Felder, Leitern, Walküren-Karren. Mit EpicLoot (magische Leinen), WAP, PlantEverything. Mehr auf GitHub. $freigabeDe"
    }
    'GC_Client' = @{
        version = '1.7.0'
        dll = 'GC_Client'
        titel = 'GC Client'
        website = 'https://github.com/gorduan/Valheim_GC_Client'
        description = "Item comparison (EpicLoot too), compass, sorting, carry limit, trash, auto doors, area harvest, dungeon map. GC Option menu edits every mod's settings, replaces Mod Menu. More on GitHub. $freigabe"
        beschreibung = "Ausrüstungs-Vergleich (auch EpicLoot), Kompass, Sortieren, Traglimit, Vernichten, Auto-Türen, Umkreis-Ernte, Dungeonkarte. Menü GC Option für alle Mods, ersetzt Mod Menu. Mehr auf GitHub. $freigabeDe"
    }
    'ValheimPlusPlus' = @{
        version = '1.6.0'
        dll = 'GC_ValheimPlusPlus'
        titel = 'ValheimPlusPlus'
        website = 'https://github.com/gorduan/Valheim_GC_ValheimPlusPlus'
        description = "Extends ValheimPlus: item and chest locks, linked chests, chests that feed smelters, auto-stack for armor and EpicLoot items. Also tree regrowth and less hunger at base. More on GitHub. $freigabe"
        beschreibung = "Erweitert ValheimPlus: Item- und Kistenschlösser, verbundene Kisten, Kisten für Schmelzen, Auto-Stapeln auch für Rüstung und EpicLoot. Dazu Baum-Nachwuchs, weniger Hunger daheim. Mehr auf GitHub. $freigabeDe"
    }
}

# Je Mod ein Satz für die README (Gorduan, 27.10.2026: auf GitHub soll alles genannt sein, Thunderstore nennt nur das Wichtigste)
$featuresEn = @{
    'kern' = 'GC Option menu for all GC mods (pause and main menu), switch single mods off, server level, mod check on joining, optional boss progression. Explanations for every feature in DeepTip windows.'
    'outfit' = 'A wardrobe (workbench, 10 fine wood, 10 bronze nails) holds your outfit: put gear in and your character shows its look, while stats stay those of the worn pieces. The outfit is kept on death, armor pieces can be hidden.'
    'portalnamen' = 'List of portal names when renaming a portal: your own list and, if the server has the mod, the server list.'
    'portalschutz' = 'Asks before a portal is renamed and remembers the previous name.'
    'felder' = 'Draw fields with the cultivator and plant them automatically. The server stores the fields and sets the rules.'
    'blitzspule' = 'Up to three lightning coils at the Obliterator divert its lightning and return 25, 55 or 85 % of the crafting materials, EpicLoot items also 50-80 % of their sacrifice products.'
    'futterfolge' = 'Tamed animals see food twice as far when a path leads there. The highest level eats first, pregnant animals last.'
    'leiter' = 'Buildable ladder pieces (1 m) with a fixed front. Stack them to any height, E takes you to the top or bottom.'
    'parade' = 'A perfect parry throws arrows and projectiles back.'
    'spitzhacke' = 'Pickaxes strike like a mace with the middle mouse button and work as a weapon.'
    'zuchtgrenze' = 'More animals per area before breeding stops. With GC_Tiere, leashed animals count half.'
    'tierarten' = 'Higher max levels per species (lox 300, wolf and bear 200), stars at birth, more loot, more tameable animals (bear, deer, hare, seal, Neck), bear cub and Unbjorn, bear and Unbjorn saddles, juveniles that fight.'
    'tiere' = 'Minecraft-like leashes (one per biome) with posts and portal travel, four whistles to command your animals, the Animal Care skill, animal experience and breeding levels. With EpicLoot: enchantable leashes, whistles and saddles, including unique items.'
    'walkuerentransport' = 'The Valkyrie whistle calls a valkyrie that picks up your cart for 20 lox meat and drops it near you later, followed by a raid. Includes an easter egg.'
    'vergleich' = 'Shows next to the tooltip what you are wearing: gains in green, losses in red.'
    'kompass' = 'Compass at the top of the screen with map markers, other players and a golden target set with Alt+left click on the map.'
    'sortieren' = 'Sorts inventory and open chests by button or key. Locked items and the hotbar stay, equal stacks are merged.'
    'kisteleeren' = 'Shift+E on a chest takes everything without opening it.'
    'traglimit' = 'Picking up and Ctrl+click from chests take only as much as you can still carry.'
    'vernichten' = 'Destroy items via a trash slot in the inventory or by key, with confirmation.'
    'tueren' = 'Doors open in front of you and close behind you. They stay shut while enemies are near or during a raid.'
    'umkreisernte' = 'Holding E harvests all plants and pickables of the same kind around you.'
    'einsammeln' = 'Loose items go into your cart while you pull it, or into your ship.'
    'dungeonkarte' = 'Map inside dungeons: small in the corner, large with the map key.'
    'schloesser' = 'White and red locks for items, plus a chest lock against ValheimPlus automation.'
    'kisten' = 'Linked chests show their contents together. The station menu shows smelters and co. and links input and output chests for ValheimPlus. The EpicLoot enchanting table also pays from chests.'
    'stackfix' = 'ValheimPlus auto-stack also stores armor, trinkets and EpicLoot items. The hotbar stays protected. Needs ValheimPlus.'
    'baeume' = 'Tree stumps grow back into trees after a sapling''s growth time.'
    'basisessen' = 'Food drains slower at your workbench and inside a ward.'
}

# Mods, mit denen wir zusammenarbeiten oder deren Funktionen wir erweitern (Gorduan, 29.10.2026: „muss in der
# Modbeschreibung drin stehen“). Je fremde Mod der Link und je GC-Mod ein Satz; die README eines Pakets nennt nur die
# Zeilen seiner Mods. Alles im Code belegt (29.10.2026). GC_Guilds und Guilds_* fehlen, weil sie nicht öffentlich sind.
$compatEn = [ordered]@{
    'EpicLoot' = @{
        url = 'https://thunderstore.io/c/valheim/p/RandyKnapp/EpicLoot/'
        uses = [ordered]@{
            'kern' = 'Optional boss progression counts EpicLoot as at least +10 %.'
            'vergleich' = 'Compares with EpicLoot''s magic values and replaces its Ctrl comparison while active.'
            'sortieren' = 'Enchanted items are never merged.'
            'schloesser' = 'Locked items are protected in every enchanting-table window that consumes or strips an item (sacrifice, disenchant, identify, runes, sets); red locks never pay costs. EpicLoot''s socket windows show no locks.'
            'kisten' = 'The enchanting table also pays from chests: like ValheimPlus Craft from Chest, or only from input chests chosen with Shift+E at the table.'
            'stackfix' = 'Auto-stack also stores enchanted gear and EpicLoot materials.'
            'blitzspule' = 'Enchanted items also return 50-80 % of their sacrifice products.'
            'parade' = 'Deflecting also works with EpicLoot''s parry window effect.'
            'tiere' = 'Leashes, whistles and all saddles (also those of GC_Tierarten) can be enchanted with their own effects, including unique legendary and mythic items.'
            'walkuerentransport' = 'Carried items keep their enchantments.'
        }
    }
    'ValheimPlus' = @{
        url = 'https://thunderstore.io/c/valheim/p/Grantapher/ValheimPlus_Grantapher_Temporary/'
        uses = [ordered]@{
            'kern' = 'Shows the ValheimPlus settings in the GC Option menu; change them in the main menu.'
            'schloesser' = 'Locked items and locked chests are skipped by Craft from Chest, auto-stack and machines that fetch fuel or ore.'
            'kisten' = 'Linked chests count as one chest while open. The station menu links input and output chests to smelters and kilns. With EpicLoot, Craft from Chest also works at the enchanting table.'
            'stackfix' = 'Fixes auto-stack so that armor, shields and trinkets are stored again.'
            'basisessen' = 'Its food duration setting still applies, less hunger at base comes on top.'
            'blitzspule' = 'Coils still need an Obliterator, even if ValheimPlus allows free placement.'
        }
    }
    'World Advancement Progression' = @{
        url = 'https://thunderstore.io/c/valheim/p/VentureValheim/World_Advancement_Progression/'
        uses = [ordered]@{
            'kern' = 'Boss progression counts the bosses each player has defeated (private keys).'
            'tiere' = 'Legendary and mythic leashes and whistles check the bosses each player has defeated.'
        }
    }
    'PlantEverything' = @{
        url = 'https://thunderstore.io/c/valheim/p/Advize/PlantEverything/'
        uses = [ordered]@{
            'kern' = 'Its texts come back after changing the language in game.'
            'felder' = 'Fields use their own spacing for its berry bushes and other plants without growth rules.'
            'umkreisernte' = 'Also knows the plants PlantEverything adds.'
        }
    }
    'PlantEasily' = @{
        url = 'https://thunderstore.io/c/valheim/p/Advize/PlantEasily/'
        uses = [ordered]@{
            'felder' = 'Fields stop planting automatically while PlantEasily is installed (it plants whole grids itself).'
        }
    }
    'ProtectiveWards' = @{
        url = 'https://thunderstore.io/c/valheim/p/shudnal/ProtectiveWards/'
        uses = [ordered]@{
            'basisessen' = 'Its food drain multiplier still applies, less hunger at base comes on top.'
        }
    }
    'Mod Menu' = @{
        url = 'https://thunderstore.io/c/valheim/p/Dismonder/ModMenu/'
        uses = [ordered]@{
            'kern' = 'The GC Option menu replaces it: switch any mod on or off, with profiles and dependencies. Its list and profiles are taken over once.'
        }
    }
    'All other mods' = @{
        url = ''
        uses = [ordered]@{
            'kern' = 'The GC Option menu shows and edits the settings of every installed mod, server rules (Jotunn, ServerSync) only for admins. On joining, the mod check lists every mod that differs from the server.'
        }
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
    $manifest = [ordered]@{ name = $name; version_number = $p.version; website_url = $p.website; description = $p.description; dependencies = @($bepinex, $jotunn, $deeptip) }
    [IO.File]::WriteAllText((Join-Path $folder 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), $utf8)

    # Gorduan, 29.10.2026: Vorn muss stehen, dass jede Funktion einzeln abschaltbar und einstellbar ist.
    $lines = @("# $($p.titel) $($p.version)", '', $p.description, '',
        '**Every feature can be switched off on its own and adjusted.** Each feature in the table below is a mod of its own: switch it off in the GC Option menu (tab Mods, main menu) or with its GUID in `BepInEx/config/GC_Kern/aus.txt`, and change its settings in the GC Option menu (pause and main menu) or in `BepInEx/config/gorduan.gc.<id>.cfg`. Server rules are set by the server.', '',
        '## Included mods', '',
        "All mods of this package are in $($p.dll).dll, next to GC_Kern.dll (the shared core).", '',
        '| Mod | Level | Runs on | Feature |', '|---|---|---|---|')
    $needs = @()
    foreach ($m in $members) {
        $what = English "gck_mod_$($m.id)" $m.titel
        if ($featuresEn.ContainsKey($m.id)) { $what = "**$what**: $($featuresEn[$m.id])" }
        $runs = if ($m.id -eq 'kern') { 'always, on server and clients' } else { $sides[$m.seite] }
        if ($m.braucht.Count -gt 0) {
            $names = ($m.braucht | ForEach-Object { if ($byId[$_]) { $byId[$_].name } else { $_ } }) -join ', '
            $what += " (requires $names)"
            $needs += "$($m.name) requires $names"
        }
        $level = English "gck_stufe$($m.stufe)" $stufen[$m.stufe]
        $lines += "| $($m.name) | $($m.stufe) $level | $runs | $what |"
    }
    # Zusammenarbeit mit anderen Mods: nur die Zeilen der Mods dieses Pakets
    $compat = @()
    foreach ($other in $compatEn.Keys) {
        $entry = $compatEn[$other]
        $rows = @($members | Where-Object { $entry.uses.Contains($_.id) } | ForEach-Object { "  - $($_.name): $($entry.uses[$_.id])" })
        if ($rows.Count -eq 0) { continue }
        $compat += $(if ($entry.url) { "- **[$other]($($entry.url))**" } else { "- **$other**" })
        $compat += $rows
    }
    if ($compat.Count -gt 0) {
        $lines += @('', '## Compatibility', '', 'Works together with these mods and extends some of them. All of them are optional, unless the table above says a mod requires one.', '') + $compat
    }
    $lines += @('', '## Rules', '',
        '- **Requires [DeepTip](https://thunderstore.io/c/valheim/p/Gorduan/DeepTip/)** (since 1.9.0): every GC feature explains itself in DeepTip windows. Rest the mouse on a function name in the GC Option menu or on a highlighted term in a tooltip.',
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
    $lines += @('', '## License', '', "MIT. Source code: $($p.website). Gorduan Community (GC): free for anyone to use, modify and re-upload.", '', 'Author: Gorduan.')
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
