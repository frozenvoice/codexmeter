# Documentation previews

These PNGs render the actual production WPF views with synthetic profiles and quota metadata.
They show the CycleArc product name and Codex provider badges in these Codex account examples.
They are not captures of a user's account or fabricated UI mockups. The sample percentages,
reset times and credits illustrate the layout; they do not promise specific plan entitlements.

| Files | Contents |
| --- | --- |
| `overview-dark.png`, `overview-light.png`, `settings.png`, `widget.png` | English single-account, settings and widget previews |
| `accounts-overview-{en,ko}-{dark,light}.png` | Three fictional accounts, with Work / 업무용 selected and Research / 실험용 marked stale |
| `accounts-manage-{en,ko}-{dark,light}.png` | Matching account manager, local nicknames and saved-order controls |

The multi-account fixtures live in `DocumentationScreenshots.SampleAccounts`. They use the
names Personal / Work / Research (개인용 / 업무용 / 실험용), reserved `example.invalid` email
addresses, display-only paths under `C:\CycleArc-Samples`, and sample weekly usage of
18%, 64% and 91%. No Codex home is created or inspected. Work is selected, so its detail
ring shows 64% used and the quota row includes 36% remaining. Account-management previews
scroll to the bottom so all three sets of order controls are visible. All dates are generated
relative to export time.

Generate on Windows after building the solution:

```powershell
dotnet run --project tests/CycleArc.UiSmoke/CycleArc.UiSmoke.csproj -c Release -- --screenshots docs/images
```

The exporter never runs production startup, requests account data, or reads/writes user settings.
It creates 12 PNGs at 2x resolution through WPF `RenderTargetBitmap`, without taking a desktop
screenshot. It runs under the smoke harness's `OfflineApp`; the live-account diagnostic path
is not used. Each image must be visually inspected before replacing the checked-in files.
To refresh only the multi-account examples, export to an `artifacts/` directory and copy only
the eight `accounts-*.png` files into this directory.
