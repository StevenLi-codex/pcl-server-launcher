Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports PclServerPack

Module Program
    Private Count As Integer
    Sub Check(value As Boolean, message As String)
        Count += 1
        If Not value Then Throw New Exception(message)
    End Sub
    Sub Reject(action As Action, message As String)
        Try
            action()
        Catch ex As ArgumentException
            Count += 1
            Return
        Catch ex As InvalidDataException
            Count += 1
            Return
        End Try
        Throw New Exception(message)
    End Sub
    Sub Main()
        Dim sha = New String("a"c, 64)
        Dim serverJson = "{""id"":""alpha"",""name"":""测试服"",""address"":""mc.example.org:25565"",""version"":""2026.1"",""packUrl"":""https://example.org/pack.mrpack"",""packSha256"":""" & sha & """}"
        Dim json = "{""schemaVersion"":1,""servers"": [" & serverJson & "]}"
        Dim servers = ModServerPack.ParseCatalog(json)
        Check(servers.Count = 1 AndAlso servers(0).Id = "alpha", "catalog object parsed")
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace("""schemaVersion"":1", """schemaVersion"":2")), "unknown schema accepted")
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace("""alpha""", """../alpha""")), "traversal accepted")
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace("https://example.org", "http://example.org")), "HTTP accepted")
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace("2026.1", "")), "missing version accepted")
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace(sha, "bad")), "bad hash accepted")
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace("mc.example.org:25565", "mc.example.org & calc")), "unsafe address accepted")
        Reject(Sub() ModServerPack.ParseCatalog("{""schemaVersion"":1,""servers"": [" & serverJson & "," & serverJson & "]}"), "duplicate IDs accepted")
        Reject(Sub() ModServerPack.ParseCatalog("{""schemaVersion"":1,""servers"": []}"), "empty catalog accepted")
        Dim name = ModServerPack.GetInstanceName("alpha", "1.21.1", "minecraft=1.21.1;fabric=0.16.0")
        Check(name.StartsWith("PCL-Server-alpha-1.21.1-"), "isolated deterministic name")
        Check(name = ModServerPack.GetInstanceName("alpha", "1.21.1", "minecraft=1.21.1;fabric=0.16.0"), "unstable name")
        Check(name <> ModServerPack.GetInstanceName("alpha", "1.21.1", "minecraft=1.21.1;fabric=0.17.0"), "loader collision")
        Reject(Sub() ModServerPack.GetInstanceName("alpha", "../1.21.1", "dep"), "unsafe MC name accepted")
        Reject(Sub() ModServerPack.GetInstanceName("alpha", "1.21.1", ""), "missing dependency accepted")
        Using input As New MemoryStream(New Byte(1024 * 1024) {})
            Reject(Sub() ModServerPack.ReadBoundedAsync(input, CancellationToken.None).GetAwaiter().GetResult(), "oversized catalog accepted")
        End Using
        Dim root = Path.Combine(Path.GetTempPath(), "pcl-ui-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        Try
            Dim target = ModServerPack.GetInstanceRoot(root, "alpha", "1.21.1", "dep")
            Check(Path.GetDirectoryName(target.TrimEnd(Path.DirectorySeparatorChar)) = Path.Combine(root, "versions"), "target outside versions")
        Finally
            Directory.Delete(root, True)
        End Try
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace("mc.example.org:25565", "mc.example.org:99999")), "invalid port accepted")
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace("测试服", "unsafe/name")), "path-like display name accepted")
        Reject(Sub() ModServerPack.ParseCatalog(json.Replace("""version"":""2026.1""", """version"":""2026.1"",""Version"":""other""")), "ambiguous property accepted")
        Using input As New MemoryStream(Text.Encoding.UTF8.GetBytes(New String(" "c, 1024 * 1024)))
            Check(ModServerPack.ReadBoundedAsync(input, CancellationToken.None).GetAwaiter().GetResult().Length = 1024 * 1024, "exact 1MiB boundary rejected")
        End Using
        Check(ModServerPack.TryBeginServerOperation(), "operation not acquired")
        Check(Not ModServerPack.TryBeginServerOperation(), "concurrent operation accepted")
        ModServerPack.EndServerOperation()
        Check(Not ModServerPack.ServerPackBusy, "operation never released")
        Dim symlinkRoot = Path.Combine(Path.GetTempPath(), "pcl-links-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(symlinkRoot)
        Try
            Directory.CreateDirectory(Path.Combine(symlinkRoot, "real"))
            Directory.CreateSymbolicLink(Path.Combine(symlinkRoot, "link"), Path.Combine(symlinkRoot, "real"))
            Reject(Sub() ModServerPack.GetInstanceRoot(Path.Combine(symlinkRoot, "link"), "alpha", "1.21.1", "dep"), "symlink ancestor accepted")
        Finally
            Directory.Delete(symlinkRoot, True)
        End Try
        RollbackTests.Run(AddressOf Check, AddressOf Reject)
        Dim actual = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "serverpack", "catalog.json"))
        Check(ModServerPack.ParseCatalog(actual).Count = 4, "actual PascalCase catalog")
        Dim bundleFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "serverpack")
        Directory.CreateDirectory(bundleFolder)
        File.WriteAllText(Path.Combine(bundleFolder, "catalog.json"), actual)
        Dim fallback = ModServerPack.LoadServerCatalogAsync(CancellationToken.None).GetAwaiter().GetResult()
        Check(fallback.Servers.Count = 4, "catalog load/fallback failed")
        Console.WriteLine("Catalog source: " & fallback.SourceDescription)
        Console.WriteLine("PASS: " & Count & " actual VB catalog/path/rollback assertions")
    End Sub
End Module
