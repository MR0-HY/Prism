# Third-party software

Prism source is distributed under the MIT license in LICENSE. The self-contained Windows package also contains Microsoft .NET Runtime and Windows Desktop Runtime 10.0.11. Their licenses and .NET third-party notices are copied from the exact restored runtime packages into the portable package's `third-party/` directory.

Build/test dependencies are declared in project files and NuGet lock files. xUnit, the Visual Studio test runner, and Microsoft.NET.Test.Sdk are used for tests and are not shipped as part of the production program. No external UI framework, icon font, stock illustration, or browser runtime is bundled. The Prism geometric icon is authored within this project; system fonts are supplied by Windows.
