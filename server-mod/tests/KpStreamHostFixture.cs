using System;
using System.Reflection;
using HomeMedia;

class KpStreamHostFixture
{
    static void Main()
    {
        var check = typeof(KinoPubController).GetMethod("HostAllowed", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Exception("KinoPub host policy not found");
        int passed = 0;
        foreach (var (host, expected) in new (string, bool)[] {
            ("api.service-kp.com", true), // Live HLS4/HLS2 master URL returned by /v1/items.
            ("api.srvkp.com", true),
            ("kino.watch", true),
            ("video.ams-static-15.cdntogo.net", true),
            ("api.service-kp.com.attacker.example", false),
            ("attacker-api.service-kp.com", false),
            ("unrelated.service-kp.com", false),
            ("127.0.0.1", false),
            ("example.org", false)
        }) {
            if ((bool)check.Invoke(null, new object[] { host }) != expected)
                throw new Exception("Unexpected stream host policy: " + host);
            Console.WriteLine("PASS " + host);
            passed++;
        }
        Console.WriteLine("TOTAL " + passed);
    }
}
