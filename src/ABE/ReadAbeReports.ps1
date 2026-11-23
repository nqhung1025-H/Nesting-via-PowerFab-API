$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType=WindowsRuntime]
$null = [Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType=WindowsRuntime]
# BEGIN ASYNC BRIDGE - also exercised by the PowerShell bridge regression check.
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
    $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
$asActionTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and -not $_.IsGenericMethod -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.FullName -eq 'Windows.Foundation.IAsyncAction'
} | Select-Object -First 1
if ($null -eq $asTask -or $null -eq $asActionTask) { throw 'The Windows asynchronous PDF/OCR bridge is unavailable.' }
function Await-Result($operation, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
    $task.GetAwaiter().GetResult()
}
function Await-Action($operation) {
    # PowerShell 5.1 cannot reliably bind a WinRT COM action to the overloaded
    # AsTask method. Select AsTask(IAsyncAction) once and invoke it directly,
    # just as Await-Result selects its generic operation overload explicitly.
    $task = $asActionTask.Invoke($null, @($operation))
    $null = $task.GetAwaiter().GetResult()
}
# END ASYNC BRIDGE
$language = New-Object -TypeName Windows.Globalization.Language -ArgumentList 'en-US'
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
if ($null -eq $engine) { throw 'No Windows OCR language is available.' }
$files = @(([Console]::In.ReadToEnd() | ConvertFrom-Json))
$pages = New-Object System.Collections.ArrayList
$totalPages = 0
foreach ($path in $files) {
    $stage = 'Open PDF'
    $pageNumber = 0
    try {
        $file = Await-Result ([Windows.Storage.StorageFile]::GetFileFromPathAsync([string]$path)) ([Windows.Storage.StorageFile])
        $pdf = Await-Result ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
        $totalPages += $pdf.PageCount
        if ($totalPages -gt 500) { throw 'Choose a batch with no more than 500 report pages.' }
        for ($n = 0; $n -lt $pdf.PageCount; $n++) {
            $pageNumber = $n + 1
            $page = $pdf.GetPage($n)
            $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
            $bitmap = $null
            try {
                $options = New-Object Windows.Data.Pdf.PdfPageRenderOptions
                $scale = [Math]::Min(3.0, ([Windows.Media.Ocr.OcrEngine]::MaxImageDimension - 1) / [Math]::Max($page.Size.Width,$page.Size.Height))
                $options.DestinationWidth = [uint32][Math]::Max(1,[Math]::Round($page.Size.Width * $scale))
                $options.DestinationHeight = [uint32][Math]::Max(1,[Math]::Round($page.Size.Height * $scale))
                $stage = 'Render PDF page'
                Await-Action ($page.RenderToStreamAsync($stream,$options))
                $stream.Seek(0)
                $stage = 'Decode rendered page'
                $decoder = Await-Result ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
                $bitmap = Await-Result ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
                    [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied)) ([Windows.Graphics.Imaging.SoftwareBitmap])
                $stage = 'Recognize report text'
                $result = Await-Result ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                $words = @($result.Lines | ForEach-Object { $_.Words } | ForEach-Object {
                    @{ Text=$_.Text; X=$_.BoundingRect.X; Y=$_.BoundingRect.Y; Width=$_.BoundingRect.Width; Height=$_.BoundingRect.Height }
                })
                $null = $pages.Add(@{ File=[string]$path; Page=$n+1; Words=$words; Error='' })
            } catch {
                # A bad page must not prevent later sheets in a merged report
                # from receiving their own runtime.
                $reason = $_.Exception.GetBaseException().Message
                $null = $pages.Add(@{ File=[string]$path; Page=$pageNumber; Words=@(); Error=($stage + ': ' + $reason) })
            } finally {
                if ($null -ne $bitmap) { $bitmap.Dispose() }
                $stream.Dispose(); $page.Dispose()
            }
        }
    } catch {
        $reason = $_.Exception.GetBaseException().Message
        $null = $pages.Add(@{ File=[string]$path; Page=$pageNumber; Words=@(); Error=($stage + ': ' + $reason) })
    }
}
[Console]::Out.Write((ConvertTo-Json -InputObject @($pages.ToArray()) -Depth 7 -Compress))
