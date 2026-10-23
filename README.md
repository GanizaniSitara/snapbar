# SnapBar

Windows 11-style snap bar for Windows 10. Drag a window toward the top edge of a monitor and a
strip of layouts drops down; hover a zone to preview it full size, release to place the window
there. Esc, resizes and drops anywhere else are left to Windows. Nothing appears until a drag
comes near the top edge, so ordinary window drags stay smooth.

One small exe with a tray icon (right-click to exit). No services, hooks DLLs or runtime installs:
it uses the .NET Framework 4.8 that ships with Windows 10.

Build:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -optimize -target:winexe -out:bin\snapbar.exe snapbar.cs
```

By default the bar shows one layout, 2x2 quarters. To choose others, put a `snapbar.ini` next to
`snapbar.exe` with one `layout =` line per layout, in the order you want them shown, and restart
SnapBar:

```
# snapbar.ini
layout = 2x2
layout = halves
layout = thirds
layout = 2/3+1/3
```

Available layouts: `2x2`, `halves`, `rows` (top/bottom), `thirds`, `2/3+1/3`, `1/3+2/3`.
Unknown names are skipped (and reported in the log). Windows are placed flush, compensating for
the invisible DWM borders. Windows running elevated can't be moved from a non-elevated SnapBar.

Run `snapbar.exe --log` to log to `%LOCALAPPDATA%\snapbar\snapbar.log`.
