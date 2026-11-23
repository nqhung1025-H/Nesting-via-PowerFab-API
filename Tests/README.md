# Development regression checks

Run with the .NET 8 SDK from the project root:

```powershell
dotnet run --project Tests/BatchSaveChecks.csproj --configuration Release
```

The v1.12 source passes 930 checks: 203 batch/save, 83 packing,
143 inventory/remnant, 66 ABE import, 32 H0373, 17 sheet-selection proposals,
24 multi-file import, 55 naming/packers/mapping, 34 Phase10/11, 54 viewport and
76 window-sizing checks, 40 sheet-report checks, plus 103 ABE cutting-geometry/profile checks. The checks
replay all three actual 240175 sheets and earlier H0306/H0373 imports, verify
curved/common cuts and micro-joints, reject unverified outlines/part sizes/stock,
and exercise result reload and SaveNest XML. PowerFab responses and image
bytes used by these checks are mocked; no live SaveNest call is made.
The 4/5 mm cases use the actual H0373 SPH4.5 files with and without a nominal
filename, verify profile-letter independence and reject ambiguous selection.

`Regression/` groups the C# checks by feature.
`Fixtures/` retains the actual NC/SY2 pairs, sample request XML and regression
IDs required by the checks. Keep these files when modifying the project.
`../docs/Validation/Offline-Checks-v1.12.txt` is the latest executed check output.

The native Windows navigation tests have their own project under `Windows/`.
They must be run on Windows; their execution is separate from the 930 checks.
The Windows runner includes pixel checks for thin vector markings at 1x/8x/32x
zoom and arrow alignment with the drawing viewport. These native rendering
checks were compiled here, but not executed on this Linux host.
The historical native blank-remnant display and live SaveNest/TFS acceptance
remain development work, not proven by the offline suite.

## ABE PDF report checks

`dotnet run --project Tests/BatchSaveChecks.csproj -c Release -- --report-checks`
checks report matching, split/merged page inputs, exact source suffixes, conflict
handling, unavailable values, >24-hour formatting, and single folder/file selection.
The three actual PDFs and OCR word-position captures are under Fixtures/ABE/Reports.
The captures were produced by local OCR and visually checked against the PDFs;
they exercise the same geometric field matcher used by Windows OCR.

On Windows, also run:
`dotnet run --project Tests/Windows/PreviewNavigationChecks.csproj -c Release -- --reports-only`
This executes the embedded Windows PDF/OCR worker against all three actual PDFs,
the merged PDF in a different page order, and a folder containing all NC/SY2
pairs plus one combined PDF under Reports/.
It was compiled, but cannot be executed on the Linux build host.

The overload failure has a separate portable regression check:

```powershell
powershell -NoLogo -NoProfile -File Tests/Interop/AbeAsyncBridgeChecks.ps1
```

It extracts the actual worker's async bridge and substitutes only the WinRT
type names with an overloaded async fixture. Its 14 checks cover explicit method
selection, completion waiting, result preservation, errors and output isolation.
The check passed here using PowerShell 7.4.6. Windows PowerShell 5.1 and actual
WinRT execution still require the Windows checks above.
