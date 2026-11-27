# TV Monitor

A Windows desktop app (C# / .NET 10 / WinForms) that finds, watches and controls
your TVs from the PC — Android TV, LG webOS and other network TVs — with a full
English and Arabic (RTL) interface.

## Features

- **Discovery** — scans the local network for TVs (SSDP/DIAL, Android TV
  ADB, LG webOS), with saved-MAC recovery and IPv4/IPv6 awareness.
- **Live view** — low-latency live screen streaming (scrcpy for Android TV),
  three quality profiles, optional TV sound, optional PC-mouse control.
- **Remote** — on-screen remote, keyboard commands, app launching and power.
- **Switch on / off** — Wake-on-LAN and standby, with a single tray switch that
  powers on the TV you pick.
- **Memory & storage** — TV-reported RAM, shared storage, usage bars and a
  low-space warning.
- **Direct file transfer** — upload and browse files on the TV (multi-file
  queue, destination folder, progress, cancellation, duplicate protection).
- **Files between PC and TV** — browse internal/external TV storage with cut,
  copy, paste, delete, rename and new folder.
- **Brand marks** — automatic brand detection with vector marks and a TV-type
  badge (Android TV / LG webOS / generic).
- **Settings tab** — language, tray TV, start with Windows, refresh interval,
  live-view defaults, saved to `settings.json` and included in restore points.
- **Safety net** — automatic restore points before every settings change and a
  backup page to review and revert them.

## Requirements

- Windows 10/11 x64.
- .NET 10 SDK (to build).
- An authorized compatible TV on the same network. Android TV features need
  ADB authorization on the TV; no universal TV support is claimed.

## Build and run

```powershell
dotnet publish work/TvMonitor -c Release -r win-x64 --self-contained true `
  -o outputs/publish
```

The self-contained output in `outputs/publish` is the portable app ("TV Monitor.exe").
`scrcpy` binaries are not stored in this repository; copy a scrcpy release next to
the exe for live view of Android TVs.

## Download

Ready-to-run builds are attached to the **Releases** page of this repository
(zip of the self-contained 1.14.0 build).

## Microsoft Store

The `outputs/Store submission` materials (listing drafts, privacy policy, MSIX
preview) are not part of this repository. The Store package is unsigned preview
material only; see the project notes.

## Privacy

The app talks only to TVs on your local network and to scrcpy/ADB locally.
No telemetry, no accounts, no cloud. See the bundled privacy policy
(`work/TvMonitor/PrivacyPolicy.txt`).

## License

All rights reserved. No license is granted for redistribution of the source or
binaries without the author's written permission.

---

## بالعربية

**مراقب التلفزيون** تطبيق ويندوز (.NET 10 / WinForms) يكتشف ويتحكم ويعرض
تلفزيونات الشبكة المحلية — أندرويد TV وLG webOS — بواجهة عربية كاملة من اليمين
إلى اليسار: العرض المباشر، الريموت، التشغيل والإطفاء، الذاكرة والمساحة، نقل
الملفات، وتبويب إعدادات يحفظ التفضيلات مع نقاط استعادة تلقائية.

التحميل الجاهز من صفحة **Releases**، وبناء الإصدار من المصدر عبر أمر
`dotnet publish` أعلاه. لا توجد قياسات أو حسابات سحابية؛ الاتصال بالشبكة المحلية فقط.