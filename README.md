# OpenTeslaBuyer

A Windows (WPF, .NET 10) tool that reads a Tesla's high-voltage battery data from the car's diagnostic
connector and shows how much capacity is left: state of health, degradation, original vs current capacity,
usable energy, rated range at 100%, per-cell voltages, lifetime energy and equivalent cycles. It also lists the
car's **alerts and warnings** (current and recorded history) with plain-language descriptions. It can export an
HTML report (print it to PDF from the browser).

It **only listens** to the car's CAN bus. It never sends anything to the car, needs no Tesla login and
does not use Service Mode or Toolbox.

**OpenTeslaBuyer is free and open source, and everyone is welcome to make it better.** Try it on your car, report
what you find, add support for another model, improve the screens or the explanations: every contribution helps
people buy and keep their Teslas with confidence. See [Contributing](#contributing) to get started.

## What is supported

| Vehicle | Decoder | Status |
|---|---|---|
| Model 3 (2017–2023), Model Y (2020–2024) | `Model3Profile` | Implemented |
| Model S / X built from 2021 (Plaid, Long Range) | `Model3Profile` | Same battery messages as the Model 3, according to ScanMyTesla; untested here |
| Model S 2012–2021, Model X 2015–2021 | `ModelSXProfile` | Implemented from community decoding; untested on a car |
| Model 3 Highland, Model Y Juniper | `Model3Profile` | Untested; signals may differ |
| Cybertruck | none | Not supported |

The platform is **detected automatically** from the messages on the bus: the tool listens for both families
and picks the one that sends at least two of its own messages. It can be forced under *Advanced*.

### Pre-2021 Model S/X: what differs

- **Capacity when new is not reported** by these cars' battery controller (ScanMyTesla's "full pack when new"
  exists only on the Model 3/Y), so the tool **estimates the pack** instead:
  1. It lists the packs offered for the car's model and year, narrowed by the early VIN battery code
     (H = 85 kWh, S = 60 kWh) and the number of cell groups (84 = the 14-module 60 kWh pack, otherwise 96).
  2. It keeps the packs today's capacity fits (68–104% of the as-new value).
  3. It picks the one whose wear best matches the wear expected for the pack's mileage. Expected wear follows
     Tesla's report of about 12% after 200,000 miles, most of it early; the battery's own odometer is used
     when the car sends it.

  The **Battery pack** picker shows the estimate and its basis, says when another pack fits almost as well, and
  lets you choose the pack or enter a capacity (both remembered per VIN). As-new values are typical owner readings,
  not Tesla specifications:

  | Pack | As new (nominal full pack) | Confidence |
  |---|---|---|
  | 60 kWh, 14 modules (2012–2015) | ≈ 59.0 kWh | ±5%, few readings |
  | 70 kWh (2015–2016) | ≈ 68.5 kWh | ±5%, few readings |
  | 75 kWh (2016–2019, incl. software-limited 60/60D) | ≈ 75.0 kWh | ±4% |
  | 85 kWh (2012–2016) | ≈ 81.5 kWh | ±2% |
  | 90 kWh (2015–2017) | ≈ 85.8 kWh | ±2% |
  | 100 kWh (2016–2021) | ≈ 98.5 kWh | ±3% (new cars read 96.9–102.4) |

  Health on these cars is therefore approximate. Replacement packs and software-limited packs can fool the
  estimate; choose the pack when you know it.
- **Pack current and rated range at 100%** are shown as "—". The published current scaling for message
  `0x102` is inconsistent and the rated consumption is not decoded, so these are left out rather than guessed.
- **Energy message `0x382`** exists in a 10-bit layout (original firmware) and, on later firmware with
  100 kWh packs (nominal values above 102.3 kWh have been reported), presumably the same 11-bit layout as the Model 3.
  Both are detected automatically and checked against the displayed state of charge. A recording from a
  real car would confirm the 11-bit case.

## Hardware

You need two things:

1. **The harness** from your cable set that matches the car. Match the plug to the connector and don't
   force it: connectors and pin layouts changed over the years.
   - Model 3/Y: behind the rear panel of the center console (rear footwell, below the air vents).
   - Model S/X before 2021: a harness that reaches the **powertrain CAN** bus, where the battery controller
     talks. Model S 2012–09/2015 and S/X 09/2015–2021 use different connectors (the later ones a 12-pin
     black connector).
   - Model S/X built 2021 to 04/2024: an S/X-specific harness. The connector (20-pin, blue) is under the
     touchscreen, above the wireless charging pad; lift the back of the phone-charger tray to reach it. The
     harness must put **vehicle CAN** on OBD pins 6/14. Don't assume a Model 3 harness fits electrically.
   - Model S/X built from 05/2024: no harness. Plug the adapter into the OBD-II port in the driver's footwell.

   **Ethernet cables (RJ45, Tesla part 1137658-00-C, "port X861/X863") are for Tesla Toolbox** and are not used here.
2. **A CAN adapter** that plugs into the harness's OBD-II socket:
   - **OBDLink MX+ / EX / SX** (recommended). The STN chip filters in hardware, so data arrives fast and complete.
   - vLinker FS or a generic ELM327: works, but slower. The tool polls one message at a time because these
     adapters overflow on a busy Tesla bus.
   - CANable (or another USB-CAN adapter) with **slcan** firmware: opens in listen-only mode and gets everything.

Bluetooth adapters show up as a COM port after pairing in Windows. If there are two, use the "outgoing" one.

## Using it

1. Plug in the harness and adapter, then **wake the car** (open a door or sit inside, screen on).
2. Choose the source and COM port (115200 baud for OBDLink USB; Bluetooth ports ignore the baud rate) and press **Connect**.
3. The connection log reports a *bus check*: how many message IDs are on the bus and whether the battery
   messages are among them. Capacity figures appear within a few seconds.
4. **Record** saves every frame to `Documents\OpenTeslaBuyer\Recordings` (candump format). A recording can
   be replayed later with the *Replay a recording* source. Recordings are also what's needed to add other models.
5. **Report** saves an HTML report and opens it.
6. **Copy buttons** (⧉) next to the VIN, on each alert, on the configuration card, and on the Battery health card.
   The last one copies a plain-text summary (VIN, health, capacity, range, active alerts) for a message, listing or
   service ticket.

Unplug the adapter when you're done: the diagnostic port is powered and can keep the car awake.

No car nearby? Choose **Simulator: Model 3** or **Simulator: Model S**. **Simulator: Model 3 charging** and
**Simulator: Model S charging** play a whole charging test (rest, charge, stop) four times faster than real time, with
one weak cell group hidden in the pack.

### Troubleshooting

| Log message | Meaning |
|---|---|
| `No reply to ATZ` | Wrong COM port or baud rate, or the adapter has no power. |
| `Bus check: no frames` | The car is asleep, or the harness doesn't reach a CAN bus. |
| `None of the battery messages are on this bus` | The harness reaches the wrong CAN bus (for example chassis CAN). Try another harness from the set. |
| Platform stays "Detecting the platform…" | Neither family's battery messages are arriving. Check the harness, or force the platform under *Advanced*. |
| Energy message stays "detecting…" | Firmware uses a layout the tool doesn't know. Force one under *Advanced*, or record a log. |
| `Adapter buffer overflowed` | Normal for plain ELM327 adapters; the tool carries on polling. |

## Menu and stored data

The menu on the left has six pages:

| Page | What it does |
|---|---|
| **Diagnostics** | The main screen: connect, live data, health, Buyer check, alerts. |
| **Charging test** | Finds weak cell groups by comparing each one at rest and while charging (see below). |
| **Recordings** | Every CAN recording, with car, length and notes. **Replay** plays one on Diagnostics; **Add recordings…** brings in candump logs from other tools. |
| **Reports** | Every saved check, searchable. Each keeps its full report, so it opens again without the car; also *Save as…* and *Copy summary*. |
| **Car history** | Every car checked: health over time, saved checks, alert history across sessions, its recordings, and your notes. |
| **Settings** | Data folders, what is saved automatically, units, database backup. |

A **check is saved when you disconnect** from a car (from replays and simulators only if enabled in Settings).

**Where data is kept** (all local, nothing is uploaded):

| Data | Location |
|---|---|
| Cars, saved checks with their reports, alert history, recordings list, per-car pack choice and notes | `data.db` (SQLite) in `%LOCALAPPDATA%\OpenTeslaBuyer`; *Settings › Change…* copies it elsewhere, e.g. OneDrive |
| CAN recordings | `Documents\OpenTeslaBuyer\Recordings` |
| Reports opened or saved from the app | `Documents\OpenTeslaBuyer\Reports` |
| Preferences (adapter, port, units) | `%LOCALAPPDATA%\OpenTeslaBuyer\settings.json` |

This tool used to be called *Tesla Battery Health*. On its first start as OpenTeslaBuyer it copies
`%LOCALAPPDATA%\TeslaBatteryHealth` to `%LOCALAPPDATA%\OpenTeslaBuyer` (the old folder is left as it was), and it keeps
using `Documents\TeslaBatteryHealth` if recordings are already there.

Data from earlier versions (alert history JSON files, per-VIN capacity choices) is imported into the database on first start;
the old files are left in place. The database code is in Core (`Storage/AppDatabase.cs`), so a future cross-platform app can use it.

## Charging test

The **Charging test** page finds cell groups with higher internal resistance than the rest of the pack. That is how cells
age, and how a damaged group shows itself. It matters because the BMS protects the weakest group, so a single weak
group can make the car limit power and charging. The car stays parked, and the tool only listens.

**Running it:** connect with the car parked and awake, wait about half a minute, then start charging and stay connected
for about a minute at full power. Stopping the charge for a minute adds a second measurement. If the car is already
charging, stop it for a minute instead. The test runs in the background whichever page is open, and its result goes
into the report and the saved check.

**How it works:** every cell group carries the same current. When charging starts or stops, each group's voltage
moves by the current times its resistance, so a group with higher resistance moves further. The test compares
every group with the pack's typical (median) group over 45 seconds just before and just after the change. That
cancels the state of charge and temperature, which move all groups together. The difference divided by the change
in current is each group's extra resistance. A group is flagged when it is at least 15% of the average group's
resistance above the typical one ("much higher" from 35%) and well outside the spread of the other groups.
**Pack resistance** comes from how far the pack voltage moves. It includes busbars and contactors, so the
per-group average and the percentages are approximate.

- A Supercharger gives the clearest result; home charging (around 30 A) is enough to find a group that clearly
  stands out.
- Cold packs read higher. Compare tests made at similar temperatures; a group that stands out every time is the one
  to show a service centre.
- **2012–2021 Model S/X** don't broadcast the pack current, so it is worked out from the BMS's lifetime energy
  counter (`0x3D2`). The counter lags by several seconds, so the test skips the samples next to each change.
  The result is less precise but finds the same groups.
- Driving is ignored: the test only compares resting with charging.

## Buyer check

The **Buyer check** tab (also first in the report, and summarised in the copied text) rates what a used-car buyer
can't easily find out any other way:

| Check | Pass / attention / fail based on | Models |
|---|---|---|
| Battery health | ≥ 80% passes, below 70% (the warranty threshold) fails | All |
| Serious alerts | Crash recorded, airbag near-deployment, pyro fuse fired, welded contactors, isolation faults, isolated or over-discharged cells, limp mode, high-voltage interlock: fail. Other critical alerts: attention | Model 3/Y, 2021+ S/X |
| Pack and mileage history | The battery's odometer versus the car's (pack replaced? odometer changed?), and lifetime energy per km (90–300 Wh/km driving, 100–400 total) | Battery odometer: pre-2021 S/X; energy: all |
| High-voltage isolation | ≥ 1 MΩ passes; below 500 Ω per volt of pack voltage fails | Model 3/Y, 2021+ S/X |
| Fast-charging share | DC versus AC lifetime charging; over 50% gets attention | Model 3/Y, 2021+ S/X |
| Battery warranty (estimate) | Tesla's US terms by model and pack, from the build date or model year and the odometer | All |
| 12 V system | 12 V alerts, and the 12 V supply voltage with the car awake | Model 3/Y, 2021+ S/X |
| Cell balance | Spread at rest: ≤ 20 mV passes, over 50 mV fails | All |
| Touchscreen computer | Estimated from the build date (Model 3/Y) or model year; Model S/X built with MCU1 (Nvidia Tegra 3, before about March 2018) get attention unless upgraded | All |

Thresholds are rules of thumb, explained in each item (hover it in the app). The report also lists what to check by
hand: touchscreen and MCU1 upgrade on older S/X, door handles, air suspension, drive-unit noise, charge-port latch,
water marks, tyre wear, recalls, and whether FSD and free Supercharging transfer.

## Configuration and infotainment

On the Model 3 platform the gateway broadcasts the car's configuration, shown in its own card: country, map region,
connectivity package, audio, Autopilot hardware (HW2.5/HW3) and package, chassis, drivetrain, performance package,
configured pack size, software range limit, Supercharging access, paint, wheels, steering side, tow package, 12 V
battery type and the gateway's "birthday" date. Pre-2021 Model S/X only broadcast their country.

The **touchscreen computer** is not broadcast, so it is estimated from when the car was built: Model S/X used MCU1
(Nvidia Tegra 3) until about March 2018, then MCU2 (Intel Atom) until the 2021 refresh (MCU3, AMD Ryzen); Model 3/Y
switched from Intel Atom to AMD Ryzen between late 2021 and mid 2022. Older S/X are often upgraded to MCU2, which the
bus does not reveal: Controls › Software › Additional Vehicle Information shows the processor on MCU2 and later.

**Not available:** the infotainment software version, map version and update history are not sent on the
diagnostic bus by any Tesla. Read them on the touchscreen under Controls › Software. The car's own record of them
is only reachable through Tesla's servers (the owner's Tesla account), which this tool does not use.

## Alerts and warnings

The **Alerts** tab shows what is active now; **History** shows every alert seen on that car, kept per VIN across
sessions in the database (also shown under Car history). Alerts come from two sources:

| Source | Models | What it is |
|---|---|---|
| **The car's alert messages** | Model 3, Model Y, 2021+ Model S/X | Each controller broadcasts its active alerts as bit flags about once a second. 2,537 alerts from 42 controllers are decoded: battery, high-voltage, charger, charge port, connectors, 12 V, drive units, body, brakes, steering, Autopilot and more. |
| **This tool's battery checks** (`TOOL_w…`) | All supported models | Cell imbalance at rest, cell voltages outside any chemistry's range, battery too hot/cold or unevenly warm, capacity worn or below the warranty threshold, low charge, VIN read errors, unrecognised capacity message. |

- **Descriptions.** Battery, high-voltage, charging and 12 V alerts have hand-written descriptions and severities.
  The rest are interpreted from Tesla's internal names (e.g. `SW_Brick_OV` → "cell group over-voltage") and are
  marked as interpreted. Tesla's owner's manual explains the alerts drivers see on screen.
- **"History" is what this tool observed while connected.** The car's own stored alert log is only reachable through
  Tesla's authenticated service tools, which this tool deliberately does not use.
- **Pre-2021 Model S/X:** their alert messages are not publicly decoded (and Tesla numbers alerts differently on
  them), so only this tool's checks are shown.
- **Adapters:** an OBDLink stores one hardware filter per message and has limited memory. Messages are added
  battery first, then battery-related alerts, then the rest; if memory runs out the log says how many were left out.
  A plain ELM327 checks alert messages every few passes, so alerts update slowly there.
- An alert must hold for 3 seconds before it counts as started or ended, so values hovering at a threshold don't
  flood the history.

## How the numbers are calculated

All capacity figures come from the battery management system (BMS):

- **Original capacity**: on the Model 3 platform, the pack's beginning-of-life energy stored in the BMS
  (message `0x292`). On pre-2021 Model S/X it is typed in (see above).
- **Current capacity**: the BMS's *nominal full pack* estimate (`0x352`, or `0x382` on pre-2021 S/X), buffer included.
- **State of health** = current ÷ original. **Degradation** = 100% − state of health.
- **Usable at 100%** = current capacity − buffer. **Rated range** = usable ÷ the car's rated consumption
  (`0x33A`; Model 3 platform only).
- **Equivalent full cycles** = lifetime discharged energy (`0x3D2`) ÷ original capacity.

The BMS estimate recalibrates after deep discharges and full charges, so it can move a few percent from day
to day. For the most trustworthy figure, read it after the car has been charged to 100% and rested. This is
not an official Tesla battery test. Tesla's battery warranty guarantees 70% retention over the warranty
period.

## Project layout

```
src/OpenTeslaBuyer.Core   adapters (ELM327/STN, SLCAN, replay, simulator), signal decoding, health math, report
src/OpenTeslaBuyer.App    WPF UI (MVVM, Fluent light/dark theme)
tests/OpenTeslaBuyer.Core.Tests
```

- `Adapters/`: `ICanAdapter` implementations. `FakeElmLink` in the tests emulates an ELM327/STN, so the
  protocol handling is tested without hardware.
- `Vehicles/Model3Signals.cs`, `Vehicles/ModelSXSignals.cs`: every CAN signal used, with its source.
- `Vehicles/Model3Profile.cs`, `Vehicles/ModelSXProfile.cs`: decoding for each platform.
- `Vehicles/EnergyStatus.cs`: the three energy-message layouts and their auto-detection.
- `Vehicles/VehicleProfiles.cs`: platform auto-detection (`AutoDetectProfile`).
- `Battery/HealthCalculator.cs`: the formulas above.
- `Battery/ChargeTest.cs`: the charging test, fed every decoded frame by `BatteryMonitor`.
- `Alerts/`: the alert catalog (`alert-catalog-model3.json`, embedded), descriptions, this tool's checks
  (`ToolDiagnostics`), current/history tracking (`AlertTracker`) and per-VIN storage (`AlertHistoryStore`).

**All logic lives in `OpenTeslaBuyer.Core`** (plain `net10.0`, no WPF), so a cross-platform app (MAUI, Avalonia,
Uno) can reuse it as is. The WPF project only displays what Core produces. Core uses source-generated JSON, so it
also works under trimming/AOT. Platform-specific pieces stay outside: the app passes in the folder for alert history,
and serial access goes through `ISerialLink`. `SerialPortLink` covers Windows/macOS/Linux; a mobile app would add a
Bluetooth implementation.

The alert catalog is generated from a DBC file:

```bash
dotnet run tools/GenerateAlertCatalog.cs -- path/to/tesla_model3.dbc src/OpenTeslaBuyer.Core/Alerts/alert-catalog-model3.json
```

To support another platform, add an `IVehicleProfile` (which IDs to listen to and how to decode them),
develop it against recordings from that car, and add its signature IDs to `AutoDetectProfile`.

```bash
dotnet test
dotnet run --project src/OpenTeslaBuyer.App
```

## Contributing

Everyone is welcome: owners, buyers, mechanics and developers alike, whether it's your first open-source contribution
or your hundredth. Some ways to help:

- **Try it on your car** and [open an issue](../../issues/new/choose) with what worked and what looked wrong. The
  code is built from community decoding and simulators, so real cars are the most valuable test there is.
- **Share a recording** (*Record* on the Diagnostics page) so decoding can be checked and extended offline. A
  recording contains your VIN; mention in the issue if you'd rather share it privately.
- **Add or fix a model**: Highland, Juniper, Cybertruck and the 2021+ Model S/X all need real-car verification.
- **Improve the app**: clearer explanations, translations, accessibility, new checks for buyers.
- **Build the cross-platform app**: all the logic is in `OpenTeslaBuyer.Core`, ready for a MAUI or Avalonia front end.

Read [CONTRIBUTING.md](CONTRIBUTING.md) for how to build, test and send a pull request. The one firm rule: the
tool stays **read-only**. Nothing that transmits on the car's bus or changes its configuration will be merged.

## License

[MIT](LICENSE): use it, change it, share it.

OpenTeslaBuyer is an independent community project, not affiliated with or endorsed by Tesla, Inc. Tesla and
Model S, 3, X and Y are trademarks of Tesla, Inc. Readings come from community reverse engineering and are not an
official Tesla diagnosis.

## Credits

Signal definitions come from community reverse engineering:
[joshwardell/model3dbc](https://github.com/joshwardell/model3dbc) (MIT),
[onyx-m2/onyx-m2-dbc](https://github.com/onyx-m2/onyx-m2-dbc) (no licence stated), the
[Tesla Owners Online "Diagnostic Port and Data Access" thread](https://www.teslaownersonline.com/threads/diagnostic-port-and-data-access.7502/),
the alert-matrix definitions in onyx-m2-dbc (alert names are Tesla's own),
wk057's "Tesla Model S CAN Deciphering" notes as transcribed in
[thezim/DBCTools](https://github.com/thezim/DBCTools/tree/master/Samples), and the
[Open Vehicle Monitoring System's Model S module](https://github.com/openvehicles/Open-Vehicle-Monitoring-System-3/tree/master/vehicle/OVMS.V3/components/vehicle_teslamodels) (MIT).
The ScanMyTesla [changelog](https://www.scanmytesla.com/changelog) shows the 2021+ S/X share the Model 3's battery signals.
OBDLink filter and monitor commands follow the OBDLink Family Reference and Programming Manual.
