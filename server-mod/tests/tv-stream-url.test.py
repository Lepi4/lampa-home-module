"""Run the actual C# URL mapper against remote clients and unchanged upstreams."""
import os, pathlib, subprocess, tempfile

root=pathlib.Path(__file__).resolve().parents[2]
source=(root/'server-mod/HomeMedia/TvGuide.cs').read_text(encoding='utf8')
start=source.index('    public static string ClientStreamUrl(')
end=source.index('\n    public static bool Enabled',start)
method=source[start:end]
assert 'url = TvStore.ClientStreamUrl(c.Url, Request.Host.Host)' in source
with tempfile.TemporaryDirectory(prefix='lmpc-stream-url-') as name:
    folder=pathlib.Path(name)
    (folder/'Check.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>')
    (folder/'Program.cs').write_text('''
using System;
public class Program {
'''+method+'''
static void Check(string original,string host,string expected){
 var actual=ClientStreamUrl(original,host);
 if(actual!=expected && !(expected.StartsWith("http://[fd00:") && new Uri(actual).Equals(new Uri(expected))))throw new Exception(actual+" != "+expected);
}
public static void Main(){
 Check("http://127.0.0.1:3002/stream/ABC?unify=1","192.168.1.197","http://192.168.1.197:3002/stream/ABC?unify=1");
 Check("http://localhost:3002/stream/ABC","box.local","http://box.local:3002/stream/ABC");
 Check("http://[::1]:3002/stream/ABC","[fd00::197]","http://[fd00::197]:3002/stream/ABC");
 Check("http://192.168.1.197:3002/stream/ABC","other.local","http://192.168.1.197:3002/stream/ABC");
 Check("https://upstream.example/live?id=1","box.local","https://upstream.example/live?id=1");
 Check("/stream/ABC","box.local","/stream/ABC");
 Check("http://127.0.0.1:3002/stream/ABC","","http://127.0.0.1:3002/stream/ABC");
 Console.WriteLine("PASS 7 client stream URL cases: LAN, hostname, IPv6, query, upstream and relative preservation");
}}
''',encoding='utf8')
    if os.name=='nt':
        subprocess.run(['powershell','-NoProfile','-Command',"Add-Type -Path '"+str(folder/'Program.cs')+"'; [Program]::Main()"],check=True,timeout=90,cwd=folder)
    else:
        subprocess.run(['dotnet','run','--project',str(folder/'Check.csproj'),'--nologo'],check=True,timeout=90,cwd=folder)
