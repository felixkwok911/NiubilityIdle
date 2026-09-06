using Godot;
using NiubilityIdle.Data;
using NiubilityIdle.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace NiubilityIdle.Autoload
{
    // 核心循环,对应原游戏 GameController + Revolution.Update + Buyable:
    //   - 每圈有转圈进度(Revolution.progress 0..maxProgress),转满一圈产出 mult × 转生倍率
    //   - 圈速 = 等级 × 0.2/(i+1) 圈/秒(圈1 Lv80 = 16 圈/秒,对齐原版左条显示)
    //   - 价格沿 Buyable(baseCost, costInc) 指数增长,外圈巨贵
    //   - 圈从 1 个逐渐解锁到 11 个(上一圈 Lv>=10 解锁下一圈)
    //   - 转生:转生窗口点 5 次执行,倍率跳涨;无限:1.79e308
    public partial class GameManager : Node
    {
        public static GameManager Instance { get; private set; }
        public SaveData Save { get; private set; } = new();
        public string SavePath => OS.GetUserDataDir() + "/save.json";

        public const int MaxCircles = 11;

        // 成就定义:id/名称/描述/检查(原版 GameData.UnlockAchievement + GetAchievementName/Desc)
        public static readonly (int id, string name, string desc, Func<SaveData, bool> check)[] AchDefs =
        {
            (1, "初次购买", "购买任意圆圈等级", s => s.game.circleLevels.Exists(l => l > 1)),
            (2, "圈 2 解锁", "圆圈 1 达到 Lv10", s => s.game.unlocked >= 2),
            (3, "初次转生", "完成一次转生", s => s.game.prestigeCount >= 1),
            (4, "转生常客", "完成 5 次转生", s => s.game.prestigeCount >= 5),
            (5, "半程无限", "距离无限进度 50%", s => s.game.score.exponent >= 154),
            (6, "分数破亿", "分数达到 100,000,000", s => s.game.score.exponent >= 8),
            (7, "时间领主", "时间流量积累 60 秒", s => s.game.timeFlux.CompareTo(new BigDouble(60, 0)) >= 0),
            (8, "全自动", "开启自动买圈", s => s.game.autoBuy),
            (9, "圈 5 解锁", "解锁圆圈 5", s => s.game.unlocked >= 5),
            (10, "十连轮转", "全部 10 圈同时转动", s => s.game.unlocked >= 10),
            (11, "无限启程", "完成第一次无限", s => s.infinity.infinities >= BigDouble.One),
            (12, "永恒瞬间", "获得第一点 EP", s => s.eternity.EP >= BigDouble.One),
        };

        public override void _Ready()
        {
            Instance = this;
            Load();
            if (Save.game.circleLevels.Count == 0) ResetToNewGame();
            GD.Print($"[NiubilityIdle] start | score {Save.game.score} | circles {Save.game.unlocked}");
        }

        public void ResetToNewGame()
        {
            Save = new SaveData();
            Save.game.circleLevels.Add(1);   // 圈1 开局 Lv1(原版教学开局)
            Save.game.unlocked = 1;
            SaveGame();
        }

        public int Level(int idx) => idx >= 0 && idx < Save.game.circleLevels.Count ? Save.game.circleLevels[idx] : 0;

        // ── Buyable(baseCost, costInc):价格指数曲线 ──
        public static BigDouble GetCircleCost(int idx, int lv)
        {
            double baseCost = 5.0 * System.Math.Pow(60.0, idx);
            return new BigDouble(baseCost, 0) * System.Math.Pow(1.4, lv);
        }

        // Revolution.speed:圈/秒 = 等级 × 0.2/(i+1)(原版左条 [+0.2]..[+0.02])
        public double GetSpeed(int idx) => idx >= Save.game.unlocked ? 0 : Level(idx) * 0.2 / (idx + 1);
        public double GetSpeedInc(int idx) => 0.2 / (idx + 1);

        // 兼容 UI 旧名
        public double GetRate(int idx) => GetSpeed(idx);
        public double GetPreview(int idx) => Save.game.bulkBuy * GetSpeedInc(idx);
        public double GetLapTime() => System.Math.Max(0.4, 1.0 / System.Math.Max(0.05, GetSpeed(0)));
        public BigDouble GetLapGain() => CalculateGainPerSecond() * GetLapTime();

        // Revolution.mult:转一圈基础产出,靠转生倍率放大
        public double GetMult(int idx) => System.Math.Pow(4, idx);
        public double GetEffectiveMult(int idx) => GetMult(idx) * Save.game.prestigeMult;

        public BigDouble GetBulkCost(int idx)
        {
            int lv = Level(idx);
            BigDouble total = BigDouble.Zero;
            for (int k = 0; k < Save.game.bulkBuy; k++) total += GetCircleCost(idx, lv + k);
            return total;
        }

        public bool TryBuyCircle(int idx)
        {
            if (idx >= Save.game.unlocked) return false;
            var cost = GetBulkCost(idx);
            if (Save.game.score < cost) return false;
            Save.game.score -= cost;
            while (Save.game.circleLevels.Count <= idx) Save.game.circleLevels.Add(0);
            Save.game.circleLevels[idx] += Save.game.bulkBuy;
            SaveGame();
            return true;
        }

        // 批量档 1 -> 10 -> 100 -> 1
        public void CycleBulk()
        {
            Save.game.bulkBuy = Save.game.bulkBuy >= 100 ? 1 : Save.game.bulkBuy * 10;
            SaveGame();
        }

        // 每帧驱动,对应原版 Revolution.Update:转圈进度满一圈产出一次
        public override void _Process(double delta)
        {
            var g = Save.game;
            g.playTime += delta;
            g.timeFlux += new BigDouble(delta, 0);     // 时间流量:随游戏时间积累
            while (g.circleLevels.Count < g.unlocked) g.circleLevels.Add(0);
            while (g.revProgress.Count < g.unlocked) g.revProgress.Add(0);

            for (int i = 0; i < g.unlocked; i++)
            {
                double speed = GetSpeed(i);
                if (speed <= 0) continue;
                g.revProgress[i] += speed * delta;
                if (g.revProgress[i] >= 1)
                {
                    int laps = (int)g.revProgress[i];
                    g.revProgress[i] -= laps;
                    var gain = new BigDouble(GetEffectiveMult(i), 0) * laps;
                    g.score += gain;
                    g.totalScore += gain;
                }
            }

            // 自动买圈:从最便宜的可买圈开始买(自动化系统)
            if (g.autoBuy)
            {
                for (int i = g.unlocked - 1; i >= 0; i--)
                {
                    if (g.score >= GetBulkCost(i) * 2) { TryBuyCircle(i); break; }
                }
            }

            // 解锁:上一圈 Lv>=10 解锁下一圈(原版逐圈解锁)
            if (g.unlocked < MaxCircles && Level(g.unlocked - 1) >= 10)
            {
                g.unlocked++;
                SaveGame();
            }

            // 无限:1.79e308(原版阈值)
            if (!g.infBroken && g.score.exponent >= 308 && g.score.mantissa > 1.79)
            {
                g.infBroken = true;
                TryInfinity();
            }

            CheckAchievements();
            if (Engine.GetFramesDrawn() % 600 == 0) SaveGame();
        }

        // 成就检查:满足即解锁(原版 UnlockAchievement)
        public event Action<int> AchievementUnlocked;
        private void CheckAchievements()
        {
            foreach (var a in AchDefs)
            {
                if (Save.game.unlockedAch.Contains(a.id)) continue;
                if (a.check(Save))
                {
                    Save.game.unlockedAch.Add(a.id);
                    AchievementUnlocked?.Invoke(a.id);
                    SaveGame();
                }
            }
        }

        public bool HasAch(int id) => Save.game.unlockedAch.Contains(id);

        public void ToggleAutoBuy()
        {
            Save.game.autoBuy = !Save.game.autoBuy;
            SaveGame();
        }

        public BigDouble CalculateGainPerSecond()
        {
            double inc = 0;
            for (int i = 0; i < Save.game.unlocked; i++) inc += GetSpeed(i) * GetEffectiveMult(i);
            return new BigDouble(inc, 0);
        }

        // ── 转生:转生窗口点 5 次执行(原版"点击 5 次进行转生") ──
        public bool PrestigeClick()
        {
            Save.game.prestigeClicks++;
            if (Save.game.prestigeClicks >= 5)
            {
                Save.game.prestigeClicks = 0;
                DoPrestige();
                return true;
            }
            SaveGame();
            return false;
        }

        public void DoPrestige()
        {
            var g = Save.game;
            // 倍率增量 ≈ 10^((exp-6)/2):1e9 分 -> +1e3,近似原版 x65 -> x93,501 的跳涨
            if (g.score.exponent > 6)
                g.prestigeMult += System.Math.Pow(10, (g.score.exponent - 6) * 0.5);
            g.prestigeExp += 0.01;
            g.prestigeCount++;
            g.score = BigDouble.Zero;
            g.circleLevels = new List<int> { 1 };
            g.revProgress = new List<double>();
            g.unlocked = 1;
            SaveGame();
        }

        // 转生窗口预览:当前倍率 -> 转生后倍率
        public double GetPrestigePreview()
        {
            var g = Save.game;
            double add = g.score.exponent > 6 ? System.Math.Pow(10, (g.score.exponent - 6) * 0.5) : 0;
            return g.prestigeMult + add;
        }

        // 晋升(promotion):转生 5 次后可用
        public bool DoPromote()
        {
            if (Save.game.prestigeCount < 5) return false;
            while (Save.game.promotionLevels.Count < 4) Save.game.promotionLevels.Add(1);
            for (int i = 0; i < Save.game.promotionLevels.Count; i++) Save.game.promotionLevels[i]++;
            SaveGame();
            return true;
        }

        public bool TryInfinity()
        {
            if (Save.game.score.exponent < 308) return false;
            Save.infinity.infinities += BigDouble.One;
            Save.infinity.infinityPoints += new BigDouble(1, Save.game.prestigeCount);
            Save.game.score = BigDouble.Zero;
            GD.Print($"Infinity! total {Save.infinity.infinities}");
            SaveGame();
            return true;
        }

        public bool DoEternity()
        {
            if (Save.infinity.infinityPoints < BigDouble.FromDouble(1e12)) return false;
            var epGain = BigDouble.FromDouble(System.Math.Log10(System.Math.Max(1, Save.infinity.infinityPoints.ToDouble())) * 2);
            Save.eternity.EP += epGain;
            Save.eternity.eters += epGain * 0.1;
            Save.infinity.infinities = BigDouble.Zero;
            Save.infinity.infinityPoints = BigDouble.Zero;
            SaveGame();
            return true;
        }

        public void SaveGame()
        {
            var json = JsonConvert.SerializeObject(Save, Formatting.Indented);
            File.WriteAllText(SavePath, json);
        }

        public void Load()
        {
            if (!File.Exists(SavePath)) return;
            try
            {
                var json = File.ReadAllText(SavePath);
                Save = JsonConvert.DeserializeObject<SaveData>(json) ?? new SaveData();
            }
            catch { Save = new SaveData(); }
        }
    }
}
