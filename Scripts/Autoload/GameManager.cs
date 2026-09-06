using Godot;
using NiubilityIdle.Data;
using NiubilityIdle.Core;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            (11, "无限启程", "完成第一次无限", s => s.infinity.infinities.CompareTo(BigDouble.One) >= 0),
            (12, "永恒瞬间", "获得第一点 EP", s => s.eternity.EP.CompareTo(BigDouble.One) >= 0),
            (13, "初次飞升", "任意圆圈完成飞升", s => s.game.revMult.Exists(m => m > 1)),
            (14, "飞升新秀", "累计飞升 5 次", s => s.game.revMult.Sum(m => System.Math.Floor(System.Math.Log(m, 2) > 0 ? System.Math.Log(m, 2) : 0)) >= 5),
            (15, "飞升大师", "单圈倍率达到 ×16", s => s.game.revMult.Exists(m => m >= 16)),
            (16, "百级圆圈", "单圈达到 Lv50", s => s.game.circleLevels.Exists(l => l >= 50)),
            (17, "分身有术", "批量购买档达到 ×100", s => s.game.bulkBuy >= 100),
            (18, "百万富翁", "分数达到 1,000,000", s => s.game.score.exponent >= 6),
            (19, "万亿大亨", "分数达到 1e12", s => s.game.score.exponent >= 12),
            (20, "皆大欢喜", "转生 10 次", s => s.game.prestigeCount >= 10),
            (21, "晋升之路", "开启层级晋升", s => s.game.promotionLevels.Count > 0),
            (22, "步步高升", "任意晋升位达到 Lv5", s => s.game.promotionLevels.Exists(p => p >= 5)),
            (23, "商店常客", "购买 1 次产出增益", s => s.game.boostLevel >= 1),
            (24, "购物狂", "产出增益达到 Lv3", s => s.game.boostLevel >= 3),
            (25, "加速大师", "使用时间流量加速", s => s.game.boostTime > 0 || s.game.timeFlux.CompareTo(new BigDouble(30, 0)) >= 0),
            (26, "无限三连", "完成 3 次无限", s => s.infinity.infinities.CompareTo(new BigDouble(3, 0)) >= 0),
            (27, "植树造林", "点亮任意无限树节点", s => s.infinity.treeLevels.Exists(t => t >= 1)),
            (28, "参天大树", "任意无限树节点 Lv5", s => s.infinity.treeLevels.Exists(t => t >= 5)),
            (29, "破限而立", "完成一次无限后重回 1e50", s => s.infinity.infinities.CompareTo(BigDouble.One) >= 0 && s.game.score.exponent >= 50),
            (30, "永恒之始", "完成一次永恒", s => s.eternity.eters.CompareTo(BigDouble.Zero) > 0),
        };

        public override void _Ready()
        {
            Instance = this;
            Load();
            if (Save.game.circleLevels.Count == 0) ResetToNewGame();
            ApplyOfflineEarnings();
            GD.Print($"[NiubilityIdle] start | score {Save.game.score} | circles {Save.game.unlocked}");
        }

        // ── 离线收益:50% 效率,上限 4 小时(原版离线结算) ──
        public double OfflineGained { get; private set; } = 0;
        private void ApplyOfflineEarnings()
        {
            var g = Save.game;
            if (g.lastSaveUnix <= 0)
            {
                g.lastSaveUnix = (double)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(); // 首次不给离线
                return;
            }
            double now = (double)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            double elapsed = now - g.lastSaveUnix;
            if (elapsed < 60) return;
            double eff = System.Math.Min(elapsed, GetOfflineCapHours() * 3600);
            var gain = CalculateGainPerSecond() * eff * 0.5;
            if (gain.CompareTo(BigDouble.Zero) > 0)
            {
                g.score += gain;
                g.totalScore += gain;
                OfflineGained = gain.ToDouble();
                g.timeFlux += new BigDouble(eff * 0.5, 0);
            }
            g.lastSaveUnix = now;
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
        public double GetSpeed(int idx) => idx >= Save.game.unlocked ? 0 : Level(idx) * 0.2 / (idx + 1) * GetSpeedTree();
        public double GetSpeedInc(int idx) => 0.2 / (idx + 1) * GetSpeedTree();

        // 兼容 UI 旧名
        public double GetRate(int idx) => GetSpeed(idx);
        public double GetPreview(int idx) => Save.game.bulkBuy * GetSpeedInc(idx);
        public double GetLapTime() => System.Math.Max(0.4, 1.0 / System.Math.Max(0.05, GetSpeed(0)));
        public BigDouble GetLapGain() => CalculateGainPerSecond() * GetLapTime();

        // Revolution.mult:基础 ×(i+1),飞升累乘 —— 原版顶链大数字的来源
        public double GetMult(int idx)
        {
            EnsureRevMult(idx);
            double asc = Save.game.revMult[idx];
            return (idx + 1) * asc;
        }
        private void EnsureRevMult(int idx)
        {
            while (Save.game.revMult.Count <= idx) Save.game.revMult.Add(1);
        }

        // ── 飞升(Ascend):等级换永久倍率,原版核心成长 ──
        public const int AscendLevel = 25;
        public bool CanAscend(int idx) => Level(idx) >= AscendLevel;
        public double AscendGain(int idx) => 1.0 + Level(idx) / (double)AscendLevel; // 本次飞升倍率增量

        public bool TryAscend(int idx)
        {
            if (!CanAscend(idx)) return false;
            EnsureRevMult(idx);
            Save.game.revMult[idx] *= AscendGain(idx);
            Save.game.circleLevels[idx] = 0;
            SaveGame();
            return true;
        }

        // 全部飞升(原版 AscendAll)
        public int AscendAll()
        {
            int n = 0;
            for (int i = 0; i < Save.game.unlocked; i++)
                if (TryAscend(i)) n++;
            return n;
        }

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
            if (g.boostTime > 0) g.boostTime = System.Math.Max(0, g.boostTime - delta);
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
            if (Engine.GetFramesDrawn() % 600 == 0)
            {
                g.lastSaveUnix = (double)System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                SaveGame();
            }
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
            double boost = System.Math.Pow(2, Save.game.boostLevel);        // 商店增益
            boost *= System.Math.Pow(2, TreeLevel(0));                      // 无限树:全局产出
            boost *= 1.0 + Save.eternity.EP.ToDouble() * 0.1;               // 永久:EP 加成
            if (Save.game.boostTime > 0) boost *= 2;                        // 时间流量加速
            return new BigDouble(inc, 0) * boost;
        }

        // ── 无限树(4 节点,IP 购买):产出/圈速/转生增益/离线上限 ──
        public int TreeLevel(int node) => node < Save.infinity.treeLevels.Count ? Save.infinity.treeLevels[node] : 0;
        public BigDouble GetTreeCost(int node) => new BigDouble((node + 1) * System.Math.Pow(5, TreeLevel(node)), 0);
        public double GetSpeedTree() => 1.0 + 0.25 * TreeLevel(1);
        public double GetOfflineCapHours() => 4.0 + 8.0 * TreeLevel(3);

        public bool TryBuyTree(int node)
        {
            if (node < 0 || node > 3) return false;
            var cost = GetTreeCost(node);
            if (Save.infinity.infinityPoints < cost) return false;
            Save.infinity.infinityPoints -= cost;
            while (Save.infinity.treeLevels.Count <= node) Save.infinity.treeLevels.Add(0);
            Save.infinity.treeLevels[node]++;
            SaveGame();
            return true;
        }

        // ── 时间流量加速:花 30 TF → 全局 ×2 持续 60 秒 ──
        public bool TryUseFluxBoost()
        {
            if (Save.game.timeFlux.CompareTo(new BigDouble(30, 0)) < 0) return false;
            Save.game.timeFlux -= new BigDouble(30, 0);
            Save.game.boostTime += 60;
            SaveGame();
            return true;
        }

        // ── 商店增益:每级全局产出 ×2,价格 ×100 递增 ──
        public double GetBoostMult() => System.Math.Pow(2, Save.game.boostLevel);
        public BigDouble GetBoostCost() => new BigDouble(1e6 * System.Math.Pow(100, Save.game.boostLevel), 0);

        public bool TryBuyBoost()
        {
            var cost = GetBoostCost();
            if (Save.game.score < cost) return false;
            Save.game.score -= cost;
            Save.game.boostLevel++;
            SaveGame();
            return true;
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
            // 倍率增量 ≈ 10^((exp-6)/2) × 无限树转生增益:1e9 分 -> +1e3,近似原版 x65 -> x93,501 的跳涨
            if (g.score.exponent > 6)
                g.prestigeMult += System.Math.Pow(10, (g.score.exponent - 6) * 0.5) * (1.0 + 0.5 * TreeLevel(2));
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
            double add = g.score.exponent > 6
                ? System.Math.Pow(10, (g.score.exponent - 6) * 0.5) * (1.0 + 0.5 * TreeLevel(2))
                : 0;
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
            var g = Save.game;
            if (g.score.exponent < 308) return false;
            // IP 增量:1e308 首次约 1e12,分数越高越多
            var ipGain = new BigDouble(System.Math.Max(1, System.Math.Pow(10, (g.score.exponent - 296) * 0.5)), 0);
            Save.infinity.infinities += BigDouble.One;
            Save.infinity.infinityPoints += ipGain;
            g.score = BigDouble.Zero;
            g.circleLevels = new List<int> { 1 };
            g.revProgress = new List<double>();
            g.revMult = new List<double>();
            g.unlocked = 1;
            GD.Print($"Infinity! total {Save.infinity.infinities} | +{ipGain} IP");
            SaveGame();
            return true;
        }

        // ── 永恒:无限次数 ≥ 100 可执行,EP 提供永久加成 ──
        public bool CanEternity() => Save.infinity.infinities.CompareTo(new BigDouble(100, 0)) >= 0;

        public bool DoEternity()
        {
            if (!CanEternity()) return false;
            var epGain = BigDouble.FromDouble(System.Math.Floor(System.Math.Pow(Save.infinity.infinities.ToDouble(), 0.4)));
            Save.eternity.EP += epGain;
            Save.eternity.eters += epGain * 0.1;
            Save.infinity.infinities = BigDouble.Zero;
            Save.infinity.infinityPoints = BigDouble.Zero;
            Save.infinity.treeLevels = new List<int>();
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
