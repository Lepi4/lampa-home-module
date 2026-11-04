"""WEB-01: ServiceUi HTML rewrite must preserve Tvheadend's <base> and mount resources.

Runs a tiny C# harness against the real ServiceUiController.Rewrite. It uses the
same compile references as the other HomeMedia regression checks.
"""
import os
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
DOTNET = os.environ.get("DOTNET", "D:/tmp/lampac-profiles-implementation-20261029/dotnet/dotnet.exe")
REFS = pathlib.Path(os.environ.get(
    "HOMEMEDIA_COMPILE_REFS", "D:/tmp/lampac-profiles-implementation-20261029/compile-refs"
))

program = r'''
using System;
using System.Text.RegularExpressions;
using HomeMedia;

static class Program
{
    static int tests;
    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) throw new Exception(name + (detail.Length > 0 ? ": " + detail : ""));
        tests++;
        Console.WriteLine("PASS " + name);
    }
    static string BaseOf(string html)
    {
        var m = Regex.Match(html, @"<base\b[^>]*\bhref\s*=\s*(['""])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? m.Groups[2].Value : null;
    }
    static int BaseCount(string html) => Regex.Matches(html, @"<base\b", RegexOptions.IgnoreCase).Count;
    static string Resolve(string baseHref, string relative) =>
        new Uri(new Uri("http://service" + baseHref), relative).AbsolutePath;
    static void Main()
    {
        const string prefix = "/home/services/tvheadend/";
        var tvh = "<html><head><base href=\"/gui/static/\"><script type=\"module\" src=\"./assets/index.js\"></script><link rel=\"stylesheet\" href=\"./assets/index.css\"></head><body></body></html>";
        var o = ServiceUiController.Rewrite(tvh, "text/html", prefix, "/gui/");
        Check("Tvheadend base is mounted", BaseOf(o) == "/home/services/tvheadend/gui/static/", BaseOf(o));
        Check("Tvheadend module resolves under gui/static", Resolve(BaseOf(o), "./assets/index.js") == "/home/services/tvheadend/gui/static/assets/index.js", Resolve(BaseOf(o), "./assets/index.js"));
        Check("Tvheadend CSS resolves under gui/static", Resolve(BaseOf(o), "./assets/index.css") == "/home/services/tvheadend/gui/static/assets/index.css", Resolve(BaseOf(o), "./assets/index.css"));
        Check("one base only", BaseCount(o) == 1, BaseCount(o).ToString());

        var mounted = "<html><head><base href=\"/home/services/tvheadend/gui/static/\"></head><body></body></html>";
        var m = ServiceUiController.Rewrite(mounted, "text/html", prefix, "/gui/");
        Check("already mounted base is not doubled", BaseOf(m) == "/home/services/tvheadend/gui/static/", BaseOf(m));

        var relative = "<html><head><base href=\"./static/\"></head><body></body></html>";
        var r = ServiceUiController.Rewrite(relative, "text/html", prefix, "/gui/");
        Check("relative base uses page path", BaseOf(r) == "/home/services/tvheadend/gui/static/", BaseOf(r));

        var noBase = "<html><head></head><body><a href=\"/api/serverinfo\">api</a></body></html>";
        var n = ServiceUiController.Rewrite(noBase, "text/html", prefix, "/gui/");
        Check("fallback base is added when absent", BaseOf(n) == "/home/services/tvheadend/", BaseOf(n));
        Check("root API href is mounted", n.Contains("href=\"/home/services/tvheadend/api/serverinfo\""), n);

        var target = "<html><head><base target=\"_blank\"></head><body></body></html>";
        var t = ServiceUiController.Rewrite(target, "text/html", prefix, "/gui/");
        Check("target-only base keeps target", BaseOf(t) == "/home/services/tvheadend/" && t.Contains("target=\"_blank\""), t);

        var css = "a{background:url(/gui/static/a.png)}b{background:url(/home/services/tvheadend/gui/static/b.png)}c{background:url(//cdn.example/c.png)}";
        var c = ServiceUiController.Rewrite(css, "text/css", prefix);
        Check("CSS root URL is mounted", c.Contains("url(/home/services/tvheadend/gui/static/a.png)"), c);
        Check("CSS mounted URL is not doubled", c.Contains("url(/home/services/tvheadend/gui/static/b.png)") && !c.Contains("tvheadend/home/services"), c);
        Check("CSS protocol-relative URL is preserved", c.Contains("url(//cdn.example/c.png)"), c);

        var cm = "<html><head><a href=\"/api/channels\">channels</a></head><body></body></html>";
        var x = ServiceUiController.Rewrite(cm, "text/html", "/home/services/channel-manager/", "/");
        Check("CM keeps a working fallback base", BaseOf(x) == "/home/services/channel-manager/", BaseOf(x));
        Check("CM root href is mounted", x.Contains("href=\"/home/services/channel-manager/api/channels\""), x);
        Console.WriteLine(tests + " rewrite tests passed");
    }
}
'''

def main():
    if not REFS.is_dir():
        raise SystemExit("compile refs not found: " + str(REFS))
    with tempfile.TemporaryDirectory(prefix="lmpc-rewrite-") as tmp:
        folder = pathlib.Path(tmp)
        (folder / "Check.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
<EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable>
</PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />
<Compile Include="Program.cs" />
<Compile Include="{ROOT / 'server-mod' / 'HomeMedia' / '*.cs'}" />
<Reference Include="Core"><HintPath>{REFS / 'Core.dll'}</HintPath></Reference>
<Reference Include="Microsoft.Data.Sqlite"><HintPath>{REFS / 'Microsoft.Data.Sqlite.dll'}</HintPath></Reference>
<Reference Include="Newtonsoft.Json"><HintPath>{REFS / 'Newtonsoft.Json.dll'}</HintPath></Reference>
<Reference Include="Shared"><HintPath>{REFS / 'Shared.dll'}</HintPath></Reference>
<Reference Include="HtmlAgilityPack"><HintPath>{REFS / 'HtmlAgilityPack.dll'}</HintPath></Reference>
</ItemGroup></Project>''', encoding="utf-8")
        (folder / "Program.cs").write_text(program, encoding="utf-8")
        result = subprocess.run([DOTNET, "run", "--project", str(folder / "Check.csproj"), "--nologo"],
                                cwd=folder, text=True, capture_output=True, timeout=120)
        print(result.stdout, end="")
        if result.returncode != 0:
            print(result.stderr, end="")
            raise SystemExit(result.returncode)
        assert "rewrite tests passed" in result.stdout

if __name__ == "__main__":
    main()
