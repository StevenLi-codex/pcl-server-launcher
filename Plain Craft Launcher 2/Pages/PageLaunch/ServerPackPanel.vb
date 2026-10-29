Option Strict On

Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports PclServerPack

''' <summary>Visitor-facing third-party server chooser; delegates installation and launch to PCL.</summary>
Public Class ServerPackPanel
    Inherits StackPanel

    Private ReadOnly CatalogStatus As TextBlock
    Private ReadOnly Cards As New StackPanel()
    Private ReadOnly ReloadButton As MyButton
    Private ReadOnly ActionButtons As New List(Of MyButton)
    Private ReadOnly Rows As New List(Of ServerRow)
    Private LoadingCatalog As Boolean
    Private IsCatalogInitialized As Boolean

    Private Class ServerRow
        Public Server As ServerDefinition
        Public Status As TextBlock
        Public Prepared As PreparedPack
        Public InstanceRoot As String
    End Class

    Private Enum ServerAction
        InstallOrUpdate
        Launch
        Rollback
    End Enum

    Public Sub New()
        Automation.AutomationProperties.SetName(Me, "服务器选择")
        Dim notice As New MyCard With {.Title = "PCL 服务器第三方修改版", .Margin = New Thickness(0, 0, 0, 15)}
        Dim body As New StackPanel With {.Margin = New Thickness(20, 38, 20, 16)}
        body.Children.Add(Label("本版本由第三方基于 PCL 独立修改，非 PCL 官方版本。原作者：龙腾猫跃。", 13))
        body.Children.Add(Label("选择服务器后安装独立版本；更新前确认变更，不会更新后自动启动。你的其他版本不会被接管。", 12))
        Dim links As New WrapPanel With {.Margin = New Thickness(0, 8, 0, 0)}
        links.Children.Add(LinkButton("修改版源码", ServerSourceUrl))
        links.Children.Add(LinkButton("原作者与赞助", "https://meloong.com/afd/a/LTCat"))
        links.Children.Add(LinkButton("整合包原下载页", ServerDownloadUrl))
        links.Children.Add(LinkButton("原版 PCL", OriginalPclUrl))
        body.Children.Add(links)
        CatalogStatus = Label("正在读取服务器目录……", 12)
        CatalogStatus.Margin = New Thickness(0, 8, 0, 0)
        body.Children.Add(CatalogStatus)
        ReloadButton = New AccessibleServerButton With {.Text = "刷新服务器", .Height = 30, .Width = 110, .HorizontalAlignment = HorizontalAlignment.Left, .Margin = New Thickness(0, 8, 0, 0)}
        Automation.AutomationProperties.SetName(ReloadButton, "刷新服务器目录")
        AddHandler ReloadButton.Click, Sub(sender, e) RefreshCatalog()
        body.Children.Add(ReloadButton)
        notice.Children.Add(body)
        Children.Add(notice)
        Children.Add(Cards)
    End Sub

    Protected Overrides Function OnCreateAutomationPeer() As Automation.Peers.AutomationPeer
        Return New Automation.Peers.FrameworkElementAutomationPeer(Me)
    End Function

    ' MyButton is a Border, which has no default automation peer.
    Private Class AccessibleServerButton
        Inherits MyButton

        Protected Overrides Function OnCreateAutomationPeer() As Automation.Peers.AutomationPeer
            Return New ServerButtonPeer(Me)
        End Function

        Private Class ServerButtonPeer
            Inherits Automation.Peers.FrameworkElementAutomationPeer

            Public Sub New(owner As AccessibleServerButton)
                MyBase.New(owner)
            End Sub

            Protected Overrides Function GetAutomationControlTypeCore() As Automation.Peers.AutomationControlType
                Return Automation.Peers.AutomationControlType.Button
            End Function
        End Class
    End Class

    Private Shared Function Label(text As String, size As Double) As TextBlock
        Dim result As New TextBlock With {.Text = text, .FontSize = size, .TextWrapping = TextWrapping.Wrap, .Margin = New Thickness(0, 0, 0, 5)}
        result.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Return result
    End Function

    Private Shared Function LinkButton(text As String, url As String) As MyButton
        Dim result As New AccessibleServerButton With {.Text = text, .Height = 28, .Padding = New Thickness(10, 0, 10, 0), .Margin = New Thickness(0, 0, 8, 6)}
        Automation.AutomationProperties.SetName(result, text)
        AddHandler result.Click, Sub(sender, e) OpenWebsite(url)
        Return result
    End Function

    Public Sub EnsureInitialized()
        If IsCatalogInitialized Then Return
        IsCatalogInitialized = True
        RefreshCatalog()
    End Sub

    Public Async Sub RefreshCatalog()
        If LoadingCatalog OrElse ServerPackBusy Then Return
        LoadingCatalog = True
        SetButtons(False)
        ' A failed refresh must not leave stale actionable server cards behind.
        Cards.Children.Clear()
        Rows.Clear()
        ActionButtons.Clear()
        CatalogStatus.Text = "正在读取服务器目录……"
        Try
            Dim result = Await LoadServerCatalogAsync(CancellationToken.None)
            For Each server In result.Servers
                AddServerCard(server)
            Next
            CatalogStatus.Text = result.SourceDescription & " · " & result.Servers.Count & " 个服务器"
            If String.IsNullOrWhiteSpace(OAuthClientId) Then CatalogStatus.Text &= vbCrLf & "微软登录尚未配置：请为此修改版设置自己的 PCL_MS_CLIENT_ID 后重新打开启动器，不会自动改用离线登录。"
        Catch ex As Exception
            CatalogStatus.Text = "服务器目录不可用，安装和启动已停止。请使用上方的整合包原下载页。"
            Logger.Warn(ex, "服务器修改版目录加载失败")
        Finally
            LoadingCatalog = False
            SetButtons(True)
        End Try
    End Sub

    Private Sub AddServerCard(server As ServerDefinition)
        Dim row As New ServerRow With {.Server = server}
        Dim card As New MyCard With {.Title = server.Name, .Margin = New Thickness(0, 0, 0, 15)}
        Dim body As New StackPanel With {.Margin = New Thickness(20, 38, 20, 16)}
        body.Children.Add(Label("整合包 " & server.Version & "  ·  " & server.Address, 13))
        row.Status = Label("检查整合包后可启动；未安装时会创建独立的 PCL 版本。", 12)
        body.Children.Add(row.Status)
        Dim actions As New WrapPanel With {.Margin = New Thickness(0, 8, 0, 0)}
        AddActionButton(actions, row, "安装 / 检查更新", ServerAction.InstallOrUpdate, True)
        AddActionButton(actions, row, "启动并连接", ServerAction.Launch, False)
        AddActionButton(actions, row, "回滚上次更新", ServerAction.Rollback, False)
        body.Children.Add(actions)
        card.Children.Add(body)
        Cards.Children.Add(card)
        Rows.Add(row)
    End Sub

    Private Sub AddActionButton(panel As WrapPanel, row As ServerRow, text As String, action As ServerAction, primary As Boolean)
        Dim button As New AccessibleServerButton With {.Text = text, .Height = 32, .Padding = New Thickness(12, 0, 12, 0), .Margin = New Thickness(0, 0, 8, 6)}
        Automation.AutomationProperties.SetName(button, row.Server.Name & "：" & text)
        If primary Then button.ColorType = MyButton.ColorState.Highlight
        AddHandler button.Click, Sub(sender, e) ExecuteServerAction(row, action)
        ActionButtons.Add(button)
        panel.Children.Add(button)
    End Sub

    Private Sub SetButtons(enabled As Boolean)
        ReloadButton.IsEnabled = enabled
        For Each button In ActionButtons
            button.IsEnabled = enabled
        Next
    End Sub

    Private Shared Sub EnsureSameMinecraftFolder(folder As String)
        If Not String.Equals(Path.GetFullPath(folder).TrimEnd("\"c, "/"c), Path.GetFullPath(McFolderSelected).TrimEnd("\"c, "/"c), StringComparison.OrdinalIgnoreCase) Then Throw New InvalidOperationException("Minecraft 文件夹已改变，请重新检查此服务器。")
    End Sub

    Private Shared Sub RequireManaged(root As String, prepared As PreparedPack)
        AssertNoReparseAncestors(root)
        If Not PackService.IsManaged(root) Then Throw New InvalidOperationException("同名文件夹存在但不属于此服务器管理器，不会覆盖或接管。请保留它，并检查实例名称冲突。")
        If Not String.Equals(PackService.ReadDependencyKey(root), prepared.DependencyKey, StringComparison.Ordinal) Then Throw New InvalidOperationException("此实例的 Minecraft / 加载器依赖不同，不会覆盖旧版本。请安装新的独立实例。")
        Dim instance As New McInstance(root & "\")
        instance.Load()
        If instance.State = McInstance.McInstanceState.Error Then Throw New InvalidDataException("PCL 实例未完整安装：" & instance.Info)
        If Not String.Equals(instance.PathIndie.TrimEnd("\"c), instance.PathVersion.TrimEnd("\"c), StringComparison.OrdinalIgnoreCase) Then Throw New InvalidOperationException("此实例未启用版本隔离。请先在 PCL 版本设置中开启版本隔离，避免影响其他游戏。")
    End Sub

    Private Async Sub ExecuteServerAction(row As ServerRow, action As ServerAction)
        If LoadingCatalog OrElse Not TryBeginServerOperation() Then Return
        SetButtons(False)
        Dim server = row.Server
        Dim folder = McFolderSelected
        ' Block the standard launch controls as well as this panel while preparing or mutating.
        Dim launchControls = FrmLaunchLeft
        Dim launchControlsWereEnabled = launchControls IsNot Nothing AndAlso launchControls.IsEnabled
        If launchControls IsNot Nothing Then launchControls.IsEnabled = False
        Dim root As String = Nothing
        Try
            ValidateServer(server)
            AssertNoGameActive()
            If action = ServerAction.Launch AndAlso String.IsNullOrWhiteSpace(OAuthClientId) Then Throw New InvalidOperationException("微软登录缺少应用 ID。请设置你自己的 PCL_MS_CLIENT_ID 环境变量后重新打开启动器，并在左侧登录正版账号。不会自动切换到离线登录。")
            row.Status.Text = "正在下载并验证整合包……"
            ' Preparation performs network / archive work on a worker, never the WPF thread.
            Dim prepared = Await Task.Run(Async Function() Await PackService.PrepareAsync(server, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "serverpack", "cache"), CancellationToken.None))
            If prepared Is Nothing OrElse Not String.Equals(prepared.Version, server.Version, StringComparison.Ordinal) Then Throw New InvalidDataException("目录与整合包版本不一致，操作已停止。请联系服务器管理员。")
            root = GetInstanceRoot(folder, server.Id, prepared.MinecraftVersion, prepared.DependencyKey)
            EnsureSameMinecraftFolder(folder)
            row.Prepared = prepared
            row.InstanceRoot = root

            If Not Directory.Exists(root) Then
                If File.Exists(root) Then Throw New InvalidDataException("实例路径被文件占用，不会覆盖。")
                If action <> ServerAction.InstallOrUpdate Then Throw New InvalidOperationException("此服务器所需的独立版本尚未安装。若 Minecraft 或加载器发生变化，请先安装新实例；旧实例仍保留在 PCL 的版本列表中。")
                Dim name = Path.GetFileName(root)
                Dim summary = server.Name & " · " & server.Version & vbCrLf & "Minecraft " & prepared.MinecraftVersion & " · " & prepared.LoaderDescription & vbCrLf &
                    "新建独立实例：" & name & vbCrLf & "位置：" & root & vbCrLf & "原版本和个人文件不被接管；安装后不会自动启动。"
                If MyMsgBox(summary, "确认安装独立实例", "安装", "取消") <> 1 Then
                    row.Status.Text = "已取消安装。"
                    Return
                End If
                EnsureSameMinecraftFolder(folder)
                AssertNoGameActive()
                row.Status.Text = "PCL 正在安装游戏和加载器，请等待任务完成……"
                Await RunLockedServerMutationAsync(root,
                    Sub()
                        EnsureSameMinecraftFolder(folder)
                        If Directory.Exists(root) OrElse File.Exists(root) Then Throw New IOException("实例路径已存在，停止安装以免覆盖。")
                        Dim loader = ModpackInstall(prepared.ArchivePath, name)
                        If loader Is Nothing Then Throw New InvalidOperationException("PCL 未返回安装任务。")
                        ' ModpackInstall already starts this loader with root as Input; preserve it.
                        loader.WaitForExit(root & "\")
                        If loader.State <> LoadState.Finished Then Throw New IOException("PCL 安装未成功，不会标记为受管实例。")
                        AssertNoGameActive()
                        AssertNoReparseAncestors(root)
                        Dim installed As New McInstance(root & "\")
                        installed.Load()
                        If installed.State = McInstance.McInstanceState.Error Then Throw New InvalidDataException("安装后版本检查失败：" & installed.Info)
                        Settings.Set("VersionArgumentIndieV2", True, Instance:=installed)
                        PackService.AdoptInstalled(root, prepared)
                        RequireManaged(root, prepared)
                    End Sub)
                McInstanceListForceRefresh = True
                row.Status.Text = "安装完成。请在左侧登录账号，再点击「启动并连接」。"
                Return
            End If

            RequireManaged(root, prepared)
            If action = ServerAction.Rollback Then
                If MyMsgBox(server.Name & vbCrLf & "将恢复此实例上一次同步前的受管文件。仅回滚上一次更新，不切换 Minecraft / 加载器，不覆盖个人目录；不会自动启动。" & vbCrLf & root,
                            "确认回滚", "回滚", "取消") <> 1 Then
                    row.Status.Text = "已取消回滚。"
                    Return
                End If
                EnsureSameMinecraftFolder(folder)
                Await RunLockedServerMutationAsync(root,
                    Sub()
                        EnsureSameMinecraftFolder(folder)
                        RequireManaged(root, prepared)
                        PackService.RollbackLast(root)
                    End Sub)
                row.Prepared = Nothing
                row.Status.Text = "已回滚上次同步。再次启动前会检查当前服务器要求的版本。"
                Return
            End If

            row.Status.Text = "正在核对受管文件……"
            Dim plan = Await Task.Run(Function() PackService.Plan(root, prepared))
            Dim changed = plan.AddCount <> 0 OrElse plan.ReplaceCount <> 0 OrElse plan.RemoveCount <> 0
            If action = ServerAction.Launch Then
                If changed Then Throw New InvalidOperationException("整合包尚未同步：新增 " & plan.AddCount & "、替换 " & plan.ReplaceCount & "、移除 " & plan.RemoveCount & "。请先点击「安装 / 检查更新」并确认变更。")
                EnsureSameMinecraftFolder(folder)
                AssertNoGameActive()
                ' Async event continuation is on the WPF dispatcher; preserve original launch checks.
                If McLaunchStart(New McLaunchOptions With {.Instance = New McInstance(root & "\"), .ServerIp = server.Address}) Then
                    row.Status.Text = "已提交 PCL 启动任务，连接 " & server.Address & "。"
                Else
                    row.Status.Text = "PCL 未开始启动，请查看登录或版本错误提示。"
                End If
                Return
            End If

            If Not changed Then
                row.Status.Text = "已是服务器要求的版本 " & server.Version & "，可以启动。"
                Return
            End If
            Dim confirmation = server.Name & " · " & server.Version & vbCrLf &
                "新增 " & plan.AddCount & "，替换 " & plan.ReplaceCount & "，移除 " & plan.RemoveCount & "，保留 " & plan.KeepCount & vbCrLf &
                plan.Summary & vbCrLf & "实例：" & root & vbCrLf &
                "只同步受管文件；个人存档、截图、日志和服务器列表不被更新覆盖。完成后不会自动启动。"
            If MyMsgBox(confirmation, "确认更新 / 移除受管文件", "确认更新", "取消") <> 1 Then
                row.Status.Text = "已取消更新，未修改实例。"
                Return
            End If
            EnsureSameMinecraftFolder(folder)
            row.Status.Text = "正在同步并备份受管文件……"
            Dim report As Action(Of String) = Sub(message) RunInUi(Sub() row.Status.Text = message)
            Await RunLockedServerMutationAsync(root,
                Sub()
                    EnsureSameMinecraftFolder(folder)
                    RequireManaged(root, prepared)
                    PackService.ApplyAsync(root, prepared, plan, CancellationToken.None, report).GetAwaiter().GetResult()
                End Sub)
            row.Status.Text = "更新完成。需要进入游戏时，请点击「启动并连接」。"
        Catch ex As Exception
            row.Prepared = Nothing
            row.Status.Text = "操作未完成：" & ex.Message & " 可前往整合包原下载页。"
            Logger.Warn(ex, "服务器修改版操作未完成：" & server.Id)
        Finally
            If launchControls IsNot Nothing Then launchControls.IsEnabled = launchControlsWereEnabled
            EndServerOperation()
            SetButtons(True)
        End Try
    End Sub
End Class
