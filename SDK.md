# GC-SDK (Gorduan)

Mit dem SDK entstehen GC-Mods für Valheim (BepInEx, Jötunn). Jede Funktion ist eine eigene Mod mit eigenem Ordner. Alle bauen auf dem **GC_Kern** auf. Stand: 24.10.2026.

## Aufbau

| Teil | Ort | Wozu |
|---|---|---|
| GC_Kern | `Kern\` | Laufzeit des SDK, ohne ihn startet keine GC-Mod (Einzelheiten unten) |
| Mods | `Mods\<Name>\` | eine Funktion je Mod, als eigene DLL in einem Paket (`GcPaket`, siehe „Pakete“) |
| Vorlage | `Vorlage\GC_Beispiel\` | kleinste lauffähige Mod zum Kopieren |
| Bau | `build.ps1` | Mods bauen, Pakete, Zips, Katalog, Auswahl-Programm |
| Auswahl-Programm | `Programm\GC-Mod-Auswahl.html` | Vorlage; `build.ps1` füllt sie zu `build-output\GC-Mod-Auswahl.html` |
| Steckbrief | `Directory.Build.targets` | macht aus den Gc*-Eigenschaften die Plugin-Attribute |

Der Kern bringt mit:
- das Menü **GC Option** (Pause-Menü und Hauptmenü);
- Einstellungen und Texte;
- Rechte und die Stufe des Servers;
- Boss-Fortschritt;
- Mod-Abgleich;
- die Verbindungsstellen zwischen den Mods.

## Pakete

Gorduan, 24.10.2026: Es bleiben die vier Pakete **GC_Server**, **GC_Client**, **GC_Guilds** und **ValheimPlusPlus**; in r2modman kommen nur sie ins Profil.
- In ihren Ordnern liegt jede Mod als eigene DLL. Der Server-Besitzer entscheidet selbst, welche DLLs er behält oder löscht.
- GC_Kern.dll liegt in GC_Server, GC_Client und ValheimPlusPlus. BepInEx lädt ihn einmal und überspringt die anderen Kopien („duplicate“).
- DLLs der Seite „Alle“ (und „ServerOptional“, wenn der Server sie hat) müssen bei Server und Spielern gleich sein. Das Auswahl-Programm baut beides aus einer Auswahl.
- Alle Mods eines Pakets tragen die Version des Pakets; `build.ps1` bricht ab, wenn eine abweicht. Der Kern hat seine eigene Version.
- **Kern geändert:** Kern-Version erhöhen (in `Kern\Api\GcKern.cs` und `Kern\GC_Kern.csproj`, `build.ps1` prüft, dass beide gleich sind) und alle drei Pakete gemeinsam veröffentlichen. BepInEx lädt je GUID die höchste Version, bei gleicher Version die zuerst gefundene Datei; sonst liefe womöglich ein alter Kern aus einem anderen Paket.
- Der Server bekommt alle DLLs, auch die nur beim Spieler laufen: Die Stufe des Servers ist die höchste Stufe seiner GC-Mods.
- GC_Guilds baut weiter `build-gc.ps1` im alten Baum (`mods\GC`).

| Paket | Inhalt |
|---|---|
| GC_Server | Funktionen für Server und alle Spieler: Tiere, Tierarten, Walküren-Transport, Futter-Reihenfolge, Zuchtgrenze, Leiter, Spitzhacke, Parade, Felder, Portalnamen |
| GC_Client | nur beim Spieler: Ausrüstungs-Vergleich, Kompass, Portalschutz, Sortieren, Kiste leeren, Traglimit, Vernichten, Türen, Umkreis-Ernte, Einsammeln, Dungeon-Karte |
| ValheimPlusPlus | Ergänzungen zu ValheimPlus: Schlösser, Auto-Stack-Fix, Kisten, Bäume, Basis-Essen |

## Stufen

Die Stufe sagt, wie weit eine Mod das Spiel von Vanilla entfernt. Im Menü und im Auswahl-Programm steht jede Mod unter ihrer Stufe.

| Stufe | Name | Was sie verändert |
|---|---|---|
| 1 | Sehr Vanilla | nur die Anzeige, keine Spielregel |
| 2 | Vanilla | Handgriffe im Inventar und an Kisten |
| 3 | Vanilla+ | Komfort in der Welt, spart Arbeit |
| 4 | GC leicht | neue Mechaniken und Bauteile, andere Balance |
| 5 | GC | eigene GC-Systeme |

**Stufe des Servers:**
- Mods der Seite „Spieler“ über dieser Stufe schalten sich auf dem Server ab. `GcModule.Allowed` ist dann false.
- Standard ist die höchste Stufe der GC-Mods auf dem Server. Admins stellen sie im Menü unter GC-Kern → „Stufe für Spieler-Mods“ ein.
- Auf Servern ohne GC_Kern gilt keine Grenze.

## Seite: wo eine Mod laufen muss

| GcSeite | Jötunn | Bedeutung |
|---|---|---|
| `Spieler` | NotEnforced | Läuft nur beim Spieler. Der Server braucht sie nicht. |
| `ServerOptional` | ClientMustHaveMod | Hat der Server die Mod, brauchen alle sie. Ohne Server-Teil fragt die Mod `Module.OnServer` ab und lässt diese Teile aus. |
| `Alle` | EveryoneMustHaveMod | Server und alle Spieler brauchen genau diese Mod, sonst lehnt Jötunn die Verbindung ab. |

## Steckbrief in der .csproj

| Eigenschaft | Beispiel | Bedeutung |
|---|---|---|
| `GcId` | `tiere` | ID; GUID wird `gorduan.gc.tiere` |
| `GcStufe` | `5` | 1 bis 5 |
| `GcSeite` | `Alle` | siehe oben |
| `GcBraucht` | `tiere` | harte Abhängigkeit: ohne sie startet die Mod nicht (BepInEx) |
| `GcPartner` | `dungeonkarte` | weiche Abhängigkeit: mit ihr kann die Mod mehr, sie lädt danach |
| `GcFremd` | `randyknapp.mods.epicloot` | fremde Mods, mit denen sie mehr kann (weiche Abhängigkeit) |
| `GcAlteConfig` | `gorduan.gc_server.cfg` | alte cfg-Datei(en), aus denen der erste Start die Werte übernimmt |
| `GcAusgleich` | `5` | Schwierigkeitsausgleich in % je besiegtem Boss, solange die Mod läuft |
| `GcPaket` | `GC_Server` | Paket, in dessen Ordner die DLL kommt (`GC_Server`, `GC_Client` oder `ValheimPlusPlus`); `Version` = Version des Pakets |
| `GcTitel`, `GcBeschreibung` | | für manifest.json, README, Katalog und Auswahl-Programm |

Daraus erzeugt das SDK `obj\GcModInfo.g.cs`:
- an der Klasse `Plugin` (partial) die Attribute `[BepInPlugin]`, `[BepInDependency]` und `[NetworkCompatibility]`;
- dazu die Klasse `GcMod` mit `Guid`, `Name`, `Version`, `Stufe` und `Info`.

## Eine neue Mod

1. `Vorlage\GC_Beispiel` nach `Mods\<Name>` kopieren, die .csproj in `GC_<Name>.csproj` umbenennen, die Gc*-Eigenschaften anpassen.
2. `dotnet sln GCMods.slnx add Mods\<Name>\GC_<Name>.csproj`
3. `powershell -ExecutionPolicy Bypass -File build.ps1`

Grundmuster in `Plugin.cs`:

```csharp
public partial class Plugin : BaseUnityPlugin
{
	internal static GcModule Module = null!;

	private void Awake()
	{
		Module = GcKern.Register(this, Logger, GcMod.Info);      // als Erstes, vor jedem Config.Bind
		Enabled = Gc.Bind(Config, "1 - Abschnitt", "Enabled", true, "…");          // nur für den Spieler
		Regel = Gc.BindSynced(Config, "1 - Abschnitt", "Regel", 5f, "…");          // vom Server, nur Admins
		Module.TakeOverOldSettings();                             // erster Start: alte Werte übernehmen
		Module.IsOn = () => Enabled.Value;                        // Hauptschalter (Boss-Fortschritt)
		new Harmony(GcMod.Guid).PatchAll(typeof(Plugin).Assembly);
		Module.MenuGroup(key => GcLoc.Get("meinprefix_" + key))
			.Main("enabled", Enabled)
			.Sub("regel", Regel, 1, " %");
	}

	private void Update() => Module.Guard("Teil", MeinTeil.Update);  // Fehler je Teil nur einmal ins Log
}
```

In Patches und Updates gilt:
- `Module.Allowed` (Stufe des Servers) und bei Server-Teilen `Module.OnServer` prüfen.
- Auf dem dedizierten Server (`Gc.IsHeadless`) gibt es keine Oberfläche.

## Kern-Schnittstelle

| Teil | Wozu |
|---|---|
| `GcKern.Register(plugin, log, info)` | Anmeldung; liefert `GcModule` |
| `GcModule.Allowed` | darf hier wirken (Stufe des Servers) |
| `GcModule.OnServer` | Server hat die Mod (eigene Welt und Server selbst: ja, Hauptmenü: nein) |
| `GcModule.IsOn` / `On` | Hauptschalter / freigegeben und an |
| `GcModule.Guard(teil, aktion)` | Fehler eines Teils halten den Rest nicht an |
| `GcModule.MenuGroup(text, titleKey)` | Gruppe im Menü, im Abschnitt der Stufe |
| `GcModule.TakeOverOldSettings()` | Werte aus `GcAlteConfig` übernehmen (nur beim ersten Start) |
| `GcKern.ModLoaded(id)`, `GcKern.Server`, `GcKern.ServerStufe` | Abfragen |
| `GcLoc.Get(schlüssel, args…)` | Texte; Platzhalter `$1`, `$2` |
| `Gc.Bind`, `Gc.BindSynced`, `Gc.Own`, `Gc.Hovered<T>`, `Gc.TopLeft` … | Hilfen |
| `GcKeys` | gemeinsame Schlüssel (Schlösser, Kisten, Leine, Portale, Leichengröße) |
| `GcHooks` | Verbindungsstellen zwischen Mods (siehe unten) |
| `Gcm.Read("datei.gcm")` | Blender-Modell (Format GCM1) aus den eingebetteten Dateien aller GC-Mods |
| `GcUi` | Fenster, Knöpfe, Listen im Valheim-Stil (Jötunn) |

## Menü GC Option

- `Main(key, eintrag, …)` ist eine Funktion, `Sub(…)` eine eingerückte Unteroption.
- Texte holt das Menü über die Text-Funktion der Gruppe:
  - `t_<key>_name` und `t_<key>_desc` für Name und Beschreibung;
  - `enum_<Wert>` für Werte einer Aufzählung.
- Optionen:

  | Option | Wirkung |
  |---|---|
  | `step`, `unit` | Schrittweite und Einheit |
  | `note` | Zusatztext, der gerade gilt |
  | `requires` | Zeile nur zeigen, wenn das zutrifft, z. B. eine Mod ist da |
  | `locked` | sichtbar, aber gerade nicht änderbar |
  | `blocked` | alte Einzelmod läuft noch |

- **Wer was sieht** (Gorduan, 25.10.2026):
  - Ohne das Kästchen „Server-Regeln anzeigen“ sieht jeder, auch ein Admin, nur seine eigenen Einstellungen.
  - Mit dem Kästchen erscheinen die Server-Regeln (Jötunn `IsAdminOnly`). Ändern darf sie ein Admin auf einem Server mit der Mod und der Host einer eigenen Welt; im Hauptmenü ist es die eigene Config.
  - Das Kästchen bleibt bis zum Spielende, beim Spielstart ist es aus.
- **Fenster:** fast bildschirmfüllend (40 Rand, höchstens 1800 breit); ab 1400 Breite steht die Beschreibung neben dem Namen.
- **Suche** oben: Name, Beschreibung, Gruppe, Abschnitt, Mod-Name und GUID.
- **Alle anderen Mods** stehen unter „Weitere Mods“, je Mod zugeklappt, getrennt in „Für dich“ und „Server-Regeln“:
  - Server-Regeln erkennt das Menü an Jötunns `IsAdminOnly` und an ServerSync (`SyncedConfigEntry` in den Tags). Jötunn verteilt Admin-Änderungen über `SynchronizeChangedConfig`, ServerSync selbst.
  - Bedienung je Typ wie bei den GC-Mods; Text, Listen, Farben und Zahlen ohne Grenzen über ein Eingabefeld. Knopf „Standard“ bei abweichendem Wert.
  - ConfigurationManager-Angaben werden beachtet: `Browsable`, `DispName`, `Category`, `Order`, `Description`, `ReadOnly`, `IsAdvanced`, `HideDefaultButton`.
- **Reiter Mods und Profile** (aus Mod Menu übernommen und erweitert): Mods ein- und ausschalten, Zustand, Profile. Neu gestartet wird über r2modman. Seit Kern 1.1.1 gibt es keinen eigenen Neustart (PowerShell, Registry) und keine Update-Prüfung (Web-Anfragen) mehr: Thunderstore hatte die Pakete abgelehnt, und beides war nicht nötig. Solche Aufrufe nicht wieder einbauen. Den Schalter setzt der Vorlader `GC_ModSchalter_<Paket>.dll` im Unterordner `patchers` jedes Pakets um (Liste `BepInEx/config/GC_Kern/aus.txt`); jede Kopie hat ihren eigenen Assembly-Namen, sonst lehnt BepInEx die zweite ab.
- **ValheimPlus** steht mit im Menü:
  - Im Spiel sind seine Werte nur zum Lesen da.
  - Im Hauptmenü sind sie änderbar. Beim Schließen wendet ValheimPlus sie neu an.
- **Ältere GC-Mods** mit eigener Menü-Kopie (GC_Guilds, GC_ProximityVoice) tragen sich weiter über die Liste `GC.Options.Registry.v2` im AppDomain ein.

## Verbindungen zwischen Mods

Keine Mod kennt die Typen einer anderen, außer bei harter Abhängigkeit:
- Die anbietende Mod setzt in `Awake` einen Eintrag in `GcHooks`.
- Die nutzende Mod fragt ihn ab und hat einen Ersatz, falls er leer ist.

Beispiele:
- `GcHooks.DungeonMapLargeOpen` (Dungeon-Karte → Kompass)
- `GcHooks.PortalRename` (Portalschutz → Portalnamen)
- `GcHooks.SpeciesMaxLevel` (Tierarten → Tiere, sonst 150)
- `GcHooks.CourierWhistle` (Walküren-Transport → Walkürenpfeife in Tiere)
- `GcHooks.MateLevel` (Futterfolge → Tiere: Stufe des besten Partners beim Trächtigwerden, sonst das nächste Tier)

Gemeinsame Schlüssel ohne Abhängigkeit stehen in `GcKeys`, z. B. `LeashHolder`, `LeashTied`, `LeashPost`, `LeashMax` und `CareLevel` von GC_Tiere. Ohne GC_Tiere sind sie nie gesetzt.

Bei harter Abhängigkeit (`GcBraucht`) darf eine Mod die andere direkt nutzen:
- `ProjectReference` mit `Private="false"`;
- `InternalsVisibleTo` in der Ziel-Mod, siehe `Mods\Tiere\Friends.cs`.

## Boss-Fortschritt

- Je besiegtem Hauptboss bekommen Gegner X % mehr Leben und Schaden.
- Für jeden Spieler zählt sein eigener Fortschritt (World Advancement Progression und Guilds_Progress, wenn vorhanden).
- Automatisch gilt der höchste `GcAusgleich` der laufenden Mods, mit EpicLoot mindestens 10 %. Ohne erleichternde Mods sind es 0 %.
- Ziel (Gorduan): neben den Mods eine vergleichbare Schwierigkeit.

## Bauen und weitergeben

- **Spielpfade:** `GamePaths.user.props` (nicht im Git) mit `GamePath` (Valheim-Ordner mit BepInEx) und `JotunnDll`, Vorlage `GamePaths.user.props.example`. Ohne gültige Pfade bricht der Bau mit einer Meldung ab.
- `build.ps1` baut alles und legt `build-output\GCMods\Gorduan-<Paket>\` an, je mit den DLLs, manifest.json, README.md (Liste der DLLs) und icon.png (aus `Pakete\<Paket>\`).
- **Öffentliche Repos** (MIT, Copyright Gorduan, seit 25.10.2026): `github.com/gorduan/Valheim_GC_Server`, `Valheim_GC_Client`, `Valheim_GC_ValheimPlusPlus`, je ein Paket samt Kern. Erzeugt und aktualisiert sie `python -I tools\oeffentlich.py` aus den getrackten Dateien (ohne `tools`, `Programm`; „Gorduan“ wird dort „Gorduan“). Die `website_url` der Manifeste zeigt dorthin.
- Dazu kommen `build-output\GCMods-zips\` (ein Zip je Paket und `katalog.json`) und `build-output\GC-Mod-Auswahl.html`.
- Ins Profil „GC Discord“: `python -I tools\deploy_profil.py` (r2modman geschlossen; alte Stände ins Archiv).
- **Auswahl-Programm:** eine HTML-Datei, läuft in jedem Browser ohne Installation.
  - Es baut den Server-Upload und die Spieler-Zips für r2modman.
  - In Chrome, Edge und Opera installiert es auch direkt in einen BepInEx-Ordner. Abgewählte GC-DLLs werden dabei zu `.dll.aus` umbenannt, nicht gelöscht.
  - Es speichert und lädt eine Auswahl-Datei.
  - Es prüft einen Ordner auf fehlende und überzählige DLLs und auf alte Dateien (GC_Server.dll, GC_Client.dll, ValheimPlusPlus.dll von vor dem Umbau, Einzelpaket-Ordner vom 20.10.2026).
- **Versionen:** je Paket nur einmal je Veröffentlichung erhöhen, ab der Version im Profil Default. Neue Funktionen oder Pflicht-Update: zweite Stelle, Fehlerbehebung: dritte Stelle.

## Valheim-Fallen (gesammelt)

- **Verweise auf andere Objekte:** nie als ZDOID speichern, beim Laden der Welt gibt es neue IDs. Stattdessen eine eigene ID (`GcIds`).
- **UI-Objekte** nicht statisch merken: Ausloggen baut das HUD neu, alte Verweise zeigen auf Zerstörtes. Unitys `== null` benutzen, nicht `?.`.
- **`Inventory.m_inventory`** nie vorübergehend verändern: Das Spiel läuft während der Anforderungsprüfung darüber. Zählen und Entnehmen im Kontext ersetzen (siehe rotes Schloss).
- **Gepflücktes** läuft nicht über `Humanoid.DropItem`: `Pickable.RPC_Pick` und `ItemDrop.OnCreateNew` nutzen.
- **Synchronisierte Einstellungen** kommen erst nach dem Verbinden. Komponenten deshalb beim Laden anlegen und erst zur Laufzeit sperren.
- **Bestehende Config-Schlüssel:** BepInEx behält gespeicherte Werte. Für einen neuen Standard braucht es einen neuen Schlüssel.
- **Hover-Texte:** Valheim nimmt die erste Hover-Komponente, eine zweite daneben wird ignoriert.
- **Overlap-Abfragen** immer mit Maske und `QueryTriggerInteraction.Ignore`.
- **Shell-Werkzeuge:** Backslash-Pfade nie in Python-Strings eines Bash-Heredocs (`\b`, `\t` werden Steuerzeichen). Skripte als Datei anlegen.
