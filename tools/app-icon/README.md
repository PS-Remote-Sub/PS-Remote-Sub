# App icon

Source of `src/PSRemoteSub.App/Assets/app.ico` and `app.icns`: blue tile #2E58E1 with a white speech bubble holding a
light bar #B6C4F5, a yellow dot #F6C448 and a blue bar (subtitle lines). Drawn on a 96-unit grid.

- `app-icon.svg`: Windows / tray (full tile).
- `app-icon-mac.svg`: macOS app icon grid (824 px tile on a 1024 px canvas).

Rebuild (ImageMagick + iconutil):

    magick -background none -density 1024 app-icon.svg -resize 1024x1024 win-1024.png
    for s in 16 20 24 32 40 48 64 128 256; do magick win-1024.png -filter Lanczos -resize ${s}x${s} -strip w$s.png; done
    magick w16.png w20.png w24.png w32.png w40.png w48.png w64.png w128.png w256.png app.ico
    magick -background none -density 1024 app-icon-mac.svg -resize 1024x1024 mac-1024.png
    mkdir app.iconset; for s in 16 32 128 256 512; do magick mac-1024.png -resize ${s}x${s} app.iconset/icon_${s}x${s}.png; magick mac-1024.png -resize $((s*2))x$((s*2)) app.iconset/icon_${s}x${s}@2x.png; done
    iconutil -c icns app.iconset -o app.icns
