# Third-party notices

PlotPick's own source code is licensed under the [MIT License](LICENSE). That license does not relicense bundled third-party libraries, OCR models, runtime files, or third-party datasets. Their original licenses and notices below continue to apply.

PlotPick is an independent implementation. No GetData executables, UI resources, registration information, or proprietary project formats are included.

| Component | License / upstream |
| --- | --- |
| .NET 10 / WPF | MIT and accompanying third-party notices; https://github.com/dotnet/runtime and https://github.com/dotnet/wpf |
| CommunityToolkit.Mvvm 8.4.0 | MIT; https://github.com/CommunityToolkit/dotnet |
| SkiaSharp 4.153.1 | MIT, Skia BSD-style notices; https://github.com/mono/SkiaSharp |
| OpenCvSharp4 4.11.0.20250507 / OpenCV | Apache-2.0 and bundled third-party notices; https://github.com/shimat/opencvsharp |
| Tesseract .NET 5.2.0 / Tesseract OCR | Apache-2.0; https://github.com/charlesw/tesseract and https://github.com/tesseract-ocr/tesseract |
| Tesseract eng / chi_sim models | Apache-2.0; https://github.com/tesseract-ocr/tessdata_fast |
| PDFiumCore 134.0.6982 | Apache-2.0; https://github.com/Dtronix/PDFiumCore |
| PDFium / bblanchon binaries | PDFium BSD-style license and third-party notices; https://github.com/bblanchon/pdfium-binaries |
| Math.NET Numerics 5.0.0 | MIT; https://github.com/mathnet/mathnet-numerics |
| ClosedXML 0.105.0 | MIT; https://github.com/ClosedXML/ClosedXML |
| DocumentFormat.OpenXml, ClosedXML.Parser, ExcelNumberFormat, RBush, SixLabors.Fonts | Upstream license files extracted from the pinned NuGet packages are in `licenses/` in the distribution |
| xUnit / FlaUI | Apache-2.0 / MIT; development and tests only |
| Inno Setup | Inno Setup license; build tool only |

Full license/notice texts distributed by the NuGet packages are collected into `assets/licenses` and included in `licenses` in the release. Native MSVC runtime files, when bundled, are Microsoft redistributable components and retain their Microsoft license.

## Sample datasets

Forty synthetic fixtures and the demo were created for PlotPick.

Twenty real-data fixtures are new charts rendered from these **CC BY 4.0** datasets:

- Fisher, R. (1936). *Iris* [Dataset]. UCI Machine Learning Repository. https://doi.org/10.24432/C56C76
- Aeberhard, S. & Forina, M. (1992). *Wine* [Dataset]. UCI Machine Learning Repository. https://doi.org/10.24432/C5PC7J
- License: https://creativecommons.org/licenses/by/4.0/
- Changes: subsets and selected features, means/standard deviations, plotting in Matplotlib. These fixtures are not scanned publication images. Per-image source and transformation metadata are in `samples/real-data/sources.json`.

No NIST images are included: the attempted source retrieval returned HTTP 403.
