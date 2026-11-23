NATIVE WINDOWS NAVIGATION CHECKS

Use Windows with the .NET 8 SDK/Desktop Runtime. From the source project root:

dotnet run --project Tests/Windows/PreviewNavigationChecks.csproj --configuration Release

The check project compiles the current extension source into its own executable.
It automatically finds the project root and the XML fixture in Tests/Fixtures/.
Pass another fixture path after -- if required.

Synthetic blue/red layouts test sheet selection, arrows, Nest/Remnant tabs,
selection-menu functions and saved-sheet review behavior. No PowerFab connection
or live SaveNest request is opened by this check runner.

The project builds successfully. Native Windows execution was not available in
the Linux validation workspace and is not counted in the 930 offline checks.

To test the actual embedded PDF renderer/OCR and report-folder import, use:
dotnet run --project Tests/Windows/PreviewNavigationChecks.csproj -c Release -- --reports-only
This checks separate PDFs, one merged PDF, and all NC/SY2 pairs plus a single
merged report under Reports/. Expected times are 02:38:02 (5 mm), 00:22:15
(6 mm), and 01:07:26 (12 mm). No PowerFab writes are performed.
