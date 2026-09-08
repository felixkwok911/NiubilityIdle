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

        public const int MaxCircles = 10;

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

        // ── Buyable(baseCost, costInc):价格指数曲线,原版圈1 Lv80 ≈ 8.6M ──
        public static BigDouble GetCircleCost(int idx, int lv)
        {
            double baseCost = 10.0 * System.Math.Pow(40.0, idx);
            return new BigDouble(baseCost, 0) * System.Math.Pow(1.22, lv);
        }

        // Revolution.speed:圈/秒 = 等级 × 0.2/(i+1)(原版左条 [+0.2]..[+0.02])
        public double GetSpeed(int idx) => idx >= Save.game.unlocked ? 0 : Level(idx) * 0.2 / (idx + 1) * GetSpeedTree() * GetRelicSpeed();
        public double GetSpeedInc(int idx) => 0.2 / (idx + 1) * GetSpeedTree() * GetRelicSpeed();

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

        // ── 飞升(Ascend):等级满 25 重置,倍率固定 ×2(原版 mult = base × 2^飞升次数) ──
        public const int AscendLevel = 25;
        public bool CanAscend(int idx) => Level(idx) >= AscendLevel;
        public double AscendGain(int idx) => 2.0;

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
            // 统一层:eters 持续产生统一碎片(EP 越多 eters 涨越快)
            if (Save.eternity.EP.CompareTo(BigDouble.Zero) > 0)
            {
                Save.eternity.eters += Save.eternity.EP * delta * 0.01;
                g.unityShards += Save.eternity.eters * delta * 0.05 * GetShardMult();
            }
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
                    g.minerals += new BigDouble(0.5 * laps * (i + 1), 0);   // 转圈掉落矿物
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
            RunMacro(delta);
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
            boost *= GetUnityMult();                                        // 统一升级
            boost *= GetTarotMult();                                        // 塔罗收集
            boost *= GetAttackBonus();                                      // 攻击波次
            boost *= GetSingularityMult();                                  // 奇点
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
            // 原版:x65.45 -> x93,501 的跳涨 —— 新倍率 = 旧倍率 × (score/1e15)^0.25
            if (g.score.exponent >= 15)
            {
                double factor = System.Math.Pow(g.score.ToDouble() / 1e15, 0.25);
                if (factor > 1) g.prestigeMult *= factor;
            }
            g.prestigeExp += 0.07;   // 原版:^1.04 -> ^1.11
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
            double factor = g.score.exponent >= 15
                ? System.Math.Pow(g.score.ToDouble() / 1e15, 0.25)
                : 1;
            return g.prestigeMult * System.Math.Max(1, factor);
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

        // ── 统一(Unity):eters 自动积累统一碎片,碎片买永久统一升级 ──
        public double GetUnityMult() => 1.0 + 0.25 * UnityLevel(1);   // node1:全局产出 +25%/级
        public double GetShardMult() => 1.0 + 0.5 * UnityLevel(0);    // node0:碎片产出 +50%/级
        public int UnityLevel(int node) => node < Save.game.unityLevels.Count ? Save.game.unityLevels[node] : 0;
        public BigDouble GetUnityCost(int node) => new BigDouble(10.0 * System.Math.Pow(25, node) * System.Math.Pow(3, UnityLevel(node)), 0);

        public bool TryBuyUnity(int node)
        {
            if (node < 0 || node > 1) return false;
            var cost = GetUnityCost(node);
            if (Save.game.unityShards.CompareTo(cost) < 0) return false;
            Save.game.unityShards -= cost;
            while (Save.game.unityLevels.Count <= node) Save.game.unityLevels.Add(0);
            Save.game.unityLevels[node]++;
            SaveGame();
            return true;
        }

        // ── 遗物:转生 5 次解锁,每级全部圈速 +10% ──
        public int RelicLevel => Save.game.relicLevel;
        public BigDouble GetRelicCost() => new BigDouble(1e7 * System.Math.Pow(10, Save.game.relicLevel), 0);
        public double GetRelicSpeed() => 1.0 + 0.1 * Save.game.relicLevel;

        public bool TryBuyRelic()
        {
            if (Save.game.prestigeCount < 5) return false;
            var cost = GetRelicCost();
            if (Save.game.score < cost) return false;
            Save.game.score -= cost;
            Save.game.relicLevel++;
            SaveGame();
            return true;
        }

        // ── 塔罗:花分数抽取,22 张集齐,每张全局产出 +5%,集齐额外 ×2 ──
        public const int TarotTotal = 22;
        public int TarotCount => Save.game.tarot.Count;
        public BigDouble GetTarotCost() => new BigDouble(1e9 * System.Math.Pow(5, TarotCount), 0);
        public double GetTarotMult()
        {
            double m = 1.0 + 0.05 * TarotCount;
            if (TarotCount >= TarotTotal) m *= 2;
            return m;
        }

        public int TryDrawTarot()
        {
            var cost = GetTarotCost();
            if (Save.game.score < cost || TarotCount >= TarotTotal) return -1;
            Save.game.score -= cost;
            var pool = Enumerable.Range(0, TarotTotal).Where(id => !Save.game.tarot.Contains(id)).ToList();
            int got = pool[new Random().Next(pool.Count)];
            Save.game.tarot.Add(got);
            SaveGame();
            return got;
        }

        // ── 攻击系统:升级攻击力,打 Boss 波次,每波全局永久 +10% ──
        public double GetAttackPower() => (1 + Save.game.attackLevel) * System.Math.Pow(1.5, Save.game.attackLevel);
        public BigDouble GetAttackUpgradeCost() => new BigDouble(1e4 * System.Math.Pow(8, Save.game.attackLevel), 0);
        public double GetAttackBonus() => System.Math.Pow(1.1, Save.game.bossWave);

        // 攻击一次:造成攻击力伤害,击杀 Boss 进入下一波
        public bool TryAttack()
        {
            var g = Save.game;
            g.bossHp -= GetAttackPower();
            if (g.bossHp <= 0)
            {
                g.bossWave++;
                g.bossHp = 50 * System.Math.Pow(3, g.bossWave);
                SaveGame();
                return true; // 击杀
            }
            SaveGame();
            return false;
        }

        public bool TryUpgradeAttack()
        {
            var cost = GetAttackUpgradeCost();
            if (Save.game.score < cost) return false;
            Save.game.score -= cost;
            Save.game.attackLevel++;
            SaveGame();
            return true;
        }

        // ── 奇点:无限 ≥ 100 可点燃,消耗全部无限次数换全局 ×2 ──
        public bool CanIgnite() => Save.infinity.infinities.CompareTo(new BigDouble(100, 0)) >= 0;

        public bool TryIgnite()
        {
            if (!CanIgnite()) return false;
            Save.infinity.infinities = BigDouble.Zero;
            Save.game.singularities++;
            SaveGame();
            return true;
        }

        public double GetSingularityMult() => System.Math.Pow(2, Save.game.singularities);

        // ── 宏:自动执行序列,每 2 秒一步(原版 MacroController) ──
        public event Action<string> MacroFired;
        public void MacroToggle()
        {
            Save.game.macroOn = !Save.game.macroOn;
            Save.game.macroTimer = 0;
            SaveGame();
        }

        public void MacroAdd(string step)
        {
            if (Save.game.macroSteps.Count < 8) Save.game.macroSteps.Add(step);
            SaveGame();
        }

        public void MacroClear()
        {
            Save.game.macroSteps.Clear();
            Save.game.macroOn = false;
            Save.game.macroIdx = 0;
            SaveGame();
        }

        private void RunMacro(double delta)
        {
            var g = Save.game;
            if (!g.macroOn || g.macroSteps.Count == 0) return;
            g.macroTimer += delta;
            if (g.macroTimer < 2) return;
            g.macroTimer = 0;
            string step = g.macroSteps[g.macroIdx % g.macroSteps.Count];
            g.macroIdx++;
            switch (step)
            {
                case "buy0": TryBuyCircle(0); break;
                case "buy1": TryBuyCircle(1); break;
                case "buyAll":
                    for (int i = g.unlocked - 1; i >= 0; i--) TryBuyCircle(i);
                    break;
                case "ascend": AscendAll(); break;
                case "prestige": PrestigeClick(); break;
                case "boost": TryUseFluxBoost(); break;
            }
            MacroFired?.Invoke(step);
            SaveGame();
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
