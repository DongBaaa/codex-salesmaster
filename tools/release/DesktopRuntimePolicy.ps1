function Get-DesktopRuntimeFrameworkVersion { '8.0.31' }

# Parse the published bundle without executing the program or loading its assemblies.
function Assert-DesktopBundledRuntime {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [string]$ExpectedVersion=(Get-DesktopRuntimeFrameworkVersion)
    )
    $ErrorActionPreference='Stop'
    if ($ExpectedVersion -notmatch '^8\.0\.[0-9]+$') { throw 'Unsupported runtime policy version.' }
    if (-not ('TradePlanRuntimeBundleReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Collections.Generic;
public static class TradePlanRuntimeBundleReader {
    static string Name(BinaryReader r) {
        int size=0;
        for(int shift=0;shift<35;shift+=7) {
            byte b=r.ReadByte();
            if(shift==28 && (b & 0xf0)!=0) throw new InvalidDataException("Invalid string length.");
            size|=(b&127)<<shift;
            if((b&128)==0) {
                if(size<0 || size>65536) throw new InvalidDataException("Oversized bundle name.");
                byte[] bytes=r.ReadBytes(size);
                if(bytes.Length!=size) throw new EndOfStreamException();
                return new UTF8Encoding(false,true).GetString(bytes);
            }
        }
        throw new InvalidDataException("Invalid bundle name.");
    }
    public static Dictionary<int,string> Read(string path) {
        byte[] marker={0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae};
        using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var r=new BinaryReader(f,new UTF8Encoding(false,true))) {
            long found=-1,manifest=-1;
            byte[] tail=new byte[0];
            while(f.Position<f.Length) {
                long beginning=f.Position-tail.Length;
                byte[] chunk=r.ReadBytes(1024*1024);
                byte[] header=new byte[tail.Length+chunk.Length];
                Buffer.BlockCopy(tail,0,header,0,tail.Length);
                Buffer.BlockCopy(chunk,0,header,tail.Length,chunk.Length);
                for(int i=8;i<=header.Length-marker.Length;i++) {
                    bool match=true;
                    for(int j=0;j<marker.Length;j++) if(header[i+j]!=marker[j]) {match=false;break;}
                    if(match) {
                        long absolute=beginning+i;
                        if(found>=0 && found!=absolute)throw new InvalidDataException("Ambiguous bundle header.");
                        found=absolute;manifest=BitConverter.ToInt64(header,i-8);
                    }
                }
                tail=new byte[Math.Min(40,header.Length)];
                Buffer.BlockCopy(header,header.Length-tail.Length,tail,0,tail.Length);
            }
            if(found<0)throw new InvalidDataException("Not a supported single-file bundle.");
            if(manifest<=0 || manifest>f.Length-12)throw new InvalidDataException("Invalid bundle offset.");
            f.Position=manifest;
            if(r.ReadUInt32()!=6 || r.ReadUInt32()!=0)throw new InvalidDataException("Unsupported bundle format.");
            int count=r.ReadInt32();
            if(count<=0 || count>100000)throw new InvalidDataException("Invalid bundle entry count.");
            Name(r);
            long depsOffset=r.ReadInt64(), depsSize=r.ReadInt64(), configOffset=r.ReadInt64(), configSize=r.ReadInt64();
            r.ReadUInt64();
            var docs=new Dictionary<int,string>();
            for(int i=0;i<count;i++) {
                long offset=r.ReadInt64(),size=r.ReadInt64(),compressed=r.ReadInt64();
                int kind=r.ReadByte();Name(r);
                long stored=compressed==0?size:compressed;
                if(offset<0 || size<0 || compressed<0 || stored>f.Length || offset>f.Length-stored)
                    throw new InvalidDataException("Invalid bundle entry bounds.");
                if(kind!=3 && kind!=4)continue;
                if(size>16*1024*1024 || stored>16*1024*1024)throw new InvalidDataException("Oversized runtime metadata.");
                if(kind==3 && (offset!=depsOffset || size!=depsSize) || kind==4 && (offset!=configOffset || size!=configSize))
                    throw new InvalidDataException("Inconsistent runtime metadata offsets.");
                long next=f.Position;f.Position=offset;
                byte[] bytes=r.ReadBytes((int)stored);
                if(bytes.Length!=stored)throw new EndOfStreamException();
                if(compressed>0) {
                    using(var input=new MemoryStream(bytes))
                    using(var decoder=new DeflateStream(input,CompressionMode.Decompress))
                    using(var output=new MemoryStream()) {
                        byte[] buffer=new byte[8192];int n;
                        while((n=decoder.Read(buffer,0,buffer.Length))>0) {
                            if(output.Length+n>size)throw new InvalidDataException("Metadata exceeded declared size.");
                            output.Write(buffer,0,n);
                        }
                        bytes=output.ToArray();
                    }
                }
                if(bytes.Length!=size)throw new InvalidDataException("Runtime metadata size mismatch.");
                docs.Add(kind,new UTF8Encoding(false,true).GetString(bytes));
                f.Position=next;
            }
            if(docs.Count!=2)throw new InvalidDataException("Missing runtime metadata.");
            return docs;
        }
    }
}
'@
    }
    $documents=[TradePlanRuntimeBundleReader]::Read([IO.Path]::GetFullPath($Path))
    $config=$documents[4] | ConvertFrom-Json
    $deps=$documents[3] | ConvertFrom-Json
    $frameworks=@($config.runtimeOptions.includedFrameworks)
    if ($frameworks.Count -eq 0 -or @($frameworks | Where-Object name -eq 'Microsoft.NETCore.App').Count -ne 1) {
        throw 'Published executable does not include the required .NET runtime.'
    }
    foreach($framework in $frameworks) {
        if ($framework.version -cne $ExpectedVersion) { throw "Bundled runtime version rejected: $($framework.name) $($framework.version). Expected $ExpectedVersion." }
        $prefix='runtimepack.'+$framework.name+'.Runtime.'
        $packs=@($deps.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith($prefix,[StringComparison]::Ordinal) })
        if($packs.Count -ne 1 -or -not $packs[0].EndsWith('/'+$ExpectedVersion,[StringComparison]::Ordinal)) {
            throw "Runtime config and bundled dependency pack disagree: $($framework.name)."
        }
    }
    [pscustomobject]@{Path=[IO.Path]::GetFullPath($Path);RuntimeVersion=$ExpectedVersion;Frameworks=@($frameworks.name);Verified=$true}
}
