# ABE Sheet Report runtime fixtures

The three single-page PDFs are unchanged reports supplied in NC Data(1).zip.
Their Data Name and Process Time values are embedded bitmap text. The JSON
files capture OCR word positions from rendered report headers, produced by
local Tesseract sparse-text OCR and visually checked against each PDF. They
exercise the field matcher without requiring Windows in the offline suite.
Expected times: 5 mm 02:38:02; 6 mm 00:22:15; 12 mm 01:07:26.

Merged-sheet-reports.pdf combines those same pages in 12, 5, 6 mm order for the
native Windows PDF/OCR check. File/page ordering must not determine the match.
The source NC/SY2 files are byte-identical to the existing ../240175 fixtures.
