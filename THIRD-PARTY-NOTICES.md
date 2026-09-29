# Third-party notices

ValheimTesting is licensed under the MIT license by Tys von Gaza; see [LICENSE](LICENSE). The notices below apply to imported or adapted material, not to authorship of the new project.

## ValheimCLI source import

The initial source extraction came through [tvongaza/valheimCLI at 4c4bdc9](https://github.com/tvongaza/valheimCLI/tree/4c4bdc916db49682db855e55f361df47267c4152). Its repository license carried the notice below, retained here for imported material. The separate `Valheim.Testing.Cli` package is ValheimCLI's transport packaged unchanged and carries ValheimCLI's own license.

MIT License

Copyright (c) 2025 warp

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Adapted synthetic terrain

Adapted from ProceduralRoads.Tests/SyntheticWorld.cs at 865120b0ed3f9543a8c3e33b46f136a6c37ab9ac. Kept separately from the game; this is synthetic input, not Valheim terrain generation.

Repository MIT license reproduced below; preserve this notice when redistributing the adapted source.

Copyright 2023 Azumatt/Tykea

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Adapted Procedural Roads test code

Adapted from the ProceduralRoads test projects, as proposed in [jneb802/ProceduralRoads#33](https://github.com/jneb802/ProceduralRoads/pull/33) at [ea6de3c](https://github.com/tvongaza/ProceduralRoads/tree/ea6de3c9dbc1f3bdcea92ea2d3991f5d34e8e3c2), and its follow-up revisions by the same author. The repository MIT license above ("Adapted synthetic terrain") applies to this material too.

- `src/Valheim.Testing.Doubles/Doubles/`: from `ProceduralRoads.Tests/Shims/` (`UnityShims.cs`, `ValheimShims.cs`, `BepInExShims.cs`, `ManualNetworkShims.cs`); Roads' own terrain logic was moved out behind the `Heightmap` hooks. `TerrainWorld` and `ValheimWorldScope` are new, replacing per-test world setup there.
- `src/Valheim.Testing.Game/Fakes/`: from the scripted transports and fake servers in the ProceduralRoads system-test tests.
- `src/Valheim.Testing.Game/PinnedServerRun.cs` and `ServerRunPlan.cs`: from `ProceduralRoads.SystemTests/Program.cs` and `RunPlan.cs`.
- `TransformMatch` in `src/Valheim.Testing.Game/Matching.cs`: from the system tests' `PieceComparison`.

## Mono.Cecil

`Valheim.Testing.Bindings` depends on the [Mono.Cecil](https://github.com/jbevain/cecil) NuGet package, pinned to 0.11.6. The `Valheim.Testing.Bindings.Tool` package ships `Mono.Cecil.dll` 0.11.6 unchanged, because a .NET tool carries its dependencies. No Cecil source is copied into this repository. Its license:

Copyright (c) 2008 - 2015 Jb Evain

Copyright (c) 2008 - 2011 Novell, Inc.

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
