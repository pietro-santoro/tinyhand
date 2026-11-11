# TinyHand per Windows

Una manina su un bastoncino che indica le cose sullo schermo — port nativo per Windows 10/11
di [TinyHand](https://github.com/nilbuild/tinyhand) (app macOS di Kamran Ahmed, licenza MIT).

Il codice originale Swift/AppKit resta intatto in `Sources/`; questo port vive in `windows/`
ed è scritto in C# / .NET 8 (WPF + Win32), senza librerie di terze parti.

## Installazione

1. Scarica `TinyHand-win-x64.zip` dalla pagina
   [Releases](https://github.com/pietro-santoro/tinyhand/releases) del fork.
2. Estrai lo zip dove preferisci (consigliato: `%LOCALAPPDATA%\Programs\TinyHand\`).
3. Avvia `TinyHand.exe`. Non serve installare .NET: l'exe è autosufficiente (self-contained, x64).

Windows SmartScreen può avvisare che l'app non è firmata: *Ulteriori informazioni → Esegui comunque*.
Non servono permessi di amministratore né permessi speciali (su macOS serve "Accessibilità";
su Windows l'hook del mouse non richiede autorizzazioni).

Al primo avvio compare la finestra di benvenuto e l'icona nell'area di notifica (vicino all'orologio;
se non la vedi, cercala nella freccia ^ delle icone nascoste).

## Uso

| Azione | Effetto |
|---|---|
| Clic sull'icona nell'area di notifica, oppure **Ctrl+Alt+H** | Accende/spegne il puntatore |
| Clic destro sull'icona | Menu e Impostazioni |
| Clic | "Tap" della manina (sotto non viene cliccato nulla) |
| Tieni premuto e trascina | Disegna sullo schermo |
| Tieni **Ctrl** mentre disegni | Aggiunge una punta di freccia alla fine della linea |
| **Ctrl+Alt+X** | Cancella il disegno |
| **Esc** | Cancella tutto e spegne il puntatore |
| Tieni **Alt** e clicca | Clic "veri" verso le app (configurabile: Alt, Ctrl o disattivato) |
| Doppio tocco di **Shift** | I clic passano alle app finché non ritocchi due volte Shift (l'icona diventa arancione) |

Mentre il puntatore è acceso, una piccola barra in basso sullo schermo permette di scegliere una
delle 36 mani o il puntatore laser, cambiare il colore dell'inchiostro, silenziare il suono del tap
o uscire. La barra compare all'accensione e quando avvicini il puntatore al bordo inferiore.

La barra delle applicazioni, i menu aperti e i pannelli di sistema (Start, ricerca, area di notifica)
ricevono sempre i clic veri; sopra di essi ricompare il cursore normale.

Le **Impostazioni** (clic destro sull'icona → *Settings…*) ricalcano quelle macOS: scorciatoie,
tasto per i clic veri, doppio Shift, avvio all'accesso, mani nella barra (riordinabili trascinandole),
"Make your own hand" (crea una mano personalizzata da un PNG trasparente, ad es. generato con ChatGPT),
dimensione e ombra del puntatore, colore e dissolvenza dell'inchiostro, 15 suoni per il tap.

### Corrispondenza dei tasti macOS → Windows

| macOS | Windows | Dove |
|---|---|---|
| ⌃⌥H / ⌃⌥X (Control+Option) | Ctrl+Alt+H / Ctrl+Alt+X | Scorciatoie globali |
| ⌥ Option (tasto per clic veri, predefinito) | Alt | Impostazioni → General |
| ⌘ Command (tasto per clic veri, opzione) | Ctrl | Impostazioni → General |
| ⌘ Command mentre disegni | Ctrl | Freccia sulla linea |
| ⇧ doppio tocco | Shift doppio tocco | Click-through |
| Esc | Esc | Spegne il puntatore |
| Barra dei menu / Dock | Barra delle applicazioni | Sempre cliccabile, toolbar sopra di essa |

Nel registratore di scorciatoie si possono usare Ctrl, Alt, Shift e Win (sul Mac ⌃, ⌥, ⇧, ⌘).

## Automazione (`tinyhand://`)

Al primo avvio TinyHand registra il protocollo `tinyhand://` per l'utente corrente
(`HKCU\Software\Classes\tinyhand`, nessun permesso admin). Da PowerShell, cmd, AutoHotkey,
PowerToys Run, Flow Launcher ecc.:

```bat
start tinyhand://toggle
```

| Link | Azione |
|---|---|
| `tinyhand://on`, `tinyhand://off`, `tinyhand://toggle` | Accende, spegne o inverte il puntatore |
| `tinyhand://clear` | Cancella il disegno |
| `tinyhand://style/<id>` | Passa a una mano (o `laser`) e accende il puntatore |
| `tinyhand://sound/on`, `/off`, `/toggle` | Suono del tap per il puntatore corrente |

Si può anche lanciare direttamente `TinyHand.exe tinyhand://toggle`. Se TinyHand è già in
esecuzione il comando viene inoltrato all'istanza attiva (una sola istanza per utente, tramite mutex
e named pipe); se non lo è, viene avviato ed esegue il comando.

Mentre TinyHand è in esecuzione, lo stato è in `%APPDATA%\TinyHand\state.json` con lo stesso
schema del Mac: `active`, `pid`, `soundOn`, `style` e `styles` (ogni stile con `id`, `name`, `type`
= `hand`, `custom` o `laser`). Il file viene rimosso all'uscita; il `pid` permette di riconoscere un
file rimasto dopo un crash.

## File

| Percorso | Contenuto |
|---|---|
| `%APPDATA%\TinyHand\settings.json` | Impostazioni (stesse chiavi di `UserDefaults` sul Mac) |
| `%APPDATA%\TinyHand\state.json` | Stato per l'automazione (solo mentre l'app è aperta) |
| `%APPDATA%\TinyHand\Hands\` | Mani personalizzate (`hands.json` + PNG) |
| `%LOCALAPPDATA%\TinyHand\TinyHand.log` | Log |
| `HKCU\...\CurrentVersion\Run\TinyHand` | Avvio all'accesso (se attivo) |

Per disinstallare: esci da TinyHand, cancella l'exe e le cartelle sopra, e la chiave
`HKCU\Software\Classes\tinyhand`.

## Come funziona (mappa dei file)

| macOS (Swift) | Windows (C#) |
|---|---|
| `OverlayController.swift` | `OverlayController.cs` — segue il monitor sotto il cursore, 120 Hz in movimento / 20 Hz da fermo (con `timeBeginPeriod(1)` solo mentre serve) |
| `ArmView.swift` | `ArmRenderer.cs` — stessa geometria (presa, inclinazione, ritardo, pressione, sollevamento, ombra) disegnata con GDI+ |
| finestra overlay `NSWindow` | `OverlayWindow.cs` — finestra layered `WS_EX_LAYERED│TRANSPARENT│TOPMOST│TOOLWINDOW│NOACTIVATE`, aggiornata con `UpdateLayeredWindowIndirect` solo nella zona cambiata |
| `InputTap.swift` (CGEventTap) | `InputHook.cs` — `WH_MOUSE_LL` + `WH_KEYBOARD_LL` su un thread dedicato ad alta priorità con message loop proprio |
| `CursorHider.swift` | `CursorHider.cs` — cursori di sistema trasparenti con `SetSystemCursor`, ripristino con `SPI_SETCURSORS` |
| `Ink.swift` | `Ink.cs` |
| `Toolbar.swift` | `Toolbar.cs` (WPF, finestre non attivabili) |
| `PointerStyle.swift`, `HandMaker.swift` | `PointerStyle.cs`, `HandMaker.cs` |
| `TapSound.swift` (AVAudioEngine) | `TapSound.cs` (winmm `waveOut`, 4 voci, thread dedicato) |
| `HotKeyCenter.swift` (Carbon) | `HotKeys.cs` (`RegisterHotKey`) |
| `AppDelegate.swift` (status item) | `AppController.cs`, `TrayIcon.cs` (icona nell'area di notifica) |
| `SettingsView.swift`, `SettingsWindow.swift`, `Preferences.swift` | `SettingsWindow.cs`, `UI.cs`, `Preferences.cs` |
| `WelcomeWindow.swift` | `WelcomeWindow.cs` |
| `Automation.swift` | `Automation.cs` (+ protocollo URL e istanza singola) |
| `RenderTest.swift`, `E2ETest.swift` | `SelfTests.cs` (eseguiti dalla CI su Windows) |
| `Updater.swift` (Sparkle) | — *Check for Updates…* apre la pagina Releases del fork |

## Compilare

Serve l'SDK .NET 8. Da Windows:

```bat
dotnet publish windows\TinyHand\TinyHand.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Il progetto compila (non esegue) anche da Linux/macOS grazie a `EnableWindowsTargeting`.
Gli asset (`windows/TinyHand/Assets`) sono generati da `assets/` con
`python windows/tools/prepare_assets.py` (richiede Pillow) e sono già nel repository: le 36 mani
sono ridotte a 1024 px di altezza (≈150 KB l'una invece di ≈2 MB).

La CI (`.github/workflows/windows.yml`, `windows-latest`) compila, pubblica, esegue i self-test
(render delle 37 frecce/mani, toolbar, impostazioni, welcome, icone e un test end-to-end con input
simulato), crea `TinyHand-win-x64.zip` e pubblica la Release `win-v<versione>`: o pushando un tag
`win-v*`, o alzando `<Version>` in `TinyHand.csproj` sul branch `windows-port` (la release e il tag
vengono creati dalla CI una sola volta per versione).

## Limitazioni note e differenze dal Mac

- **Cursore**: Windows non ha un "nascondi cursore" globale; TinyHand sostituisce temporaneamente i
  cursori di sistema con cursori trasparenti. Le app che usano cursori propri (alcuni giochi, editor,
  cursori CSS personalizzati nei browser) possono ancora mostrarli. I cursori vengono sempre ripristinati
  all'uscita, in caso di crash (gestori + un piccolo processo "guardiano" `--cursor-guard`) e al
  successivo avvio. In caso estremo: *Impostazioni di Windows → Mouse → Puntatore* o disconnessione.
- **Finestre elevate**: un'app normale non riceve l'input diretto a finestre eseguite come
  amministratore (UIPI): sopra quelle finestre i clic potrebbero passare.
- **Tasto Alt per i clic veri**: su Mac il modificatore viene tolto dal clic; su Windows non è possibile,
  quindi l'app sotto riceve un Alt+clic (o Ctrl+clic se scegli Ctrl). Rilasciare Alt da solo può
  attivare la barra dei menu di alcune app.
- **Ombra e vetro**: la toolbar usa un vetro scuro semitrasparente (niente sfocatura dello sfondo come
  il materiale HUD di macOS); l'ombra della mano è un'approssimazione con sfocatura precalcolata.
- **Schermi intero esclusivo** (alcuni giochi DirectX) possono coprire l'overlay.
- **Aggiornamenti automatici** (Sparkle) non portati: *Check for Updates…* apre la pagina Releases.
- **Avvio all'accesso**: attivato automaticamente solo se l'exe sta in `Program Files` o
  `%LOCALAPPDATA%\Programs` (come il Mac lo attiva solo da `/Applications`); altrimenti dalle Impostazioni.
- Le cartelle Dock/Spaces/Accessibilità del Mac non hanno equivalente e sono state omesse.

## Licenza e crediti

MIT, come l'originale — vedi [LICENSE](../LICENSE).
Copyright (c) 2026 Kamran Ahmed ([@nilbuild](https://github.com/nilbuild)): app originale, design,
mani e suoni. Port Windows distribuito con la stessa licenza e lo stesso avviso di copyright.
