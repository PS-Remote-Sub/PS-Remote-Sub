# App icon

Source of `src/PSRemoteSub.App/Assets/app.ico` and `app.icns` (the logo from the redesign: accent tile #5AA8FF with the
translate glyph in #08111F, glyph at 2/3 of the tile, corner radius 5/18).

- `app-icon.svg`: Windows / tray (full tile).
- `app-icon-mac.svg`: macOS app icon grid (824 px tile on a 1024 px canvas).

Rebuild (ImageMagick + iconutil):

    magick -background none -density 384 app-icon.svg -resize 1024x1024 win-1024.png
    for s in 16 20 24 32 40 48 64 128 256; do magick win-1024.png -filter Lanczos -resize ${s}x${s} -strip w$s.png; done
    magick w16.png w20.png w24.png w32.png w40.png w48.png w64.png w128.png w256.png app.ico
    magick -background none -density 384 app-icon-mac.svg -resize 1024x1024 mac-1024.png
    mkdir app.iconset; for s in 16 32 128 256 512; do magick mac-1024.png -resize ${s}x${s} app.iconset/icon_${s}x${s}.png; magick mac-1024.png -resize $((s*2))x$((s*2)) app.iconset/icon_${s}x${s}@2x.png; done
    iconutil -c icns app.iconset -o app.icns
