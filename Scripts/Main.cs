using Godot;
using NiubilityIdle.Autoload;
using NiubilityIdle.Core;
using NiubilityIdle.Data;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NiubilityIdle
{
	// ═══════════════════════════════════════════════════════════════════
	// 原版 Revolution Idle 视觉复刻：
	//   黑底 · 顶部彩色乘数链(×) · 左侧彩色圆圈产量条 · 中央旋转弧线轨道
	//   · 右侧转生窗口+绿色晋升按钮 · 最右侧图标菜单 · 底部"距离无限"进度条
	// 本 UI 只读展示完全体数据；所有按钮仅弹 toast，绝不改动真实存档。
	// ═══════════════════════════════════════════════════════════════════
	public partial class Main : Control
	{
		// ── 原版配色（取自原游戏截图）──
		private static readonly Color C_Bg      = new("1f1f1f");
		private static readonly Color C_MenuBg  = new("191919");
		private static readonly Color C_Card    = new("3a3a3a");
		private static readonly Color C_Card2   = new("2e2e2e");
		private static readonly Color C_Line    = new("4a4a4a");
		private static readonly Color C_White   = new("f2f2f2");
		private static readonly Color C_Gray    = new("9a9a9a");
		private static readonly Color C_Green   = new("3ddb62");
		private static readonly Color C_Pink    = new("e857c8");

		// 11 色产量条/乘数链色板 —— 从原版 rev1~10 贴图采样的真实配色
		private static readonly Color[] BarCols =
		{
			new("d42020"), new("e07800"), new("e8cc00"), new("27ae60"), new("00d696"),
			new("00d3d0"), new("1e62f0"), new("5b4bd8"), new("9a4dd8"), new("e91ea4"),
			new("f4f4f4"),
		};

		// ── 菜单定义 ──
		private static readonly string[] MenuNames =
		{
			"轮转", "无限", "无限树", "永恒", "统一", "时间流量",
			"成就", "统计", "选项", "帮助", "商店", "制作名单",
		};
		// 菜单图标:Vector2 = 原版图集 tmp_sprites.png 的裁切区域(57x64 网格),string = 系统字符
		private static readonly object[] MenuIconDefs =
		{
			new Vector2(0, 0),      // 轮转 ◎
			new Vector2(57, 0),     // 无限 ∞
			new Vector2(114, 0),    // 无限树 火焰
			new Vector2(228, 0),    // 永恒 节点
			new Vector2(285, 0),    // 统一
			"⏱",                    // 时间流量
			new Vector2(57, 128),   // 成就 奖杯
			"📊",                   // 统计
			new Vector2(342, 64),   // 选项 齿轮
			"❓",                   // 帮助
			new Vector2(171, 64),   // 商店 金条
			new Vector2(0, 64),     // 制作名单 ℹ
		};
		private static readonly Color[] MenuCols =
		{
			new("f2f2f2"), new("38cfc0"), new("8bc94f"), new("f5d43c"), new("9a4dd8"), new("ef8b33"),
			new("f5d43c"), new("3f66e0"), new("9a9a9a"), new("f2f2f2"), new("3ddb62"), new("e857c8"),
		};

		private static readonly string[] Sufs =
		{
			"", "K", "M", "B", "T", "Qa", "Qi", "Sx", "Sp", "Oc", "No", "Dc",
			"UDc", "DDc", "TDc", "QaDc", "QiDc", "SxDc", "SpDc", "OcDc", "NoDc",
		};

		// ── 运行时控件 ──
		private HBoxContainer _topBar;
		private Panel _progFill;
		private Label _progLbl;
		private OrbitView _orbit;
		private Label _toast;
		private float _toastT;
		private VBoxContainer _menuList;
		private readonly List<Panel> _menuRows = new();
		private int _curMenu = -1;
		private MarginContainer _centerArea;
		private Label _scoreLbl;
		private RichTextLabel _gainLbl;
		private Label _perRevLbl;

		private static GameManager GM() => GameManager.Instance;
		private static SaveData SD() => GM()?.Save;

		// ════════════════════════════════════════════════════════
		public override void _Ready()
		{
			SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			MouseFilter = MouseFilterEnum.Stop;

			// 原版观感的关键:原游戏自带的 Oswald-Bold(数字/拉丁) + 雅黑粗体(中文回退)
			var oswald = new FontFile();
			oswald.LoadDynamicFont("res://Assets/Fonts/Oswald-Bold.ttf");
			var zh = new SystemFont();
			zh.FontNames = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
			zh.FontWeight = 700;
			oswald.Fallbacks = new Godot.Collections.Array<Font> { zh };
			var uiTheme = new Theme();
			uiTheme.DefaultFont = oswald;
			uiTheme.DefaultFontSize = 16;
			Theme = uiTheme;

			var bg = new ColorRect { Color = C_Bg, MouseFilter = MouseFilterEnum.Ignore };
			bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			AddChild(bg);

			BuildTopMultiplierBar();
			BuildRightMenu();
			BuildBottomProgress();
			BuildCenterArea();
			ShowMenu(0);

			_toast = new Label
			{
				Visible = false,
				HorizontalAlignment = HorizontalAlignment.Center,
				MouseFilter = MouseFilterEnum.Ignore,
			};
			_toast.AddThemeFontSizeOverride("font_size", 16);
			_toast.AddThemeColorOverride("font_color", C_White);
			var tsb = Flat(new Color(0, 0, 0, 0.85f), 8);
			tsb.ContentMarginLeft = 18; tsb.ContentMarginRight = 18;
			tsb.ContentMarginTop = 8; tsb.ContentMarginBottom = 8;
			_toast.AddThemeStyleboxOverride("normal", tsb);
			_toast.SetAnchorsPreset(LayoutPreset.CenterBottom);
			_toast.OffsetTop = -86; _toast.OffsetBottom = -46;
			_toast.OffsetLeft = -260; _toast.OffsetRight = 260;
			AddChild(_toast);

			// 引擎内截图(调试验收用):启动 2 秒后把 12 个菜单页各存一张 PNG 到用户目录
			var t = GetTree().CreateTimer(2.0);
			t.Timeout += CaptureAllPages;
		}

		private async void CaptureAllPages()
		{
			try
			{
				// 窗口最小化时引擎不渲染(截图会全黑):先恢复并置前
				int wid = (int)GetWindow().GetWindowId();
				int wait = 0;
				while (DisplayServer.WindowGetMode(wid) == DisplayServer.WindowMode.Minimized && wait < 300)
				{
					DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					wait++;
				}
				GetWindow().MoveToForeground();
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

				var dir = OS.GetUserDataDir();
				for (int i = 0; i < MenuNames.Length; i++)
				{
					ShowMenu(i);
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					var img = GetViewport().GetTexture().GetImage();
					img.SavePng($"{dir}/ui_menu{i}.png");
				}
				ShowMenu(0);
				GD.Print($"[UI-SHOT] captured {MenuNames.Length} pages -> {dir}");
			}
			catch (Exception e)
			{
				GD.PrintErr("[UI-SHOT] failed: " + e.Message);
			}
		}

		public override void _Process(double delta)
		{
			if (_toastT > 0)
			{
				_toastT -= (float)delta;
				if (_toastT <= 0) _toast.Visible = false;
			}
			var g = SD();
			if (g == null) return;
            if (_scoreLbl != null) _scoreLbl.Text = Suf(g.game.score);
            if (_gainLbl != null)
            {
                var gain = GM().CalculateGainPerSecond();
                _gainLbl.Clear();
                _gainLbl.AppendText($"[center]+{Suf(gain)} / 秒[/center]");
                if (_perRevLbl != null) _perRevLbl.Text = $"转生 x{g.game.prestigeCount} · {Suf(gain)} / 轮转";
            }
            if (_progFill != null)
            {
                double sc = g.game.score.ToDouble();
                double p = sc <= 1 ? 0 : Math.Log10(sc) / 308.0;
                p = Math.Clamp(p, 0, 1);
                _progFill.AnchorRight = (float)p;
                if (_progLbl != null) _progLbl.Text = $"距离无限:{p * 100:0.##}%";
            }
		}

		private void Toast(string msg)
		{
			if (_toast == null) return;
			_toast.Text = msg;
			_toast.Visible = true;
			_toastT = 2.6f;
		}

		// ════════════════════════════════════════════════════════
		// 格式化：原版风格后缀 (2.49 No / 65.45)
		// ════════════════════════════════════════════════════════
		private static string Suf(BigDouble v)
		{
			// 原版风格: <1e9 用逗号全展开(8,640,916 / 65.45),>=1e9 用后缀(20.7 B / 2.49 No)
			if (v.exponent < 9)
			{
				double d = v.ToDouble();
				return d >= 100 ? d.ToString("N0", CultureInfo.InvariantCulture) : d.ToString("F2");
			}
			int tier = (int)(v.exponent / 3);
			if (tier < 0 || tier >= Sufs.Length)
				return $"{v.mantissa:F2}e{v.exponent}";
			double m = v.mantissa * Math.Pow(10, v.exponent % 3);
			return m.ToString("F2") + Sufs[tier];
		}

		private static string Comma(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

		// ════════════════════════════════════════════════════════
		// 顶部彩色乘数链： N × N × N × …
		// ════════════════════════════════════════════════════════
		private void BuildTopMultiplierBar()
		{
			if (_topBar != null) { _topBar.QueueFree(); _topBar = null; }
			var bar = new HBoxContainer
			{
				MouseFilter = MouseFilterEnum.Ignore,
				Alignment = BoxContainer.AlignmentMode.Center,
			};
			bar.AddThemeConstantOverride("separation", 8);
			bar.SetAnchorsPreset(LayoutPreset.TopWide);
			bar.OffsetTop = 14; bar.OffsetBottom = 100;

			var g = SD();
			var chain = new List<(string txt, Color col)>();
			int n = Math.Min(g.game.circleLevels.Count, 10);
			// 原版风格:倍率从大到小递减,结尾是短数字
			for (int i = 0; i < n; i++)
			{
				long mult = (long)g.game.circleLevels[i] * (11 - i) * 99L;
				chain.Add((Comma(mult), BarCols[i]));
			}
			chain.Add((Comma(g.game.prestigeCount), BarCols[9]));

			for (int i = 0; i < chain.Count; i++)
			{
                if (i > 0)
                {
                    var x = new Label { Text = "×" };
                    x.AddThemeFontSizeOverride("font_size", 22);
                    x.AddThemeColorOverride("font_color", new Color("6a6a6a"));
                    bar.AddChild(x);
                }
                var l = new Label { Text = chain[i].txt };
                l.AddThemeFontSizeOverride("font_size", 30);
                l.AddThemeColorOverride("font_color", chain[i].col);
                bar.AddChild(l);
			}
			_topBar = bar;
			AddChild(bar);
		}

		// ════════════════════════════════════════════════════════
		// 最右侧图标菜单（轮转 / 无限 / … / 制作名单）
		// ════════════════════════════════════════════════════════
		private void BuildRightMenu()
		{
			var side = new Panel { MouseFilter = MouseFilterEnum.Stop };
			var ssb = Flat(C_MenuBg, 0);
			side.AddThemeStyleboxOverride("panel", ssb);
			side.SetAnchorsPreset(LayoutPreset.RightWide);
			side.OffsetLeft = -238;

			_menuList = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
			_menuList.AddThemeConstantOverride("separation", 4);
			_menuList.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			_menuList.OffsetLeft = 14; _menuList.OffsetRight = -14;
			_menuList.OffsetTop = 26; _menuList.OffsetBottom = -14;
			side.AddChild(_menuList);

			for (int i = 0; i < MenuNames.Length; i++)
			{
				int idx = i;
				var row = new Panel { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(0, 46) };
				row.AddThemeStyleboxOverride("panel", Flat(new Color(0, 0, 0, 0), 6));

				var hb = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
				hb.AddThemeConstantOverride("separation", 12);
				hb.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
				hb.OffsetLeft = 12; hb.OffsetTop = 6; hb.OffsetRight = -8; hb.OffsetBottom = -6;
				row.AddChild(hb);

				Control icon;
				if (MenuIconDefs[idx] is Vector2 region)
				{
					// 原版图集裁切图标
					var atlas = new AtlasTexture
					{
						Atlas = GD.Load<Texture2D>("res://Assets/UI/tmp_sprites.png"),
						Region = new Rect2(region.X, region.Y, 56, 56),
					};
					icon = new TextureRect
					{
						Texture = atlas,
						CustomMinimumSize = new Vector2(34, 34),
						ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
						StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
						MouseFilter = MouseFilterEnum.Ignore,
					};
					icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
				}
				else
				{
					// 系统字符图标(图集中没有的:秒表/柱状图/问号)
					var ic = new Label { Text = (string)MenuIconDefs[idx], MouseFilter = MouseFilterEnum.Ignore };
					ic.AddThemeFontSizeOverride("font_size", 22);
					ic.AddThemeColorOverride("font_color", MenuCols[idx]);
					ic.CustomMinimumSize = new Vector2(34, 0);
					ic.HorizontalAlignment = HorizontalAlignment.Center;
					ic.SizeFlagsVertical = SizeFlags.ShrinkCenter;
					icon = ic;
				}
				hb.AddChild(icon);

				var name = new Label { Text = MenuNames[idx], MouseFilter = MouseFilterEnum.Ignore };
				name.AddThemeFontSizeOverride("font_size", 19);
				name.AddThemeColorOverride("font_color", C_White);
				name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
				hb.AddChild(name);

				row.MouseEntered += () =>
				{
					if (idx != _curMenu) row.AddThemeStyleboxOverride("panel", Flat(C_Card2, 6));
				};
				row.MouseExited += () =>
				{
					if (idx != _curMenu) row.AddThemeStyleboxOverride("panel", Flat(new Color(0, 0, 0, 0), 6));
				};
				row.GuiInput += ev =>
				{
					if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
						ShowMenu(idx);
				};

				_menuList.AddChild(row);
				_menuRows.Add(row);
			}
			AddChild(side);
		}

		private void ShowMenu(int idx)
		{
			_curMenu = idx;
			BuildTopMultiplierBar();
			// 旧页控件即将释放,先断开动态刷新引用,避免 _Process 访问已释放对象
			_scoreLbl = null; _gainLbl = null; _perRevLbl = null;
			for (int i = 0; i < _menuRows.Count; i++)
				_menuRows[i].AddThemeStyleboxOverride("panel",
					Flat(i == idx ? C_Card : new Color(0, 0, 0, 0), 6));

			foreach (var c in _centerArea.GetChildren()) c.QueueFree();
			Control page = idx switch
			{
				0 => BuildRevolutionView(),
				1 => BuildInfinityPage(),
				2 => BuildInfTreePage(),
				3 => BuildEternityPage(),
				4 => BuildUnityPage(),
				5 => BuildTimeFluxPage(),
				6 => BuildAchievementsPage(),
				7 => BuildStatsPage(),
				8 => BuildSettingsPage(),
				9 => BuildHelpPage(),
				10 => BuildShopPage(),
				_ => BuildCreditsPage(),
			};
			_centerArea.AddChild(page);
		}

		// ════════════════════════════════════════════════════════
		// 底部 "距离无限" 进度条（品红满条）
		// ════════════════════════════════════════════════════════
		private void BuildBottomProgress()
		{
			var outer = new Panel { MouseFilter = MouseFilterEnum.Ignore };
			outer.AddThemeStyleboxOverride("panel", Flat(C_Bg, 0));
			outer.SetAnchorsPreset(LayoutPreset.BottomWide);
			outer.OffsetTop = -48;

			var track = new Panel { MouseFilter = MouseFilterEnum.Ignore };
			track.AddThemeStyleboxOverride("panel", Flat(new Color("3a3a3a"), 8));
			track.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			track.OffsetLeft = 320; track.OffsetRight = -320;
			track.OffsetTop = 9; track.OffsetBottom = -9;
			outer.AddChild(track);

			var fill = new Panel { MouseFilter = MouseFilterEnum.Ignore };
			fill.AddThemeStyleboxOverride("panel", Flat(C_Pink, 8));
			fill.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			fill.AnchorRight = 0;
			track.AddChild(fill);
			_progFill = fill;

			// 进度位置标记(品红箭头,原版样式)
			var marker = new Label { Text = "◀", MouseFilter = MouseFilterEnum.Ignore };
			marker.AddThemeFontSizeOverride("font_size", 17);
			marker.AddThemeColorOverride("font_color", new Color("ffb1ec"));
			marker.SetAnchorsPreset(LayoutPreset.CenterRight);
			marker.OffsetLeft = -12; marker.OffsetRight = 6;
			marker.OffsetTop = -14; marker.OffsetBottom = 14;
			track.AddChild(marker);

			var lbl = new Label
			{
				Text = "距离无限:0%",
				MouseFilter = MouseFilterEnum.Ignore,
			};
			_progLbl = lbl;
			lbl.AddThemeFontSizeOverride("font_size", 17);
			lbl.AddThemeColorOverride("font_color", C_White);
			lbl.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
			lbl.HorizontalAlignment = HorizontalAlignment.Center;
			lbl.VerticalAlignment = VerticalAlignment.Center;
			track.AddChild(lbl);

			AddChild(outer);
		}

		// ════════════════════════════════════════════════════════
		// 中央切换区
		// ════════════════════════════════════════════════════════
		private void BuildCenterArea()
		{
			_centerArea = new MarginContainer { MouseFilter = MouseFilterEnum.Pass };
			_centerArea.SetAnchorsPreset(LayoutPreset.FullRect);
			_centerArea.OffsetRight = -238;
			_centerArea.OffsetTop = 104;
			_centerArea.OffsetBottom = -50;
			_centerArea.AddThemeConstantOverride("margin_left", 18);
			_centerArea.AddThemeConstantOverride("margin_right", 18);
			_centerArea.AddThemeConstantOverride("margin_top", 6);
			_centerArea.AddThemeConstantOverride("margin_bottom", 12);
			AddChild(_centerArea);
		}

		// ──────────────────────────────────────────────────────
		// 轮转主视图：左彩色条堆 + 中央轨道 + 转生窗口
		// ──────────────────────────────────────────────────────
		private Control BuildRevolutionView()
		{
			var root = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
			root.AddThemeConstantOverride("separation", 24);

			// ── 左：圆圈产量条堆 ──
			var stack = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.Fill };
			stack.CustomMinimumSize = new Vector2(248, 0);
			stack.SizeFlagsHorizontal = SizeFlags.Fill;
			stack.AddThemeConstantOverride("separation", 6);
			var g = SD();
			for (int i = 0; i < g.game.circleLevels.Count && i < 11; i++)
			{
				int idx = i;
				long lv = g.game.circleLevels[idx];
				BigDouble cost = idx < g.game.circleCosts.Count ? g.game.circleCosts[idx] : new BigDouble(2.7 * (idx + 1), 3 * idx + 6);
				double inc = 0.5 * (idx + 1) * (1 + lv * 0.01);

				var bar = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(0, 54) };
				bar.AddThemeStyleboxOverride("panel", Flat(BarCols[idx], 8));
				var mv = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
				mv.AddThemeConstantOverride("margin_left", 8);
				mv.AddThemeConstantOverride("margin_right", 10);
				mv.AddThemeConstantOverride("margin_top", 5);
				mv.AddThemeConstantOverride("margin_bottom", 5);
				bar.AddChild(mv);
				var hb2 = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
				hb2.AddThemeConstantOverride("separation", 8);
				mv.AddChild(hb2);
				var icon2 = new TextureRect
				{
					Texture = GD.Load<Texture2D>($"res://Assets/UI/rev{(idx % 10) + 1}.png"),
					CustomMinimumSize = new Vector2(42, 42),
					ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
					StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
					MouseFilter = MouseFilterEnum.Ignore,
				};
				icon2.SizeFlagsVertical = SizeFlags.ShrinkCenter;
				hb2.AddChild(icon2);
				var vb = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
				vb.AddThemeConstantOverride("separation", 1);
				hb2.AddChild(vb);
				var l1 = new Label { Text = $"圈/秒:{(lv * (idx + 1) * 0.5).ToString("0.##")} [+{inc:0.##}]" };
				l1.AddThemeFontSizeOverride("font_size", 13);
				l1.AddThemeColorOverride("font_color", new Color("1a1a1a"));
				vb.AddChild(l1);
				var l2 = new Label { Text = Suf(cost) };
				l2.AddThemeFontSizeOverride("font_size", 15);
				l2.AddThemeColorOverride("font_color", new Color("111111"));
				vb.AddChild(l2);
				bar.GuiInput += ev =>
				{
					if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
					{
						if (GM().TryBuyCircle(idx))
							Toast($"圈 {idx + 1} 升级到 Lv{GM().Save.game.circleLevels[idx]}");
						else
							Toast($"分数不足，圈 {idx + 1} 需要 {Suf(GameManager.GetCircleCost(idx, (int)lv))}");
						ShowMenu(_curMenu);
					}
				};
				stack.AddChild(bar);
			}
			root.AddChild(stack);

			// ── 中：分数 + 轨道 + 礼包行 ──
			var mid = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			mid.AddThemeConstantOverride("separation", 4);

			var scoreRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
			scoreRow.AddThemeConstantOverride("separation", 10);
			_scoreLbl = new Label { Text = Suf(g.game.score) };
			_scoreLbl.AddThemeFontSizeOverride("font_size", 40);
			_scoreLbl.AddThemeColorOverride("font_color", C_White);
			scoreRow.AddChild(_scoreLbl);
			var odot = new Label { Text = "⊙" };
			odot.AddThemeFontSizeOverride("font_size", 24);
			odot.AddThemeColorOverride("font_color", new Color("ffaa33"));
			odot.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			scoreRow.AddChild(odot);
			mid.AddChild(scoreRow);

			var orbitRow = new HBoxContainer();
			orbitRow.AddThemeConstantOverride("separation", 16);

			// 左上 "1" 购买倍率按钮
			var leftCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.Fill };
			leftCol.SizeFlagsHorizontal = SizeFlags.Fill;
			var buy = MakeTextButton("1", C_White, new Color("141414"), 40, 38, 20);
			buy.Pressed += () => Toast("演示:购买倍率 ×1(自动购买已全开)");
			var buyWrap = new CenterContainer();
			buyWrap.CustomMinimumSize = new Vector2(120, 0);
			buyWrap.AddChild(buy);
			leftCol.AddChild(buyWrap);
			orbitRow.AddChild(leftCol);

			// 轨道动画
			_orbit = new OrbitView { CustomMinimumSize = new Vector2(430, 430) };
			_orbit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			_orbit.SizeFlagsVertical = SizeFlags.ExpandFill;
			orbitRow.AddChild(_orbit);

			// 右：产出速率 + 转生窗口
			var rightCol = new VBoxContainer();
			rightCol.CustomMinimumSize = new Vector2(300, 0);
			rightCol.AddThemeConstantOverride("separation", 8);

			var gain = GM().CalculateGainPerSecond();
			_gainLbl = new RichTextLabel
			{
				BbcodeEnabled = true,
				FitContent = true,
				ScrollActive = false,
				MouseFilter = MouseFilterEnum.Ignore,
				SizeFlagsHorizontal = SizeFlags.ExpandFill,
			};
			_gainLbl.AddThemeFontSizeOverride("normal_font_size", 28);
			_gainLbl.BbcodeEnabled = true;
			_gainLbl.AppendText($"[center]+{Suf(gain)} / 秒[/center]");
			rightCol.AddChild(_gainLbl);

			_perRevLbl = new Label { Text = "+0 / 轮转" };
			_perRevLbl.AddThemeFontSizeOverride("font_size", 22);
			_perRevLbl.AddThemeColorOverride("font_color", C_White);
			_perRevLbl.HorizontalAlignment = HorizontalAlignment.Center;
			rightCol.AddChild(_perRevLbl);

			var giftRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
			giftRow.AddThemeConstantOverride("separation", 10);
			var gift = MakeTextButton("新手礼包", new Color("2ecc71"), new Color("103312"), 118, 34, 15);
			gift.Pressed += () => Toast("演示:礼包奖励已全部领取");
			giftRow.AddChild(gift);
			// 绿色圆形按钮 + 橙色通知点(原版样式)
			var cal = new Panel { CustomMinimumSize = new Vector2(34, 34), SizeFlagsVertical = SizeFlags.ShrinkCenter };
			cal.AddThemeStyleboxOverride("panel", Flat(new Color("2ecc71"), 17));
			var dot = new Panel
			{
				CustomMinimumSize = new Vector2(10, 10),
				MouseFilter = MouseFilterEnum.Ignore,
			};
			dot.AddThemeStyleboxOverride("panel", Flat(new Color("f39c12"), 5));
			dot.SetAnchorsPreset(LayoutPreset.TopRight);
			dot.OffsetLeft = -9; dot.OffsetTop = -3;
			dot.OffsetRight = 1; dot.OffsetBottom = 7;
			cal.AddChild(dot);
			cal.GuiInput += ev =>
			{
				if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
					Toast("演示:连续签到 999 天(已满)");
			};
			giftRow.AddChild(cal);
			// 蓝色圆形按钮(排行)
			var chart = new Panel { CustomMinimumSize = new Vector2(34, 34), SizeFlagsVertical = SizeFlags.ShrinkCenter };
			chart.AddThemeStyleboxOverride("panel", Flat(new Color("3f8fe0"), 17));
			chart.GuiInput += ev =>
			{
				if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
					Toast("演示:排行榜 #1");
			};
			giftRow.AddChild(chart);
			var rankLbl = new Label { Text = "#28603" };
			rankLbl.AddThemeFontSizeOverride("font_size", 11);
			rankLbl.AddThemeColorOverride("font_color", C_Gray);
			rankLbl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
			giftRow.AddChild(rankLbl);
			rightCol.AddChild(giftRow);

			rightCol.AddChild(BuildPrestigeWindow());
			orbitRow.AddChild(rightCol);

			mid.AddChild(orbitRow);
			root.AddChild(mid);
			return root;
		}

		private Control BuildPrestigeWindow()
		{
			var g = SD();
			var win = new PanelContainer();
			win.AddThemeStyleboxOverride("panel", Flat(C_Card, 10));
			var vb = new VBoxContainer();
			vb.AddThemeConstantOverride("separation", 7);
			win.AddChild(vb);

			var title = new Label { Text = "转生窗口", HorizontalAlignment = HorizontalAlignment.Center };
			title.AddThemeFontSizeOverride("font_size", 22);
			title.AddThemeColorOverride("font_color", C_White);
			vb.AddChild(title);

			vb.AddChild(HRule());

			var promo = g.game.promotionLevels;
			string p0 = promo.Count > 0 ? Comma(promo[0]) : "1";
			vb.AddChild(SmallLine($"转生指数:^{p0} -> ^{p0}", 15, new Color("d8d8d8")));
			double pm = 1;
			foreach (var p in promo) pm *= p;
			vb.AddChild(SmallLine($"转生倍率:x{Suf(new BigDouble(pm, 0))} -> x{Suf(new BigDouble(pm, 0))}", 15, new Color("d8d8d8")));
			vb.AddChild(SmallLine($"已转生 {g.game.prestigeCount} 次 · 自动晋升已启用", 13, C_Gray));

			var up = MakeTextButton("晋 升", C_Green, new Color("0c2a10"), 0, 44, 22);
			up.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			vb.AddChild(up);
			up.Pressed += () =>
			{
				if (GM().DoPromote())
					Toast("晋升成功， promotion 等级+1");
				else
					Toast("晋升需要转生 5 次");
				ShowMenu(_curMenu);
			};
			return win;
		}

		// ──────────────────────────────────────────────────────
		// 通用页壳：黑底 + 大数字 + 卡片
		// ──────────────────────────────────────────────────────
		private Control PageShell(string title, string bigValue, string bigSub, Color bigCol)
		{
			var scroll = new ScrollContainer { MouseFilter = MouseFilterEnum.Pass };
			scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
			scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;

			var vb = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			vb.AddThemeConstantOverride("separation", 14);
			scroll.AddChild(vb);

			var t = new Label { Text = title };
			t.AddThemeFontSizeOverride("font_size", 30);
			t.AddThemeColorOverride("font_color", C_White);
			vb.AddChild(t);

			if (bigValue != null)
			{
				var big = new Label { Text = bigValue, HorizontalAlignment = HorizontalAlignment.Center };
				big.AddThemeFontSizeOverride("font_size", 44);
				big.AddThemeColorOverride("font_color", bigCol);
				vb.AddChild(big);
				var sub = new Label { Text = bigSub, HorizontalAlignment = HorizontalAlignment.Center };
				sub.AddThemeFontSizeOverride("font_size", 15);
				sub.AddThemeColorOverride("font_color", C_Gray);
				vb.AddChild(sub);
			}
			return scroll;
		}

		private Control Card(string title, IEnumerable<string> lines)
		{
			var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			card.AddThemeStyleboxOverride("panel", Flat(C_Card2, 8));
			var mv = new MarginContainer();
			mv.AddThemeConstantOverride("margin_left", 14);
			mv.AddThemeConstantOverride("margin_right", 14);
			mv.AddThemeConstantOverride("margin_top", 10);
			mv.AddThemeConstantOverride("margin_bottom", 10);
			card.AddChild(mv);
			var vb = new VBoxContainer();
			vb.AddThemeConstantOverride("separation", 6);
			mv.AddChild(vb);
			if (title != null)
			{
				var t = new Label { Text = title };
				t.AddThemeFontSizeOverride("font_size", 17);
				t.AddThemeColorOverride("font_color", MenuCols[0]);
				vb.AddChild(t);
			}
			foreach (var ln in lines)
			{
				var l = new Label { Text = ln };
				l.AddThemeFontSizeOverride("font_size", 14);
				l.AddThemeColorOverride("font_color", new Color("cfcfcf"));
				vb.AddChild(l);
			}
			return card;
		}

		// ──────────────────────────────────────────────────────
		// 各菜单页面
		// ──────────────────────────────────────────────────────
		private Control BuildInfinityPage()
		{
			var g = SD();
			var page = (ScrollContainer)PageShell("无限 Infinity", Suf(g.infinity.infinityPoints), "无限点数 IP · 无限次数 " + Suf(g.infinity.infinities), new Color("38cfc0"));
			var vb = (VBoxContainer)page.GetChild(0);
			vb.AddChild(Card("无限挑战", new[] { "challenge_all · Lv99(满级)", "所有挑战奖励已全部领取" }));
			vb.AddChild(Card("自动无限", new[] { "autoInfinity:开 · autoInfinityIP:开", "autoInfTree:开 · 无限树全节点 MAX" }));
			var infBtn = MakeTextButton("执 行 无 限 (需 1.79e308)", new Color("38cfc0"), new Color("0a2220"), 0, 44, 20);
			infBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			infBtn.Pressed += () =>
			{
				if (GM().TryInfinity())
					Toast("无限成功，IP 已发放");
				else
					Toast("分数未达到 1.79e308");
				ShowMenu(_curMenu);
			};
			vb.AddChild(infBtn);
			vb.AddChild(MilestoneGrid("无限里程碑", 8, new Color("38cfc0")));
			return page;
		}

		private Control BuildInfTreePage()
		{
			var page = (ScrollContainer)PageShell("无限树 Infinity Tree", null, null, C_White);
			var vb = (VBoxContainer)page.GetChild(0);
			vb.AddChild(MilestoneGrid("全部节点 · 等级 MAX", 12, new Color("8bc94f")));
			vb.AddChild(Card("自动重置", new[] { "autoInfTree:开 · 每次无限后自动重植全树" }));
			return page;
		}

		private Control BuildEternityPage()
		{
			var g = SD();
			var page = (ScrollContainer)PageShell("永恒 Eternity", Suf(g.eternity.EP), "永恒点数 EP · eters " + Suf(g.eternity.eters), new Color("f5d43c"));
			var vb = (VBoxContainer)page.GetChild(0);
			vb.AddChild(MilestoneGrid("永恒里程碑 · 20/20", 10, new Color("f5d43c")));
			vb.AddChild(MilestoneGrid("动物里程碑 · 10/10", 5, new Color("ef8b33")));
			vb.AddChild(Card("自动永恒", new[] { "autoEternity:开 · 达到阈值自动永恒并保留全部里程碑" }));
			var eteBtn = MakeTextButton("执 行 永 恒 (需 IP 1e12)", new Color("f5d43c"), new Color("2a230a"), 0, 44, 20);
			eteBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
			eteBtn.Pressed += () =>
			{
				if (GM().DoEternity())
					Toast("永恒成功，EP 已发放");
				else
					Toast("无限点数未达到 1e12");
				ShowMenu(_curMenu);
			};
			vb.AddChild(eteBtn);
			return page;
		}

		private Control BuildUnityPage()
		{
			var g = SD();
			var page = (ScrollContainer)PageShell("统一 Unity", Suf(g.game.unityShards), "统一碎片 · 矿物 " + Suf(g.game.minerals), new Color("9a4dd8"));
			var vb = (VBoxContainer)page.GetChild(0);
			vb.AddChild(Card("自动统一", new[] { "autoUnity:开 · autoMinMerge:开(矿物自动合并)" }));
			vb.AddChild(MilestoneGrid("统一节点", 6, new Color("9a4dd8")));
			return page;
		}

		private Control BuildTimeFluxPage()
		{
			var g = SD();
			var page = (ScrollContainer)PageShell("时间流量 Time Flux", Suf(g.game.timeFlux), "时间流量 · 灵魂 " + Suf(g.game.souls), new Color("ef8b33"));
			var vb = (VBoxContainer)page.GetChild(0);
			vb.AddChild(Card("自动减速", new[] { "autoSlowdown:开 · 时间流量自动收益最大化" }));
			return page;
		}

		private Control BuildAchievementsPage()
		{
			string[][] defs =
			{
				new[] { "初次轮转", "完成第一次转生" },
				new[] { "轮转大师", "完成 50 次转生" },
				new[] { "百转千回", "完成 99 次转生" },
				new[] { "圈满功成", "8 个圆圈全部 Lv30" },
				new[] { "无限启程", "完成第一次无限" },
				new[] { "无限意志", "无限次数达到 1e6" },
				new[] { "永恒瞬间", "完成第一次永恒" },
				new[] { "永恒之躯", "集齐 20 个永恒里程碑" },
				new[] { "动物之友", "集齐 10 个动物里程碑" },
				new[] { "统一万界", "统一碎片达到 1e18" },
				new[] { "时间领主", "时间流量达到 1e9" },
				new[] { "全自动", "解锁全部 17 项自动化" },
			};
			var page = (ScrollContainer)PageShell("成就 Achievements", "12 / 12", "全部成就已解锁", new Color("f5d43c"));
			var vb = (VBoxContainer)page.GetChild(0);
			var grid = new GridContainer { Columns = 3 };
			grid.AddThemeConstantOverride("h_separation", 10);
			grid.AddThemeConstantOverride("v_separation", 10);
			foreach (var d in defs)
			{
				var card = new PanelContainer { CustomMinimumSize = new Vector2(300, 74), SizeFlagsHorizontal = SizeFlags.ExpandFill };
				card.AddThemeStyleboxOverride("panel", Flat(new Color("2a3a2c"), 8));
				var mv = new MarginContainer();
				mv.AddThemeConstantOverride("margin_left", 12); mv.AddThemeConstantOverride("margin_top", 8);
				mv.AddThemeConstantOverride("margin_right", 12); mv.AddThemeConstantOverride("margin_bottom", 8);
				card.AddChild(mv);
				var cvb = new VBoxContainer(); cvb.AddThemeConstantOverride("separation", 3); mv.AddChild(cvb);
				var n = new Label { Text = "[已解锁] " + d[0] };
				n.AddThemeFontSizeOverride("font_size", 15);
				n.AddThemeColorOverride("font_color", C_Green);
				cvb.AddChild(n);
				var ds = new Label { Text = d[1] };
				ds.AddThemeFontSizeOverride("font_size", 12);
				ds.AddThemeColorOverride("font_color", C_Gray);
				cvb.AddChild(ds);
				grid.AddChild(card);
			}
			vb.AddChild(grid);
			return page;
		}

		private Control BuildStatsPage()
		{
			var g = SD();
			var gain = GM().CalculateGainPerSecond();
			var page = (ScrollContainer)PageShell("统计 Statistics", null, null, C_White);
			var vb = (VBoxContainer)page.GetChild(0);
			vb.AddChild(Card("核心", new[]
			{
				"当前分数:" + Suf(g.game.score),
				"总分:" + Suf(g.game.totalScore),
				"每秒产出:" + Suf(gain),
				"转生次数:" + Comma(g.game.prestigeCount) + " 次",
				"游戏时长:" + FmtTime(g.game.playTime),
			}));
			vb.AddChild(Card("层级货币", new[]
			{
				"无限次数:" + Suf(g.infinity.infinities) + " · IP:" + Suf(g.infinity.infinityPoints),
				"EP:" + Suf(g.eternity.EP) + " · eters:" + Suf(g.eternity.eters),
				"统一碎片:" + Suf(g.game.unityShards) + " · 矿物:" + Suf(g.game.minerals),
				"时间流量:" + Suf(g.game.timeFlux) + " · 灵魂:" + Suf(g.game.souls),
			}));
			vb.AddChild(Card("自动化 · 17/17 开启", new[]
			{
				"autoAll / autoPrestige / autoPromote / autoSlowdown",
				"autoInfinity / autoInfinityIP / autoInfTree / autoEternity",
				"autoUnity / autoMinMerge / autoRP / autoStar / autoAnimals",
				"autoBuyRelics / autoBuyRunes / autoTarotDraw / autoSingularity",
			}));
			return page;
		}

		private Control BuildSettingsPage()
		{
			var page = (ScrollContainer)PageShell("选项 Options", null, null, C_White);
			var vb = (VBoxContainer)page.GetChild(0);

			vb.AddChild(Card("通用", new[]
			{
				"数字格式:后缀(2.49 No) · 主题:黑 · 语言:中文",
				"完全体演示:v1.0.0-godot-max(全解锁 · 全自动化 · 数值拉满)",
			}));

			var warn = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			warn.AddThemeStyleboxOverride("panel", Flat(new Color("3a2222"), 8));
			var mv = new MarginContainer();
			mv.AddThemeConstantOverride("margin_left", 14); mv.AddThemeConstantOverride("margin_top", 10);
			mv.AddThemeConstantOverride("margin_right", 14); mv.AddThemeConstantOverride("margin_bottom", 10);
			warn.AddChild(mv);
			var wv = new VBoxContainer(); wv.AddThemeConstantOverride("separation", 8); mv.AddChild(wv);
			var wl = new Label { Text = "危险区(演示版保护:真实存档绝不改动)" };
			wl.AddThemeFontSizeOverride("font_size", 15);
			wl.AddThemeColorOverride("font_color", new Color("ff7a6a"));
			wv.AddChild(wl);
			var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 10); wv.AddChild(row);
			var clear = MakeTextButton("清除存档", new Color("c0483f"), C_White, 110, 36, 14);
			clear.Pressed += () => Toast("演示版:已拦截(你的真实存档安全无损)");
			row.AddChild(clear);
			var reset = MakeTextButton("重置游戏", new Color("c0483f"), C_White, 110, 36, 14);
			reset.Pressed += () => Toast("演示版:已拦截(你的真实存档安全无损)");
			row.AddChild(reset);
			var exp = MakeTextButton("导出存档", new Color("555555"), C_White, 110, 36, 14);
			exp.Pressed += () => Toast("演示版:导出功能未开放");
			row.AddChild(exp);
			vb.AddChild(warn);
			return page;
		}

		private Control BuildHelpPage()
		{
			var page = (ScrollContainer)PageShell("帮助 Help", null, null, C_White);
			var vb = (VBoxContainer)page.GetChild(0);
			vb.AddChild(Card("玩法", new[]
			{
				"1. 圆圈自动产出分数;分数购买更高级圆圈,层层倍增。",
				"2. 分数足够时进行转生(晋升),获得转生指数与倍率加成。",
				"3. 达到 1.79e308 触发无限(IP),之后是永恒(EP)、统一、时间流量。",
				"4. 全部 17 项自动化在本演示中已开启,挂机即可。",
			}));
			vb.AddChild(Card("演示说明", new[]
			{
				"本版本为完全体最终形态:全系统解锁、养成拉满。",
				"界面上的所有按钮均为演示交互,不会写入或破坏真实存档。",
			}));
			return page;
		}

		private Control BuildShopPage()
		{
			string[][] items =
			{
				new[] { "新手礼包", "全部奖励已领取" },
				new[] { "遗物商店", "autoBuyRelics 开启 · 全遗物已购" },
				new[] { "符文商店", "autoBuyRunes 开启 · 全符文已购" },
				new[] { "塔罗抽卡", "autoTarotDraw 开启 · 全塔罗已收集" },
				new[] { "星之祝福", "autoStar 开启 · 全星辰点亮" },
				new[] { "奇点礼包", "autoSingularity 开启 · 奇点已激活" },
			};
			var page = (ScrollContainer)PageShell("商店 Shop", null, null, C_White);
			var vb = (VBoxContainer)page.GetChild(0);
			var grid = new GridContainer { Columns = 3 };
			grid.AddThemeConstantOverride("h_separation", 10);
			grid.AddThemeConstantOverride("v_separation", 10);
			foreach (var it in items)
			{
				var card = new PanelContainer { CustomMinimumSize = new Vector2(280, 84), SizeFlagsHorizontal = SizeFlags.ExpandFill };
				card.AddThemeStyleboxOverride("panel", Flat(C_Card2, 8));
				var mv = new MarginContainer();
				mv.AddThemeConstantOverride("margin_left", 12); mv.AddThemeConstantOverride("margin_top", 8);
				mv.AddThemeConstantOverride("margin_right", 12); mv.AddThemeConstantOverride("margin_bottom", 8);
				card.AddChild(mv);
				var cvb = new VBoxContainer(); cvb.AddThemeConstantOverride("separation", 4); mv.AddChild(cvb);
				var n = new Label { Text = it[0] };
				n.AddThemeFontSizeOverride("font_size", 16);
				n.AddThemeColorOverride("font_color", C_White);
				cvb.AddChild(n);
				var s = new Label { Text = "[已拥有] " + it[1] };
				s.AddThemeFontSizeOverride("font_size", 12);
				s.AddThemeColorOverride("font_color", C_Green);
				cvb.AddChild(s);
				grid.AddChild(card);
			}
			vb.AddChild(grid);
			return page;
		}

		private Control BuildCreditsPage()
		{
			var page = (ScrollContainer)PageShell("制作名单 Credits", null, null, C_White);
			var vb = (VBoxContainer)page.GetChild(0);
			vb.AddChild(Card("Revolution Idle · Godot 复刻演示", new[]
			{
				"原作:oninou — Revolution Idle",
				"移植:Godot 4.2 · C# / BigDouble",
				"本版本:完全体最终形态演示(数值与解锁仅用于展示)",
			}));
			return page;
		}

		// ════════════════════════════════════════════════════════
		// 小部件
		// ════════════════════════════════════════════════════════
		private Control MilestoneGrid(string title, int count, Color col)
		{
			var vb = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			vb.AddThemeConstantOverride("separation", 8);
			var t = new Label { Text = title + "  ·  全部点亮" };
			t.AddThemeFontSizeOverride("font_size", 16);
			t.AddThemeColorOverride("font_color", col);
			vb.AddChild(t);
			var grid = new GridContainer { Columns = 10 };
			grid.AddThemeConstantOverride("h_separation", 8);
			grid.AddThemeConstantOverride("v_separation", 8);
			var rnd = new Random(7);
			for (int i = 0; i < count; i++)
			{
				var dot = new Panel { CustomMinimumSize = new Vector2(34, 34) };
				var c = col;
				c = c.Darkened(0.25f * (rnd.Next(3) - 1) * -1);
				dot.AddThemeStyleboxOverride("panel", Flat(new Color("2a2a2a"), 17));
				var inner = new Panel { MouseFilter = MouseFilterEnum.Ignore };
				inner.AddThemeStyleboxOverride("panel", Flat(c, 12));
				inner.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
				inner.OffsetLeft = 5; inner.OffsetTop = 5; inner.OffsetRight = -5; inner.OffsetBottom = -5;
				dot.AddChild(inner);
				grid.AddChild(dot);
			}
			vb.AddChild(grid);
			var wrap = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
			wrap.AddThemeStyleboxOverride("panel", Flat(C_Card2, 8));
			var mv = new MarginContainer();
			mv.AddThemeConstantOverride("margin_left", 14); mv.AddThemeConstantOverride("margin_top", 10);
			mv.AddThemeConstantOverride("margin_right", 14); mv.AddThemeConstantOverride("margin_bottom", 10);
			wrap.AddChild(mv);
			mv.AddChild(vb);
			return wrap;
		}

		private Label SmallLine(string txt, int size, Color col)
		{
			var l = new Label { Text = txt };
			l.AddThemeFontSizeOverride("font_size", size);
			l.AddThemeColorOverride("font_color", col);
			return l;
		}

		private Button MakeTextButton(string txt, Color bg, Color fg, int w, int h, int fontSize)
		{
			var b = new Button { Text = txt, CustomMinimumSize = new Vector2(w, h), FocusMode = FocusModeEnum.None };
			var n = Flat(bg, 6);
			var h2 = Flat(bg.Lightened(0.12f), 6);
			var p = Flat(bg.Darkened(0.12f), 6);
			b.AddThemeStyleboxOverride("normal", n);
			b.AddThemeStyleboxOverride("hover", h2);
			b.AddThemeStyleboxOverride("pressed", p);
			b.AddThemeFontSizeOverride("font_size", fontSize);
			b.AddThemeColorOverride("font_color", fg);
			b.AddThemeColorOverride("font_hover_color", fg);
			b.AddThemeColorOverride("font_pressed_color", fg);
			return b;
		}

		private Control HRule()
		{
			var r = new ColorRect { Color = C_Line, CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore };
			return r;
		}

		private static StyleBoxFlat Flat(Color bg, int radius)
		{
			var sb = new StyleBoxFlat { BgColor = bg };
			sb.SetCornerRadiusAll(radius);
			return sb;
		}

		private static string FmtTime(double sec)
		{
			var ts = TimeSpan.FromSeconds(sec);
			return ts.Days > 0
				? $"{ts.Days}天{ts.Hours}小时{ts.Minutes}分"
				: $"{ts.Hours}小时{ts.Minutes}分{ts.Seconds}秒";
		}
	}

	// ═══════════════════════════════════════════════════════════
	// 中央轨道动画：红心 + 8 条彩色旋转弧线(原版标志性视觉)
	// ═══════════════════════════════════════════════════════════
	public partial class OrbitView : Control
	{
		private float _t;
		// 原版:9 条长短参差的彩色弧线,由内到外 黄绿青蓝紫品红粉白
		private static readonly Color[] ArcCols =
		{
			new("f5d43c"), new("8bc94f"), new("3fbf7f"), new("38cfc0"),
			new("3f66e0"), new("9a4dd8"), new("d844c8"), new("ef6ab8"), new("efefef"),
		};
		private static readonly float[] ArcSweeps = { 2.4f, 4.8f, 3.3f, 5.6f, 2.9f, 4.4f, 3.8f, 5.1f, 4.2f };

		public override void _Process(double delta)
		{
			_t += (float)delta;
			QueueRedraw();
		}

		public override void _Draw()
		{
			var c = new Vector2(Size.X / 2f, Size.Y / 2f);
			float min = Math.Min(Size.X, Size.Y);
			float baseR = min * 0.12f;
			float step = min * 0.034f;
			float w = 13f;

			// 中心红圆 + 深红外环(原版样式)
			DrawCircle(c, baseR * 0.78f, new Color("8f1f16"));
			DrawCircle(c, baseR * 0.56f, new Color("e8483f"));

			for (int i = 0; i < 9; i++)
			{
				float r = baseR + step * (i + 1) * 1.05f;
				float speed = 0.5f + i * 0.055f;
				// 黄金角分布起始相位,参差有机
				float start = _t * speed + i * 2.4f;
				float sweep = ArcSweeps[i];
				float end = start + sweep;
				DrawArc(c, r, start, end, 96, ArcCols[i], w, true);
				// 圆头端点(原版弧线两端为圆帽)
				DrawCircle(c + new Vector2(MathF.Cos(start), MathF.Sin(start)) * r, w / 2f, ArcCols[i]);
				DrawCircle(c + new Vector2(MathF.Cos(end), MathF.Sin(end)) * r, w / 2f, ArcCols[i]);
			}
		}
	}
}
