Option Strict On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports MeloongCore
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq
Imports PclServerPack

' Third-party server edition integration. Original PCL launch / install checks are not bypassed.
Public Module ModServerPack
    Public Const ServerCatalogUrl As String = "https://raw.githubusercontent.com/StevenLi-codex/pcl-server-launcher/server-edition/serverpack/catalog.json"
    Public Const ServerSourceUrl As String = "https://github.com/StevenLi-codex/pcl-server-launcher"
    Public Const ServerDownloadUrl As String = "https://mc-download.220719.uk/"
    Public Const OriginalPclUrl As String = "https://meloong.com/pcl"
    Public Const CatalogByteLimit As Integer = 1024 * 1024
    Private OperationBusy As Integer

    Public ReadOnly Property ServerPackBusy As Boolean
        Get
            Return Interlocked.CompareExchange(OperationBusy, 0, 0) <> 0
        End Get
    End Property

    Public Function TryBeginServerOperation() As Boolean
        Return Interlocked.CompareExchange(OperationBusy, 1, 0) = 0
    End Function

    Public Sub EndServerOperation()
        Interlocked.Exchange(OperationBusy, 0)
    End Sub

    Public Class ServerCatalogResult
        Public Property Servers As IList(Of ServerDefinition)
        Public Property SourceDescription As String
    End Class

    Public Async Function LoadServerCatalogAsync(token As CancellationToken) As Task(Of ServerCatalogResult)
        Dim onlineError As Exception = Nothing
        Try
            Dim text = Await DownloadCatalogAsync(New Uri(ServerCatalogUrl), token).ConfigureAwait(False)
            Return New ServerCatalogResult With {.Servers = ParseCatalog(text), .SourceDescription = "在线目录"}
        Catch ex As Exception When Not token.IsCancellationRequested
            onlineError = ex
        End Try
        ' Only the explicitly bundled catalog is a fallback, never arbitrary cached remote data.
        Dim bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "serverpack", "catalog.json")
        Try
            Using input As New FileStream(bundled, FileMode.Open, FileAccess.Read, FileShare.Read)
                Dim text = Await ReadBoundedAsync(input, token).ConfigureAwait(False)
                Return New ServerCatalogResult With {.Servers = ParseCatalog(text), .SourceDescription = "离线随附目录（在线目录不可用，请检查版本）"}
            End Using
        Catch fallbackError As Exception When Not TypeOf fallbackError Is OperationCanceledException
            Throw New InvalidDataException("服务器目录不可用，已停止安装和启动。请访问原下载页获取整合包：" & ServerDownloadUrl, New AggregateException(onlineError, fallbackError))
        End Try
    End Function

    Private Async Function DownloadCatalogAsync(address As Uri, token As CancellationToken) As Task(Of String)
        Using handler As New HttpClientHandler With {.AllowAutoRedirect = False, .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate}
            Using client As New HttpClient(handler) With {.Timeout = TimeSpan.FromSeconds(25)}
                For hop = 0 To 5
                    ValidateHttps(address.AbsoluteUri)
                    Using request As New HttpRequestMessage(HttpMethod.Get, address)
                        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 PCL-Server-ThirdParty/1.0")
                        If address.Host.Equals("mc-download.220719.uk", StringComparison.OrdinalIgnoreCase) Then request.Headers.Referrer = New Uri(ServerDownloadUrl)
                        Using response = Await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(False)
                            Dim code = CInt(response.StatusCode)
                            If code >= 300 AndAlso code <= 399 Then
                                Dim location = response.Headers.Location
                                If location Is Nothing OrElse hop = 5 Then Throw New InvalidDataException("目录重定向无效或过多。")
                                address = If(location.IsAbsoluteUri, location, New Uri(address, location))
                                ValidateHttps(address.AbsoluteUri)
                                Continue For
                            End If
                            response.EnsureSuccessStatusCode()
                            If response.Content.Headers.ContentLength.HasValue AndAlso response.Content.Headers.ContentLength.Value > CatalogByteLimit Then Throw New InvalidDataException("服务器目录超过 1 MiB。")
                            ' HttpClient.Timeout does not bound streamed body reads; use a separate deadline.
                            Using deadline = CancellationTokenSource.CreateLinkedTokenSource(token)
                                deadline.CancelAfter(TimeSpan.FromSeconds(25))
                                Using input = Await response.Content.ReadAsStreamAsync().ConfigureAwait(False)
                                    Return Await ReadBoundedAsync(input, deadline.Token).ConfigureAwait(False)
                                End Using
                            End Using
                        End Using
                    End Using
                Next
            End Using
        End Using
        Throw New InvalidDataException("服务器目录下载失败。")
    End Function

    Public Async Function ReadBoundedAsync(input As Stream, token As CancellationToken) As Task(Of String)
        Using output As New MemoryStream()
            Dim buffer(8191) As Byte
            Do
                Dim count = Await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(False)
                If count = 0 Then Exit Do
                If output.Length + count > CatalogByteLimit Then Throw New InvalidDataException("服务器目录超过 1 MiB。")
                output.Write(buffer, 0, count)
            Loop
            Dim text = New UTF8Encoding(False, True).GetString(output.ToArray())
            Return text.TrimStart(ChrW(&HFEFF))
        End Using
    End Function

    Public Function ParseCatalog(text As String) As IList(Of ServerDefinition)
        If text Is Nothing OrElse Encoding.UTF8.GetByteCount(text) > CatalogByteLimit Then Throw New InvalidDataException("目录为空或超过 1 MiB。")
        Try
            Dim root As JObject
            Using reader As New JsonTextReader(New StringReader(text)) With {.MaxDepth = 16, .DateParseHandling = DateParseHandling.None}
                root = JObject.Load(reader, New JsonLoadSettings With {.DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error})
                If reader.Read() Then Throw New InvalidDataException("目录尾部存在额外内容。")
            End Using
            Dim schema = root("schemaVersion")
            If schema Is Nothing OrElse schema.Type <> JTokenType.Integer OrElse schema.Value(Of Integer)() <> 1 Then Throw New InvalidDataException("不支持的服务器目录格式。")
            Dim entries = TryCast(root("servers"), JArray)
            If entries Is Nothing OrElse entries.Count = 0 OrElse entries.Count > 64 Then Throw New InvalidDataException("目录未提供有效服务器。")
            Dim result As New List(Of ServerDefinition)
            Dim ids As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each entry In entries
                Dim item = TryCast(entry, JObject)
                If item Is Nothing Then Throw New InvalidDataException("服务器记录不是对象。")
                Dim server As New ServerDefinition With {
                    .Id = CatalogString(item, "Id", 32), .Name = CatalogString(item, "Name", 128),
                    .Address = CatalogString(item, "Address", 255), .Version = CatalogString(item, "Version", 128),
                    .PackUrl = CatalogString(item, "PackUrl", 2048), .PackSha256 = CatalogString(item, "PackSha256", 64)}
                ValidateServer(server)
                If Not ids.Add(server.Id) Then Throw New InvalidDataException("服务器 ID 重复。")
                result.Add(server)
            Next
            Return result
        Catch ex As JsonException
            Throw New InvalidDataException("服务器目录 JSON 无效。", ex)
        End Try
    End Function

    Private Function CatalogString(item As JObject, key As String, maxLength As Integer) As String
        ' Accept the agreed PascalCase model names and older camelCase catalogs, not ambiguous duplicates.
        Dim matches = item.Properties().Where(Function(p) p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList()
        If matches.Count <> 1 OrElse matches(0).Value.Type <> JTokenType.String Then Throw New InvalidDataException("目录缺少字段：" & key)
        Dim value = matches(0).Value.Value(Of String)()
        If String.IsNullOrWhiteSpace(value) OrElse value.Length > maxLength OrElse value <> value.Trim() OrElse value.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("目录字段无效：" & key)
        Return value
    End Function

    Public Sub ValidateServer(server As ServerDefinition)
        If server Is Nothing Then Throw New ArgumentException("未选择服务器。")
        ValidateId(server.Id)
        If String.IsNullOrWhiteSpace(server.Name) OrElse String.IsNullOrWhiteSpace(server.Version) Then Throw New InvalidDataException("服务器名称或整合包版本缺失。")
        If server.Name.IndexOfAny(New Char() {"/"c, "\"c}) >= 0 OrElse server.Name.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("服务器名称无效。")
        If server.Address Is Nothing OrElse Not Regex.IsMatch(server.Address, "\A(?:[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?|\[[0-9A-Fa-f:]+\])(?::[0-9]{1,5})?\z", RegexOptions.CultureInvariant) Then Throw New InvalidDataException("服务器地址无效。")
        Dim portMatch = Regex.Match(server.Address, ":([0-9]+)\z")
        If portMatch.Success AndAlso (Integer.Parse(portMatch.Groups(1).Value, Globalization.CultureInfo.InvariantCulture) < 1 OrElse Integer.Parse(portMatch.Groups(1).Value, Globalization.CultureInfo.InvariantCulture) > 65535) Then Throw New InvalidDataException("服务器端口无效。")
        ValidateHttps(server.PackUrl)
        If server.PackSha256 Is Nothing OrElse Not Regex.IsMatch(server.PackSha256, "\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant) Then Throw New InvalidDataException("整合包 SHA-256 缺失或无效。")
    End Sub

    Private Sub ValidateHttps(value As String)
        Dim address As Uri = Nothing
        If Not Uri.TryCreate(value, UriKind.Absolute, address) OrElse address.Scheme <> Uri.UriSchemeHttps OrElse String.IsNullOrEmpty(address.Host) OrElse address.UserInfo <> "" OrElse address.Fragment <> "" Then Throw New InvalidDataException("仅允许不含凭据的 HTTPS 下载地址。")
    End Sub

    Private Sub ValidateId(id As String)
        If id Is Nothing OrElse Not Regex.IsMatch(id, "\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,31}\z", RegexOptions.CultureInvariant) Then Throw New InvalidDataException("服务器 ID 必须是 1–32 个 ASCII 字母、数字、下划线或连字符。")
    End Sub

    Public Function GetInstanceName(id As String, minecraftVersion As String, dependencyKey As String) As String
        ValidateId(id)
        If minecraftVersion Is Nothing OrElse Not Regex.IsMatch(minecraftVersion, "\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,47}\z", RegexOptions.CultureInvariant) OrElse minecraftVersion.EndsWith(".", StringComparison.Ordinal) Then Throw New InvalidDataException("整合包 Minecraft 版本无效。")
        If String.IsNullOrWhiteSpace(dependencyKey) Then Throw New InvalidDataException("整合包依赖信息缺失。")
        Using hash = SHA256.Create()
            Dim suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(dependencyKey))).Replace("-", "").Substring(0, 8).ToLowerInvariant()
            Return "PCL-Server-" & id & "-" & minecraftVersion & "-" & suffix
        End Using
    End Function

    Public Function GetInstanceRoot(minecraftRoot As String, id As String, minecraftVersion As String, dependencyKey As String) As String
        If String.IsNullOrWhiteSpace(minecraftRoot) OrElse Not Path.IsPathRooted(minecraftRoot) Then Throw New InvalidDataException("请先在 PCL 中选择有效的 Minecraft 文件夹。")
        Dim versions = Path.GetFullPath(Path.Combine(minecraftRoot, "versions"))
        Dim target = Path.GetFullPath(Path.Combine(versions, GetInstanceName(id, minecraftVersion, dependencyKey)))
        If Not String.Equals(Path.GetDirectoryName(target), versions, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("实例路径超出 versions 文件夹。")
        AssertNoReparseAncestors(target)
        Return target
    End Function

    Public Sub AssertNoReparseAncestors(target As String)
        Dim current As New DirectoryInfo(Path.GetFullPath(target))
        Do While current IsNot Nothing
            If (Directory.Exists(current.FullName) OrElse File.Exists(current.FullName)) AndAlso (File.GetAttributes(current.FullName) And FileAttributes.ReparsePoint) <> 0 Then Throw New InvalidDataException("拒绝通过链接或联接点修改实例：" & current.FullName)
            current = current.Parent
        Loop
    End Sub

    ' Conservative: reject mutations even for another Java application / externally launched game.
    Public Sub AssertNoGameActive()
        If HasRunningMinecraft OrElse McLaunchLoader.State = LoadState.Loading Then Throw New InvalidOperationException("游戏正在运行或启动，请先退出游戏后再操作。")
        If McLaunchProcess IsNot Nothing AndAlso Not McLaunchProcess.HasExited Then Throw New InvalidOperationException("检测到运行中的游戏进程，请先退出。")
        For Each watcher In McWatcherList.ToArray()
            If watcher.State = Watcher.MinecraftState.Loading OrElse watcher.State = Watcher.MinecraftState.Running OrElse (watcher.GameProcess IsNot Nothing AndAlso Not watcher.GameProcess.HasExited) Then Throw New InvalidOperationException("检测到运行中的 Minecraft，请先退出。")
        Next
        For Each process In Diagnostics.Process.GetProcesses()
            Using process
                If process.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase) OrElse process.ProcessName.Equals("javaw", StringComparison.OrdinalIgnoreCase) Then
                    If Not process.HasExited Then Throw New InvalidOperationException("检测到 Java 进程。为避免修改运行中的游戏，请先关闭 Java 游戏或应用。")
                End If
            End Using
        Next
    End Sub

    ' Use PCL's real instance lock so the normal launch button cannot launch the target mid-write.
    Public Function RunLockedServerMutationAsync(root As String, action As Action) As Task
        Dim completion As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Dim failure As Exception = Nothing
        Dim worker As New RedoableWorker(
            Sub()
                AssertNoReparseAncestors(root)
                AssertNoGameActive()
                action()
            End Sub)
        If Not McInstanceLock.TryAcquire(worker, root) Then Throw New InvalidOperationException("此实例正被 PCL 的其他任务使用。")
        AddHandler worker.Failed, Sub(ex) failure = ex
        AddHandler worker.Canceled, Sub() failure = New OperationCanceledException("操作已取消。")
        AddHandler worker.Stopped,
            Sub()
                If failure Is Nothing Then
                    completion.TrySetResult(True)
                Else
                    completion.TrySetException(failure)
                End If
            End Sub
        worker.Start()
        Return completion.Task
    End Function
End Module
