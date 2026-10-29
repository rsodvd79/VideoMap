# VideoMap

Video mapping desktop per **Windows x64 e macOS Apple Silicon**, scritto in
**Rust + eframe/egui**, con rendering GPU **wgpu** e decodifica video **LibVLC 3**.
Non richiede .NET.

## Funzionalità

- Editor di poligoni: creazione di quadrilateri, selezione, trascinamento del
  poligono o dei vertici, nome e ordine di sovrapposizione.
- Immagini e video con **vera trasformazione prospettica a quattro punti**.
  Le modifiche che creano quadrilateri concavi, incrociati o degeneri vengono
  rifiutate. I poligoni generici vengono ritagliati senza prospettiva.
- Play, Pausa/ripresa, Stop e seek globali; Solo, Mute e Loop per video.
- Scene con durata, assegnazioni e riproduzione sequenziale dalla scena
  selezionata fino all'ultima. Una scena senza assegnazioni mostra tutti i
  poligoni; un output senza assegnazioni non mostra media.
- Uscite simultanee in finestre native, dimensioni e assegnazioni indipendenti,
  scelta monitor e fullscreen senza bordi. Proporzioni preservate con bande nere.
  **Esc** esce dal fullscreen; scollegando un monitor, la relativa uscita torna
  in finestra sul monitor principale.
- Un solo player per poligono, condiviso fra editor e uscite, senza audio
  duplicato. Nessuna garanzia di sincronizzazione hardware fra display.
- Progetti JSON `.vmap`, consolidamento in `Assets/`, percorsi relativi,
  libreria media, ricollegamento singolo o ricorsivo dei mancanti ed esportazione
  preset `.vmapout`.

**I file della precedente versione .NET non sono compatibili.**
I nuovi documenti includono formato e versione espliciti. Per trasferire un
progetto, copiare il file `.vmap` insieme alla cartella `Assets/`.

## Requisiti e avvio

- Toolchain Rust indicata in `rust-toolchain.toml` (installabile con rustup).
- Toolchain nativa: Xcode Command Line Tools su macOS; Visual Studio Build Tools
  con C++ su Windows.
- Driver GPU compatibile con wgpu (Metal su macOS, Direct3D su Windows).
- **VLC 3** installato separatamente, della stessa architettura dell'app.
  Immagini ed editor funzionano anche senza VLC; VLC 4 non è supportato.

```bash
cargo run --locked
# Apertura di un progetto:
cargo run --locked -- /percorso/progetto.vmap
```

In **Impostazioni → Riproduzione video** è possibile impostare la cartella di VLC.
Su macOS indicare `/Applications/VLC.app`, `Contents/MacOS` oppure la cartella
delle librerie. Il riavvio configura soltanto l'ambiente del processo figlio:
non impostare manualmente `DYLD_LIBRARY_PATH`.
Salvare le modifiche prima di applicare la configurazione.

Le impostazioni Rust sono separate da quelle precedenti, nella directory
di configurazione utente determinata dal sistema operativo.

## Utilizzo

1. Usare **+ Poligono** oppure il pulsante al centro dell'area di lavoro.
2. Selezionarlo nella scheda **Livelli** e usare **Importa media** nelle proprietà.
3. Configurare sequenze e proiezioni nelle schede **Scene** e **Uscite**.
4. Usare **Apri finestra** per un'uscita o **Apri tutte le uscite** per tutte.
5. Selezionare il monitor nelle proprietà dell'uscita e attivare **Schermo intero**.
   Le finestre non si aprono automaticamente al caricamento del progetto.
6. Usare **Salva progetto** o **File → Salva con nome** per consolidare i media.

La scheda **Media** raccoglie i file e i comandi di ricollegamento.
Le proprietà a destra seguono la scheda e la selezione; **Impostazioni** contiene
nome e dimensioni del progetto e configurazione VLC. I controlli video globali
rimangono nella barra inferiore; la riproduzione delle scene è separata.

Formati immagini: PNG, JPEG, BMP, GIF, TIFF, WebP (immagine statica).
Video: MP4, MOV, MKV, AVI, WebM, MPEG/MPG, secondo i codec disponibili in VLC.
Il video viene decodificato in software e caricato sulla GPU; non è previsto
zero-copy. I limiti di memoria, decodifica e GPU dipendono dall'hardware.

## Verifica e distribuzione

```bash
cargo fmt --all -- --check
cargo clippy --all-targets --all-features --locked -- -D warnings
cargo test --locked
# Test effettivo dello shader con readback, richiede una GPU:
cargo test --locked gpu_uses_perspective_not_affine_uvs -- --ignored
cargo build --release --locked
```

Per una prova nativa con video e almeno due output già configurati:

```bash
cargo run --locked -- --smoke-test /percorso/progetto.vmap
```

La prova apre due uscite e verifica frame video, pausa, stop, fullscreen e
aggiornamenti con editor minimizzato, poi termina automaticamente.
Non sostituisce le verifiche di monitor fisici, DPI e hotplug.

Su macOS `./build-macos.sh` crea e apre `dist/VideoMap.app`;
`./build-macos.sh --no-open` crea soltanto il bundle.
Su Windows l'eseguibile è `target/release/videomap.exe`.

Il workflow GitHub Actions esegue lint, test e build sulle due piattaforme e
produce ZIP. I tag e gli input di release devono rispettare
`^v[0-9]+(\.[0-9]+)*$`, per esempio `v1.2.3`. La release attende entrambi gli
artefatti. VLC non viene redistribuito; firma e notarizzazione non sono incluse.
`VIDEOMAP_VERSION` imposta la versione mostrata e i metadati dei pacchetti,
indipendentemente dalla versione semver Cargo. Per macOS vengono utilizzate
le prime tre componenti numeriche; per i campi numerici Windows le prime quattro,
limitate a 65535 ciascuna. La versione testuale completa viene preservata.

## Licenza

TBD. Prima di redistribuire l'applicazione verificare le licenze delle
dipendenze e del runtime VLC esterno.
