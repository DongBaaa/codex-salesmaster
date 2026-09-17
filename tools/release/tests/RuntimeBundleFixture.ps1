# Metadata-only fixture for packaging tests; never execute it or treat it as a
# runnable runtime. Production builders still execute their normal runtime gate.
function Add-FixtureRuntimeMetadata {
    param([Parameter(Mandatory=$true)][string]$Path)
    $version = '8.0.31'
    $config = [Text.Encoding]::UTF8.GetBytes((@{runtimeOptions=@{includedFrameworks=@(
        @{name='Microsoft.NETCore.App';version=$version},
        @{name='Microsoft.WindowsDesktop.App';version=$version}
    )}} | ConvertTo-Json -Depth 6 -Compress))
    $libraries = @{}
    foreach ($name in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        $libraries["runtimepack.$name.Runtime.win-x64/$version"] = @{}
    }
    $deps = [Text.Encoding]::UTF8.GetBytes((@{libraries=$libraries} | ConvertTo-Json -Depth 6 -Compress))
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
    $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::UTF8, $true)
    try {
        $stream.Position = $stream.Length
        $pointer = $stream.Position
        $writer.Write([long]0)
        $marker = '8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae'
        for ($i=0; $i -lt $marker.Length; $i+=2) { $writer.Write([Convert]::ToByte($marker.Substring($i,2),16)) }
        $depsOffset = $stream.Position; $writer.Write($deps)
        $configOffset = $stream.Position; $writer.Write($config)
        $manifestOffset = $stream.Position
        $writer.Write([uint32]6); $writer.Write([uint32]0); $writer.Write([int]2)
        $writer.Write('isolated-packaging-metadata-fixture')
        $writer.Write([long]$depsOffset); $writer.Write([long]$deps.Length)
        $writer.Write([long]$configOffset); $writer.Write([long]$config.Length)
        $writer.Write([uint64]0)
        foreach ($entry in @(
            @{Offset=$depsOffset;Size=$deps.Length;Kind=3;Name='fixture.deps.json'},
            @{Offset=$configOffset;Size=$config.Length;Kind=4;Name='fixture.runtimeconfig.json'}
        )) {
            $writer.Write([long]$entry.Offset); $writer.Write([long]$entry.Size)
            $writer.Write([long]0); $writer.Write([byte]$entry.Kind); $writer.Write([string]$entry.Name)
        }
        $stream.Position = $pointer
        $writer.Write([long]$manifestOffset)
    } finally { $writer.Dispose(); $stream.Dispose() }
}
