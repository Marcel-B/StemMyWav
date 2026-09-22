# StemMyWav

StemMyWav trennt eine FLAC- oder WAV-Datei in 48-kHz-/16-Bit-Stereo-WAV-Stems. Das Trennmodell ist wählbar: siebenundzwanzig Modelle von zwei bis sechs Stems stehen zur Auswahl, von schnellen ONNX-Modellen bis zu den großen RoFormern. Optional entfernt ein zweites MLX-Modell Hall aus dem Gesang.

## Architektur

```text
YuE_To_Logic (Proxmox/Docker)
  -> StemMyWav.Gateway (Proxmox/Docker, persistente Aufträge)
  -> Tailscale Serve (HTTPS im Tailnet)
  -> StemMyWav.Api (nativ auf dem Mac, nur 127.0.0.1)
  -> mlx-audio-separator (Metal-GPU auf dem Mac)
```

Die MLX-Anwendung läuft nativ auf macOS. Docker Desktop und Podman starten auf dem Mac Linux-Container ohne Zugriff auf Metal. Deshalb enthält `compose.proxmox.yaml` nur den Gateway. Die Mac-API läuft als `launchd`-Dienst und Tailscale Serve stellt sie ausschließlich im Tailnet bereit. Beide API-Strecken benötigen jeweils einen eigenen Schlüssel.

## Projektaufbau

Beide Dienste sind nach Zuständigkeit gegliedert; `Program.cs` enthält jeweils nur noch die Komposition.

```text
models.json           Modellkatalog, in beide Dienste eingebettet
StemMyWav.Gateway/
  Http/           Endpunkte, Antworttypen, Schlüsselprüfung, Upload-Erkennung
  Jobs/           Auftragsmodell, Ablage auf der Platte, Übertragung an den Mac
  Catalog/        Modellkatalog aus der eingebetteten models.json
  OpenApi/        Ergänzungen am erzeugten Dokument
  Configuration/  Gateway- und Backend-Einstellungen, Geheimnisse aus Datei
StemMyWav.Api/
  Http/           Endpunkte, Schlüsselprüfung, Upload-Erkennung
  Separation/     Ablauf der Trennung, Arbeitsverzeichnisse, externe Programme
  Catalog/        Modellkatalog aus der eingebetteten models.json
  Configuration/  Separator- und Schlüsseleinstellungen
StemMyWav.Api.Tests/  Unit-Tests der Trennlogik und des Katalogs, ohne echte Prozesse
```

Die Schlüsselprüfung, die Upload-Erkennung und das Lesen des Katalogs sind in beiden Diensten bewusst doppelt vorhanden: ein gemeinsames Projekt für so wenige Zeilen würde Build und Auslieferung mehr belasten, als es spart. Geteilt wird stattdessen die Datendatei: `models.json` liegt im Wurzelverzeichnis und ist in beide Dienste als Ressource eingebettet. Dadurch kennt der Gateway die Kennungen auch dann, wenn der Mac nicht erreichbar ist, und lehnt eine unbekannte Kennung sofort ab, statt eine Datei stundenlang vorzuhalten.

Einstellungen werden beim Start geprüft. Ein fehlender Schlüssel, eine unerreichbare Backend-Adresse oder ein unsinniger Zahlenwert lassen den Dienst sofort abbrechen statt erst beim ersten Auftrag.

Die Trennlogik ruft externe Programme über eine schmale Schnittstelle auf. Dadurch prüfen die Unit-Tests Vorbereitung, Modellaufrufe und Verpackung ohne FFmpeg und ohne Modelle: `dotnet test StemMyWav.Api.Tests`. Der Gateway wird als Prozess gegen einen Mac-Stub getestet: `python3 -m unittest discover -s tests`.

## Mac einrichten

Voraussetzungen: Apple Silicon, macOS, .NET 10, Python 3.10+ und FFmpeg. Auf diesem Mac liegen die Python-Umgebung in `.venv` und die geladenen Modelle in `.models`. Ein erneutes Setup ist möglich mit:

```sh
brew install python@3.13 ffmpeg
/opt/homebrew/bin/python3.13 -m venv .venv
.venv/bin/python -m pip install 'mlx-audio-separator[convert]'
dotnet publish StemMyWav.Api -c Release -o deploy/mac/publish
```

Einen zufälligen Mac-Schlüssel erzeugen und nur für den eigenen Benutzer lesbar machen:

```sh
mkdir -p ~/.config/stemmywav
umask 077
openssl rand -hex 32 > ~/.config/stemmywav/mac-api-key
```

`deploy/mac/start.sh` startet die API auf `127.0.0.1:5081` mit diesem Schlüssel. Der vorbereitete LaunchAgent liegt in `deploy/mac/com.marcel.stemmywav.macapi.plist` und verwendet die Pfade dieses Macs. Nach dem Kopieren nach `~/Library/LaunchAgents` kann er mit `launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.marcel.stemmywav.macapi.plist` gestartet werden. Danach `tailscale serve --bg 5081` ausführen und die angezeigte HTTPS-Adresse für den Gateway notieren. Tailscale Funnel wird nicht benötigt. Die Tailnet-Zugriffsregeln sollten den Mac-Dienst für den Proxmox-Knoten freigeben.

Jede Anfrage arbeitet in einem eigenen Temp-Verzeichnis, das nach dem Senden der Antwort gelöscht wird — auch dann, wenn die Trennung fehlschlägt. Bricht der Dienst mitten in einer Trennung ab, etwa durch einen Neustart, bleibt das Verzeichnis mit den bereits erzeugten WAVs liegen. Solche Reste sammelt der Dienst beim Start und danach stündlich ein, sobald sie älter als eine Stunde sind. Verzeichnisse laufender Anfragen sind ausgenommen, unabhängig davon, wie lange eine Trennung dauert.

## Proxmox-Gateway einrichten

Der Workflow `.github/workflows/ci.yaml` baut beide .NET-Projekte und führt bei Pull Requests und Pushes die Unit-Tests der Trennlogik sowie den Gateway-Integrationstest aus. Nach erfolgreichen Tests wird auf `main` und bei Tags `v*` ein Linux/amd64-Gateway-Image nach `ghcr.io/<owner>/<repository>` veröffentlicht. Verfügbar sind `:main`, Versions-Tags ohne führendes `v` und ein `sha-...`-Tag. Der Workflow verwendet dafür das automatische `GITHUB_TOKEN` mit `packages: write`; API-Schlüssel werden nicht an den Build übergeben. Pull Requests bauen das Image nur zur Prüfung, ohne Push. Vor einem Release müssen Repository und Git-Remote auf GitHub eingerichtet sein.

Wenn das GHCR-Paket privat ist, muss Proxmox sich mit einem Token mit `read:packages` bei `ghcr.io` anmelden, bevor `docker compose pull` funktioniert. Für ein öffentliches Paket ist kein Pull-Token nötig.

Docker Compose liest die Konfiguration aus Umgebungsvariablen. Die lokale `.env` ist aus Git ausgeschlossen. `STEMMYWAV_MAC_API_KEY` muss denselben Wert wie die Schlüsseldatei auf dem Mac enthalten; für `STEMMYWAV_GATEWAY_API_KEY` einen anderen Zufallswert verwenden. `.env.example` enthält nur Platzhalter.

```sh
umask 077
cp .env.example .env
# In .env die GHCR-Adresse, Tailscale-URL und beide Schlüssel einsetzen.
docker compose -f compose.proxmox.yaml pull
docker compose -f compose.proxmox.yaml up -d
```

YuE_To_Logic muss den Gateway im Docker-Netz `stemmywav` erreichen können (`http://stemmywav:8080`). Falls YuE_To_Logic in einer anderen Compose-Installation läuft, dessen Container mit diesem Netz verbinden. Der Gateway-Container muss die Mac-HTTPS-Adresse auflösen und erreichen können; dafür muss die Proxmox-Docker-Umgebung Zugang zum Tailnet haben. Vor der Kopplung mit YuE kann `https://.../health` aus dieser Umgebung getestet werden.

Das Volume `stemmywav-data` hält Eingaben nur bis zur erfolgreichen Verarbeitung vor. Danach wird die hochgeladene Datei sofort gelöscht; bis zum bestätigten Import liegt nur das Ergebnis-ZIP bereit. YuE_To_Logic löscht den abgeschlossenen Auftrag nach dem Import mit `DELETE /api/jobs/{id}`. Nicht bestätigte abgeschlossene oder fehlgeschlagene Aufträge werden nach einem Tag automatisch entfernt. Das Volume muss bei Container-Neustarts erhalten bleiben. Ein kurzzeitig ausgeschalteter Mac lässt Aufträge in `queued`; Wiederholungen erfolgen nach 15 bis maximal 300 Sekunden. Bei einem Verbindungsabbruch nach Beginn der Verarbeitung kann ein Auftrag auf dem Mac erneut berechnet werden. Bleibt der Mac dauerhaft unerreichbar, gibt ein Auftrag nach 24 Stunden auf und geht auf `failed`; erst dadurch greifen Löschung und Aufbewahrungsfrist, und die Warteschlange läuft nicht dauerhaft voll. Der Gateway nimmt standardmäßig höchstens zwei wartende Aufträge an (`429` mit `Retry-After` bei voller Warteschlange). Frist, Warteschlange und Aufgabegrenze sind über `Gateway__RetentionDays`, `Gateway__MaxPendingJobs` und `Gateway__MaxQueueHours` änderbar.

## API für YuE_To_Logic

Jede `/api`-Anfrage an den Gateway braucht `X-Api-Key` mit dem Wert aus `STEMMYWAV_GATEWAY_API_KEY`. Die Schnittstelle ist asynchron, damit ein Mac-Ausfall keinen Upload verliert:

1. `GET /api/models` nennt die wählbaren Trennmodelle mit Kennung, Stems und Rechenaufwand. Die Liste kommt aus dem Gateway selbst und steht deshalb auch bei ausgeschaltetem Mac bereit.
2. `POST /api/jobs?model=mel-roformer-kim-vocals&dereverb=true` mit `Content-Type: audio/flac` oder `audio/wav` und dem rohen Dateiinhalt liefert `202 Accepted`, eine Job-ID, die verwendete Modellkennung und einen `Location`-Header. Ohne `model` gilt die Voreinstellung.
3. `GET /api/jobs/{id}` liefert `queued`, `processing`, `completed` oder `failed`, die Anzahl der Versuche sowie `model` und `stems` — also das verwendete Modell und die Dateien, die das Ergebnis-ZIP enthalten wird. `GET /api/jobs` listet alle bekannten Aufträge, jüngste zuerst; damit lässt sich finden, was die Warteschlange belegt und mit welchem Modell.
4. Bei `completed` liefert `GET /api/jobs/{id}/result` ein ZIP mit einer WAV je Stem des gewählten Modells — bei den Zwei-Stem-Modellen also `vocals.wav` und `instrumental.wav`, bei `htdemucs-6s` sechs Dateien. Bei `dereverb=true` kommen `vocals_dry.wav` sowie `vocals_reverb.wav` hinzu, sofern das De-Reverb-Modell den Hallanteil ausgibt.
5. **Nach erfolgreichem Speichern und Importieren** ruft YuE_To_Logic `DELETE /api/jobs/{id}` auf. Damit verschwinden ZIP und Job-Status sofort aus dem Gateway. Ein späterer Abruf liefert `404`. Derselbe Aufruf bricht einen noch wartenden Auftrag (`queued`) ab und gibt dessen Platz in der Warteschlange frei; nur während der laufenden Übertragung zum Mac (`processing`) ist das Löschen mit `409` gesperrt.

Die Ausgabe ist unabhängig von der Eingabe immer 48-kHz-/16-Bit-Stereo-WAV. Eine Eingabe, die nicht bereits Stereo bei 44,1 kHz ist, wird vor dem Modell umgerechnet; eine WAV, die schon passt, geht unverändert weiter.

Eine unbekannte Modellkennung beantwortet der Gateway sofort mit `400`, ohne den Upload anzunehmen — ebenso `dereverb=true` zu einem Modell ohne Gesangs-Stem, etwa `mdx23c-drumsep`. Das Upload-Limit beträgt 512 MiB. Fehlerantworten sind `application/problem+json`. `GET /health` prüft nur den lokalen API-Prozess, nicht die Erreichbarkeit des Macs; der Gateway-Container meldet damit zusätzlich seinen Docker-Healthstatus.

Endgültig `failed` wird ein Auftrag nur, wenn die Mac-API die Datei selbst ablehnt (`400`, `413`, `415`, `422`); der genannte Grund steht dann in `lastError`. Dazu zählt eine FLAC, die sich nicht dekodieren lässt — etwa eine abgeschnittene Datei, die zwar mit `fLaC` beginnt, aber keinen lesbaren Audiostrom enthält. Alles andere gilt als behebbar und wird wiederholt, auch ein `401` nach einem Schlüsselwechsel, damit ein Konfigurationsfehler die hochgeladene FLAC nicht verwirft.

## OpenAPI und Swagger UI

Der Gateway stellt den maschinenlesbaren Vertrag unter `/openapi/v1.json` und Swagger UI unter `/swagger` bereit. Er beschreibt den rohen FLAC- oder WAV-Body, die Parameter `model` und `dereverb`, die Modellliste mit ihren Geschwindigkeitsklassen, Status- und Fehlerantworten, das binäre ZIP sowie den erforderlichen Header `X-Api-Key`. Die OpenAPI-Datei enthält keine Schlüsselwerte. Für einen Agenten kann sie auf dem CT exportiert werden:

```sh
curl -fsS http://127.0.0.1:8080/openapi/v1.json > stemmywav-openapi.json
```

Compose bindet Port 8080 standardmäßig nur an `127.0.0.1` des CT. Für Swagger UI auf einem anderen Rechner kann ein SSH-Tunnel zum CT geöffnet und danach `http://127.0.0.1:8080/swagger` im Browser aufgerufen werden:

```sh
ssh -L 8080:127.0.0.1:8080 root@stem
```

Die API selbst bleibt für YuE_To_Logic im Docker-Netz unter `http://stemmywav:8080` erreichbar. In Swagger UI lässt sich der Gateway-Schlüssel über **Authorize** setzen; danach sind dort auch die Betriebsaufgaben erledigt: `GET /api/jobs` zeigt, was die Warteschlange belegt, und `DELETE /api/jobs/{id}` bricht einen wartenden Auftrag ab oder räumt einen abgeschlossenen weg.

Soll ein Reverse Proxy auf einem anderen LAN-Rechner (`stem.idsrv.info`) den Gateway erreichen, in der `.env` auf dem CT `STEMMYWAV_GATEWAY_BIND` auf dessen LAN-Adresse setzen, z. B. `192.168.2.74`. Der Proxy muss dann per HTTP auf `192.168.2.74:8080` weiterleiten. Diese Bindung macht die gesamte Gateway-API im LAN erreichbar; API-Aufrufe unter `/api` erfordern weiterhin `X-Api-Key`. Schlüsselwerte stehen weder in Swagger UI noch im OpenAPI-Dokument.
Bei dieser Einstellung den OpenAPI-Export und den Health-Check über `http://192.168.2.74:8080` statt über `127.0.0.1` aufrufen.

## Modelle und Konfiguration

Der Katalog steht in [models.json](models.json) und ist in beide Dienste eingebettet. Alle Einträge kennt `mlx-audio-separator`; die Modelldatei wird beim ersten Lauf nach `Separator__ModelDirectory` geladen, ein bis dahin ungenutztes Modell braucht also einmalig etwas Zeit und Plattenplatz (300 bis 900 MB je RoFormer).

Voreingestellt ist `bs-roformer-viperx-1297`. Das lässt sich über `Gateway__DefaultModel` ändern; `Separator__DefaultModel` gilt für Aufrufe, die direkt an die Mac-API gehen. Eine unbekannte Kennung bricht in beiden Fällen den Start ab. Das De-Reverb-Modell hinter `dereverb=true` steht bewusst nicht im Katalog — es ist keine Wahl des Aufrufers, sondern ein Nachbearbeitungsschritt, und bleibt über `Separator__DereverbModel` einstellbar.

Die Spalte *Tempo* ist die Entscheidungshilfe: **schnell** rechnet schneller als der Titel dauert, **sehr langsam** braucht ein Vielfaches davon. Der Faktor ist Audiodauer geteilt durch Rechenzeit — bei 0,3× dauert ein Vier-Minuten-Titel rund dreizehn Minuten, bei 8,1× knapp eine halbe Minute. Gemessen wurde mit `audio-separator` 0.47 auf einem Mac mini M4 (24 GB); MLX rechnet anders, die Werte sind also eine Größenordnung und keine Zusage. Wo kein Faktor steht, stammt die Einstufung aus dem Vergleich mit einem gemessenen Modell derselben Familie.

| Kennung | Aufgabe | Stems | Tempo | Faktor |
|---|---|---|---|---:|
| `bs-roformer-viperx-1297` | vocals | vocals, instrumental | sehr langsam | 0,3× |
| `bs-roformer-viperx-1296` | vocals | vocals, instrumental | sehr langsam | 0,3× |
| `mel-roformer-kim-vocals` | vocals | vocals, instrumental | mittel | 2,5× |
| `mel-roformer-kim-ft-unwa` | vocals | vocals, instrumental | sehr langsam | 0,4× |
| `mel-roformer-kim-ft2-unwa` | vocals | vocals, instrumental | sehr langsam (geschätzt) | — |
| `mel-roformer-big-beta6x` | vocals | vocals, instrumental | sehr langsam | 0,6× |
| `mel-roformer-big-beta5e` | vocals | vocals, instrumental | sehr langsam (geschätzt) | — |
| `mel-roformer-big-syhft-v1` | vocals | vocals, instrumental | sehr langsam (geschätzt) | — |
| `mel-roformer-vocals-becruily` | vocals | vocals, instrumental | langsam | 1,4× |
| `mel-roformer-vocals-fv4-gabox` | vocals | vocals, instrumental | langsam (geschätzt) | — |
| `bs-roformer-vocals-gabox` | vocals | vocals, instrumental | langsam (geschätzt) | — |
| `bs-roformer-vocals-resurrection-unwa` | vocals | vocals, instrumental | langsam (geschätzt) | — |
| `mdx23c-instvoc-hq` | vocals | vocals, instrumental | langsam (geschätzt) | — |
| `mdx-net-voc-ft` | vocals | vocals, instrumental | schnell | 4,3× |
| `mdx-net-kim-vocal-2` | vocals | vocals, instrumental | schnell (geschätzt) | — |
| `mel-roformer-inst-becruily` | instrumental | instrumental, vocals | langsam | 1,4× |
| `mel-roformer-inst-v1e-plus-unwa` | instrumental | instrumental, vocals | langsam (geschätzt) | — |
| `bs-roformer-inst-resurrection-unwa` | instrumental | instrumental, vocals | langsam (geschätzt) | — |
| `mdx-net-inst-hq-5` | instrumental | instrumental, vocals | schnell | 8,1× |
| `mdx-net-inst-hq-4` | instrumental | instrumental, vocals | schnell (geschätzt) | — |
| `mel-roformer-karaoke-becruily` | karaoke | vocals, karaoke | langsam (geschätzt) | — |
| `htdemucs-6s` | 6stem | vocals, drums, bass, guitar, piano, other | schnell | 5,6× |
| `bs-roformer-sw` | 6stem | vocals, drums, bass, guitar, piano, other | langsam | 1,1× |
| `htdemucs-ft` | 4stem | vocals, drums, bass, other | mittel | 2,1× |
| `htdemucs` | 4stem | vocals, drums, bass, other | schnell (geschätzt) | — |
| `hdemucs-mmi` | 4stem | vocals, drums, bass, other | schnell (geschätzt) | — |
| `mdx23c-drumsep` | drums | kick, snare, toms, hh, ride, crash | langsam (geschätzt) | — |

Dazu einige Anhaltspunkte: `mdx-net-voc-ft` und `mdx-net-inst-hq-5` sind die schnellen ONNX-Modelle für den Alltag, `mel-roformer-kim-vocals` bietet den besten veröffentlichten Gesangswert bei noch vertretbarem Aufwand, und `htdemucs-6s` liefert sechs Stems schneller als die meisten Zwei-Stem-Modelle. `mdx23c-drumsep` erwartet als Eingabe bereits einen Schlagzeug-Stem. Die veröffentlichten SDR-Werte in der Modellliste stammen aus dem Testset von `audio-separator` und sind nicht mit MUSDB18-Zahlen aus Veröffentlichungen vergleichbar.

Im ZIP heißen die Dateien so, wie der Katalog die Stems nennt. Das ist nicht immer der Name, den das Modell selbst vergibt: einige Konfigurationen schreiben den Gegenpart des Gesangs als `(other)` statt `(Instrumental)`. Bei einem Modell mit zwei Stems ordnet die Mac-API die übrig gebliebene Datei deshalb dem übrig gebliebenen Stem zu; bleibt mehr als eine Zuordnung offen, scheitert der Lauf, statt einen falsch benannten Stem auszuliefern.

`Separator__Executable` legt den Pfad zur MLX-CLI fest, `Separator__ModelDirectory` den Modell-Cache.

Ein neues Modell aufzunehmen heißt, einen Eintrag in `models.json` zu ergänzen; `mlx-audio-separator --list_models` zeigt, welche Dateinamen die CLI kennt. Die Unit-Tests prüfen den Katalog auf eindeutige Kennungen, mindestens zwei Stems je Modell und darauf, dass Geschwindigkeitsklasse und gemessener Faktor zueinander passen.
