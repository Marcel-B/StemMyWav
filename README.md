# StemMyWav

StemMyWav trennt eine YuE-FLAC in WAV-Stems. Optional entfernt ein zweites MLX-Modell Hall aus dem Gesang.

## Architektur

```text
YuE_To_Logic (Proxmox/Docker)
  -> StemMyWav.Gateway (Proxmox/Docker, persistente Aufträge)
  -> Tailscale Serve (HTTPS im Tailnet)
  -> StemMyWav.Api (nativ auf dem Mac, nur 127.0.0.1)
  -> mlx-audio-separator (Metal-GPU auf dem Mac)
```

Die MLX-Anwendung läuft nativ auf macOS. Docker Desktop und Podman starten auf dem Mac Linux-Container ohne Zugriff auf Metal. Deshalb enthält `compose.proxmox.yaml` nur den Gateway. Die Mac-API läuft als `launchd`-Dienst und Tailscale Serve stellt sie ausschließlich im Tailnet bereit. Beide API-Strecken benötigen jeweils einen eigenen Schlüssel.

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

`deploy/mac/start.sh` startet die API auf `127.0.0.1:5080` mit diesem Schlüssel. Der vorbereitete LaunchAgent liegt in `deploy/mac/com.marcel.stemmywav.macapi.plist` und verwendet die Pfade dieses Macs. Nach dem Kopieren nach `~/Library/LaunchAgents` kann er mit `launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.marcel.stemmywav.macapi.plist` gestartet werden. Danach `tailscale serve --bg 5080` ausführen und die angezeigte HTTPS-Adresse für den Gateway notieren. Tailscale Funnel wird nicht benötigt. Die Tailnet-Zugriffsregeln sollten den Mac-Dienst für den Proxmox-Knoten freigeben.

## Proxmox-Gateway einrichten

Der Workflow `.github/workflows/ci.yaml` baut beide .NET-Projekte und führt den Gateway-Integrationstest bei Pull Requests und Pushes aus. Nach erfolgreichen Tests wird auf `main` und bei Tags `v*` ein Linux/amd64-Gateway-Image nach `ghcr.io/<owner>/<repository>` veröffentlicht. Verfügbar sind `:main`, Versions-Tags ohne führendes `v` und ein `sha-...`-Tag. Der Workflow verwendet dafür das automatische `GITHUB_TOKEN` mit `packages: write`; API-Schlüssel werden nicht an den Build übergeben. Pull Requests bauen das Image nur zur Prüfung, ohne Push. Vor einem Release müssen Repository und Git-Remote auf GitHub eingerichtet sein.

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

Das Volume `stemmywav-data` enthält Eingaben, Status und Ergebnisse. Es muss bei Container-Neustarts erhalten bleiben und in Backups berücksichtigt werden. Ein kurzzeitig ausgeschalteter Mac lässt Aufträge in `queued`; Wiederholungen erfolgen nach 15 bis maximal 300 Sekunden. Bei einem Verbindungsabbruch nach Beginn der Verarbeitung kann ein Auftrag auf dem Mac erneut berechnet werden. Der Gateway nimmt höchstens 20 wartende Aufträge an (`429` bei voller Warteschlange). Abgeschlossene und fehlgeschlagene Aufträge werden nach 7 Tagen entfernt. Diese Werte sind über `Gateway__MaxPendingJobs` und `Gateway__RetentionDays` änderbar.

## API für YuE_To_Logic

Jede `/api`-Anfrage an den Gateway braucht `X-Api-Key` mit dem Wert aus `STEMMYWAV_GATEWAY_API_KEY`. Die Schnittstelle ist asynchron, damit ein Mac-Ausfall keinen FLAC-Upload verliert:

1. `POST /api/jobs?dereverb=true` mit `Content-Type: audio/flac` und dem FLAC-Dateiinhalt liefert `202 Accepted`, eine Job-ID und einen `Location`-Header.
2. `GET /api/jobs/{id}` liefert `queued`, `processing`, `completed` oder `failed` sowie die Anzahl der Versuche.
3. Bei `completed` liefert `GET /api/jobs/{id}/result` ein ZIP mit `vocals.wav`, `instrumental.wav` und bei `dereverb=true` zusätzlich `vocals_dry.wav` und `vocals_reverb.wav`.

Das Upload-Limit beträgt 512 MiB. `GET /health` prüft nur den lokalen API-Prozess, nicht die Erreichbarkeit des Macs. Dauerhaft ungültige Anfragen an die Mac-API werden als `failed` markiert; Netzwerkfehler und Serverfehler werden wiederholt.

## Modelle und Konfiguration

Die Mac-API verwendet `model_bs_roformer_ep_317_sdr_12.9755.ckpt` zur Vocal-Separation und `dereverb_mel_band_roformer_anvuew_sdr_19.1729.ckpt` für optionales De-Reverb. Sie können mit `Separator__Model` und `Separator__DereverbModel` geändert werden. `Separator__Executable` legt den Pfad zur MLX-CLI fest, `Separator__ModelDirectory` den Modell-Cache. Eingaben mit anderer Abtastrate oder Kanalzahl werden vor MLX als Stereo-FLAC bei 44,1 kHz vorbereitet.
