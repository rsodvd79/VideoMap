# VideoMap

Applicazione desktop di video mapping in Rust + eframe/egui con backend wgpu.
Piattaforme di rilascio: Windows x64 e macOS Apple Silicon. UI e documentazione
utente in italiano.

## Struttura

Un singolo package Cargo. `src/main.rs` avvia eframe; `app.rs` gestisce stato,
playback e timeline; `ui.rs` contiene pannelli e interazioni.
`model.rs` contiene solo dati serializzabili; `project_io.rs` gestisce file,
Assets e preset. `geometry.rs` calcola omografia e trasformazioni.
`render.rs` e `shaders/mapping.wgsl` condividono il rendering fra tutte le viste.
`outputs.rs` gestisce viewport/monitor; `video.rs` isola le API native LibVLC.

## Comandi

```bash
cargo run --locked
cargo fmt --all -- --check
cargo clippy --all-targets --all-features --locked -- -D warnings
cargo test --locked
cargo test --locked gpu_uses_perspective_not_affine_uvs -- --ignored
cargo build --release --locked
./build-macos.sh --no-open
```

Usare la toolchain fissata in `rust-toolchain.toml` e committare `Cargo.lock`.
Le versioni eframe/egui-wgpu/wgpu devono essere compatibili: usare i re-export
di eframe invece di aggiungere una diversa versione diretta di wgpu.

## Invarianti

- Un player per poligono video, mai per finestra. Le assegnazioni output non
  modificano la visibilità globale del modello o il trasporto.
- La timeline avanza una volta nel ciclo applicativo usando `Instant`, non
  nelle callback delle uscite.
- Prospettiva vera: UV omogenee e divisione per pixel nello shader. Due triangoli
  con UV affini non sono equivalenti.
- Le modifiche geometriche non valide conservano l'ultima geometria valida.
- Frame completi e buffer limitati; nessun accesso UI/GPU dalle callback C.
  Libreria, player e memoria callback devono rispettare l'ordine di teardown.
- Operazioni file e decodifica immagini fuori dal thread UI.
- Nessun import legacy .NET. Nuovi documenti con formato e `schema_version`.
- Validare un caricamento prima di sostituire il progetto corrente; salvare
  tramite file temporaneo e sostituzione del documento.

## VLC e pacchetti

VLC 3 è esterno e caricato dinamicamente. Supportare editor e immagini anche
senza runtime. Architettura VLC e app devono coincidere.
Su macOS il bootstrap rilancia il processo con il proprio ambiente: non
configurare manualmente `DYLD_LIBRARY_PATH` nella shell.

Icone in `assets/`; metadati in `packaging/`.
`build-macos.sh` crea `dist/VideoMap.app`; CI crea ZIP Windows/macOS.
Tag release numerici con prefisso `v`; il job release dipende dalle build.
Non includere runtime .NET, VLC o directory di build nel repository.

Le prove automatiche senza GPU/VLC non certificano la riproduzione o il
fullscreen. Verificare anche più monitor, DPI misti, hotplug e chiusura con
video attivi su entrambe le piattaforme.
