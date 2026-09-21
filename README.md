# StemMyWav

StemMyWav trennt eine YuE-FLAC in 48-kHz-/16-Bit-Stereo-WAV-Stems. Optional entfernt ein zweites MLX-Modell Hall aus dem Gesang.

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
StemMyWav.Gateway/
  Http/           Endpunkte, Antworttypen, Schlüsselprüfung
  Jobs/           Auftragsmodell, Ablage auf der Platte, Übertragung an den Mac
  OpenApi/        Ergänzungen am erzeugten Dokument
  Configuration/  Gateway- und Backend-Einstellungen, Geheimnisse aus Datei
StemMyWav.Api/
  Http/           Endpunkte und Schlüsselprüfung
  Separation/     Ablauf der Trennung und Ausführung externer Programme
  Configuration/  Separator- und Schlüsseleinstellungen
StemMyWav.Api.Tests/  Unit-Tests der Trennlogik, ohne echte Prozesse
```

Die Schlüsselprüfung ist in beiden Diensten bewusst doppelt vorhanden: ein gemeinsames Projekt für rund fünfzehn Zeilen würde Build und Auslieferung mehr belasten, als es spart.

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

Das Volume `stemmywav-data` hält Eingaben nur bis zur erfolgreichen Verarbeitung vor. Danach wird die FLAC sofort gelöscht; bis zum bestätigten Import liegt nur das Ergebnis-ZIP bereit. YuE_To_Logic löscht den abgeschlossenen Auftrag nach dem Import mit `DELETE /api/jobs/{id}`. Nicht bestätigte abgeschlossene oder fehlgeschlagene Aufträge werden nach einem Tag automatisch entfernt. Das Volume muss bei Container-Neustarts erhalten bleiben. Ein kurzzeitig ausgeschalteter Mac lässt Aufträge in `queued`; Wiederholungen erfolgen nach 15 bis maximal 300 Sekunden. Bei einem Verbindungsabbruch nach Beginn der Verarbeitung kann ein Auftrag auf dem Mac erneut berechnet werden. Bleibt der Mac dauerhaft unerreichbar, gibt ein Auftrag nach 24 Stunden auf und geht auf `failed`; erst dadurch greifen Löschung und Aufbewahrungsfrist, und die Warteschlange läuft nicht dauerhaft voll. Der Gateway nimmt standardmäßig höchstens zwei wartende Aufträge an (`429` mit `Retry-After` bei voller Warteschlange). Frist, Warteschlange und Aufgabegrenze sind über `Gateway__RetentionDays`, `Gateway__MaxPendingJobs` und `Gateway__MaxQueueHours` änderbar.

## API für YuE_To_Logic

Jede `/api`-Anfrage an den Gateway braucht `X-Api-Key` mit dem Wert aus `STEMMYWAV_GATEWAY_API_KEY`. Die Schnittstelle ist asynchron, damit ein Mac-Ausfall keinen FLAC-Upload verliert:

1. `POST /api/jobs?dereverb=true` mit `Content-Type: audio/flac` und dem FLAC-Dateiinhalt liefert `202 Accepted`, eine Job-ID und einen `Location`-Header.
2. `GET /api/jobs/{id}` liefert `queued`, `processing`, `completed` oder `failed` sowie die Anzahl der Versuche.
3. Bei `completed` liefert `GET /api/jobs/{id}/result` ein ZIP mit `vocals.wav`, `instrumental.wav` und bei `dereverb=true` zusätzlich `vocals_dry.wav` sowie `vocals_reverb.wav`, sofern das De-Reverb-Modell den Hallanteil ausgibt.
4. **Nach erfolgreichem Speichern und Importieren** ruft YuE_To_Logic `DELETE /api/jobs/{id}` auf. Damit verschwinden ZIP und Job-Status sofort aus dem Gateway. Ein späterer Abruf liefert `404`. Derselbe Aufruf bricht einen noch wartenden Auftrag (`queued`) ab und gibt dessen Platz in der Warteschlange frei; nur während der laufenden Übertragung zum Mac (`processing`) ist das Löschen mit `409` gesperrt.

Das Upload-Limit beträgt 512 MiB. Fehlerantworten sind `application/problem+json`. `GET /health` prüft nur den lokalen API-Prozess, nicht die Erreichbarkeit des Macs; der Gateway-Container meldet damit zusätzlich seinen Docker-Healthstatus. Endgültig `failed` wird ein Auftrag nur, wenn die Mac-API die Datei selbst ablehnt (`400`, `413`, `415`, `422`). Alles andere gilt als behebbar und wird wiederholt — auch ein `401` nach einem Schlüsselwechsel, damit ein Konfigurationsfehler die hochgeladene FLAC nicht verwirft.

## OpenAPI und Swagger UI

Der Gateway stellt den maschinenlesbaren Vertrag unter `/openapi/v1.json` und Swagger UI unter `/swagger` bereit. Er beschreibt den rohen FLAC-Body, den optionalen `dereverb`-Parameter, Status- und Fehlerantworten, das binäre ZIP sowie den erforderlichen Header `X-Api-Key`. Die OpenAPI-Datei enthält keine Schlüsselwerte. Für einen Agenten kann sie auf dem CT exportiert werden:

```sh
curl -fsS http://127.0.0.1:8080/openapi/v1.json > stemmywav-openapi.json
```

Compose bindet Port 8080 standardmäßig nur an `127.0.0.1` des CT. Für Swagger UI auf einem anderen Rechner kann ein SSH-Tunnel zum CT geöffnet und danach `http://127.0.0.1:8080/swagger` im Browser aufgerufen werden:

```sh
ssh -L 8080:127.0.0.1:8080 root@stem
```

Die API selbst bleibt für YuE_To_Logic im Docker-Netz unter `http://stemmywav:8080` erreichbar. In Swagger UI lässt sich der Gateway-Schlüssel über **Authorize** für Testaufrufe setzen.

Soll ein Reverse Proxy auf einem anderen LAN-Rechner (`stem.idsrv.info`) den Gateway erreichen, in der `.env` auf dem CT `STEMMYWAV_GATEWAY_BIND` auf dessen LAN-Adresse setzen, z. B. `192.168.2.74`. Der Proxy muss dann per HTTP auf `192.168.2.74:8080` weiterleiten. Diese Bindung macht die gesamte Gateway-API im LAN erreichbar; API-Aufrufe unter `/api` erfordern weiterhin `X-Api-Key`. Schlüsselwerte stehen weder in Swagger UI noch im OpenAPI-Dokument.
Bei dieser Einstellung den OpenAPI-Export und den Health-Check über `http://192.168.2.74:8080` statt über `127.0.0.1` aufrufen.

## Modelle und Konfiguration

Die Mac-API verwendet `model_bs_roformer_ep_317_sdr_12.9755.ckpt` zur Vocal-Separation und `dereverb_mel_band_roformer_anvuew_sdr_19.1729.ckpt` für optionales De-Reverb. Sie können mit `Separator__Model` und `Separator__DereverbModel` geändert werden. `Separator__Executable` legt den Pfad zur MLX-CLI fest, `Separator__ModelDirectory` den Modell-Cache. Eingaben mit anderer Abtastrate oder Kanalzahl werden vor MLX als Stereo-FLAC bei 44,1 kHz vorbereitet. Alle Ergebnis-Stems werden anschließend mit FFmpeg auf 48 kHz, 16 Bit und Stereo konvertiert.
