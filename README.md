<h1 align="center">AppIP</h1>

<p align="center">
  Widget per il desktop di Windows, in stile <i>conky</i>: nome macchina, IP, DNS, IP pubblico, velocità di rete, Wi-Fi, VPN.<br>
  <a href="#english">English below</a>
</p>

<p align="center">
  <a href="https://github.com/corbolma/AppIP/releases/latest"><b>⬇ Scarica l'ultima versione</b></a> ·
  <a href="https://github.com/corbolma/AppIP/wiki">Wiki</a> ·
  <a href="https://github.com/corbolma/AppIP/issues">Segnala un problema</a>
</p>

<p align="center">
  <img src="docs/widget-scuro.png" alt="AppIP, tema scuro" width="330">
  &nbsp;
  <img src="docs/widget-chiaro-vpn.png" alt="AppIP, tema chiaro con VPN attiva" width="330">
  &nbsp;
  <img src="docs/widget-compatto.png" alt="AppIP, modalità compatta" width="270">
</p>
<p align="center"><sub>Tema scuro · tema chiaro con VPN attiva · modalità compatta (dati di esempio)</sub></p>

---

## Italiano

### Cosa mostra
- **Nome macchina** e dominio
- Per ogni scheda di rete attiva: **IP**, **gateway**, **DNS**
- **IP pubblico** (aggiornato ogni 5 minuti e quando cambia la rete)
- **Velocità** di download/upload con grafico degli ultimi 60 secondi
- **Wi-Fi**: nome della rete e qualità del segnale
- **VPN** attiva, con badge e IP assegnato
- **Ping** verso router e Internet (disattivato di default, intervallo configurabile)
- Interfaccia in **italiano e inglese** (segue la lingua di Windows, modificabile dal menu *Lingua / Language*); altre lingue si aggiungono con un semplice file di testo, vedi [lang/README.md](lang/README.md)

### Come si usa
- Il widget resta **incollato al desktop** (sotto le finestre, visibile anche con Win+D). Trascinalo dove vuoi.
- **Clic su un valore** per copiarlo negli appunti.
- **Tasto destro** sul widget o sull'icona "IP" vicino all'orologio per il menu: schede da mostrare, tema chiaro/scuro, opacità, modalità compatta, ping, sempre in primo piano, avvio con Windows.
- Fissata alla barra delle applicazioni, il tasto destro sull'icona offre *Mostra in primo piano*, *Sempre in primo piano* ed *Esci* (solo mentre l'app è in esecuzione).

### Installazione
1. Scarica `AppIP-Setup-X.Y.Z.exe` dalle [Releases](https://github.com/corbolma/AppIP/releases/latest).
2. Avvialo. **Non servono permessi di amministratore**: installa solo per l'utente corrente.
3. Se compare *"Windows ha protetto il PC"*, clicca **Ulteriori informazioni → Esegui comunque** (l'eseguibile non è firmato digitalmente).
4. Per fissarla alla barra: **Start → cerca AppIP → tasto destro → Aggiungi alla barra delle applicazioni**.

Installazione silenziosa (per l'IT): `AppIP-Setup-X.Y.Z.exe /silent` · Disinstallazione: Impostazioni → App, oppure `"%LOCALAPPDATA%\Programs\AppIP\Uninstall.exe" /uninstall /silent`.

Guida completa e domande frequenti nella [Wiki](https://github.com/corbolma/AppIP/wiki).

### Compilare dal sorgente
Non serve installare nulla: si usa il compilatore C# incluso in Windows (.NET Framework 4.x).

```bat
build.bat                      :: crea AppIP.exe
build-installer.bat            :: crea dist\AppIP-Setup-X.Y.Z.exe
build-installer.bat patch      :: incrementa la versione (1.0.1 -> 1.0.2) e crea l'installer
```

### Privacy
AppIP non raccoglie dati. Le uniche connessioni esterne sono la richiesta dell'IP pubblico (`api.ipify.org`, con `icanhazip.com` e `checkip.amazonaws.com` come riserva) e, solo se lo attivi, il ping verso `1.1.1.1` / `8.8.8.8`.

---

## English

**AppIP** is a small *conky*-style desktop widget and tray icon for Windows 10/11 that shows the computer name, IP addresses, gateway, DNS servers, public IP, live network speed with a 60-second graph, Wi-Fi SSID and signal, and active VPN. Optional ping to the gateway and the Internet (off by default).

- The widget sticks to the desktop (below other windows, survives Win+D) and can be dragged anywhere.
- Click any value to copy it. Right-click for options: adapters to show, light/dark theme, opacity, compact mode, ping interval, always on top, start with Windows.
- **Install:** download `AppIP-Setup-X.Y.Z.exe` from [Releases](https://github.com/corbolma/AppIP/releases/latest). Per-user install, no admin rights required. The executable is not code-signed, so SmartScreen may ask you to confirm (*More info → Run anyway*). Silent install: `/silent`.
- **Build:** run `build-installer.bat` — it uses the C# compiler bundled with Windows (.NET Framework 4.x), no SDK needed.
- **Privacy:** no telemetry. The only outbound requests are the public-IP lookup and, if enabled, ICMP pings.

The user interface is available in **English and Italian**: it follows the Windows display language and can be changed from the menu (*Lingua / Language*).
**Translations are welcome**: adding a language is just a text file, no coding required — see [lang/README.md](lang/README.md).

## Licenza / License
[MIT](LICENSE)
