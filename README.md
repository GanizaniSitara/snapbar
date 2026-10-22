# SnapBar

Windows 11-style snap bar for Windows 10. Drag a window toward the top edge of a monitor and a
strip of layouts drops down; hover a zone to preview it full size, release to place the window
there. Esc, resizes and drops anywhere else are left to Windows.

One small exe with a tray icon (right-click to exit). No services, hooks DLLs or runtime installs:
it uses the .NET Framework 4.8 that ships with Windows 10.

Build:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -optimize -target:winexe -out:bin\snapbar.exe snapbar.cs
```

Landscape monitors get halves, 2/3 + 1/3, thirds, half + two stacked, and quarters; portrait
monitors get two rows, three rows, top + two below, and quarters. Windows are placed flush,
compensating for the invisible DWM borders. Windows running elevated can't be moved from a
non-elevated SnapBar.

Set `SNAPBAR_LOG=1` to log geometry to `%LOCALAPPDATA%\snapbar\snapbar.log`.
