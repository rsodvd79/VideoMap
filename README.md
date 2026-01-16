# VideoMap

VideoMap is a cross-platform video mapping tool built with .NET 8 and Avalonia.
It provides a design window to create polygonal mapping surfaces and a preview
window to see the final output. Polygons can be associated with images or videos,
saved, and reloaded.

## Features
- Design surface with polygon creation and vertex dragging
- Image and video assignment per polygon
- Video preview using LibVLC (software callback rendering)
- Polygon clipping and 4-point warp (perspective) for images and videos
- Project save/load with relative asset paths

## Requirements
- .NET SDK 8.0
- VLC installed (LibVLC runtime)

## Build
```bash
dotnet build VideoMap.sln
```

## Run
```bash
dotnet run --project VideoMap.App
```

### LibVLC configuration (macOS)
The app can configure VLC without manual environment variables.
1. Open the app.
2. In the Properties panel, set "Percorso VLC (Contents/MacOS)" to `/Applications/VLC.app`.
3. Click "Applica" and the app will restart automatically to load LibVLC.
4. If VLC is installed elsewhere, point to that `.app` folder instead.

## Usage
1. Click "Aggiungi poligono" to create a centered square.
2. Drag vertices to shape the polygon.
3. Select a polygon and import media (image or video).
4. Open Preview to see the warped/clipped output.

## License
TBD
