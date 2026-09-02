# 07 — THE PRESENTATION LAYER (UI)

`MowIT/Presentation/` + `App.xaml` + `AppShell.xaml` + `Resources/`.

---

## 1. MVVM in plain words

**Model-View-ViewModel** splits a screen in three:

* **View** = the XAML file. It describes *what things look like*. It contains **no logic**, only *bindings*: "put whatever is in `BatteryPct` into this label".
* **ViewModel** = a plain C# class. It holds the *state of the screen* (`BatteryPct`, `IsConnected`, `CanStartMowing`) and the *actions* (`StartMowingCommand`). It knows nothing about labels, colours or XAML.
* **Model** = the Domain/Application objects underneath.

The View and ViewModel are connected by one line in the page's constructor:

```csharp
public DashboardPage(DashboardViewModel vm) { InitializeComponent(); BindingContext = _vm = vm; }
```

`DashboardViewModel` arrives by **constructor injection** because both are registered in DI and MAUI Shell resolves pages from the container.

**Why bother?** Two concrete reasons for this project:
1. The ViewModel can be tested without a screen — `new DashboardViewModel(fakeSensors, fakeControl, …)` then assert on `CanStartMowing`. (See [`10-TESTING-AND-QUALITY.md`](10-TESTING-AND-QUALITY.md).)
2. Telemetry arrives from a background thread 10 times a second. With MVVM the ViewModel updates a property and MAUI repaints; without it, every transport callback would need to know about controls.

---

## 2. `BaseViewModel` — [file](MowIT/Presentation/ViewModels/Base/BaseViewModel.cs)

```csharp
public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsNotBusy))] private bool _isBusy;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    public bool IsNotBusy => !IsBusy;

    protected async Task RunSafeAsync(Func<Task> action, string errorPrefix = "") {
        if (IsBusy) return;                                   // ← re-entrancy guard
        IsBusy = true;  ErrorMessage = string.Empty;
        try { await action(); }
        catch (Exception ex) {
            var msg = string.IsNullOrEmpty(errorPrefix) ? ex.Message : $"{errorPrefix}: {ex.Message}";
            await MainThread.InvokeOnMainThreadAsync(() => ErrorMessage = msg);
        }
        finally { await MainThread.InvokeOnMainThreadAsync(() => IsBusy = false); }
    }

    public virtual Task OnAppearingAsync() => Task.CompletedTask;
    public virtual void OnDisappearing() { }
}
```

### 2.1 What the source generators do

`[ObservableProperty] private bool _isBusy;` — the CommunityToolkit.Mvvm **source generator** expands this at compile time into:

```csharp
public bool IsBusy {
    get => _isBusy;
    set { if (!EqualityComparer<bool>.Default.Equals(_isBusy, value)) {
            OnIsBusyChanging(value); OnPropertyChanging(nameof(IsBusy));
            _isBusy = value;
            OnIsBusyChanged(value);  OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsNotBusy));      // ← from [NotifyPropertyChangedFor]
    } }
}
```

That is ~15 lines of `INotifyPropertyChanged` boilerplate replaced by one attribute — **per property**, and this project has over 60 of them. This is the single biggest reason the ViewModels stay readable.

Two related attributes are used throughout:
* `[NotifyPropertyChangedFor(nameof(X))]` — "when I change, also tell the UI that computed property `X` changed".
* `[NotifyCanExecuteChangedFor(nameof(SomeCommand))]` — "when I change, re-evaluate whether that button should be enabled".

`partial` on the class is required because the generator emits the other half of it.

### 2.2 `RunSafeAsync` — one error-handling policy for the whole app

Every command that can fail is written as:

```csharp
await RunSafeAsync(() => _control.SendActionAsync(RobotAction.CaptureBase),
                   "Base capture failed - ensure GPS has a good fix");
```

It gives, in one place: a busy flag (so spinners work and double-taps are ignored), error clearing, exception→`ErrorMessage` conversion with a human prefix, and correct UI-thread marshalling of the result. Every page then renders `ErrorMessage` through the same red banner:

```xml
<Border IsVisible="{Binding ErrorMessage, Converter={StaticResource StringToBool}}" Style="{StaticResource ErrorBanner}">
```

---

## 3. Navigation — Shell

[`AppShell.xaml`](MowIT/AppShell.xaml)

```xml
<Shell Shell.FlyoutBehavior="Disabled" Title="MowIT">
  <ShellContent Route="login" ContentTemplate="{DataTemplate pages:LoginPage}"/>
  <ShellContent Route="scan"  ContentTemplate="{DataTemplate pages:ScanPage}"/>

  <TabBar x:Name="MainTabs">
    <Tab Title="Dashboard" Icon="tab_dashboard.svg"><ShellContent Route="dashboard" …/></Tab>
    <Tab Title="Control"   Icon="tab_control.svg">  <ShellContent Route="control"   …/></Tab>
    <Tab Title="Map"       Icon="tab_map.svg">      <ShellContent Route="map"       …/></Tab>
    <Tab Title="Schedule"  Icon="tab_schedule.svg"> <ShellContent Route="schedule"  …/></Tab>
  </TabBar>
</Shell>
```

* Login and Scan are **outside** the `TabBar` → no tabs are visible before you are connected. The tab bar appears automatically once you navigate to a route inside it.
* Navigation is by **route string**: `await Shell.Current.GoToAsync("//dashboard");`. The `//` prefix means "absolute route, reset the navigation stack" — so you cannot press Back from the Dashboard into the Scan page.
* `ContentTemplate="{DataTemplate pages:LoginPage}"` makes Shell construct the page **lazily and through the DI container**, which is what allows constructor injection of ViewModels.

### 3.1 `AppShell.xaml.cs` — global disconnect handling

```csharp
_connectionSub = connection.ConnectionState.Subscribe(state => MainThread.BeginInvokeOnMainThread(async () => {
    if (state == RobotConnectionState.Connected) _wasConnected = true;
    else if (state == RobotConnectionState.Disconnected && _wasConnected) {
        _wasConnected = false;
        bool intentional = _userInitiatedDisconnect;
        _userInitiatedDisconnect = false;
        await Current.GoToAsync("//scan");
        if (!intentional)
            await Current.DisplayAlert("Disconnected", "Connection to the mower was lost. Please reconnect.", "OK");
    }
}));
```

**Plain:** if the link drops at *any* moment, on *any* page, the app throws you back to the Scan screen and tells you why. Handling this once in the Shell instead of in five ViewModels is the right level of abstraction.

The `_wasConnected` latch prevents the alert from firing at startup, when the state is legitimately `Disconnected` and nothing has been lost yet. The subscription is disposed in `OnHandlerChanged` when the handler becomes null (Shell teardown).

**Deliberate vs. accidental disconnects.** The Shell cannot tell the two apart from the state stream alone, so `DashboardViewModel.DisconnectAsync` publishes a `UserDisconnectRequestedMessage` *immediately before* it drops the link. `AppShell` registers for it and sets `_userInitiatedDisconnect`, which suppresses the alert for exactly one transition. The messenger is synchronous, so the flag is always set before the state change arrives. The navigation still happens in the Shell either way, so there is only one place in the app that knows where a disconnected user should end up.

**Note:** it resolves `IRobotConnection` via `IPlatformApplication.Current?.Services.GetService<…>()` — a service-locator call rather than constructor injection, because `AppShell` is constructed by `App` and not by the DI container. That is a known small compromise; mention it before you are asked.

---

## 4. The six pages

### 4.1 LoginPage — [xaml](MowIT/Presentation/Pages/LoginPage.xaml) · [vm](MowIT/Presentation/ViewModels/LoginViewModel.cs)

Visuals: a `LinearGradientBrush` page background, two big translucent `Ellipse`s as decoration, a 100×100 gradient circle with a 🌿 emoji as the logo, a name `Entry`, a *Remember me* `CheckBox`, and the primary button.

```csharp
[ObservableProperty][NotifyCanExecuteChangedFor(nameof(ContinueCommand))] private string _profileName = "";
[ObservableProperty] private bool _rememberMe;

public LoginViewModel() {
    _profileName = Preferences.Get("profile_name", string.Empty);
    _rememberMe  = !string.IsNullOrEmpty(_profileName);
}

[RelayCommand(CanExecute = nameof(CanContinue))]
private async Task ContinueAsync() {
    if (RememberMe) Preferences.Set("profile_name", ProfileName); else Preferences.Remove("profile_name");
    await Shell.Current.GoToAsync("//scan");
}
private bool CanContinue() => !string.IsNullOrWhiteSpace(ProfileName);
```

**How the disabled button works:** `[RelayCommand(CanExecute = nameof(CanContinue))]` generates an `IRelayCommand` whose `CanExecute` calls that method; `[NotifyCanExecuteChangedFor(nameof(ContinueCommand))]` on the property re-raises `CanExecuteChanged` on every keystroke; MAUI's `Button` greys itself out automatically when a bound command cannot execute. **Zero lines of enable/disable code.**

*Be honest:* this is not authentication. It is a profile name used for the Dashboard greeting. Do not call it "login security".

### 4.2 ScanPage — [xaml](MowIT/Presentation/Pages/ScanPage.xaml) · [vm](MowIT/Presentation/ViewModels/ScanViewModel.cs)

Layout: `Grid RowDefinitions="Auto,Auto,*,Auto"` — header, connection-type card, device list, status footer.

```csharp
public ObservableCollection<MowerDevice> DiscoveredDevices { get; } = new();

_deviceSub = _scanner.DiscoveredDevices.Subscribe(d => MainThread.BeginInvokeOnMainThread(() => {
    if (!DiscoveredDevices.Any(x => x.Id == d.Id)) { DiscoveredDevices.Add(d); ErrorMessage = ""; _evt.Info("SCAN", $"found device {d.Name}"); }
}));
_stateSub = _connection.ConnectionState.Subscribe(s => MainThread.BeginInvokeOnMainThread(() => { ConnectionState = s; … }));
```

**`ObservableCollection<T>`** implements `INotifyCollectionChanged`, so adding an item makes the `CollectionView` insert a row automatically. The de-duplication by `Id` matters because BLE re-advertises the same device many times per second.

**Why the subscriptions live in a re-callable `Subscribe()` method.** Shell **caches** the content it builds from `ContentTemplate`, so navigating away and back reuses the same `ScanPage` and `ScanViewModel` instance. `OnDisappearing` disposes both Rx subscriptions and unregisters from the messenger — which meant that on a second visit the page was permanently dead: a scan would run and find devices, but nothing would ever reach `DiscoveredDevices`. `Subscribe()` is therefore called from **both** the constructor and `OnAppearingAsync`, disposing any previous subscriptions first and calling `Unregister` before `Register` (the toolkit messenger throws on a duplicate registration). `OnAppearingAsync` also seeds `ConnectionState` from `_connection.CurrentState`, because the state stream is a plain `Subject` and does not replay its last value to a new subscriber. This is what makes *disconnect → reconnect to another mower* work.

`ToggleScanAsync`:
```csharp
if (IsScanning) { _scanCts?.Cancel(); await _scanner.StopScanAsync(); IsScanning = false; return; }
if (IsBluetooth) {
    if (!await _permissions.IsBluetoothEnabledAsync()) { ErrorMessage = "Please enable Bluetooth …"; return; }
    if (!await _permissions.RequestPermissionsAsync()) { ErrorMessage = "Bluetooth permissions are required …"; return; }
}
DiscoveredDevices.Clear();
_scanCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // hard timeout
IsScanning = true;
try { await _scanner.StartScanAsync(_scanCts.Token); } catch (OperationCanceledException) { … }
IsScanning = false;
```
Permission checks run **only for Bluetooth** — the WiFi path needs none. `new CancellationTokenSource(TimeSpan)` is a self-cancelling token: a scan can never run forever.

`SelectTransportAsync(string kindName)` parses the enum, stops any running scan, calls `_transport.SelectAsync(kind)`, clears the list and resets state. Computed properties `IsBluetooth`, `IsWifi`, `ModeSubtitle`, `ScanButtonText` drive the two toggle buttons' colours and the button caption — all through `[NotifyPropertyChangedFor]` on `SelectedTransport`.

The list template binds its Connect button back to the **page-level** ViewModel:
```xml
Command="{Binding Source={RelativeSource AncestorType={x:Type vm:ScanViewModel}}, Path=ConnectCommand}"
CommandParameter="{Binding .}"
```
That is the standard MAUI idiom for "the item template needs a command that lives on the parent ViewModel, with the item itself as the parameter".

### 4.3 DashboardPage — [xaml](MowIT/Presentation/Pages/DashboardPage.xaml) · [vm](MowIT/Presentation/ViewModels/DashboardViewModel.cs)

The read-only cockpit. A big state card whose **background colour is bound to `StateColor`**, plus eight stat tiles in a 2-column grid: Battery (with `ProgressBar`), Speed, GPS Signal, Heading, Blade, Mode, Uptime, Last Mowed. A rain banner and an error banner appear conditionally.

**Responsive layout without any code:**
```xml
ColumnDefinitions="{OnIdiom Default='*', Desktop='1*,1.05*'}"
RowDefinitions="{OnIdiom Default='Auto,Auto', Desktop='Auto'}"
…
Grid.Row="{OnIdiom Default=1, Desktop=0}" Grid.Column="{OnIdiom Default=0, Desktop=1}"
```
`OnIdiom` is a XAML markup extension that picks a value per device class. On a phone the state card sits above the tiles; on desktop they sit side by side.

The ViewModel is essentially **a big projection from telemetry to display strings and colours**:

```csharp
public string HeadingLabel { get {
    var deg = ((HeadingRad*180/π % 360) + 360) % 360;
    string[] dirs = { "N","NE","E","SE","S","SW","W","NW" };
    return $"{dirs[(int)Math.Round(deg/45.0) % 8]} {deg:0}°"; } }     // "NE 47°"

public string UptimeLabel => UptimeMinutes <= 0 ? "-" : UptimeMinutes < 60 ? $"{UptimeMinutes}m"
                                                     : $"{UptimeMinutes/60}h {UptimeMinutes%60}m";

public bool   CanStartMowing => IsConnected && HasDatum && HasPath && RobotState != RobotState.Mowing;
public string ReadinessText  => (IsConnected, HasDatum, HasPath) switch {
    (false,_,_) => "Connect to mower first",
    (_,false,_) => "Set base on the Control page",
    (_,_,false) => "Record + save a boundary",
    _           => "Ready to mow" };
```

**The back button.** The header is a `Grid ColumnDefinitions="Auto,*"` with a 44x44 rounded `Button` showing an arrow glyph bound to `DisconnectCommand`. It is the only way in the app to leave a mower on purpose. The command confirms first (`Shell.Current.DisplayAlert`), then **sends `RobotAction.Stop` before dropping the link** — once the app disconnects, `ConnectionGuardProxy` silently drops every later command, so anything still spinning would keep spinning. It then publishes `UserDisconnectRequestedMessage` and calls `_connection.DisconnectAsync()`; the Shell handles the navigation back to `//scan`.

**`ReadinessText` is the best small piece of UX in the app.** Instead of a greyed-out button with no explanation, the tuple-pattern `switch` tells the user *exactly which precondition is missing and where to fix it*. Highlight this in the demo.

`RefreshLastMowedLabel` turns a timestamp into "Just now / 12m ago / 3h ago / 2d ago / 14 Mar 2026" with a property-pattern switch on `TimeSpan`.

`FriendlyError` translates raw protocol codes into human sentences:
```csharp
"MOWER_START_FAIL/NO_DATUM"   => "Can't start - set base first",
"BASE_CAPTURE_FAIL/ACCURACY"  => "GPS not accurate enough yet - wait for RTK",
var s when s.StartsWith("CAPTURE_POINT_FAIL") => "Couldn't mark point - session reset",
```

It registers for **six** messenger message types (connected, disconnected, base captured, capture end, boundary cleared, error, geofence breach) and shows each as a `Toast`.

### 4.4 ControlPage — [xaml](MowIT/Presentation/Pages/ControlPage.xaml) · [vm](MowIT/Presentation/ViewModels/ControlViewModel.cs) · [code-behind](MowIT/Presentation/Pages/ControlPage.xaml.cs)

The largest and most complex screen: 26 KB of XAML, a 665-line ViewModel and a 404-line code-behind that draws a live map with SkiaSharp.

**Structure:** `Grid RowDefinitions="Auto,*,Auto,Auto"` — header / scrollable content / error banner / joystick dock. On desktop the joystick moves to a right-hand column (`IsVisible="{OnIdiom Default=False, Desktop=True}"`) because a 200 px-tall thumb-dock makes no sense with a mouse.

**Cards, top to bottom:**
1. **Mowing status banner** — background bound to `MowingColor` (green/red), headline `MowingHeadline`, plus a manual-state chip.
2. **GPS + DATUM card** — live coordinates, an accuracy chip, and the **Set Base** button, enabled only when `HasGpsFix`.
3. **Mode toggle** — *Capture* (drive-and-record) vs *Capture and plan*.
4. **Plan card** (visible when `IsPlanMode`) — a single-line "Capture by driving" title with a compact point-count chip (`PlanHint`, hidden at zero via `CountToBool`), Undo / Clear, the Capture point button, route-type buttons, Save zone / Mow now. The title was originally a letter-spaced all-caps label plus a full sentence of hint text next to an empty 38x38 icon box; in the narrow phone column all three wrapped onto multiple lines, so the box was dropped and the copy cut to the two things that actually change: the name of the mode and how many points you have.
5. **Drive card** (visible when `IsDriveMode`) — Record, then the **Mark** and **New outline** tiles, then **Save boundary**.
6. **Local map** — an `SKCanvasView` named `LocalMap` (280 px tall) with overlay chips.
7. **Joystick dock** — three concentric decorative `Ellipse`s behind the custom `JoystickView`.

#### The joystick pipeline — the single most sophisticated Rx expression in the project

```csharp
_joystickSub = _joystickSubject
    .Sample(TimeSpan.FromMilliseconds(100))                       // ① throttle to 10 Hz
    .Select(v => IsIdleVector(v)
        ? Observable.Return(v)                                    // ② stop → send once
        : Observable.Return(v).Concat(                            // ③ moving → send once…
              Observable.Interval(TimeSpan.FromMilliseconds(250)) //    …then repeat every 250 ms
                        .Select(_ => v)))
    .Switch()                                                     // ④ a new value cancels the old repeater
    .SelectMany(v => Observable.FromAsync(() => _control.SendMotorCommandAsync(v.lin, v.ang)))
    .Subscribe();
```

Walk it in the defence:
* **①** A finger drag fires hundreds of touch events per second. `Sample` keeps the newest value every 100 ms → 10 commands/s, matched to the link.
* **②/③** The robot has a **600 ms watchdog** (`ManualWatchdogMs` in the simulator, and real firmware does the same) — if it hears nothing it stops the motors. Holding the stick still produces no new touch events, so the app must *repeat* the last value. 250 ms repeat < 600 ms watchdog, with margin.
* An **idle** vector (stick released) is sent once and **not** repeated — no point spamming "stop".
* **④** `Switch()` subscribes only to the newest inner observable, so moving the stick automatically tears down the previous keep-alive loop. Without `Switch()` you would accumulate one repeating timer per joystick movement.
* `SelectMany(Observable.FromAsync(...))` turns each value into the async send and flattens the results.

`IsIdleVector` uses an epsilon (`< 0.001f`) rather than `== 0` — correct float comparison.

#### Velocity mapping

```csharp
public void OnJoystickMoved(float nx, float ny) {
    float lin =  ny * 0.5f;     //  forward +0.5 m/s … backward −0.5 m/s
    float ang =  nx * 1.0f;     //  right stick → positive angular → heading increases → turns right
    LinearVelocity = lin; AngularVelocity = ang;
    _joystickSubject.OnNext((lin, ang));
}
```
**The sign on `ang` follows the heading convention, not the maths convention.** `_heading` is a compass bearing, so *increasing* it turns clockwise — to the right. Pushing the stick right gives `normalizedX = +1`, which must therefore produce a **positive** angular velocity. The code originally negated it, on the assumption that positive yaw is counter-clockwise as it would be in a standard maths frame, and the mower steered inverted: stick left, turn right. It is the same frame mix-up that put the map arrow 90° out of phase (below) — one codebase, two conventions, and the compass one wins because that is what the motion model integrates.

#### The boundary-recording state model

The ViewModel keeps four parallel collections:

| Collection | Contents | Purpose |
|---|---|---|
| `ActivePolygon` (`ObservableCollection<LocalPoint>`) | points of the polygon being drawn | Skia map + counters |
| `ClosedPolygons` (`ObservableCollection<List<LocalPoint>>`) | finished polygons | Skia map |
| `ActiveGpsPolygon` (`List<GpsPoint>`) | same points in GPS | building the saved zone |
| `ClosedGpsPolygons` (`List<List<GpsPoint>>`) | finished polygons in GPS | building the saved zone |

Two parallel sets exist because the two firmware families report differently (see [`05-APPLICATION-LAYER.md §6`](05-APPLICATION-LAYER.md)); `ProjectToLocal` fills the local set when only GPS arrives.

The **enable/disable rules** are pure computed properties:
```csharp
public bool CanStartRecording => HasDatum && !IsRecordingBoundary;
public bool CanCapturePoint   => IsRecordingBoundary;
public bool CanCaptureOutline => IsRecordingBoundary && BoundaryPointCount >= 3;
public bool CanSaveBoundary   => IsRecordingBoundary && (BoundaryPointCount >= 3
                                                      || (PolygonCount >= 1 && BoundaryPointCount == 0));
```
The last one encodes a real rule: *you may save either when the current polygon is a valid shape, or when you have already closed at least one polygon and have no dangling points.* Note the long `[NotifyPropertyChangedFor]` / `[NotifyCanExecuteChangedFor]` attribute stacks on `IsRecordingBoundary` and `BoundaryPointCount` — that is how one state change repaints five buttons.

`AbortRecording(reason)` is the error path: on any `CAPTURE_*` or `BASE_CAPTURE_*` error the ViewModel clears **all four** collections and both counters and toasts "Recording aborted (…) — please press Record to start over". Refusing to continue with a half-broken polygon is the safe choice.

`BuildWalkedGpsZone()` runs on `CaptureEndMessage(Success)`:
1. Prefer the **GPS** polygons; take the one with the most points as the outer boundary.
2. Otherwise fall back to the local-frame polygons and convert them with `LocalProjection(baseGps).ToGps(x/100, y/100)`.
3. Name it `"Walked HH:mm"`, save it via `_repo.SaveAsync`, then call `_geofence.ReloadAsync()` — **the fence arms itself on the boundary you just walked.** That is the safety loop closing.

`OnDisappearing()` sends `RobotAction.Stop` — leave the Control page, the robot stops. Deliberate safety behaviour.

#### The SkiaSharp local map — `ControlPage.xaml.cs`

A hand-written 2-D renderer for the robot-local ENU frame. `OnLocalMapPaint` does:

1. Vertical gradient background.
2. `ComputeBounds()` over the datum, every polygon, plan points and the mower.
3. Add a 2 m margin and enforce a **minimum 10 m span** so a single point does not zoom to infinity.
4. Compute a uniform scale (`Math.Min(scaleX, scaleY)`) so the aspect ratio is preserved, and centre it.
5. Define the world→screen function — note the **Y flip**, because screen Y grows downwards and North grows upwards:
   ```csharp
   SKPoint W2S(LocalPoint p) => new(p.XCm*scale + offX, -p.YCm*scale + offY);
   ```
6. Draw: grid → datum cross → (plan route + plan points) *or* (closed polygons + active polygon) → mower → N/E axis labels → scale bar.

**Adaptive grid spacing:**
```csharp
float[] candidates = { 50, 100, 200, 500, 1000, 2000, 5000 };     // centimetres
foreach (var c in candidates) if (worldSpan / c <= 10) { step = c; break; }
```
"Pick the smallest step that keeps the number of grid lines ≤ 10." Same idea for the **scale bar**: choose from 1/2/5/10/20/50/100/200 m the one whose on-screen width lands between 60 and 140 px. This is how professional map software picks nice round numbers, and it is a nice detail to point out.

**Heading convention — the one that bit.** `SensorSnapshot.HeadingRad` is a **compass bearing**: radians measured *clockwise from North*. That is set by the motion model (`StepGps` does `north += cos h`, `east += sin h`) and assumed by the dashboard's `HeadingLabel` (`N / NE / E …`). But a Skia canvas is a maths plane with +X right and +Y **down**, so drawing the nose needs:

```csharp
float dx =  (float)Math.Sin(h) * 16;    // east  component → screen +X
float dy = -(float)Math.Cos(h) * 16;    // north component → screen −Y
```

The original code used `dx = cos(h)`, `dy = -sin(h)` — i.e. it read the bearing as a standard maths angle measured counter-clockwise from East. The result was an arrow **90° out of phase with the actual motion**: a mower heading due east drove right across the map but pointed its nose straight up. Mixing a navigation frame (clockwise from North) with a graphics frame (counter-clockwise from East) is one of the classic robotics bugs, and it is invisible until you watch the icon move.

**Motion smoothing (33 ms `IDispatcherTimer`, ~30 fps):**
```csharp
float ddx = target.XCm - _dispX, ddy = target.YCm - _dispY;
float dh  = NormalizeAngle(_vm.MowerHeadingRad - _dispHeading);
if (ddx*ddx + ddy*ddy < 0.04f && Math.Abs(dh) < 0.002f) return;   // close enough → stop repainting
const float k = 0.25f;
_dispX += ddx*k;  _dispY += ddy*k;  _dispHeading += dh*k;
LocalMap.InvalidateSurface();
```
An **exponential smoothing filter**: each frame move 25 % of the way to the target. Telemetry arrives at 5–10 Hz; without this the icon would jump. The dead-zone check stops the timer from repainting when nothing is moving (battery saving). `NormalizeAngle` wraps the heading difference into (−π, π] so the arrow rotates the *short* way instead of spinning 350° the wrong direction — a classic and easily-missed bug.

### 4.5 MapPage — [xaml](MowIT/Presentation/Pages/MapPage.xaml) · [vm](MowIT/Presentation/ViewModels/MapViewModel.cs) · [code-behind](MowIT/Presentation/Pages/MapPage.xaml.cs)

A full-screen `mapsui:MapControl` with floating cards on top.

**Five layers, added bottom-to-top (draw order matters):**
```csharp
MapView.Map.Layers.Add(OpenStreetMap.CreateTileLayer());   // 0 base tiles
_savedZonesLayer  // 1 faint green polygons of every saved zone
_trailLayer       // 2 blue line: where the robot has been
_boundaryLayer    // 3 bright green polygon being drawn + vertex dots
_routeLayer       // 4 orange line: the planned mowing path
_robotLayer       // 5 red dot: the robot (drawn last = always on top)
```

**Projection to web-map coordinates:**
```csharp
var (x, y) = SphericalMercator.FromLonLat(p.Longitude, p.Latitude);
```
OpenStreetMap tiles are in EPSG:3857 (Spherical Mercator), so every GPS point must be converted before it becomes an `MPoint`/`Coordinate`. Reverse for taps:
```csharp
private void OnMapInfo(object? sender, MapInfoEventArgs e) {
    if (!_vm.IsDrawingMode) return;
    var (lon, lat) = SphericalMercator.ToLonLat(e.MapInfo.WorldPosition.X, e.MapInfo.WorldPosition.Y);
    MainThread.BeginInvokeOnMainThread(() => _vm.OnMapTapped(new GpsPoint(lat, lon)));
}
```
The `IsDrawingMode` check is what makes tapping *sometimes* add a point and otherwise just pan the map.

**Auto-centre once:**
```csharp
if (!_mapCentred && (pos.Latitude != 0 || pos.Longitude != 0)) {
    _mapCentred = true; MapView.Map.Navigator.CenterOn(pt); MapView.Map.Navigator.ZoomTo(3.0); }
```
Centre on the first *real* fix, then never again — otherwise the map would fight the user's panning.

**Why the code-behind is not "cheating MVVM":** Mapsui layers are imperative graphics objects with no bindable surface. The accepted MVVM answer is that the View may contain **view-specific rendering code** as long as no business logic lives there. The page only *listens*:
```csharp
_vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MapViewModel.RobotPosition)) UpdateRobotPin();
                                   else if (e.PropertyName == nameof(MapViewModel.RouteVersion)) UpdateRoute(); };
_vm.BoundaryPoints.CollectionChanged += (_, _) => UpdateBoundary();
```

**The `RouteVersion` trick:** `RoutePoints` is a plain `List<GpsPoint>` (not observable) because it is rebuilt wholesale and can hold thousands of points — an `ObservableCollection` would raise thousands of change notifications. Instead the ViewModel increments an `[ObservableProperty] int _routeVersion` after rebuilding, and the page redraws on that one notification. A deliberate, defensible performance decision.

**`MapViewModel` highlights:**
```csharp
_sensorSub = _sensors.SensorStream.Sample(TimeSpan.FromSeconds(1)).Subscribe(s => MainThread…(() => {
    RobotPosition = s.Gps;  RobotHeadingDeg = s.HeadingRad * 180f / MathF.PI;
    if (RobotTrail.Count == 0 || RobotTrail[^1].DistanceTo(s.Gps) > 0.3) RobotTrail.Add(s.Gps);  // ← distance filter
}));
```
The trail only grows when the robot has actually moved 30 cm — a stationary robot does not add 3 600 identical points per hour.

```csharp
var route = await Task.Run(() => _planner.Plan(zone, index));    // ← off the UI thread
double meters = 0; for (int i = 1; i < route.Count; i++) meters += route[i-1].DistanceTo(route[i]);
RouteInfo = $"{route.Count} waypoints, {meters:F0} m, {_planner.StrategyName(index)}";
```
Planning thousands of waypoints is CPU work, so it runs on a thread-pool thread and the UI stays responsive.

`NotifyBoundaryChanged()` is called after every edit and **invalidates any existing route** ("No route - tap Calc Route") — because a route computed for an old polygon is wrong. It then refreshes all four `Can…` properties and both commands' `CanExecute`.

**Geofence re-arming.** `MapViewModel` takes `GeofenceMonitor` in its constructor and calls `await _geofence.ReloadAsync()` after saving a zone (both in `SaveZoneLocallyAsync` and in `SendBoundaryToRobotAsync`, which also persists) **and after deleting one**. The monitor otherwise only loads zones when the robot connects, so without these calls a boundary drawn on the map would not actually protect anything until the next reconnect, and deleting the armed zone would leave the fence watching a polygon that no longer exists. `ControlViewModel.SavePlanZoneAsync` does the same for Plan-mode zones, matching what `PersistWalkedZoneAsync` already did for walked boundaries — the rule is simply *"whenever the saved-zone set changes, re-arm"*.

### 4.6 SchedulePage — [xaml](MowIT/Presentation/Pages/SchedulePage.xaml) · [vm](MowIT/Presentation/ViewModels/ScheduleViewModel.cs)

A creation card (7 day buttons, a zone `Picker`, a `TimePicker`, Save) plus a list of existing schedules rendered with `BindableLayout` inside a `VerticalStackLayout` (chosen over `CollectionView` to avoid a nested-scrolling conflict inside the page's `ScrollView`).

```csharp
[ObservableProperty] private bool _monday, _tuesday, _wednesday, _thursday, _friday, _saturday, _sunday;

public DayOfWeek[] SelectedDays => new[] {
    (Monday, DayOfWeek.Monday), (Tuesday, DayOfWeek.Tuesday), … }
    .Where(x => x.Item1).Select(x => x.Item2).ToArray();

[RelayCommand] private void ToggleDay(string day) => …;   // one command, seven buttons, string parameter
```
Seven booleans in one `[ObservableProperty]` declaration (the generator handles each), and a single command parameterised by the day name — the buttons' colours come from `{Binding Monday, Converter={StaticResource ActiveColor}}`.

`OnAppearingAsync` reloads both schedules and zones and **preserves the user's zone selection across the reload**:
```csharp
var selectedId = SelectedZone?.Id;
SavedZones.Clear(); foreach (var z in zones) SavedZones.Add(z);
SelectedZone = SavedZones.FirstOrDefault(z => z.Id == selectedId);
```
Necessary because `Picker.SelectedItem` matches by reference, and reloading creates new objects. A small, real bug that has been correctly handled.

`MowNowAsync` shows a progress bar driven by `IProgress<int>`:
```csharp
var progress = new Progress<int>(p => { SendProgress = p; OnPropertyChanged(nameof(SendFraction)); });
await _sendZone.ExecuteAsync(schedule.ZoneId.Value, startMowingAfter: true, progress);
```
`Progress<T>` captures the current `SynchronizationContext`, so its callback is automatically marshalled back to the UI thread — that is why there is no explicit `MainThread` call here.

The `Switch` bound to `IsActive` uses two-way binding, and the `▶` and `🗑` buttons use the same `RelativeSource AncestorType` idiom as the Scan page.

---

## 5. Custom controls

### 5.1 `JoystickView` — [file](MowIT/Presentation/Controls/JoystickView.cs)

A `SKCanvasView` subclass drawing a virtual analogue stick.

```csharp
public static readonly BindableProperty ThumbColorProperty =
    BindableProperty.Create(nameof(ThumbColor), typeof(Color), typeof(JoystickView), Colors.Green,
        propertyChanged: (b,_,_) => ((JoystickView)b).InvalidateSurface());
```
**`BindableProperty` is what makes a custom control bindable** — with it, `ThumbColor="{StaticResource EarthPrimary}"` works in XAML and a change repaints the canvas.

Painting: background circle → cross-hair lines → coloured ring → drop shadow → thumb → white highlight. All with `using var` on every `SKPaint` (Skia objects are unmanaged and must be disposed).

Touch handling:
```csharp
protected override void OnTouch(SKTouchEventArgs e) {
    e.Handled = true;                               // ← stops the parent ScrollView stealing the gesture
    switch (e.ActionType) {
        case Pressed:  _dragging = true; MoveThumb(e.Location); break;
        case Moved:    if (_dragging) MoveThumb(e.Location);    break;
        case Released: case Cancelled: case Exited: if (_dragging) Recenter(); break;
    }
}
private void MoveThumb(SKPoint pos) {
    float dx = pos.X-_center.X, dy = pos.Y-_center.Y, dist = sqrt(dx*dx+dy*dy);
    if (dist > _maxRadius) { dx = dx/dist*_maxRadius; dy = dy/dist*_maxRadius; }   // clamp to the circle
    _thumbOffset = new SKPoint(dx, dy);
    JoystickMoved?.Invoke(this, new JoystickEventArgs(dx/_maxRadius, -dy/_maxRadius));  // ← Y inverted
    InvalidateSurface();
}
```
Three details worth naming: the **radial clamp** keeps the thumb inside the ring (`dx/dist*maxRadius` is the unit vector times the radius); the **Y inversion** makes "up = forward"; and handling `Cancelled`/`Exited` guarantees the robot stops if your finger slides off the control or a phone call interrupts — a safety-relevant edge case.

The control raises plain .NET events; `ControlPage` forwards them to the ViewModel:
```csharp
private void OnJoystickMoved(object? s, JoystickEventArgs e) => _vm.OnJoystickMoved(e.NormalizedX, e.NormalizedY);
```

### 5.2 `GpsStatusBadge` — [xaml](MowIT/Presentation/Controls/GpsStatusBadge.xaml) · [cs](MowIT/Presentation/Controls/GpsStatusBadge.xaml.cs)

A small `ContentView` with two `BindableProperty`s (`FixType`, `Accuracy`) that recolours itself green/orange/blue/red per fix quality. **Currently not used by any page** — the Control page renders its own accuracy chip and the Dashboard has a GPS tile. Say so if asked; it is a reusable component that was superseded.

---

## 6. Converters — [`Presentation/Converters/Converters.cs`](MowIT/Presentation/Converters/Converters.cs)

19 `IValueConverter` classes, all registered as `StaticResource` in [`App.xaml`](MowIT/App.xaml).

**Plain:** a converter is a translator that sits inside a binding. The ViewModel holds a `bool`; the XAML needs a `Color`. Rather than adding a `Color` property to the ViewModel for every visual state, you write the translation once and reuse it.

| Key | Class | Translation |
|---|---|---|
| `PctToFraction` | `PctToFractionConverter` | `int 0..100` → `double 0..1` (for `ProgressBar.Progress`) |
| `InvertBool` | `InvertBoolConverter` | `bool` → `!bool` (the only two-way one) |
| `BatteryToColor` | `BatteryToColorConverter` | <20 red, <40 orange, else green |
| `StateToColor` | `ConnectionStateToColorConverter` | Connected green / Connecting orange / Scanning blue / else grey |
| `StringToBool` | `StringToBoolConverter` | non-empty string → `true` (drives every error banner's `IsVisible`) |
| `CountToBool` | `CountToBoolConverter` | `int > 0` → `true` |
| `ActiveColor` | `ActiveColorConverter` | selected = dark green, else grey |
| `BoolToEarthOrGrey` | `BoolToEarthOrGreyConverter` | selected = `#4A7C59`, else grey |
| `BoolToOpacity` | `BoolToOpacityConverter` | `true` → 1.0, `false` → 0.35 (visual "disabled") |
| `BoolToSuccessColor` | `BoolToSuccessColorConverter` | green / orange |
| `DrawModeLabel`, `ZonesPanelLabel` | … | button captions that flip with state |
| `BladeToText`, `BoolToOnOff`, `BoolToRainText`, `RainToColor`, `BoolToScanLabel`, `RecordModeLabel`, `RecordColor`, `PolygonModeLabel` | … | **registered but no longer referenced by any page** — the ViewModels compute those strings directly now |

Almost all implement `ConvertBack` as `throw new NotSupportedException()` — correct, because a one-way visual translation has no meaningful inverse.

---

## 7. Resources, styling and theming

### `Resources/Styles/Colors.xaml`
Defines the palette twice — a semantic "Earth" set and a MAUI-conventional set:
```
EarthPrimary #75C915   EarthPrimaryDark #4F8F0E   EarthPrimarySoft #EEF7E9
EarthSoft #A3B18A      EarthAccent #DDB892        EarthDanger #C44536
TextMain #151915       TextMuted #747B74
PageBg #F2F3EF         CardBg #FFFFFF             SoftPanelBg/InputBg #F3F6F1
PageBgDark #121412     CardBgDark #1E221E         …
```
The `…Dark` variants exist so styles can use `{AppThemeBinding Light=…, Dark=…}` and follow the OS light/dark setting.

### `Resources/Styles/Styles.xaml`
Named styles reused everywhere: `PageTitleLabel`, `SectionLabel`, `CardBorder`, `LargeCardBorder`, `StatTileBorder`, `InputField`, `IconBadge`, `PrimaryButton`, `SecondaryButton`, `DangerButton`, `ErrorBanner`, `SuccessBanner`. Because they use `AppThemeBinding`, dark mode works without any code.

### Other resources
* **Fonts** — `OpenSans-Regular.ttf`, `OpenSans-Semibold.ttf`, `Pixie.ttf`, registered in `ConfigureFonts`.
* **Images** — four SVG tab icons (`tab_dashboard/control/map/schedule`), `lawn.png`, `dotnet_bot.png`.
* **AppIcon / Splash** — one SVG each, expanded to every platform size at build time, tinted `#1B5E20`.

### The visual language (worth one slide)
Every page follows the same recipe: a soft off-white background, one or two very low-opacity decorative `Ellipse`s bleeding off the corners, white rounded cards (`RoundRectangle 20–28`) with soft shadows, a small coloured `BoxView` bar as a section marker, ALL-CAPS letter-spaced micro-labels above values, and one saturated green as the only accent. It reads as a deliberate design system rather than default controls — mention it, because examiners notice UI polish.

---

## 8. Page lifecycle

Every page overrides:
```csharp
protected override async void OnAppearing()   { base.OnAppearing(); await _vm.OnAppearingAsync(); }
protected override void      OnDisappearing() { base.OnDisappearing(); _vm.OnDisappearing(); }
```

| Page | `OnAppearingAsync` | `OnDisappearing` |
|---|---|---|
| Dashboard | refresh "last mowed", re-raise `Greeting` | — |
| Map | reload saved zones, redraw them | — |
| Schedule | reload schedules + zones | — |
| Scan | **rebuild the subscriptions** (`Subscribe()`), seed the connection badge from `CurrentState` | cancel scan, dispose both subscriptions, unregister from the messenger |
| Control | — | **send `RobotAction.Stop`**, stop the animation timer |

`OnAppearing` being `async void` is the one place `async void` is acceptable — it is an event handler and MAUI's override signature is `void`.
