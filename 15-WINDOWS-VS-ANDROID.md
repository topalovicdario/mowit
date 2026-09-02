# 15 — WINDOWS vs ANDROID: HOW ONE CODEBASE BECOMES FOUR APPS

*This chapter answers the question "so what actually changes between platforms?" — the UI, the logic, and how you edit and verify each one.*

---

## 1. The one-sentence answer

> "There is **one** XAML file and **one** ViewModel per screen. They are compiled four times, once per platform head. Roughly 97 % of the code is shared; the platform-specific parts are isolated behind four well-defined mechanisms, and none of them live in the ViewModels."

If a professor asks *"did you write the Android UI and the Windows UI separately?"* — the answer is **no, and that is the entire point of .NET MAUI**.

---

## 2. What is shared and what is not

| Layer | Shared? | Notes |
|---|---|---|
| `Domain/` | **100 %** | Pure C#. No platform types at all |
| `Application/` | **100 %** | Use cases, geofence, pipeline, state machine |
| `Infrastructure/` | ~90 % | Only the Bluetooth transports have `#if` branches |
| `Presentation/` XAML | **100 %** | One file per page, four renderings |
| `Presentation/` ViewModels | **100 %** | Zero platform code |
| `Platforms/` | 0 % | By definition — one folder per OS |

Concretely: out of ~90 C# files, **three** contain `#if ANDROID` / `#if WINDOWS`, and about 20 live under `Platforms/`.

---

## 3. The four mechanisms

### 3.1 `OnIdiom` / `OnPlatform` — different *values* in the same XAML

A markup extension that picks a value at runtime based on device class (`Phone`, `Tablet`, `Desktop`, `TV`, `Watch`).

```xml
HorizontalOptions="{OnIdiom Default=Fill, Desktop=Center}"
WidthRequest="{OnIdiom Default=-1, Desktop=1120}"
```

**Every use of it in this project — this is the table to show the professor:**

| File | Line | What it changes |
|---|---|---|
| `ControlPage.xaml` | 36–37 | Content fills a phone; capped at 1120 units and centred on desktop |
| `ControlPage.xaml` | 375 | The side joystick panel exists **only** on desktop |
| `ControlPage.xaml` | 406 | The bottom joystick dock exists **only** on phone/tablet |
| `DashboardPage.xaml` | 42–43 | Fill vs. centred 960 |
| `DashboardPage.xaml` | 46–47 | **1 column on phone, 2 columns on desktop** |
| `DashboardPage.xaml` | 124–125 | The telemetry tiles move from *below* the state card to *beside* it |
| `LoginPage.xaml` | 30–31 | Fill vs. centred 460 |
| `ScanPage.xaml` | 12–13 | Fill vs. centred 600 |
| `SchedulePage.xaml` | 37–38 | Fill vs. centred 720 |
| `Styles.xaml` | 7–9 | `OnPlatform` picks the default font family per OS |

The Dashboard is the best one to demo live: the *same* XAML is a vertical phone layout and a two-column desktop layout, decided by four `OnIdiom` expressions and **no code at all**.

> **`OnIdiom` vs `OnPlatform`:** idiom = *form factor* (phone / desktop), platform = *operating system* (Android / iOS / WinUI). This project uses `OnIdiom` for layout, because the thing that matters for layout is screen size, not which OS drew it. That distinction is worth stating — it is a design decision, not an accident.

### 3.2 The `Platforms/` folder — files compiled for one OS only

MSBuild compiles `Platforms/Android/**` only into the `net8.0-android` head, `Platforms/Windows/**` only into the Windows head, and so on. No `#if` needed.

```
Platforms/
├── Android/     MainActivity.cs · MainApplication.cs · AndroidBlePermissionService.cs
│                PlatformRegistration.cs · AndroidManifest.xml
├── Windows/     App.xaml(.cs) · Package.appxmanifest · app.manifest · PlatformRegistration.cs
├── iOS/         AppDelegate.cs · Program.cs · IosBlePermissionService.cs · Info.plist
└── MacCatalyst/ AppDelegate.cs · Program.cs · Entitlements.plist · PlatformRegistration.cs
```

### 3.3 Partial methods — platform-specific DI with no `#if`

The cleanest trick in the project. In shared code:

```csharp
// MauiProgram.cs
static partial void RegisterPlatformPermissions(IServiceCollection services);
```

and one implementation per platform folder:

| File | Registers |
|---|---|
| `Platforms/Android/PlatformRegistration.cs` | `AndroidBlePermissionService` (real runtime permissions) |
| `Platforms/iOS/PlatformRegistration.cs` | `IosBlePermissionService` |
| `Platforms/Windows/PlatformRegistration.cs` | `NullBlePermissionService` |
| `Platforms/MacCatalyst/PlatformRegistration.cs` | `NullBlePermissionService` |

If a platform has no implementation, the C# compiler **erases the call entirely** — zero cost, zero `#if`. That is why Android asks for Bluetooth permission and Windows never does, with no branching in `MauiProgram`.

### 3.4 `#if ANDROID` / `#if WINDOWS` — genuinely different APIs

Used only where the two operating systems expose fundamentally different APIs — all of it in the Classic Bluetooth transport:

| File | What differs |
|---|---|
| `Infrastructure/ClassicBt/GreenTitanSppService.cs` | Android `BluetoothSocket` + `CreateRfcommSocketToServiceRecord` vs Windows `StreamSocket` + `RfcommDeviceService` (16 `#if` blocks) |
| `Infrastructure/ClassicBt/BtDiscoveryReceiver.cs` | The whole file is Android-only (`BroadcastReceiver`) |
| `Infrastructure/Wifi/WifiRobotOptions.cs` | Android tries `10.0.2.2` (emulator alias for the host) first; desktop uses `localhost` |

**That last one is the best small example.** Same feature, different network reality:

```csharp
#if ANDROID
    return new[] { $"http://10.0.2.2:{ServerPort}", … };   // emulator → host machine
#else
    return new[] { $"http://localhost:{ServerPort}" };
#endif
```

---

## 4. Why the *same* XAML can still look different

This is the part most students get wrong, so it is worth understanding properly.

**MAUI does not draw the UI itself.** It maps each cross-platform control onto a **native control** through a *handler*:

| MAUI control | Windows (WinUI 3) | Android |
|---|---|---|
| `Button` | `Microsoft.UI.Xaml.Controls.Button` | `AppCompatButton` |
| `Label` | `TextBlock` | `AppCompatTextView` |
| `Entry` | `TextBox` | `AppCompatEditText` |
| `Switch` | `ToggleSwitch` | `SwitchCompat` |

**But the layout containers — `Grid`, `HorizontalStackLayout`, `VerticalStackLayout` — are shared C#.** Their measure/arrange logic is identical on every platform.

So the difference comes from exactly two places:

1. **Leaf controls report different desired sizes.** An `AppCompatButton` on Android has a built-in **minimum width of 88 dp** and different font metrics than a WinUI `Button`. The same `<Button Text="Undo"/>` is simply *wider* on Android.
2. **The screen is a completely different size.** MAUI measures in device-independent units:
   * Pixel 7 emulator ≈ **412 × 915** units
   * A desktop window ≈ **1200+** units wide

That is a factor of ~3. A row that fits comfortably on desktop has a third of the space on a phone.

---

## 5. Case study — the bug in "Capture by driving"

This is a real defect from this project and the single best example to walk a professor through.

**The broken markup** put the title and the buttons in nested `HorizontalStackLayout`s:

```xml
<Grid ColumnDefinitions="*,Auto">
    <HorizontalStackLayout Grid.Column="0">   <!-- title + count chip -->
    <HorizontalStackLayout Grid.Column="1">   <!-- Undo + Clear -->
</Grid>
```

**Why it worked on Windows and broke on Android — do the arithmetic:**

| | Desktop | Pixel 7 |
|---|---|---|
| Available page width | 1120 (capped by `OnIdiom`) | 412 |
| − joystick side column (desktop only) | −294 | — |
| − page padding `18,·,18` | −36 | −36 |
| − card padding `16` | −32 | −32 |
| **Width for the header row** | **≈ 758** | **≈ 344** |
| Title "Capture by driving" @15 bold | ≈ 160 | ≈ 160 |
| Undo button (Android min-width 88 dp) | ≈ 63 | **88** |
| Clear button | ≈ 63 | **88** |
| Spacing | 16 | 16 |
| **Total needed** | **≈ 302** ✅ fits | **≈ 352** ❌ overflows |

**The real cause is not "Android is different".** It is that **`HorizontalStackLayout` never compresses its children** — it hands each child its full desired width and lets the row overflow. The layout had no constraint at all; Windows just had enough room to hide the mistake.

**The fix** replaces the nested stacks with three real `Grid` columns:

```xml
<Grid ColumnDefinitions="*,Auto,Auto" ColumnSpacing="6">
    <Label Grid.Column="0" Text="Capture by driving"
           LineBreakMode="TailTruncation" MaxLines="1"/>   <!-- takes what is left, truncates -->
    <Button Grid.Column="1" Text="Undo"  MinimumWidthRequest="0"/>   <!-- sizes to content -->
    <Button Grid.Column="2" Text="Clear" MinimumWidthRequest="0"/>
</Grid>
```

Three separate lessons, all worth stating:

* **`Auto` columns win, `*` columns yield.** The buttons get exactly what they need; the title absorbs the remainder. It can never push them off screen.
* **`LineBreakMode="TailTruncation"` + `MaxLines="1"`** makes the failure mode graceful — "Capture by dri…" instead of a broken row.
* **`MinimumWidthRequest="0"`** overrides Android's native 88 dp button minimum, which is invisible on Windows and dominant on a phone.

The point count moved from a chip in that cramped row onto the full-width **Capture point** button (`CapturePointLabel`), where there is always space on any screen.

---

## 6. How to edit and verify each platform

### Switching target in Visual Studio
The debug-target dropdown in the toolbar lists every head. Your current setting is stored in `MowIT.csproj.user`:

```xml
<ActiveDebugFramework>net8.0-android</ActiveDebugFramework>
<ActiveDebugProfile>Pixel 7 - API 34 (Android 14.0 - API 34)</ActiveDebugProfile>
```

### From the command line
```powershell
dotnet build MowIT/MowIT.csproj -f net8.0-android
dotnet build MowIT/MowIT.csproj -f net8.0-windows10.0.19041.0
```

> If the Windows build fails with `MSB3027 … file is locked by MowIT`, the Windows app is still running — close it first. The Android head is unaffected.

### XAML Hot Reload
Run under the debugger, edit the XAML, save — the layout updates on the running device without a rebuild. This is the fastest way to tune a layout on the phone, and it is how a layout like §5 should be verified: **on the narrow screen, not the wide one**.

### Seeing what the app is doing on each platform

| | Windows | Android |
|---|---|---|
| `ILogger` / debug output | VS **Output → Debug** | VS **Output → Debug**, or `adb logcat` |
| Session log | `%LOCALAPPDATA%\Packages\com.greentitan.mowit_…\LocalState\logs\` | `adb exec-out run-as com.greentitan.mowit cat files/logs/<file>` |
| GPS trace CSV | same folder | same command |
| Database | `mowit.db3` in that folder | `files/mowit.db3` via `run-as` |

Both platforms write **identical** log formats, because `EventLogService` and `GpsTraceService` are shared code — only `FileSystem.AppDataDirectory` resolves differently. That is a nice demonstration in itself: the *instrumentation* is cross-platform too.

---

## 7. Rules for layouts that survive both

1. **Use `Grid` with `Auto`/`*` columns for any row that mixes text and buttons.** Never nest `HorizontalStackLayout`s and hope.
2. **Always give text a `LineBreakMode`** (`TailTruncation` or `WordWrap`) when it shares a row with anything else.
3. **Design for the narrow screen first.** If it fits on a phone it fits everywhere; the reverse is false.
4. **Set `MinimumWidthRequest="0"` on compact buttons** — Android's native minimum is otherwise invisible on Windows.
5. **Put variable-length content on full-width elements**, not in cramped header rows (why the point count is on the button).
6. **Use `OnIdiom` for *structure*, not for patching sizes.** Moving the joystick to a side panel on desktop is structural; nudging a font size to make something fit is a smell.

---

## 8. What to say in the defence

> "One XAML per screen, one ViewModel per screen, compiled into four apps. Platform differences are handled in four places and nowhere else: `OnIdiom` for layout structure, the `Platforms/` folder for per-OS entry points, partial methods for per-platform dependency injection, and `#if` only where the operating systems genuinely expose different APIs — which in this project is just the Classic Bluetooth stack.
>
> The interesting part is that MAUI maps controls onto **native** widgets, so a Button really is a WinUI Button on Windows and an AppCompatButton on Android — with different minimum sizes and font metrics — while the layout containers are shared C#. That combination is exactly where cross-platform bugs come from. I hit one: a header row built from stack layouts fitted in 758 units on desktop and needed 352 in the 344 available on a phone. The fix was to express the intent properly with Grid columns, and the real lesson was that the layout had no constraint at all — the desktop screen was just wide enough to hide it."

**Files to have open:** [`DashboardPage.xaml:42-47`](MowIT/Presentation/Pages/DashboardPage.xaml#L42) (one-column vs two-column), [`ControlPage.xaml:375`](MowIT/Presentation/Pages/ControlPage.xaml#L375) (joystick moves), [`MauiProgram.cs`](MowIT/MauiProgram.cs) bottom (the partial method), [`WifiRobotOptions.cs:23`](MowIT/Infrastructure/Wifi/WifiRobotOptions.cs#L23) (`10.0.2.2`), [`Platforms/`](MowIT/Platforms/).
