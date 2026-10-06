# Contributing

Bug reports, speaker-compatibility reports and small pull requests are welcome.

## Reporting

- **Bug:** use the bug report template and attach `%APPDATA%\AirplayFox\airplayfox.log`.
- **Does it work with my speaker?** Only one HomePod has been tested. Use the speaker compatibility template and include the model and firmware version, working or not.

## Build and test

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
dotnet build src/AirplayFox -c Release
dotnet test tests/AirplayFox.Tests
```

## Pull requests

Keep changes focused, run the tests, and describe what you tried on real hardware. By contributing you agree your work is released under the [MIT license](LICENSE).

## Screenshots wanted

The README has no screenshot yet. A real screenshot of the tray menu on Windows is welcome.
