# Documentation previews

These PNGs render the actual production WPF views in English, with synthetic quota metadata.
They are not captures of a user's account or fabricated UI mockups. The sample percentages,
reset times and credits illustrate the layout; they do not promise specific plan entitlements.

Generate on Windows after building the solution:

```powershell
dotnet run --project tests/CodexMeter.UiSmoke/CodexMeter.UiSmoke.csproj -c Release -- --screenshots docs/images
```

The exporter never runs production startup, requests account data, or reads/writes user settings.
Images are rendered at 2x resolution. Each theme and settings/widget image must be visually
inspected before replacing the checked-in files.
