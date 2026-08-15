# Lyricify Island

**该项目由 AI 编写**

https://github.com/user-attachments/assets/bc65054e-03d4-4007-9032-1123cf56aa45

Linux 桌面顶置歌词岛。当前曲目和播放进度来自 Spotify Web API；逐字歌词与翻译通过
[Lyricify Lyrics Helper](https://github.com/WXRIW/Lyricify-Lyrics-Helper) 获取和解析。
字体、逐字裁剪、双层微光、播放头 bloom 与换行动画都在 Skia 自绘层完成，正常桌面运行时由 GPU 渲染。

## 首次运行

1. 在 [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) 的应用设置中加入回调地址：
   `http://127.0.0.1:43821/callback`。
2. 构建并启动：

   ```bash
   ./build.sh
   ./run.sh
   ```
3. 岛屿提示未配置时，打开托盘菜单的“设置”，填写 Spotify Client ID 和
   Client Secret，然后点击“保存并重新连接”。

首次启动会打开浏览器，请登录并允许读取当前播放状态。刷新令牌只保存在当前用户的状态目录，
凭据只保存在当前用户的设置文件中；凭据、令牌、构建缓存和发布目录都不会进入 Git。
发布物 `dist/LyricifyIsland` 是单个可执行文件，自带 .NET 运行时和原生库，启动不要求系统安装 .NET。

Spotify 开发模式下，还需要在 Dashboard 中把登录所用 Spotify 账号加入应用用户列表。
程序启动后常驻系统托盘；托盘菜单提供“设置”和“退出”。
黑色岛屿区域支持按住左键拖动、左键双击临时隐藏，区域外保持鼠标穿透；
右键菜单也可临时隐藏、调整当前歌曲偏移、水平居中或退出。隐藏时长和拖动行为都可在设置中调整。

## 本地验收

无需 Spotify 即可查看内置的参考动效：

```bash
./run.sh --demo
./dist/LyricifyIsland --self-test
./dist/LyricifyIsland --snapshot /tmp/lyricify-island.png
```

`--demo --exit-after 5` 可用于五秒启动冒烟检查。

## 调整

托盘菜单的“设置”页面提供：

- 整体缩放：50%–200%，默认 100%，同步缩放字体、图标、胶囊、间距和光效。
- 最大宽度：可用屏幕宽度的 40%–100%，默认 70%。
- 背景不透明度：35%–95%；歌词翻译也可以单独关闭。
- 歌词时间偏移：全局偏移范围为 -2000–2000 毫秒，正值提前、负值延后。
- 每首歌曲偏移：可单独开关；启用后从灵动岛右键菜单调整当前歌曲，并在设置页管理已保存歌曲。
- 歌词源：可选自动、网易云、酷狗或 LRCLIB；也可重新获取当前歌曲。
- 位置与交互：选择显示器、调整顶部距离、记住拖动位置、锁定位置或让整个窗口穿透鼠标。
- 暂停行为：立即隐藏、3 秒后隐藏或保持显示；连接和错误提示不会被隐藏。
- 临时隐藏：可选 2、5、10 或 30 秒。
- 登录后自动启动：写入当前用户的 XDG 自启动目录。
- Spotify Client ID 和 Client Secret：点击保存后重新连接，Secret 在界面中遮蔽显示。
- 缓存：显示歌词、封面和歌曲信息缓存占用，并可一键清理。

设置保存在 `$XDG_CONFIG_HOME/lyricify-island/settings.json`；未设置 `XDG_CONFIG_HOME` 时使用
`~/.config/lyricify-island/settings.json`。Linux 下设置目录权限为 `0700`，文件权限为 `0600`。
歌曲缓存保存在 `$XDG_CACHE_HOME/lyricify-island/tracks`；未设置时使用 `~/.cache`。

以下环境变量只用于旧配置迁移：对应字段还没有写入设置文件时才会读取。

- `LYRICIFY_Y=58`：浮层距屏幕顶边的位置。
- `LYRICIFY_OFFSET_MS=0`：歌词同步偏移，正数提前、负数延后。
- `LYRICIFY_CLICK_THROUGH=1`：开启全窗口鼠标穿透；可从设置窗口关闭。

程序在 Linux 上使用 Avalonia 的 X11/XWayland 后端，以便在 GNOME Wayland 会话中可靠定位、置顶和鼠标穿透。
Fedora 缺少运行依赖时可安装：

```bash
sudo dnf install libX11 libXfixes gnome-shell-extension-appindicator \
  google-noto-sans-cjk-fonts julietaula-montserrat-fonts
```

GNOME 还需要启用 AppIndicator 扩展，其他支持 StatusNotifier 的桌面无需额外托盘依赖。

## 歌词回退

自动模式按质量依次尝试：网易云 YRC（逐字 + 翻译）、酷狗 KRC（逐字 + 内嵌翻译）、
网易云 LRC、LRCLIB LRC。指定首选歌词源后会先请求该来源，没有结果再尝试其他来源。
Spotify 未公开歌词 Web API，因此程序不会要求或保存 `sp_dc` 浏览器 Cookie。

上游 Helper 以 Apache-2.0 许可固定为 Git submodule；其许可证保留在
`vendor/Lyricify-Lyrics-Helper/LICENSE`。
