# Contributing to OpenTeslaBuyer

Thank you for helping! Everyone is welcome here, whatever your experience: owners testing on their cars, buyers
suggesting checks, mechanics correcting explanations, and developers of every level. If you're unsure about
anything, open an issue and ask: there are no silly questions.

## Ways to contribute

- **Test on a real car.** Most of the decoding comes from community reverse engineering and has only been run
  against simulators. Open an issue with your model, year and firmware version, what looked right and what looked
  wrong. A screenshot of the Diagnostics page helps.
- **Share a recording.** Press *Record* while connected; the file lands in `Documents\OpenTeslaBuyer\Recordings`.
  Recordings let anyone replay your car's data and fix decoding without the car. They include the car's VIN,
  so attach one only if you're comfortable with that, or say in the issue that you'd like to share it privately.
- **Report a bug or suggest an idea** using the issue templates.
- **Improve the code, the screens or the docs.** Issues labelled *good first issue* are a gentle start.

## Ground rules

1. **Read-only, always.** OpenTeslaBuyer only listens to the car. Adapters open the bus in listen-only or monitor
   mode, and nothing may transmit frames, start or stop anything, unlock features or change the car's
   configuration. Pull requests that do will not be merged.
2. **Logic goes in `OpenTeslaBuyer.Core`.** The WPF project only displays what Core produces, so a future
   cross-platform app can reuse everything. Core must stay plain `net10.0` without Windows dependencies.
3. **Cite where a signal comes from.** When adding or changing a CAN signal, say in the code which DBC file,
   thread or recording it comes from, and whether it has been checked on a car.
4. **Add tests** for decoding and calculations (`tests/OpenTeslaBuyer.Core.Tests`). The simulators in
   `SimulatorAdapter` are a good way to exercise a whole feature end to end.
5. **Be kind.** Assume good intent, explain rather than judge, and help newcomers along.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app is WPF, so it needs Windows; Core and
the tests build on any OS.

```bash
dotnet build OpenTeslaBuyer.sln
dotnet test
dotnet run --project src/OpenTeslaBuyer.App
```

No car? Pick one of the simulators as the source on the Diagnostics page.

## Sending a pull request

1. Fork the repository and create a branch for your change.
2. Keep the change focused; match the style of the surrounding code.
3. Run `dotnet test` and make sure everything passes.
4. Describe what you changed and how you tested it, including the car (model, year, firmware) if you tried it on one.

The build and tests run automatically on every pull request.

## Project layout

See *Project layout* in the [README](README.md#project-layout) for where things live, and *How the numbers are
calculated* for the formulas.

By contributing, you agree that your contribution is licensed under the project's [MIT License](LICENSE).
