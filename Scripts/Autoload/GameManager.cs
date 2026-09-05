using Godot;
using NiubilityIdle.Data;
using NiubilityIdle.Core;
using Newtonsoft.Json;
using System.IO;

namespace NiubilityIdle.Autoload
{
    // 独立工程的单例，对应原游戏 GameController:2136 + SaveController:2180
    public partial class GameManager : Node
    {
        public static GameManager Instance { get; private set; }
        public SaveData Save { get; private set; } = new();
        public string SavePath => OS.GetUserDataDir() + "/save.json";

        public override void _Ready()
        {
            Instance = this;
            Load();
            // 初始状态开局：无存档则 8 圈 Lv0 起步，可完整体验成长过程
            // 完全体演示改走调试入口 UnlockAllMaxOut()，不再自动覆盖
            if (Save.game.circleLevels.Count == 0)
            {
                ResetToNewGame();
            }
            GD.Print($"[NiubilityIdle_Godot] Score {Save.game.score} | IP {Save.infinity.infinityPoints} | EP {Save.eternity.EP}");
        }

        public void ResetToNewGame()
        {
            Save = new SaveData();
            for (int i = 0; i < 8; i++) { Save.game.circleLevels.Add(0); Save.game.circleCosts.Add(GetCircleCost(i, 0)); }
            SaveGame();
        }

        public static BigDouble GetCircleCost(int idx, int lv) => new BigDouble(10 * (idx + 1), 0) * System.Math.Pow(1.5, lv);

        public bool TryBuyCircle(int idx)
        {
            while (Save.game.circleLevels.Count <= idx) Save.game.circleLevels.Add(0);
            while (Save.game.circleCosts.Count <= idx) Save.game.circleCosts.Add(GetCircleCost(idx, 0));
            var cost = GetCircleCost(idx, Save.game.circleLevels[idx]);
            if (Save.game.score < cost) return false;
            Save.game.score -= cost;
            Save.game.circleLevels[idx]++;
            Save.game.circleCosts[idx] = GetCircleCost(idx, Save.game.circleLevels[idx]);
            SaveGame();
            return true;
        }

        public bool DoPrestige()
        {
            if (Save.game.score < BigDouble.FromDouble(1e6)) return false;
            var gain = BigDouble.FromDouble(System.Math.Log10(System.Math.Max(1, Save.game.score.ToDouble())) * 0.5);
            Save.game.souls += gain;
            Save.game.score = BigDouble.Zero;
            Save.game.prestigeCount++;
            SaveGame();
            return true;
        }

        public bool DoPromote()
        {
            if (Save.game.prestigeCount < 5) return false;
            while (Save.game.promotionLevels.Count < 4) Save.game.promotionLevels.Add(1);
            for (int i = 0; i < Save.game.promotionLevels.Count; i++) Save.game.promotionLevels[i]++;
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

        public void UnlockAllMaxOut()
        {
            // 11圈全满 Lv30(原版 11 条产量条),并填充购买价格
            Save.game.circleLevels.Clear();
            for (int i = 0; i < 11; i++) Save.game.circleLevels.Add(30);
            Save.game.circleCosts.Clear();
            for (int i = 0; i < 11; i++) Save.game.circleCosts.Add(new BigDouble(2.7 * (i + 1), 3 * i + 6));
            Save.game.score = new BigDouble(9.99, 308);
            Save.game.totalScore = new BigDouble(9.99, 310);
            Save.game.prestigeCount = 99;
            Save.game.souls = new BigDouble(9.99, 12);
            Save.game.timeFlux = new BigDouble(9.99, 9);
            Save.game.promotionLevels = new System.Collections.Generic.List<int> { 99, 99, 99, 99 };
            Save.game.unityShards = new BigDouble(9.99, 18);
            Save.game.minerals = new BigDouble(9.99, 15);
            Save.game.allUnlocked = true;
            var autos = new[]{"autoAll","autoPrestige","autoInfinity","autoInfinityIP","autoEternity","autoInfTree","autoSlowdown","autoPromote","autoStar","autoAnimals","autoRP","autoUnity","autoMinMerge","autoBuyRelics","autoBuyRunes","autoTarotDraw","autoSingularity"};
            Save.game.automation.Clear();
            foreach (var a in autos) Save.game.automation[a] = true;
            // Infinity / Eternity 拉满
            Save.infinity.infinities = new BigDouble(9.99, 6);
            Save.infinity.infinityPoints = new BigDouble(9.99, 12);
            Save.infinity.stats.Clear();
            Save.infinity.stats.Add(new InfinityStat { id = "challenge_all", level = 99 });
            Save.eternity.EP = new BigDouble(9.99, 18);
            Save.eternity.eters = new BigDouble(9.99, 15);
            Save.eternity.eternityMilestones.Clear();
            Save.eternity.animalMilestones.Clear();
            for (int i = 0; i < 20; i++) Save.eternity.eternityMilestones.Add(true);
            for (int i = 0; i < 10; i++) Save.eternity.animalMilestones.Add(true);
            Save.version = "1.0.0-godot-max";
        }

        public override void _Process(double delta)
        {
            Save.game.playTime += delta;
            // 核心挂机：每秒按 circleLevels 产出
            var gain = CalculateGainPerSecond();
            Save.game.score += gain * delta;
            Save.game.totalScore += gain * delta;
            // 自动保存
            if (Engine.GetFramesDrawn() % 600 == 0) SaveGame();
        }

        public BigDouble CalculateGainPerSecond()
        {
            // 简化公式：每个 circle 等级 * 倍率，原游戏在 GameController:2180 附近
            double mult = 1;
            for (int i = 0; i < Save.game.circleLevels.Count; i++)
                mult += Save.game.circleLevels[i] * (i + 1) * 0.5;
            // 叠加 Infinity/Eternity 倍率
            mult *= (1 + Save.infinity.infinityPoints.ToDouble() * 0.1);
            mult *= (1 + Save.eternity.EP.ToDouble() * 0.2);
            return new BigDouble(mult, 0);
        }

        public void AddCircle(int index)
        {
            while (Save.game.circleLevels.Count <= index) Save.game.circleLevels.Add(0);
            Save.game.circleLevels[index]++;
            GD.Print($"Circle {index} -> Lv{Save.game.circleLevels[index]}");
        }

        public bool TryInfinity()
        {
            // 原游戏 1.79e308 阈值
            if (Save.game.score.exponent < 308) return false;
            Save.infinity.infinities += BigDouble.One;
            Save.infinity.infinityPoints += new BigDouble(1, Save.game.prestigeCount);
            Save.game.score = BigDouble.Zero;
            Save.game.prestigeCount++;
            GD.Print($"Infinity! 总 {Save.infinity.infinities}");
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
