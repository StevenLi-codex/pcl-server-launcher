# PCL Steven 服务器修改版（开发测试）

原作：龙腾猫跃；[上游](https://github.com/Meloong-Git/PCL)；[赞助原作者](https://meloong.com/afd/a/LTCat)。本分支为第三方独立修改，不是官方版本，沿用根目录 LICENCE 及第三方声明。

## 范围

- Windows / .NET Framework 4.8；固定上游 PCL 2.14.0.0。
- 启动器内选服、安装固定整合包、比较受管文件、确认后更新及回退。
- 仅使用全新的独立实例；不接管玩家原来的普通版本目录。
- 现有下载站提供固定版本 mrpack，维护者在 `serverpack/catalog.json` 发布经审核的版本及 SHA-256。GitHub 上的目录不含模组 JAR、玩家账号或令牌。
- Minecraft / Loader 改变时新建实例，不直接在旧实例上跨版本迁移。
- 受管模组和必要包文件与个人设置分离；不删除未知个人模组，不改存档、截图、地图、键位。文件冲突时应停止并说明，不静默覆盖。
- 没有承诺跨所有文件的事务原子性；同步引擎的具体失败恢复行为以实际自动化测试覆盖为准。

## 正版登录（正式使用前的独立门槛）

上游开源代码的 `OAuthClientId` 默认为空。此仓库不冒用官方或其他启动器的应用身份。维护者需要注册自己的微软公共客户端应用，并确认 Xbox / Minecraft 相关访问要求；注册获得一个客户端 ID 不等于已经通过完整 Minecraft 登录链路。

测试构建可从进程环境变量 `PCL_MS_CLIENT_ID` 读取维护者自己的应用 ID。应用 ID 本身不是玩家密码，不需要客户端 secret；不得在客户端放置服务端 secret。没有 ID 时，不应把测试版称为可完成正版登录的交钥匙客户端。

使用独立文件夹解压，不覆盖已有 PCL。首次运行仍保留上游协议与正版购买提示。玩家通过微软官方登录界面授权，不把账号密码交给维护者。

## 构建

Windows 上安装 Visual Studio Build Tools（MSBuild、.NET Framework 4.8 开发工具及 WPF）、.NET SDK 9。递归克隆子模块后运行：

```powershell
dotnet run --project ServerPack.Tests/ServerPack.Tests.csproj --configuration Release
dotnet restore PCLCS/PCLCS.csproj
dotnet restore ServerPack.Core/ServerPack.Core.csproj
msbuild 'Plain Craft Launcher 2/Plain Craft Launcher 2.vbproj' /m /p:Configuration=Release /p:Platform=AnyCPU
```

GitHub Actions 的 `Server Edition Windows Build` 在 Windows runner 编译，提供带依赖和许可的 ZIP 测试产物。没有创建正式 Release。编译产物没有代码签名，Windows 可能提示未知发布者。

## 发布清单

每个服务器条目包含 `Id`、`Name`、`Address`、`Version`、`PackUrl` 和 `PackSha256`。只在客户端兼容性和固定版本分发条件核实后修改。更新清单不意味着允许未经审核的模组镜像；目前复用已有站点上的 mrpack 和其中的官方下载链接。

现有的原版生存服务端不强制客户端模组，但已发布的客户端 mrpack 包含 Fabric 优化组件；启动器应显示实际包依赖，不把服务端“无需模组”描述成客户端压缩包“没有模组”。

## 验收层次

分别记录：源码／自动化同步测试、Windows 编译、Windows 启动／界面、在线初次安装、微软正版登录、实际进服、一次真实发布更新与回滚。前一项不能代替后一项。

正式使用前仍须真人 Windows 客户端验证完整链路；没有把自动化测试或 API 请求成功当作真人进服。
