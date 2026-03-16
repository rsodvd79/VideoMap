# VideoMap

VideoMap è uno strumento di video mapping multipiattaforma realizzato con .NET 8 e Avalonia.
Offre una finestra di progettazione per creare superfici di mappatura poligonali e una finestra
di anteprima per visualizzare il risultato finale. I poligoni possono essere associati a immagini
o video, salvati e ricaricati.

## Funzionalità
- Superficie di progettazione con creazione di poligoni e trascinamento dei vertici
- Assegnazione di immagini e video per poligono
- Anteprima video tramite LibVLC (rendering software con callback)
- Ritaglio poligonale e warp a 4 punti (prospettiva) per immagini e video
- Salvataggio/caricamento del progetto con percorsi relativi delle risorse

## Requisiti
- .NET SDK 8.0
- VLC installato (runtime LibVLC)

## Compilazione
```bash
dotnet build VideoMap.sln
```

## Avvio
```bash
dotnet run --project VideoMap.App
```

### Configurazione LibVLC (macOS)
L'applicazione può configurare VLC senza dover impostare manualmente le variabili d'ambiente.
1. Aprire l'applicazione.
2. Nel pannello Proprietà, impostare "Percorso VLC (Contents/MacOS)" su `/Applications/VLC.app`.
3. Fare clic su "Applica": l'app si riavvierà automaticamente per caricare LibVLC.
4. Se VLC è installato in un'altra posizione, indicare quella cartella `.app`.

## Utilizzo
1. Fare clic su "Aggiungi poligono" per creare un quadrato centrato.
2. Trascinare i vertici per modellare il poligono.
3. Selezionare un poligono e importare un file multimediale (immagine o video).
4. Aprire l'Anteprima per visualizzare l'output deformato/ritagliato.

## Licenza
TBD
